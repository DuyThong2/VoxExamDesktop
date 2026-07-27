using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using VoxOralExam.Core.Models;

namespace VoxOralExam.DesktopApp.Infra.Clients.StreamService;

public sealed record StreamUploadSession(
    string StreamId,
    string StreamType,
    DateTimeOffset ExpiresAt,
    string UploadToken
);

public sealed record SegmentAuditGap(long FromSeq, long ToSeq, long MissingSecs);

public sealed record SegmentAudit(
    string StreamId,
    int TotalSegments,
    long RecordedDurationSecs,
    bool HasGaps,
    IReadOnlyList<SegmentAuditGap> Gaps
);

public sealed class StreamSessionClient
{
    private readonly HttpClient _http;

    public StreamSessionClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<StreamUploadSession> CreateAsync(string streamType, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/stream/sessions"
        );

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            streamType
        });

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<StreamUploadSession>(cancellationToken: ct) ?? throw new InvalidOperationException("Streaming service return an empty session.");
    }

    /// <summary>
    /// Tells the server what this device has captured for the stream, uploaded or not.
    ///
    /// Sent repeatedly while recording, not just at the end: an inventory that only arrives with
    /// /complete says nothing in the one case worth protecting against, which is the client never
    /// reaching /complete at all. <paramref name="complete"/> marks the final declaration, after
    /// which a missing tail really is a gap rather than simply the next segment not existing yet.
    /// </summary>
    public async Task DeclareInventoryAsync(
        string streamId,
        string uploadToken,
        bool complete,
        IReadOnlyList<DeclaredSegment> segments,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/stream/sessions/{Uri.EscapeDataString(streamId)}/inventory"
        );

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);
        request.Content = JsonContent.Create(new
        {
            complete,
            declaredAt = DateTimeOffset.UtcNow,
            segments = segments.Select(segment => new
            {
                seq = segment.Seq,
                startedAt = segment.StartedAt,
                endedAt = segment.EndedAt,
                sha256 = segment.Sha256,
                sizeBytes = segment.SizeBytes,
                framesWritten = segment.FramesWritten
            })
        });

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Tells vox-streaming this stream has no more segments coming, so it can assemble the
    /// recording instead of waiting out the grace period.
    /// </summary>
    /// <param name="stopReason">
    /// Why recording ended, so the server can tell an exam that finished normally from one whose
    /// app was killed or whose capture died -- a short recording means very different things in
    /// each case. Purely diagnostic: the server ignores values it does not recognise and accepts
    /// the call with no body at all, so this can never be the reason a recording fails to assemble.
    /// </param>
    public async Task CompleteAsync(
        string streamId,
        string uploadToken,
        RecordingStopReason stopReason,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/stream/sessions/{Uri.EscapeDataString(streamId)}/complete"
        );

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);
        request.Content = JsonContent.Create(new { stopReason = stopReason.ToString() });

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<SegmentAudit> AuditAsync(string streamId, string uploadToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/stream/sessions/{Uri.EscapeDataString(streamId)}/audit"
        );

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SegmentAudit>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Streaming service returned an empty audit.");
    }
}
