using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using VoxOralExam.Core.Models;

namespace VoxOralExam.DesktopApp.Infra.Clients.StreamService;

public sealed class SegmentUploadClient
{
    private readonly HttpClient _http;

    public SegmentUploadClient(HttpClient http)
    {
        _http = http;
    }

    public async Task UploadAsync(CompletedSegment segment, string token, CancellationToken ct)
    {
        var streamId = Uri.EscapeDataString(segment.StreamId);
        var seq = segment.Sequence.ToString(CultureInfo.InvariantCulture);

        var startedAt = Uri.EscapeDataString(segment.StartedAt.UtcDateTime.ToString("O"));

        var endedAt = Uri.EscapeDataString(segment.EndedAt.UtcDateTime.ToString("O"));

        var url = $"/stream/sessions/{streamId}/segments/{seq}" + $"?startedAt={startedAt}&endedAt={endedAt}";

        await using var file = new FileStream(
            segment.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true
        );

        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        request.Headers.TryAddWithoutValidation("X-Segment-SHA256", segment.Sha256);

        request.Content = new StreamContent(file);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        request.Content.Headers.ContentLength = file.Length;

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);

        response.EnsureSuccessStatusCode();
    }
}
