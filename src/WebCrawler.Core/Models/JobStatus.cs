// =============================================================================
// JobStatus
// Pending -> Running -> Completed | Failed | Canceled. Stored as text in the db.
// =============================================================================
namespace WebCrawler.Core.Models;

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled
}
