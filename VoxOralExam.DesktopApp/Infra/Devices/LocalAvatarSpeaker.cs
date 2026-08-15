using System.IO;
using System.Text;
using Microsoft.CognitiveServices.Speech;
using NAudio.Wave;
using VoxOralExam.DesktopApp.Dtos.Requests;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Devices;

/// <summary>
/// Prototype of moving avatar TTS from Python (agents/src/realtime/tts_client.py +
/// avatar_speech.py, synthesized server-side and streamed to WPF over the avatar WebRTC audio
/// track) to WPF calling Azure Speech directly and playing the result through the student's
/// already-chosen output device (ExamSessionState.SelectedAudioOutputDeviceIndex, same device
/// AvatarWebRtcClient uses). See task/performance.txt for why: the avatar WebRTC connection was
/// only ever carrying this synthesized audio (plus idle silence) -- the text to speak is already
/// delivered to WPF over the realtime WS (RealtimeSessionClient's "speak" message), so this
/// avoids a whole synth-on-server -> re-encode-to-PCMU -> WebRTC -> decode-mu-law round trip.
///
/// Secret-distribution note: this reuses the same static AZURE_SPEECH_KEY the Python side already
/// has in agents/.env, copied into this project's own .env for the prototype. That's fine for a
/// local dev build but is a real problem for a shipped desktop client -- a production version
/// would need a short-lived token issued per attempt instead of a static subscription key baked
/// into every install.
/// </summary>
public sealed class LocalAvatarSpeaker
{
    // S1 Neural Text To Speech Characters, region southeastasia (Azure Retail Prices API,
    // public, không cần key -- xác nhận 2026-08-14, xem prices.azure.com/api/retail/prices).
    // Đếm theo text.Length (text gốc TRƯỚC khi bọc SSML) -- Azure tính cả markup <prosody> vào
    // ký tự bị charge (chỉ <speak>/<voice> được miễn), nên khi rate != null, số ký tự tính ở
    // đây hụt nhẹ so với Azure thật (thiếu phần overhead tag <prosody rate="...">...</prosody>,
    // ~30-40 ký tự/lần) -- chấp nhận được, không đáng để đếm chính xác từng tag SSML.
    private const decimal TtsPricePerCharacterUsd = 0.000015m;

    private readonly AppSettings _settings;
    private readonly ExamSessionState _sessionState;
    private readonly IExamApiService _examApiService;
    private readonly object _playbackSync = new();
    private WaveOutEvent? _activeWaveOut;

    public LocalAvatarSpeaker(AppSettings settings, ExamSessionState sessionState, IExamApiService examApiService)
    {
        _settings = settings;
        _sessionState = sessionState;
        _examApiService = examApiService;
    }

    public async Task SpeakAsync(string text, string? rate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var wav = await SynthesizeAsync(text, rate, ct).ConfigureAwait(false);
        // Chi phí đã phát sinh ngay khi Azure trả về audio -- báo cáo không phụ thuộc ct của lượt
        // phát (playback có thể bị huỷ sau đó, nhưng tiền đã tốn rồi), và không được chặn/làm hỏng
        // việc phát âm thanh nếu báo cáo lỗi.
        _ = ReportTtsUsageBestEffortAsync(text, wav);
        await PlayAsync(wav, ct).ConfigureAwait(false);
    }

    private async Task ReportTtsUsageBestEffortAsync(string text, byte[] wav)
    {
        try
        {
            var examSessionId = _sessionState.ExamAttemptId;
            if (examSessionId == Guid.Empty)
            {
                return;
            }

            double durationSeconds;
            using (var stream = new MemoryStream(wav))
            using (var reader = new WaveFileReader(stream))
            {
                durationSeconds = reader.TotalTime.TotalSeconds;
            }

            var characterCount = text.Length;

            var questionId = _sessionState.CurrentQuestion?.Id;
            var turnId = questionId.HasValue
                && _sessionState.AttemptAnswerIdsByQuestionId.TryGetValue(questionId.Value, out var answerId)
                ? answerId
                : examSessionId;

            var request = new ReportAiUsageRequestDto
            {
                TurnId = turnId,
                UsageEvents =
                [
                    new AiUsageEventItemDto
                    {
                        UsageEventId = Guid.NewGuid(),
                        Type = "DURATION",
                        Provider = "azure_tts",
                        DurationMs = (long)(durationSeconds * 1000),
                        UnitPrice = new { amount = TtsPricePerCharacterUsd, unit = "per_character", currency = "USD" },
                        CostUsd = Math.Round(characterCount * TtsPricePerCharacterUsd, 8),
                        OccurredAt = DateTimeOffset.UtcNow,
                    }
                ],
            };

            await _examApiService.ReportAiUsageAsync(examSessionId, request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "ai_usage_report_failed", ex, new { provider = "azure_tts" });
        }
    }

    public void Stop()
    {
        WaveOutEvent? waveOut;
        lock (_playbackSync)
        {
            waveOut = _activeWaveOut;
        }

        try
        {
            waveOut?.Stop();
        }
        catch
        {
            // Best-effort interruption only.
        }
    }

    private async Task<byte[]> SynthesizeAsync(string text, string? rate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.AzureSpeechKey) || string.IsNullOrWhiteSpace(_settings.AzureSpeechRegion))
        {
            throw new InvalidOperationException(
                "AZURE_SPEECH_KEY/AZURE_SPEECH_REGION are not configured -- set them in .env (see .env.example).");
        }

        var speechConfig = SpeechConfig.FromSubscription(_settings.AzureSpeechKey, _settings.AzureSpeechRegion);
        // Matches agents/src/realtime/tts_client.py's _OUTPUT_FORMAT exactly, so playback here
        // behaves the same as the WAV files that side already produces.
        speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm);
        speechConfig.SpeechSynthesisVoiceName = _settings.AzureTtsVoice;

        // audioConfig: null -- same as Python's SpeechSynthesizer(speech_config, audio_config=None)
        // -- keeps the synthesized audio in SpeechSynthesisResult.AudioData instead of the SDK
        // playing it to the system default device itself, so PlayAsync below can route it through
        // whichever output device the student picked in the device-preflight screen.
        using var synthesizer = new SpeechSynthesizer(speechConfig, null);
        ct.ThrowIfCancellationRequested();

        using var result = string.IsNullOrEmpty(rate)
            ? await synthesizer.SpeakTextAsync(text).ConfigureAwait(false)
            : await synthesizer.SpeakSsmlAsync(BuildSsml(text, _settings.AzureTtsVoice, rate)).ConfigureAwait(false);

        if (result.Reason != ResultReason.SynthesizingAudioCompleted)
        {
            var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
            throw new InvalidOperationException(
                $"Azure TTS synthesis failed: reason={result.Reason} details={cancellation.ErrorDetails}");
        }

        return result.AudioData;
    }

    private static string BuildSsml(string text, string voiceName, string rate)
    {
        var escaped = EscapeXml(text);
        return "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\">"
            + $"<voice name=\"{voiceName}\"><prosody rate=\"{rate}\">{escaped}</prosody></voice>"
            + "</speak>";
    }

    private static string EscapeXml(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                _ => c.ToString(),
            });
        }
        return builder.ToString();
    }

    private async Task PlayAsync(byte[] wav, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var stream = new MemoryStream(wav);
        using var reader = new WaveFileReader(stream);
        using var waveOut = new WaveOutEvent { DeviceNumber = _sessionState.SelectedAudioOutputDeviceIndex };

        void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is not null)
            {
                tcs.TrySetException(e.Exception);
            }
            else
            {
                tcs.TrySetResult();
            }
        }

        waveOut.PlaybackStopped += OnPlaybackStopped;
        try
        {
            lock (_playbackSync)
            {
                _activeWaveOut = waveOut;
            }

            waveOut.Init(reader);
            waveOut.Play();

            using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            waveOut.PlaybackStopped -= OnPlaybackStopped;
            lock (_playbackSync)
            {
                if (ReferenceEquals(_activeWaveOut, waveOut))
                {
                    _activeWaveOut = null;
                }
            }
            waveOut.Stop();
        }
    }
}
