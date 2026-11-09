// =============================================================================
// IJobWork + WorkResult
// THE plug-in point. JobRunner owns the lifecycle (claim, cancel, finish, fail);
// an IJobWork owns the actual work for one InputType. To build something new
// (EDI file, API import, report...): add a class here, register it in
// PipelineSetup, done. Queue, worker, API, statuses and UI stay as they are.
// =============================================================================
using WebCrawler.Core.Models;

namespace WebCrawler.Core.Work;

public interface IJobWork
{
    // InputType - which jobs this work handles
    JobInputType InputType { get; }

    // RunWork - do the work, save your own results, report back; return a FAIL
    // WorkResult for expected failures, throw only for truly unexpected ones
    Task<WorkResult> RunWork(Job job, CancellationToken cancel);
}

public class WorkResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";           // why the job failed (Ok = false)
    public string Summary { get; set; } = "";         // one line for the UI
    public int ItemsOk { get; set; }
    public int ItemsFailed { get; set; }

    public static WorkResult Fail(string error, int itemsFailed = 1)
    {
        return new WorkResult { Ok = false, Error = error, ItemsFailed = itemsFailed };
    }

    public string GetLine()
    {
        return $"{(Ok ? "OK" : "FAIL")} ok:{ItemsOk} fail:{ItemsFailed} {Summary}{Error}";
    }
}
