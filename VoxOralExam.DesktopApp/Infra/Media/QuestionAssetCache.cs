using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Media;

/// <inheritdoc cref="IQuestionAssetCache"/>
public sealed class QuestionAssetCache : IQuestionAssetCache
{
    // URL tài nguyên là link S3 công khai (không ký, không hết hạn) nên tải trần, không cần token.
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(PerAssetTimeoutSeconds)
    };

    private const int PerAssetTimeoutSeconds = 60;
    private const int MaxAttemptsPerAsset = 3;

    // PrefetchAsync được gọi hai lần cho cùng một bộ tài nguyên: một lượt chạy nền ngay sau khi
    // nhận đề, một lượt chờ-cho-xong lúc bấm vào thi. Không khoá thì hai lượt cùng ghi vào một tệp
    // .part. Khoá lại thì lượt sau chờ lượt trước rồi thấy tệp đã nằm sẵn -- gần như tức thì.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string _cacheDirectory;

    public QuestionAssetCache()
    {
        _cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "VoxOralExam",
            "question-assets");
    }

    public async Task<IReadOnlyList<string>> PrefetchAsync(
        IEnumerable<QuestionAsset> assets,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        // TEXT_PASSAGE không có tệp -- nội dung nằm ngay trong Transcript.
        var urls = assets
            .Where(asset => asset.Type != QuestionAssetType.TextPassage)
            .Select(asset => asset.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (urls.Count == 0)
        {
            return Array.Empty<string>();
        }

        await _gate.WaitAsync(ct);
        try
        {
            return await DownloadAllAsync(urls, onProgress, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> DownloadAllAsync(
        List<string> urls,
        Action<int, int>? onProgress,
        CancellationToken ct)
    {
        var failed = new List<string>();

        Directory.CreateDirectory(_cacheDirectory);
        var done = 0;
        onProgress?.Invoke(done, urls.Count);

        // Tải TUẦN TỰ, không song song: máy phòng thi thường dùng chung một đường truyền hẹp, mà
        // tải đồng loạt chỉ chia nhỏ băng thông chứ không rút ngắn tổng thời gian -- đổi lại làm
        // tiến độ hiện ra giật cục và khó đoán.
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();

            if (await TryDownloadAsync(url, ct))
            {
                done++;
                onProgress?.Invoke(done, urls.Count);
                continue;
            }

            failed.Add(url);
            done++;
            onProgress?.Invoke(done, urls.Count);
        }

        LocalFileLogger.Info("asset_cache", "prefetch_done", new
        {
            total = urls.Count,
            failed = failed.Count
        });

        return failed;
    }

    public string? TryGetLocalPath(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var path = ResolveCachePath(url);
        return File.Exists(path) ? path : null;
    }

    public void Clear()
    {
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // Không để việc dọn rác làm hỏng luồng nộp bài -- lần thi sau tải đè lên là xong.
            LocalFileLogger.Error("asset_cache", "clear_failed", ex);
        }
    }

    private async Task<bool> TryDownloadAsync(string url, CancellationToken ct)
    {
        var path = ResolveCachePath(url);
        if (File.Exists(path))
        {
            return true;
        }

        for (var attempt = 1; attempt <= MaxAttemptsPerAsset; attempt++)
        {
            // Tải vào tệp .part rồi mới đổi tên: cắt điện hay đóng app giữa chừng sẽ để lại một
            // tệp dở, mà tệp dở nằm đúng tên đệm thì lần sau File.Exists coi như đã tải xong và
            // phát ra một đoạn media cụt.
            var partialPath = path + ".part";

            try
            {
                var bytes = await Http.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(partialPath, bytes, ct);
                File.Move(partialPath, path, overwrite: true);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDeleteQuietly(partialPath);
                throw;
            }
            catch (Exception ex)
            {
                TryDeleteQuietly(partialPath);
                LocalFileLogger.Error("asset_cache", "download_failed", ex, new
                {
                    url,
                    attempt
                });

                if (attempt == MaxAttemptsPerAsset)
                {
                    return false;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }

        return false;
    }

    /// <summary>
    /// Tên tệp = hash của URL + phần mở rộng gốc.
    ///
    /// <para>Hash vì URL chứa ký tự không hợp lệ cho tên tệp và có thể dài quá giới hạn đường dẫn.
    /// Giữ phần mở rộng vì <c>MediaElement</c> chọn bộ giải mã theo đuôi tệp -- mất đuôi là mất
    /// khả năng phát.</para>
    /// </summary>
    private string ResolveCachePath(string url)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(_cacheDirectory, hash + GetExtension(url));
    }

    private static string GetExtension(string url)
    {
        try
        {
            // Cắt query string trước: đuôi tệp nằm ở phần path, còn tham số phía sau dấu ? sẽ bị
            // Path.GetExtension nhặt nhầm.
            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.AbsolutePath
                : url;
            var extension = Path.GetExtension(path);
            return string.IsNullOrWhiteSpace(extension) ? ".bin" : extension;
        }
        catch
        {
            return ".bin";
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Tệp dở sót lại không ảnh hưởng gì: lần tải sau ghi đè lên chính nó.
        }
    }
}
