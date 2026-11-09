// =============================================================================
// JobDtos
// What the API sends and receives. Never the EF entities themselves.
// Dates are UTC (SQLite hands them back without a Kind, so AsUtc fixes that).
// =============================================================================
using System.Text.Json.Serialization;
using WebCrawler.Core.Models;

namespace WebCrawler.Api.Endpoints;

public record CreateJobRequest(string? Url, int? MaxDepth);

// PageCounts - pages of a job by status; progress comes from these (no counter columns)
public record PageCounts(int Total, int Queued, int Running, int Done, int Skipped, int Failed)
{
    [JsonIgnore]
    public int Finished => Done + Skipped + Failed;                    // used for progress, not sent

    public static PageCounts From(Dictionary<string, int> countsByStatus)
    {
        int Get(string status) => countsByStatus.GetValueOrDefault(status);
        return new PageCounts(countsByStatus.Values.Sum(), Get(PageResult.StatusQueued), Get(PageResult.StatusRunning),
            Get(PageResult.StatusDone), Get(PageResult.StatusSkipped), Get(PageResult.StatusFailed));
    }
}

public record JobDto(
    Guid JobId, JobInputType InputType, string Url, int MaxDepth, JobStatus Status,
    DateTime CreatedAt, DateTime? StartedAt, DateTime? CompletedAt, string FailureReason, string Summary,
    PageCounts? PageCounts, double Progress)
{
    // From - pageCounts only on the job details call (history stays one cheap query)
    public static JobDto From(Job job, PageCounts? pageCounts = null)
    {
        double progress = job.IsFinished ? 1
            : pageCounts == null || pageCounts.Total == 0 ? 0
            : (double)pageCounts.Finished / pageCounts.Total;          // grows as pages finish; the frontier also grows
        return new JobDto(job.Id, job.InputType, job.InputName, job.MaxDepth, job.Status,
            AsUtc(job.CreatedAt), AsUtc(job.StartedAt), AsUtc(job.CompletedAt), job.FailureReason, job.Summary,
            pageCounts, Math.Round(progress, 3));
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static DateTime? AsUtc(DateTime? value) => value == null ? null : AsUtc(value.Value);
}

public record JobHistoryDto(List<JobDto> Items, int Page, int PageSize, int TotalCount);

public record LinkDto(string Url, bool IsInternal, int TimesFound);

public record EdgeDto(int ParentPageId, int ChildPageId);
