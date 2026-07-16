using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using NAudio.Wave;
using VoxOralExam.DesktopApp.Infra.Clients.DomainService;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

/// <summary>
/// WAV-encodes a turn's raw PCM buffer and uploads it via a server-issued presigned PUT URL.
/// The client no longer holds AWS credentials or talks to S3 directly (that static-credential model
/// was a security hole -- see docs/wpf-redesign-plan.md Â§D): it asks ITurnUploadUrlProvider (server)
/// for a short-lived upload target scoped to (attemptAnswerId, turnOrder), PUTs the WAV there, and
/// returns the server-provided audioRef for the downstream /turns/archive call.
/// </summary>
public class TurnAudioUploader
{
    private readonly ITurnUploadUrlProvider _uploadUrlProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public TurnAudioUploader(ITurnUploadUrlProvider uploadUrlProvider, IHttpClientFactory httpClientFactory)
    {
        _uploadUrlProvider = uploadUrlProvider;
        _httpClientFactory = httpClientFactory;
    }

    public byte[] EncodeWav(byte[] pcm)
    {
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(16_000, 16, 1)))
        {
            writer.Write(pcm, 0, pcm.Length);
            writer.Flush();
        }

        return stream.ToArray();
    }

    public async Task<string> UploadTurnAudioAsync(byte[] wav, Guid attemptAnswerId, int turnOrder, CancellationToken ct)
    {
        LocalFileLogger.Info("s3", "upload_turn_begin", new
        {
            attemptAnswerId,
            turnOrder,
            wavBytes = wav.Length
        });

        var target = await _uploadUrlProvider.GetUploadTargetAsync(attemptAnswerId, turnOrder, ct);

        using var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, target.UploadUrl) { Content = content };
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        LocalFileLogger.Info("s3", "upload_turn_complete", new
        {
            attemptAnswerId,
            turnOrder,
            audioUrl = target.AudioRef
        });
        return target.AudioRef;
    }
}

