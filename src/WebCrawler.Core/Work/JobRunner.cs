// =============================================================================
// JobRunner
// RunJob is the table of contents for ONE job, and it is the same for every kind
// of work: claim -> (demo wait) -> cancel check -> pick the work -> run it -> finish.
// The work itself lives in an IJobWork (Work/Pages, Work/Files, ...).
// Idempotent: a job is only claimed while Pending, so a duplicate message is a no-op.
// takeOver (a RabbitMQ REdelivery: the worker holding it died) may also claim a
// Running job; that is safe because pages are claimed one by one (CrawlFrontier).
// =============================================================================
using WebCrawler.Core.Data;
using WebCrawler.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WebCrawler.Core.Work;

public class JobRunner
{
    private readonly AppDbContext db;
    private readonly IEnumerable<IJobWork> works;
    private readonly Settings settings;
    private readonly ILogger<JobRunner> logger;

    public JobRunner(AppDbContext db, IEnumerable<IJobWork> works, Settings settings, ILogger<JobRunner> logger)
    {
        this.db = db;
        this.works = works;
        this.settings = settings;
        this.logger = logger;
    }

    // RunJob - claim the job, run its work, finish
    public async Task RunJob(Guid jobId, CancellationToken cancel, bool takeOver = false)
    {
        using IDisposable? logScope = logger.BeginScope("JobId:{JobId}", jobId);   // every log line of this job carries JobId

        Job? job = await MarkRunning(jobId, takeOver, cancel);
        if (job == null)
            return;

        try
        {
            await WaitForDemo(cancel);
            if (await CheckCanceled(jobId, cancel))
                return;

            IJobWork? work = GetWork(job);
            WorkResult result = work == null
                ? WorkResult.Fail("No work step registered for InputType " + job.InputType)
                : await work.RunWork(job, cancel);
            await MarkFinished(job, result);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            logger.LogWarning("Stopped by shutdown; job {JobId} is re-queued on next start", jobId);
        }
        catch (Exception ex)
        {
            // last line of defence: an unexpected error fails this job, never the worker
            logger.LogError(ex, "FAIL job {JobId} unexpected error", jobId);
            await MarkFailed(jobId, "Unexpected error: " + ex.Message);
        }
    }

    // MarkRunning - atomic Pending -> Running; null when another worker has it or it is done/canceled
    private async Task<Job?> MarkRunning(Guid jobId, bool takeOver, CancellationToken cancel)
    {
        DateTime now = DateTime.UtcNow;
        int claimed = await db.Jobs
            .Where(j => j.Id == jobId && (j.Status == JobStatus.Pending || (takeOver && j.Status == JobStatus.Running)))
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.StartedAt, j => j.StartedAt ?? now), cancel);

        if (claimed == 0)
        {
            logger.LogInformation("Skip job {JobId}: not Pending (done, canceled or taken)", jobId);
            return null;
        }
        return await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId, cancel);
    }

    // WaitForDemo - optional delay (WorkDelayMs) so Running and Cancel can be seen in the UI
    private async Task WaitForDemo(CancellationToken cancel)
    {
        int delayMs = settings.GetInt("WorkDelayMs", 0);
        if (delayMs > 0)
            await Task.Delay(delayMs, cancel);
    }

    // CheckCanceled - the user may cancel while we wait
    private async Task<bool> CheckCanceled(Guid jobId, CancellationToken cancel)
    {
        bool isCanceled = await IsCanceled(db, jobId, cancel);
        if (isCanceled)
            logger.LogInformation("Job {JobId} was canceled, stopping", jobId);
        return isCanceled;
    }

    // IsCanceled - one query, shared with work steps that check between items (PageWork: before every page)
    public static async Task<bool> IsCanceled(AppDbContext db, Guid jobId, CancellationToken cancel)
    {
        return await db.Jobs.AnyAsync(j => j.Id == jobId && j.Status == JobStatus.Canceled, cancel);
    }

    // GetWork - the IJobWork registered for this job's InputType
    private IJobWork? GetWork(Job job)
    {
        logger.LogInformation(":TRYING: {InputType} {Input} job {JobId}", job.InputType, job.InputName, job.Id);
        return works.FirstOrDefault(work => work.InputType == job.InputType);
    }

    // MarkFinished - Completed, or Failed with the work's reason; never overwrites Canceled
    private async Task MarkFinished(Job job, WorkResult result)
    {
        JobStatus finalStatus = result.Ok ? JobStatus.Completed : JobStatus.Failed;
        string reason = Shorten(result.Error, 1000);
        string summary = Shorten(result.Summary, Job.SummaryMaxLength);
        DateTime now = DateTime.UtcNow;

        int updated = await db.Jobs
            .Where(j => j.Id == job.Id && j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, finalStatus)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.FailureReason, reason)
                .SetProperty(j => j.Summary, summary)
                .SetProperty(j => j.ItemsTotal, result.ItemsOk + result.ItemsFailed)
                .SetProperty(j => j.ItemsOk, result.ItemsOk)
                .SetProperty(j => j.ItemsFailed, result.ItemsFailed));

        if (updated == 0)
        {
            // canceled by the user, or another worker on the same job finished it first
            logger.LogInformation("THE end job {JobId}: already Canceled or finished by another worker; results kept", job.Id);
            return;
        }
        if (!result.Ok)
            logger.LogWarning("FAIL job {JobId} {Reason}", job.Id, reason);
        logger.LogInformation("THE end ok:{Ok} fail:{Fail} job {JobId} {Status}", result.ItemsOk, result.ItemsFailed, job.Id, finalStatus);
    }

    // MarkFailed - used when something unexpected happens (not for a normal failure)
    private async Task MarkFailed(Guid jobId, string reason)
    {
        DateTime now = DateTime.UtcNow;
        string shortReason = Shorten(reason, 1000);
        await db.Jobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.FailureReason, shortReason));
    }

    private static string Shorten(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text.Substring(0, maxLength);
    }
}
