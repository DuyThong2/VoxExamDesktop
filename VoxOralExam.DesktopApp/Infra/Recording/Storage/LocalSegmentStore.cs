using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoxOralExam.Core.Models;

namespace VoxOralExam.DesktopApp.Infra.Recording.Storage;

public sealed class LocalSegmentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private RecordingManifest? _manifest;
    private string? _attemptDirectory;
    private string? _manifestPath;

    public string BaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vox",
        "Recordings");

    public async Task InitializeAsync(
        RecordingSessionContext context,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _attemptDirectory = Path.Combine(BaseDirectory, context.AttemptId.ToString("D"));
            _manifestPath = Path.Combine(_attemptDirectory, "recording.json");
            Directory.CreateDirectory(_attemptDirectory);
            Directory.CreateDirectory(Path.Combine(_attemptDirectory, "camera"));
            Directory.CreateDirectory(Path.Combine(_attemptDirectory, "screen"));

            foreach (var partial in Directory.EnumerateFiles(
                         _attemptDirectory,
                         "*.partial.mp4",
                         SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(partial);
                }
                catch
                {
                    // A stale partial is never uploaded; keep initializing the valid manifest.
                }
            }

            if (File.Exists(_manifestPath))
            {
                await using var input = File.OpenRead(_manifestPath);
                _manifest = await JsonSerializer.DeserializeAsync<RecordingManifest>(
                    input,
                    JsonOptions,
                    ct);
            }

            _manifest ??= new RecordingManifest
            {
                AttemptId = context.AttemptId,
                ScheduleId = context.ScheduleId,
                SessionId = context.SessionId
            };

            foreach (var segment in _manifest.Segments.Where(
                         segment => segment.State == SegmentUploadState.Uploading))
            {
                segment.State = SegmentUploadState.Pending;
            }

            await SaveManifestUnsafeAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void EnsureFreeSpace(long requiredBytes)
    {
        Directory.CreateDirectory(BaseDirectory);
        var root = Path.GetPathRoot(Path.GetFullPath(BaseDirectory))
            ?? throw new InvalidOperationException("Cannot resolve the recording drive.");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < requiredBytes)
        {
            throw new IOException(
                $"Not enough free space for recording. Required {requiredBytes} bytes, " +
                $"available {drive.AvailableFreeSpace} bytes.");
        }
    }

    public string CreatePartialPath(
        RecordingStreamType streamType,
        string streamId,
        long sequence)
    {
        var attemptDirectory = _attemptDirectory
            ?? throw new InvalidOperationException("The segment store is not initialized.");
        var streamDirectory = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(streamId)))
            .ToLowerInvariant()[..16];
        var directory = Path.Combine(
            attemptDirectory,
            ToWireValue(streamType),
            streamDirectory);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{sequence:D6}.partial.mp4");
    }

    public async Task<CompletedSegment> CommitAsync(
        string streamId,
        RecordingStreamType streamType,
        long sequence,
        string partialPath,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var attemptDirectory = _attemptDirectory
                ?? throw new InvalidOperationException("The segment store is not initialized.");
            var manifest = _manifest
                ?? throw new InvalidOperationException("The segment manifest is not initialized.");

            var readyPath = partialPath.Replace(
                ".partial.mp4",
                ".mp4",
                StringComparison.OrdinalIgnoreCase);
            File.Move(partialPath, readyPath, overwrite: true);

            await using var input = new FileStream(
                readyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                useAsync: true);
            var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(input, ct))
                .ToLowerInvariant();
            var size = input.Length;

            manifest.Segments.RemoveAll(segment =>
                segment.StreamId == streamId && segment.Sequence == sequence);
            manifest.Segments.Add(new StoredSegment
            {
                StreamId = streamId,
                StreamType = ToWireValue(streamType),
                Sequence = sequence,
                RelativePath = Path.GetRelativePath(attemptDirectory, readyPath),
                StartedAt = startedAt,
                EndedAt = endedAt,
                Sha256 = hash,
                State = SegmentUploadState.Pending
            });

            await SaveManifestUnsafeAsync(ct);

            return new CompletedSegment(
                streamId,
                streamType,
                sequence,
                readyPath,
                startedAt,
                endedAt,
                size,
                hash);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CompletedSegment>> GetPendingSegmentsAsync(
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var manifest = _manifest;
            var attemptDirectory = _attemptDirectory;
            if (manifest is null || attemptDirectory is null)
            {
                return [];
            }

            return manifest.Segments
                .Where(segment => segment.State is SegmentUploadState.Pending or SegmentUploadState.Failed)
                .OrderBy(segment => segment.StreamId, StringComparer.Ordinal)
                .ThenBy(segment => segment.Sequence)
                .Select(segment => ToCompletedSegment(segment, attemptDirectory))
                .Where(segment => File.Exists(segment.FilePath))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkUploadingAsync(CompletedSegment segment, CancellationToken ct) =>
        UpdateStateAsync(segment, SegmentUploadState.Uploading, null, ct);

    public Task MarkAcknowledgedAsync(CompletedSegment segment, CancellationToken ct) =>
        UpdateStateAsync(segment, SegmentUploadState.Acknowledged, null, ct);

    public Task MarkFailedAsync(
        CompletedSegment segment,
        string error,
        CancellationToken ct) =>
        UpdateStateAsync(segment, SegmentUploadState.Failed, error, ct);

    public async Task<int> GetOutstandingCountAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _manifest?.Segments.Count(segment =>
                segment.State != SegmentUploadState.Acknowledged) ?? 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpdateStateAsync(
        CompletedSegment completed,
        SegmentUploadState state,
        string? error,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var segment = _manifest?.Segments.FirstOrDefault(candidate =>
                candidate.StreamId == completed.StreamId &&
                candidate.Sequence == completed.Sequence);
            if (segment is null)
            {
                return;
            }

            segment.State = state;
            segment.LastError = error;
            await SaveManifestUnsafeAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveManifestUnsafeAsync(CancellationToken ct)
    {
        var manifest = _manifest
            ?? throw new InvalidOperationException("The segment manifest is not initialized.");
        var path = _manifestPath
            ?? throw new InvalidOperationException("The manifest path is not initialized.");
        var temporaryPath = path + ".tmp";

        await using (var output = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(output, manifest, JsonOptions, ct);
            await output.FlushAsync(ct);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private static CompletedSegment ToCompletedSegment(
        StoredSegment stored,
        string attemptDirectory)
    {
        var streamType = string.Equals(
            stored.StreamType,
            "camera",
            StringComparison.OrdinalIgnoreCase)
            ? RecordingStreamType.Camera
            : RecordingStreamType.Screen;
        var path = Path.GetFullPath(Path.Combine(attemptDirectory, stored.RelativePath));
        var size = File.Exists(path) ? new FileInfo(path).Length : 0;

        return new CompletedSegment(
            stored.StreamId,
            streamType,
            stored.Sequence,
            path,
            stored.StartedAt,
            stored.EndedAt,
            size,
            stored.Sha256);
    }

    private static string ToWireValue(RecordingStreamType streamType) =>
        streamType == RecordingStreamType.Camera ? "camera" : "screen";
}
