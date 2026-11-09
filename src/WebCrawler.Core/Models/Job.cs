// =============================================================================
// Job
// One submitted unit of work: the input, its status, timestamps (UTC), counts
// and a one-line summary. Nothing here knows WHAT the work is; InputType picks
// the IJobWork that runs it (Url -> PageWork; a new kind of work = a new value).
// =============================================================================
namespace WebCrawler.Core.Models;

public enum JobInputType
{
    Url
}

public class Job
{
    public const int InputMaxLength = 2048;
    public const int SummaryMaxLength = 1000;

    public Guid Id { get; set; } = Guid.NewGuid();
    public JobInputType InputType { get; set; } = JobInputType.Url;
    public string Input { get; set; } = "";            // the normalized start url
    public string InputName { get; set; } = "";        // what the user sees: the url, or the original file name
    public int MaxDepth { get; set; }                   // work option; only PageWork uses it today
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string FailureReason { get; set; } = "";
    public string Summary { get; set; } = "";           // one line from the work step, shown in the UI
    public int ItemsTotal { get; set; }                 // items = pages, claims, rows... whatever the work counts
    public int ItemsOk { get; set; }
    public int ItemsFailed { get; set; }

    // IsFinished - nothing more will happen to this job (not mapped: no setter)
    public bool IsFinished => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;

    // GetLine - one readable line, for logs
    public string GetLine()
    {
        return $"{Status} {Id} {InputType}:{InputName} ok:{ItemsOk} fail:{ItemsFailed} {Summary}{FailureReason}";
    }
}
