using VoxOralExam.Core.Models;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Question;

/// <summary>
/// Trình bày tài nguyên của câu hỏi theo hai luật khác nhau:
///
/// <para><b>Ảnh / đoạn văn</b>: hiện lên rồi GIỮ NGUYÊN suốt câu hỏi, thí sinh nhìn bao nhiêu tuỳ
/// ý. Không chờ ở đây -- thời gian chuẩn bị do <c>RunPreparationAsync</c> đếm SAU khi đề bài đã
/// được đọc. Trước đây chỗ này chờ đúng <c>preparationTimeSeconds</c> rồi hàm kia lại chờ thêm
/// lần nữa, nên prep 30 giây thành 60, và 30 giây đầu là ngồi nhìn ảnh khi CHƯA biết đề bài.</para>
///
/// <para><b>Audio / video</b>: phát đúng MỘT lần, không nghe lại -- giống mọi kỳ thi nói thật
/// (TOEFL/IELTS/VSTEP/PTE). Chờ hết media rồi mới đọc đề bài.</para>
/// </summary>
public sealed class QuestionAssetPresentationCoordinator
{
    /// <summary>
    /// Trần an toàn cho một lượt phát, KHÔNG phải độ dài dự kiến của media.
    ///
    /// <para>Trước đây trần là <c>asset.DurationSeconds ?? preparationTimeSeconds</c> với fallback
    /// 30 giây. Đó là con số người soạn gõ tay: gõ thiếu thì cắt ngang đoạn nghe, gõ đúng thì vô
    /// ích vì <c>MediaEnded</c> vốn đã báo đúng lúc kết thúc thật. Nên bỏ hẳn khỏi luồng và để một
    /// trần rộng cố định, chỉ dùng khi KHÔNG sự kiện nào tới (codec lạ, file hỏng kiểu
    /// <c>MediaFailed</c> cũng không bắn).</para>
    /// </summary>
    private const int MediaCeilingSeconds = 300;

    private TaskCompletionSource<bool>? _pendingMediaCompletionTcs;

    /// <summary>
    /// Asset cần hiện; <c>null</c> nghĩa là dọn khung. Cờ thứ hai là ĐƯỢC PHÉP PHÁT hay không --
    /// ảnh và đoạn văn không quan tâm, nhưng audio/video thì đây là ranh giới giữa "phát lần đầu"
    /// và "hiện lại khung của thứ đã nghe xong" (xem <see cref="ShowWithoutWaiting"/>).
    /// </summary>
    public event Action<QuestionAsset?, bool>? OnAssetDisplayRequested;

    /// <summary>
    /// Media bắt đầu / kết thúc phát. <c>ExamAttemptRunner</c> dùng để TẮT MIC trong lúc phát.
    ///
    /// <para>Bắt buộc: mic thu liên tục bất kể lượt nói có mở hay không
    /// (<c>TurnAudioRecorder.StreamChunkAvailable</c>), <c>handle_audio_frame</c> bên Python nhồi
    /// thẳng vào bộ đệm lượt, <c>TranscriptAccumulator</c> chỉ reset lúc chốt lượt, và <c>WaveIn</c>
    /// không khử vọng. Không tắt mic thì tiếng loa bị chép thành lời thí sinh và đi vào file WAV
    /// dùng chấm phát âm.</para>
    /// </summary>
    public event Action<bool>? MediaPlaybackStateChanged;

    /// <summary>
    /// Chạm trần an toàn mà media vẫn đang chạy -- bảo View dừng hẳn <c>MediaElement</c>.
    /// Thiếu bước này thì media quá hạn vẫn kêu chồng lên tiếng AI đọc đề, đúng lúc mic đã mở.
    /// </summary>
    public event Action? MediaStopRequested;

    public async Task PresentAsync(QuestionAsset asset, CancellationToken ct)
    {
        // Ảnh và đoạn văn không có gì để chờ: hiện ra rồi trả về ngay.
        if (asset.Type == QuestionAssetType.Image || asset.Type == QuestionAssetType.TextPassage)
        {
            OnAssetDisplayRequested?.Invoke(asset, true);
            return;
        }

        // Chỗ chờ phải dựng TRƯỚC khi yêu cầu phát, không được sau.
        //
        // OnAssetDisplayRequested đi tới ExamViewModel.HandleAssetDisplayRequested, nơi dùng
        // Dispatcher.Invoke -- ĐỒNG BỘ. Nên khi lời gọi đó trả về thì việc phát đã bắt đầu (và với
        // tệp hỏng thì đã kết thúc). Dựng chỗ chờ sau đó nghĩa là CompleteMediaPlayback của lần
        // hỏng ấy bắn vào một tcs chưa tồn tại, rơi vào hư không -- rồi hàm này chờ tiếp cho tới
        // hết trần an toàn 300 giây. Một tệp audio không mở được sẽ làm bài thi đứng 5 phút.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingMediaCompletionTcs = tcs;
        MediaPlaybackStateChanged?.Invoke(true);

        OnAssetDisplayRequested?.Invoke(asset, true);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(MediaCeilingSeconds));
            using var registration = timeoutCts.Token.Register(() => tcs.TrySetResult(false));
            var playedToEnd = await tcs.Task;
            if (!playedToEnd)
            {
                MediaStopRequested?.Invoke();
            }
        }
        finally
        {
            MediaPlaybackStateChanged?.Invoke(false);
            if (ReferenceEquals(_pendingMediaCompletionTcs, tcs))
            {
                _pendingMediaCompletionTcs = null;
            }
        }
    }

    /// <summary>
    /// Hiện asset mà KHÔNG chờ và KHÔNG phát lại -- dùng cho nhánh vào lại giữa câu.
    ///
    /// <para>Thí sinh đã chuẩn bị ở lần vào trước và ngân sách nói đang đếm tiếp từ
    /// <c>resumeSpokenSeconds</c>, nên chờ lại là lấy thời gian của họ lần thứ hai. Với audio/video
    /// thì chỉ hiện khung ở trạng thái đã phát xong: nhánh này chỉ chạy khi đã có ít nhất một lượt
    /// hoàn thành, mà lượt chỉ mở được sau khi media chạy hết -- tức thí sinh CHẮC CHẮN đã nghe.</para>
    /// </summary>
    public void ShowWithoutWaiting(QuestionAsset asset) => OnAssetDisplayRequested?.Invoke(asset, false);

    public void CompleteMediaPlayback()
    {
        _pendingMediaCompletionTcs?.TrySetResult(true);
    }

    public void Clear()
    {
        _pendingMediaCompletionTcs?.TrySetResult(false);
        _pendingMediaCompletionTcs = null;
        OnAssetDisplayRequested?.Invoke(null, false);
    }
}
