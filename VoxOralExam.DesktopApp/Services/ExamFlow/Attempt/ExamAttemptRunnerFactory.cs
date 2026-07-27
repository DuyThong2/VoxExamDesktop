using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.Services.ExamFlow.Question;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Attempt;

public sealed class ExamAttemptRunnerFactory
{
    private readonly TurnAudioUploader _audioUploader;
    private readonly TurnArchiveClient _archiveClient;
    private readonly ExamSessionState _sessionState;
    private readonly AppSettings _settings;
    private readonly RealtimeSessionClient _sessionClient;
    private readonly AvatarWebRtcClient _avatarClient;
    private readonly LocalAvatarSpeaker _avatarSpeaker;
    private readonly IExamApiService _examApi;
    private readonly QuestionAssetPresentationCoordinator _assets;
    private readonly IProctoringService _proctoring;

    public ExamAttemptRunnerFactory(
        TurnAudioUploader audioUploader,
        TurnArchiveClient archiveClient,
        ExamSessionState sessionState,
        AppSettings settings,
        RealtimeSessionClient sessionClient,
        AvatarWebRtcClient avatarClient,
        LocalAvatarSpeaker avatarSpeaker,
        IExamApiService examApi,
        QuestionAssetPresentationCoordinator assets,
        IProctoringService proctoring)
    {
        _audioUploader = audioUploader;
        _archiveClient = archiveClient;
        _sessionState = sessionState;
        _settings = settings;
        _sessionClient = sessionClient;
        _avatarClient = avatarClient;
        _avatarSpeaker = avatarSpeaker;
        _examApi = examApi;
        _assets = assets;
        _proctoring = proctoring;
    }

    internal ExamAttemptRunner Create(bool isMicMuted) =>
        new(
            _audioUploader,
            _archiveClient,
            _sessionState,
            _settings,
            _sessionClient,
            _avatarClient,
            _avatarSpeaker,
            _examApi,
            _assets,
            _proctoring,
            isMicMuted);
}
