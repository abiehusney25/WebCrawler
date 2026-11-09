// =============================================================================
// PageEdge
// One parent -> child link between two pages of the same job (the crawl graph).
// Pages.ParentId keeps only the FIRST parent (that is the tree); Edges keeps
// every parent that links to a page. Unique (JobId, ParentPageId, ChildPageId)
// = a re-delivered message or a taken-over page never adds an edge twice.
// =============================================================================
namespace WebCrawler.Core.Models;

public class PageEdge
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public int ParentPageId { get; set; }
    public int ChildPageId { get; set; }

    public string GetLine()
    {
        return $"job {JobId} page {ParentPageId} -> page {ChildPageId}";
    }
}
