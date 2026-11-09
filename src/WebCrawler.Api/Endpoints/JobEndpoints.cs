// =============================================================================
// JobEndpoints
// The Crawl API: save the job as Pending -> put the id on the queue -> 202.
// Slow work never happens in a request. Errors are ProblemDetails.
// Tree and links read what PageWork saved (available while the job runs too).
// =============================================================================
using WebCrawler.Core;
using WebCrawler.Core.Data;
using WebCrawler.Core.Models;
using WebCrawler.Core.Queue;
using WebCrawler.Core.Work.Pages;
using Microsoft.EntityFrameworkCore;

namespace WebCrawler.Api.Endpoints;

public static class JobEndpoints
{
    public const string CreateJobPolicy = "create-job";

    public static void Map(WebApplication app)
    {
        RouteGroupBuilder jobs = app.MapGroup("/api/jobs").WithTags("Jobs");
        jobs.MapPost("/", CreateJob).RequireRateLimiting(CreateJobPolicy);
        jobs.MapGet("/", GetJobs);
        jobs.MapGet("/{id:guid}", GetJob);
        jobs.MapPost("/{id:guid}/cancel", CancelJob);
        jobs.MapGet("/{id:guid}/tree", GetTree);
        jobs.MapGet("/{id:guid}/pages/{pageId:int}/links", GetLinks);
        jobs.MapGet("/{id:guid}/edges", GetEdges);
    }

    // CreateJob - a url job (JSON body)
    private static async Task<IResult> CreateJob(CreateJobRequest request, AppDbContext db, IJobQueue queue,
        Settings settings, ILoggerFactory loggerFactory)
    {
        Dictionary<string, string[]> errors = JobChecks.CheckUrlRequest(request, settings, out string inputUrl, out int maxDepth);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        Job job = new Job { InputType = JobInputType.Url, Input = inputUrl, InputName = inputUrl, MaxDepth = maxDepth };
        return await QueueJob(job, db, queue, loggerFactory.CreateLogger("JobEndpoints"));
    }

    // QueueJob - save as Pending, enqueue, 202 + Location (503 if the queue is down)
    private static async Task<IResult> QueueJob(Job job, AppDbContext db, IJobQueue queue, ILogger logger)
    {
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        try
        {
            await queue.EnqueueAsync(job.Id);
        }
        catch (QueueUnavailableException ex)
        {
            // the job is kept as Failed with the reason, so nothing silently disappears
            logger.LogWarning("FAIL job {JobId} not queued: {Reason}", job.Id, ex.Message);
            job.Status = JobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.FailureReason = "Queue unavailable: " + ex.Message;
            await db.SaveChangesAsync();
            return Results.Problem(statusCode: 503, title: "Queue unavailable", detail: "The job could not be queued. Try again in a moment.");
        }

        logger.LogInformation("Job queued {JobId} {InputType} {Input}", job.Id, job.InputType, job.InputName);
        return Results.Accepted("/api/jobs/" + job.Id, new { jobId = job.Id, status = job.Status });
    }

    // GetJobs - history, newest first, paged
    private static async Task<IResult> GetJobs(int? page, int? pageSize, AppDbContext db, Settings settings)
    {
        int pageNumber = Math.Max(1, page ?? 1);
        int size = Math.Clamp(pageSize ?? settings.GetInt("HistoryPageSize", 20), 1, 100);

        int totalCount = await db.Jobs.CountAsync();
        List<Job> jobs = await db.Jobs.AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync();

        return Results.Ok(new JobHistoryDto(jobs.Select(job => JobDto.From(job)).ToList(), pageNumber, size, totalCount));
    }

    // GetJob - status, timestamps, summary, page counts by status, progress
    private static async Task<IResult> GetJob(Guid id, AppDbContext db)
    {
        JobDto? job = await GetJobDto(id, db, CancellationToken.None);
        return job == null ? NotFoundJob(id) : Results.Ok(job);
    }

    // GetJobDto - one job + its page counts (shared with the live events stream); null when unknown
    public static async Task<JobDto?> GetJobDto(Guid id, AppDbContext db, CancellationToken cancel)
    {
        Job? job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancel);
        if (job == null)
            return null;

        Dictionary<string, int> countsByStatus = await db.Pages.Where(p => p.JobId == id)
            .GroupBy(p => p.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancel);
        return JobDto.From(job, PageCounts.From(countsByStatus));
    }

    // CancelJob - Pending/Running -> Canceled (202); already finished -> 409
    private static async Task<IResult> CancelJob(Guid id, AppDbContext db)
    {
        Job? job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
            return NotFoundJob(id);

        DateTime now = DateTime.UtcNow;
        int updated = await db.Jobs
            .Where(j => j.Id == id && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Canceled)
                .SetProperty(j => j.CompletedAt, now)
                .SetProperty(j => j.FailureReason, "Canceled by user"));

        if (updated == 0)
            return Results.Problem(statusCode: 409, title: "Job already finished", detail: $"Job is {job.Status}, it can't be canceled.");

        await CrawlFrontier.SkipQueued(db, id, "Job canceled", CancellationToken.None);   // counts are final right away; crawled pages are kept
        return Results.Accepted("/api/jobs/" + id, new { jobId = id, status = JobStatus.Canceled });
    }

    // GetTree - the job's pages as a tree (parent -> children); works while the job runs too
    private static async Task<IResult> GetTree(Guid id, AppDbContext db)
    {
        if (!await db.Jobs.AnyAsync(j => j.Id == id))
            return NotFoundJob(id);

        List<PageResult> pages = await db.Pages.AsNoTracking().Where(p => p.JobId == id).ToListAsync();
        return Results.Ok(PageNode.SetUpTree(pages));
    }

    // GetLinks - PageWork results: one page's unique links, loaded when the UI expands a page
    private static async Task<IResult> GetLinks(Guid id, int pageId, AppDbContext db)
    {
        if (!await db.Pages.AnyAsync(p => p.Id == pageId && p.JobId == id))
            return Results.Problem(statusCode: 404, title: "Page not found", detail: $"No page {pageId} in job {id}.");

        List<LinkDto> links = await db.Links.AsNoTracking()
            .Where(l => l.PageResultId == pageId)
            .OrderBy(l => l.IsInternal ? 0 : 1).ThenBy(l => l.Url)
            .Select(l => new LinkDto(l.Url, l.IsInternal, l.TimesFound))
            .ToListAsync();
        return Results.Ok(links);
    }

    // GetEdges - every parent -> child pair of the job (the tree shows only each page's first parent)
    private static async Task<IResult> GetEdges(Guid id, AppDbContext db)
    {
        if (!await db.Jobs.AnyAsync(j => j.Id == id))
            return NotFoundJob(id);

        List<EdgeDto> edges = await db.Edges.AsNoTracking()
            .Where(e => e.JobId == id)
            .OrderBy(e => e.ParentPageId).ThenBy(e => e.ChildPageId)
            .Select(e => new EdgeDto(e.ParentPageId, e.ChildPageId))
            .ToListAsync();
        return Results.Ok(edges);
    }

    public static IResult NotFoundJob(Guid id)
    {
        return Results.Problem(statusCode: 404, title: "Job not found", detail: $"No job with id {id}.");
    }
}
