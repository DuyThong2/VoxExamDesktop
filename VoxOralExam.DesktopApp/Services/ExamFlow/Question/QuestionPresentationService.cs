using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Dtos;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Services;
using ExamQuestion = VoxOralExam.Core.Models.Question;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Question;

internal sealed class QuestionPresentationService : IDisposable
{
    private static readonly string[] PreparationTemplates =
    [
        "You have {0} seconds to prepare. I will start recording in {0} seconds.",
        "Take {0} seconds to think about your answer. Recording starts in {0} seconds.",
        "You have {0} seconds to get ready. I'll begin recording in {0} seconds."
    ];

    private static readonly string[] BothDurationTemplates =
    [
        "We expect an answer between {0} and {1} seconds.",
        "Try to answer in about {0} to {1} seconds.",
        "Aim for somewhere between {0} and {1} seconds when you respond."
    ];

    private static readonly string[] MinDurationTemplates =
    [
        "We expect an answer of at least {0} seconds.",
        "Please speak for at least {0} seconds."
    ];

    private static readonly string[] MaxDurationTemplates =
    [
        "Please keep your answer under {0} seconds.",
        "Try to answer within {0} seconds."
    ];

    private static readonly string[] RecordingStartedTemplates =
    [
        "I am recording now.",
        "Recording has started -- go ahead.",
        "I'm listening now, please begin."
    ];

    private readonly RealtimeSessionClient _sessionClient;
    private readonly LocalAvatarSpeaker _avatarSpeaker;
    private readonly QuestionAssetPresentationCoordinator _assets;
    private TaskCompletionSource<bool>? _avatarCompletion;
    private Guid? _lastAnnouncedSectionId;
    private bool _started;

    public QuestionPresentationService(
        RealtimeSessionClient sessionClient,
        LocalAvatarSpeaker avatarSpeaker,
        QuestionAssetPresentationCoordinator assets)
    {
        _sessionClient = sessionClient;
        _avatarSpeaker = avatarSpeaker;
        _assets = assets;
    }

    public event Action<string>? StatusChanged;
    public event Action<bool>? AvatarSpeakingChanged;

    /// <summary>
    /// Lời avatar SẮP đọc, bắn ngay trước khi phát tiếng để chữ và tiếng tới cùng lúc.
    ///
    /// <para>Móc ở đây chứ không ở <c>decision.NextPromptText</c> bên QuestionFlowRunner vì mọi
    /// lời avatar nói đều đi qua frame <c>speak</c> của Python -- lời dẫn section, đề bài, thông
    /// báo chuẩn bị, "I am recording now", follow-up, lời chào kết. Đường kia chỉ có follow-up.</para>
    /// </summary>
    public event Action<string>? AvatarUtteranceStarted;

    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _lastAnnouncedSectionId = null;
        _sessionClient.OnSpeakRequested += HandleSpeakRequested;
        _sessionClient.OnAvatarUtteranceComplete += HandleAvatarCompletion;
        _assets.MediaPlaybackStateChanged += HandleAssetMediaPlaybackChanged;
    }

    public async Task<(bool AvatarSpoke, bool Interrupted)> PresentInitialAsync(
        ExamQuestion question,
        Guid answerId,
        Guid paperItemId,
        QuestionContextDto context,
        string promptText,
        Action openSpeechWindow,
        Func<Task> getSpeechStartedTask,
        CancellationToken cancellationToken)
    {
        // KHÔNG Clear() ở đây: QuestionFlowRunner.RunAsync đã dọn asset của câu trước ngay đầu
        // mỗi câu. Gọi lần nữa chỉ thừa, và là một trong những chỗ từng làm ảnh chớp tắt.
        var sectionInstruction = GetSectionInstruction(question);
        await WaitForAvatarAfterAsync(
            token => _sessionClient.SendQuestionStartAsync(
                answerId,
                paperItemId,
                context,
                "en-US",
                null,
                sectionInstruction,
                token),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(sectionInstruction))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        // Thứ tự giữa HƯỚNG DẪN và TÀI NGUYÊN phụ thuộc loại tài nguyên, vì hai nhóm chiếm giác
        // quan khác nhau:
        //
        //   IMAGE, TEXT_PASSAGE -- chiếm MẮT. Hiện ra tức thì rồi nằm nguyên trên màn hình suốt
        //     câu hỏi. Hiện TRƯỚC rồi mới đọc hướng dẫn: học sinh vừa nhìn vừa nghe, đúng như
        //     "Look at the picture, then describe it" mô tả.
        //
        //   AUDIO, VIDEO -- chiếm TAI, và chỉ phát MỘT LẦN. Đọc hướng dẫn TRƯỚC rồi mới phát.
        //     Trước bản này hai việc chạy song song bằng Task.WhenAll, nghĩa là giọng AI đọc đè
        //     lên mấy giây đầu bản ghi -- mà hướng dẫn của chính những câu đó lại là "You will
        //     hear the recording once only", nên phần bị đè không có cách nào nghe lại.
        //
        // Ba nhánh này gộp được thành một chuỗi tuần tự, không cần Task.WhenAll: với ảnh và đoạn
        // văn thì PresentAsync trả về ngay, nên "chờ" nó không tốn gì.
        var hasInstruction = !string.IsNullOrWhiteSpace(question.InstructionText);
        var asset = question.Asset;
        var assetPlaysOverTime =
            asset is not null
            && (asset.Type == QuestionAssetType.Audio || asset.Type == QuestionAssetType.Video);

        if (asset is not null)
        {
            StatusChanged?.Invoke("Đang hiển thị tài nguyên câu hỏi...");
        }

        if (asset is not null && !assetPlaysOverTime)
        {
            await _assets.PresentAsync(asset, cancellationToken);
        }

        if (hasInstruction)
        {
            await WaitForAvatarAfterAsync(
                token => _sessionClient.SendPresentQuestionAsync(
                    question.InstructionText,
                    token),
                cancellationToken);
        }

        if (asset is not null && assetPlaysOverTime)
        {
            // TẠM THỜI, THÊM 2026-08-26 ĐỂ CHẨN ĐOÁN -- xoá khi xong.
            //
            // Nghi vấn số một cho ca "mất nửa bài thi": đo thật ca 01a03d48, hai câu Part 2 (một
            // clip, một bản ghi âm) đều bị tạo response rồi bỏ TRẮNG 0 lượt, trong khi câu Part 1
            // và câu ảnh -- không có media phát theo thời gian -- đều bình thường. Bài vẫn được
            // chấm trên 3 lượt và chuyển GRADED.
            //
            // Log ba mốc: vào, ra, và ngoại lệ. Nếu "ket_thuc" không bao giờ xuất hiện thì lỗi nằm
            // trong PresentAsync; nếu xuất hiện mà câu hỏi thật vẫn không được gửi thì lỗi nằm ở
            // đoạn sau.
            LocalFileLogger.Info("exam_flow", "asset_present_bat_dau", new
            {
                assetType = asset.Type.ToString()
            });
            try
            {
                await _assets.PresentAsync(asset, cancellationToken);
                LocalFileLogger.Info("exam_flow", "asset_present_ket_thuc", new
                {
                    assetType = asset.Type.ToString()
                });
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("exam_flow", "asset_present_loi", ex, new
                {
                    assetType = asset.Type.ToString()
                });
                throw;
            }
        }

        openSpeechWindow();
        var speechStarted = getSpeechStartedTask();
        var promptTask = WaitForAvatarAfterAsync(
            token => _sessionClient.SendPresentQuestionAsync(promptText, token),
            cancellationToken);
        if (await WasInterruptedAsync(
                promptTask,
                speechStarted,
                cancellationToken))
        {
            _avatarSpeaker.Stop();
            await promptTask;
            return (true, true);
        }

        return (await promptTask, false);
    }

    public async Task<bool> PresentResumeAsync(
        ExamQuestion question,
        Guid answerId,
        Guid paperItemId,
        QuestionContextDto context,
        string activePrompt,
        CancellationToken cancellationToken)
    {
        // Hiện LẠI asset thay vì dọn nó đi. Trước bản này nhánh vào lại gọi _assets.Clear() rồi
        // không bao giờ hiện lại, nên thí sinh bị cấm giữa câu tả tranh khi quay lại sẽ nghe AI
        // hỏi tiếp về tấm ảnh mà trên màn hình không còn ảnh nào -- trong khi Python VẪN nhận đủ
        // asset qua question_start. AI biết tấm ảnh, thí sinh thì không.
        if (question.Asset is not null)
        {
            _assets.ShowWithoutWaiting(question.Asset);
        }
        await WaitForAvatarAfterAsync(
            token => _sessionClient.SendQuestionStartAsync(
                answerId,
                paperItemId,
                context,
                "en-US",
                null,
                null,
                token),
            cancellationToken);
        return await WaitForAvatarAfterAsync(
            token => _sessionClient.SendPresentQuestionAsync(activePrompt, token),
            cancellationToken);
    }

    public async Task RunPreparationAsync(
        ExamQuestion question,
        Task speechStarted,
        CancellationToken cancellationToken)
    {
        var preparationSeconds = question.PreparationTimeSeconds;
        if (preparationSeconds <= 0)
        {
            return;
        }

        var preparation = string.Format(
            PreparationTemplates[Random.Shared.Next(PreparationTemplates.Length)],
            preparationSeconds);
        var durationClause = BuildDurationClause(
            question.MinResponseSeconds,
            question.MaxResponseSeconds);
        var announcement = string.IsNullOrEmpty(durationClause)
            ? preparation
            : $"{preparation} {durationClause}";

        var announcementTask = WaitForAvatarAfterAsync(
            token => _sessionClient.SendPresentQuestionAsync(announcement, token),
            cancellationToken);
        if (await WasInterruptedAsync(
                announcementTask,
                speechStarted,
                cancellationToken))
        {
            _avatarSpeaker.Stop();
            return;
        }

        if (await WasInterruptedAsync(
                Task.Delay(TimeSpan.FromSeconds(preparationSeconds), cancellationToken),
                speechStarted,
                cancellationToken))
        {
            return;
        }

        var recordingText = RecordingStartedTemplates[
            Random.Shared.Next(RecordingStartedTemplates.Length)];
        var recordingTask = WaitForAvatarAfterAsync(
            token => _sessionClient.SendPresentQuestionAsync(recordingText, token),
            cancellationToken);
        if (await WasInterruptedAsync(
                recordingTask,
                speechStarted,
                cancellationToken))
        {
            _avatarSpeaker.Stop();
        }
    }

    public Task<(T Result, bool AvatarSpoke)> WaitForAvatarAfterAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) =>
        WaitForAvatarAfterCoreAsync(action, cancellationToken);

    public Task<bool> WaitForAvatarAfterAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        WaitForAvatarAfterCoreAsync(action, cancellationToken);

    public void Clear() => _assets.Clear();

    /// <summary>
    /// Gửi bản tin lúc WebSocket đã chết KHÔNG được phép làm sập ứng dụng.
    ///
    /// <para>Đo thật 2026-08-26, ca 01a03d5f: mạng đứt đúng lúc luồng câu hỏi đang gửi lời dẫn
    /// chuẩn bị. <c>SendJsonAsync</c> ném <c>InvalidOperationException("The WebSocket is not
    /// connected.")</c>, ngoại lệ đó làm hỏng task chạy bài, và tới khi cửa sổ đóng thì
    /// <c>StopAsync</c> await lại task hỏng đó -- ngoại lệ nổi lên dispatcher, không ai bắt,
    /// <c>appdomain_unhandled_exception {IsTerminating: true}</c>, tiến trình chết. Từ phía thí
    /// sinh là "tự thoát phần mềm".</para>
    ///
    /// <para>Vì sao lúc có lúc không: chỉ sập khi đứt mạng rơi ĐÚNG vào khoảnh khắc gửi. Đứt lúc
    /// đang thu tiếng hoặc đang chờ thì không chạm tới đường này. Là một cuộc đua, nên ngẫu nhiên.</para>
    ///
    /// <para>Nuốt lỗi ở đây là đúng chỗ, không phải giấu lỗi: mọi đường gửi khác trong app đều đã
    /// làm vậy (<c>send_audio_frame_failed</c>, <c>speech_budget_progress_failed</c>,
    /// <c>force_end_poll_failed</c>) vì tầng dưới đã có tự nối lại. Riêng đường này bị bỏ sót.
    /// Trả về <c>false</c> = "avatar chưa đọc xong", đúng nghĩa và luồng đã biết xử lý.</para>
    /// </summary>
    private static bool IsTransportDown(Exception ex) =>
        ex is InvalidOperationException
            or System.Net.WebSockets.WebSocketException
            or System.IO.IOException;

    private async Task<bool> WaitForAvatarAfterCoreAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var completion = WaitForAvatarCompletionAsync(cancellationToken);
        try
        {
            await action(cancellationToken);
        }
        catch (Exception ex) when (IsTransportDown(ex))
        {
            LocalFileLogger.Error("exam_flow", "gui_ban_tin_that_bai_mat_ket_noi", ex, null);
            _avatarCompletion?.TrySetResult(false);
            return false;
        }
        return await completion;
    }

    // CỐ Ý KHÔNG bắt lỗi truyền tải ở bản generic này -- khác hẳn bản không generic ngay trên.
    //
    // Bản này trả về một GIÁ TRỊ mà bên gọi dùng ngay (RealtimeDecision). Nuốt lỗi rồi trả
    // `default` nghĩa là trả null, và QuestionFlowRunner dòng 382 đọc `decision.NextPromptText`
    // ngay sau đó -> NullReferenceException, task chạy bài hỏng, app chết. Đã xảy ra thật
    // 2026-08-26 ca 01a03d6d: tôi thêm catch ở đây và đổi một lỗi mạng thành một lỗi null.
    //
    // Bên gọi ĐÃ tự lo: `catch (Exception ex) when (ex is not OperationCanceledException)` bọc
    // đúng lời gọi này và dựng sẵn một RealtimeDecision thay thế với reason
    // "connection_lost_timeout". Chú thích ở đó ghi rõ nó được nới rộng chính vì
    // InvalidOperationException/WebSocketException lúc socket đóng. Để ngoại lệ bay lên là đúng.
    private async Task<(T Result, bool AvatarSpoke)> WaitForAvatarAfterCoreAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var completion = WaitForAvatarCompletionAsync(cancellationToken);
        var result = await action(cancellationToken);
        return (result, await completion);
    }

    private async Task<bool> WaitForAvatarCompletionAsync(
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _avatarCompletion = tcs;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var registration = timeout.Token.Register(
                () => tcs.TrySetResult(false));
            return await tcs.Task;
        }
        finally
        {
            if (ReferenceEquals(_avatarCompletion, tcs))
            {
                _avatarCompletion = null;
            }
        }
    }

    /// <summary>
    /// Tài nguyên (audio/video) của câu hỏi có đang phát không. Cập nhật từ
    /// <see cref="QuestionAssetPresentationCoordinator.MediaPlaybackStateChanged"/>.
    /// </summary>
    private volatile bool _assetMediaPlaying;

    /// <summary>
    /// Thí sinh có đang nói không. Do <see cref="Attempt.ExamAttemptRunner"/> nối từ
    /// <c>SpeechTurnCoordinator.StudentSpeakingChanged</c> -- lớp này không được tiêm coordinator đó.
    /// </summary>
    private volatile bool _studentSpeaking;

    private void HandleAssetMediaPlaybackChanged(bool isPlaying) => _assetMediaPlaying = isPlaying;

    public void SetStudentSpeaking(bool isSpeaking) => _studentSpeaking = isSpeaking;

    private void HandleSpeakRequested(int sequence, string text, string? rate)
    {
        // KHÔNG đọc đè lên audio/video của đề đang phát.
        //
        // Luồng thường không bao giờ rơi vào đây: PresentInitialAsync `await _assets.PresentAsync`
        // cho media chạy hết rồi mới gửi đề đi đọc (đo thật 2026-08-26: câu có clip im 55 giây
        // giữa "You will see the clip once only" và câu hỏi). Nguồn duy nhất bắn `speak` ngoài
        // chuỗi await đó là `_handle_resume` phía Python -- nó tự phát lời nhắc khi client nối lại,
        // và nó KHÔNG biết máy đang phát media.
        //
        // Vì sao không chỉ là khó chịu: lúc media chạy thì mic vẫn bật, mà mic không khử vọng --
        // tiếng loa đi thẳng vào transcript. Câu hỏi do CHÍNH AI đọc bị ghi như lời thí sinh nói,
        // rồi đem đi chấm. Hỏng điểm, không phải hỏng trải nghiệm.
        //
        // BỎ HẲN chứ không để dành đọc sau: luồng đang `await` media sẽ tự đọc đề ngay khi media
        // xong. Giữ lại rồi phát tiếp là thí sinh nghe đề HAI lần -- đổi một lỗi lấy một lỗi khác.
        //
        // Ghi log kèm nguyên văn: nếu sau này có ca thí sinh không nghe thấy đề, đây là chỗ đầu
        // tiên phải soi -- nghĩa là có một đường nào đó mà `speak` này là nguồn đọc DUY NHẤT, điều
        // tôi chưa dò hết được.
        if (_assetMediaPlaying && !string.IsNullOrWhiteSpace(text))
        {
            LocalFileLogger.Info(
                "exam_flow",
                "speak_skipped_media_playing",
                new { sequence, text });
            // PHẢI báo hoàn tất dù không phát.
            //
            // HandleAvatarCompletion nằm trong `finally` của PlayRequestedSpeechAsync, nên bỏ qua
            // hàm đó là không ai bắn `_avatarCompletion`. Mọi chỗ đang await WaitForAvatarAfterAsync
            // sẽ treo tới hết AvatarSpeechMaxWaitSeconds (25s) rồi trả về "avatar chưa đọc xong",
            // và luồng câu hỏi coi đó là lý do đóng cửa sổ trả lời.
            //
            // Đo thật 2026-08-26: bỏ qua seq 5 xong, log KHÔNG có avatar_utterance_complete_signal
            // cho seq 5 trong khi seq 6-10 đều có. Bỏ qua tiếng thì được, bỏ qua tín hiệu thì không.
            HandleAvatarCompletion(sequence, text);
            return;
        }

        // Không chen lời khi thí sinh ĐANG NÓI.
        //
        // Cùng một nguồn với nhánh trên, khác thời điểm: mạng đứt lâu rồi nối lại đúng lúc thí sinh
        // đang trả lời dở. Bản tin `speak` của `_handle_resume` ập vào giữa câu nói, AI đọc chen
        // ngang và thí sinh mất nhịp.
        //
        // Bỏ chứ không hoãn, cùng lý do: đây là lời nhắc PHÁT LẠI, không mang thông tin mới. Thí
        // sinh nói xong thì `turn_end` chạy và follow-up thật sẽ tới theo đường bình thường.
        //
        // Nhất quán với thiết kế sẵn có: WasInterruptedAsync đã coi "thí sinh cất tiếng" là lý do
        // để DỪNG avatar đang đọc. Ở đây chỉ là mặt còn lại của cùng nguyên tắc -- đang nói thì
        // không bắt đầu đọc.
        if (_studentSpeaking && !string.IsNullOrWhiteSpace(text))
        {
            LocalFileLogger.Info(
                "exam_flow",
                "speak_skipped_student_speaking",
                new { sequence, text });
            // Xem chú thích ở nhánh media ngay trên: bỏ tiếng thì được, bỏ tín hiệu hoàn tất thì
            // treo cả luồng câu hỏi.
            HandleAvatarCompletion(sequence, text);
            return;
        }

        _ = PlayRequestedSpeechAsync(sequence, text, rate);
    }

    private async Task PlayRequestedSpeechAsync(
        int sequence,
        string text,
        string? rate)
    {
        var hasSpeech = !string.IsNullOrWhiteSpace(text);
        try
        {
            if (hasSpeech)
            {
                AvatarSpeakingChanged?.Invoke(true);
                AvatarUtteranceStarted?.Invoke(text);
            }
            await _avatarSpeaker.SpeakAsync(text, rate, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error(
                "exam_flow",
                "avatar_speak_failed",
                ex,
                new { sequence, text });
        }
        finally
        {
            if (hasSpeech)
            {
                AvatarSpeakingChanged?.Invoke(false);
            }
            HandleAvatarCompletion(sequence, text);
        }
    }

    private void HandleAvatarCompletion(int sequence, string utteranceText)
    {
        LocalFileLogger.Info(
            "exam_flow",
            "avatar_utterance_complete_signal",
            new { sequence, utteranceText });
        _avatarCompletion?.TrySetResult(true);
    }

    private string? GetSectionInstruction(ExamQuestion question)
    {
        if (question.SectionId is null || question.SectionId == Guid.Empty)
        {
            return null;
        }
        if (_lastAnnouncedSectionId == question.SectionId)
        {
            return null;
        }

        _lastAnnouncedSectionId = question.SectionId;
        if (string.IsNullOrWhiteSpace(question.SectionInstruction)
            && string.IsNullOrWhiteSpace(question.SectionTitle))
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(question.SectionTitle)
            ? question.SectionInstruction
            : $"{question.SectionTitle}. {question.SectionInstruction}".Trim().Trim('.');
    }

    private static string BuildDurationClause(int minimum, int maximum)
    {
        if (minimum > 0 && maximum > 0)
        {
            return string.Format(
                BothDurationTemplates[Random.Shared.Next(BothDurationTemplates.Length)],
                minimum,
                maximum);
        }
        if (minimum > 0)
        {
            return string.Format(
                MinDurationTemplates[Random.Shared.Next(MinDurationTemplates.Length)],
                minimum);
        }
        if (maximum > 0)
        {
            return string.Format(
                MaxDurationTemplates[Random.Shared.Next(MaxDurationTemplates.Length)],
                maximum);
        }
        return string.Empty;
    }

    private static async Task<bool> WasInterruptedAsync(
        Task step,
        Task interruption,
        CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(step, interruption);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed == interruption)
        {
            return true;
        }
        await step;
        return false;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }
        _started = false;
        _sessionClient.OnSpeakRequested -= HandleSpeakRequested;
        _sessionClient.OnAvatarUtteranceComplete -= HandleAvatarCompletion;
        _assets.MediaPlaybackStateChanged -= HandleAssetMediaPlaybackChanged;
        _assetMediaPlaying = false;
        _studentSpeaking = false;
        _avatarCompletion?.TrySetResult(false);
        _avatarCompletion = null;
        _assets.Clear();
        _avatarSpeaker.Stop();
        AvatarSpeakingChanged?.Invoke(false);
    }
}
