using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using System.IO;
using NAudio.Wave;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

/// <summary>
/// Extracted from the former TavusFullPipelineExamFlowService: WAV-encodes a turn's raw PCM
/// buffer and uploads it to S3 using the same static-credential IAmazonS3 client and the same
/// object key shape as before ({attemptAnswerId:D}/turn-{turnOrder}.wav). Kept byte-identical
/// on the wire so later phases (and the existing /turns/archive contract) don't notice the
/// refactor.
/// </summary>
public class TurnAudioUploader
{
    private readonly AppSettings _settings;
    private readonly IAmazonS3 _s3Client;

    public TurnAudioUploader(AppSettings settings)
    {
        _settings = settings;
        _s3Client = CreateS3Client(settings);
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
        var objectKey = $"{attemptAnswerId:D}/turn-{turnOrder}.wav";
        LocalFileLogger.Info("s3", "upload_turn_begin", new
        {
            attemptAnswerId,
            turnOrder,
            objectKey,
            wavBytes = wav.Length,
            bucket = _settings.S3BucketName,
            region = _settings.S3Region
        });

        using var stream = new MemoryStream(wav, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _settings.S3BucketName,
            Key = objectKey,
            InputStream = stream,
            ContentType = "audio/wav",
            AutoCloseStream = false
        };

        await _s3Client.PutObjectAsync(request, ct);
        var url = BuildS3ObjectUrl(_settings.S3BucketName, _settings.S3Region, objectKey);
        LocalFileLogger.Info("s3", "upload_turn_complete", new
        {
            attemptAnswerId,
            turnOrder,
            objectKey,
            audioUrl = url
        });
        return url;
    }

    private static IAmazonS3 CreateS3Client(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AwsAccessKeyId) || string.IsNullOrWhiteSpace(settings.AwsSecretAccessKey))
        {
            throw new InvalidOperationException("AWS credentials are missing. Set AwsAccessKeyId and AwsSecretAccessKey in appsettings.json.");
        }

        AWSCredentials credentials = string.IsNullOrWhiteSpace(settings.AwsSessionToken)
            ? new BasicAWSCredentials(settings.AwsAccessKeyId, settings.AwsSecretAccessKey)
            : new SessionAWSCredentials(settings.AwsAccessKeyId, settings.AwsSecretAccessKey, settings.AwsSessionToken);

        var region = RegionEndpoint.GetBySystemName(settings.S3Region);
        return new AmazonS3Client(credentials, region);
    }

    private static string BuildS3ObjectUrl(string bucketName, string region, string objectKey)
    {
        var escapedKey = string.Join("/", objectKey.Split('/').Select(Uri.EscapeDataString));
        return $"https://{bucketName}.s3.{region}.amazonaws.com/{escapedKey}";
    }
}
