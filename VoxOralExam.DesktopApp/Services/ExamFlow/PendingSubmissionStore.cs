using System.IO;
using System.Text.Json;
using VoxOralExam.DesktopApp.Services.DomainService;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

/// <summary>One attempt whose final status never reached Java.</summary>
public sealed record PendingSubmission(Guid AttemptId, string Status, DateTimeOffset MarkedAt);

/// <summary>
/// Remembers, on disk, that an attempt still owes Java its final status — and replays it later.
///
/// <para>Why it has to be on disk: nothing but the desktop client can move an exam session to
/// SUBMITTED. UpdateExamSessionStatusUseCase is the only writer, and it is driven by a PATCH from
/// here, so a client that dies between "the exam is over" and that one HTTP call leaves a finished
/// exam reading "Đang làm" forever. That window is not small either — CompleteAttemptAsync drains
/// archives and then deliberately waits out a settle delay first.</para>
///
/// <para>It stopped being hypothetical on 2026-09-02: an unplugged headset killed the process 1.2
/// seconds into an 8-second settle, with every answer archived and nothing pending. The exam was
/// complete in every sense except the status field.</para>
///
/// <para>Same shape as OrphanedUploadRecoveryService, deliberately: a marker written before the
/// risky work, cleared after it succeeds, and swept on a later run. Strictly additive — it only ever
/// re-sends a status the client had already decided on, and any failure leaves the marker for the
/// run after that.</para>
/// </summary>
public sealed class PendingSubmissionStore
{
    /// <summary>How long a marker is worth replaying; see the expiry branch in ReplayAsync.</summary>
    private static readonly TimeSpan MarkerLifetime = TimeSpan.FromDays(7);

    private readonly IExamApiService _examApi;

    /// <summary>
    /// Sibling of LocalSegmentStore's Recordings directory, under the same Vox root: this outlives a
    /// crash for the same reason the segments do, and is cheap to find when debugging one.
    /// </summary>
    public string BaseDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vox",
        "PendingSubmissions");

    public PendingSubmissionStore(IExamApiService examApi)
    {
        _examApi = examApi;
    }

    /// <summary>The only status worth replaying; see the guard in <see cref="Mark"/>.</summary>
    private const string SubmittedStatus = "SUBMITTED";

    /// <summary>
    /// Records that this attempt owes Java a status. Call BEFORE the work that might not survive.
    /// Best-effort: failing to write a marker must never stop an exam from finishing.
    ///
    /// <para>SUBMITTED only, and the guard lives here rather than at the call site so no future
    /// caller can reintroduce the hazard. Replaying INTERRUPTED — which CompleteAttemptAsync also
    /// produces, on the stop and run-failed paths — can actively corrupt a session. The student who
    /// quits mid-exam because their network died is exactly the one whose PATCH fails and whose
    /// marker survives; on their next login the replay races the resume they are about to perform.
    /// Java allows IN_PROGRESS → INTERRUPTED (UpdateExamSessionStatusUseCase.isAllowedTransition),
    /// so a late replay lands on a student who is mid-answer, and INTERRUPTED → SUBMITTED is NOT
    /// allowed, so their real submission is then refused for the rest of the exam.</para>
    ///
    /// <para>Nothing is lost by dropping it: both statuses are RESUMABLE server-side, so the
    /// distinction gates nothing, and an attempt genuinely abandoned is moved to EXPIRED by
    /// ExamScheduleTimeoutGradingJob anyway.</para>
    /// </summary>
    public void Mark(Guid attemptId, string status)
    {
        if (attemptId == Guid.Empty || !string.Equals(status, SubmittedStatus, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(BaseDirectory);
            var pending = new PendingSubmission(attemptId, status, DateTimeOffset.UtcNow);
            File.WriteAllText(PathFor(attemptId), JsonSerializer.Serialize(pending));
            LocalFileLogger.Info("exam_flow", "submission_marked_pending", new { attemptId, status });
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "submission_mark_failed", ex, new { attemptId, status });
        }
    }

    /// <summary>Drops the marker once Java has the status. Missing file is the normal case.</summary>
    public void Clear(Guid attemptId)
    {
        if (attemptId == Guid.Empty)
        {
            return;
        }

        try
        {
            var path = PathFor(attemptId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // A marker that outlives its submission costs one redundant PATCH on the next run, and
            // that PATCH is harmless: the session is already in the status being asked for.
            LocalFileLogger.Error("exam_flow", "submission_clear_failed", ex, new { attemptId });
        }
    }

    /// <summary>
    /// Re-sends every status still owed. Requires a signed-in user, so this belongs AFTER login and
    /// not in the startup sweep that OrphanedUploadRecoveryService runs — there is no access token
    /// before a student signs in, and a PATCH without one only burns the marker's credibility.
    /// </summary>
    public async Task ReplayAsync(CancellationToken ct)
    {
        List<string> files;
        try
        {
            if (!Directory.Exists(BaseDirectory))
            {
                return;
            }
            files = [.. Directory.EnumerateFiles(BaseDirectory, "*.json")];
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "submission_replay_scan_failed", ex);
            return;
        }

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            PendingSubmission? pending;
            try
            {
                pending = JsonSerializer.Deserialize<PendingSubmission>(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                // Unreadable marker: delete it rather than retrying a file that will never parse.
                LocalFileLogger.Error("exam_flow", "submission_marker_unreadable", ex, new { file });
                try { File.Delete(file); } catch { }
                continue;
            }

            if (pending is null || pending.AttemptId == Guid.Empty)
            {
                try { File.Delete(file); } catch { }
                continue;
            }

            // Belt and braces against the Mark guard: a marker written by an older build, or by hand
            // during debugging, must never be sent. See Mark for what replaying INTERRUPTED does to
            // a session the student has since resumed.
            if (!string.Equals(pending.Status, SubmittedStatus, StringComparison.Ordinal))
            {
                LocalFileLogger.Info("exam_flow", "submission_marker_discarded", new
                {
                    pending.AttemptId,
                    pending.Status
                });
                try { File.Delete(file); } catch { }
                continue;
            }

            // Past this age the marker cannot be honoured and would otherwise retry on every login
            // for the life of the machine. ExamScheduleTimeoutGradingJob moves any unfinished
            // session to EXPIRED a minute after its schedule ends, and Java then treats SUBMITTED as
            // a no-op that keeps EXPIRED -- so a marker this old is asking for something the server
            // will never do. Generous on purpose: the point is to stop an infinite retry, not to
            // impose a deadline on recovery.
            if (DateTimeOffset.UtcNow - pending.MarkedAt > MarkerLifetime)
            {
                LocalFileLogger.Error(
                    "exam_flow",
                    "submission_marker_expired",
                    new InvalidOperationException(
                        $"Status {pending.Status} was never delivered for attempt {pending.AttemptId}."),
                    new { pending.AttemptId, pending.Status, pending.MarkedAt });
                try { File.Delete(file); } catch { }
                continue;
            }

            try
            {
                await _examApi.UpdateSessionStatusAsync(pending.AttemptId, pending.Status, ct);
                Clear(pending.AttemptId);
                LocalFileLogger.Info("exam_flow", "submission_replayed", new
                {
                    pending.AttemptId,
                    pending.Status,
                    pending.MarkedAt
                });
            }
            catch (Exception ex)
            {
                // Kept for the next run. Note the server may legitimately refuse: once
                // ExamScheduleTimeoutGradingJob has moved the session to EXPIRED, Java treats a
                // later SUBMITTED as a no-op that keeps EXPIRED, so a marker can outlive any chance
                // of being honoured. It is still worth one attempt per launch -- the cost is a
                // single request and the alternative is a silently wrong status.
                LocalFileLogger.Error("exam_flow", "submission_replay_failed", ex, new
                {
                    pending.AttemptId,
                    pending.Status
                });
            }
        }
    }

    private string PathFor(Guid attemptId) =>
        Path.Combine(BaseDirectory, $"{attemptId:D}.json");
}
