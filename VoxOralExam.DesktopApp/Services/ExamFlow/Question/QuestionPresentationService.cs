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
            await _assets.PresentAsync(asset, cancellationToken);
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

    private async Task<bool> WaitForAvatarAfterCoreAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var completion = WaitForAvatarCompletionAsync(cancellationToken);
        await action(cancellationToken);
        return await completion;
    }

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

    private void HandleSpeakRequested(int sequence, string text, string? rate)
    {
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
        _avatarCompletion?.TrySetResult(false);
        _avatarCompletion = null;
        _assets.Clear();
        _avatarSpeaker.Stop();
        AvatarSpeakingChanged?.Invoke(false);
    }
}
