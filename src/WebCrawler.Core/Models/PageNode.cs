// =============================================================================
// PageNode
// One page in the job's page tree (parent -> children), shaped for the API.
// Each page hangs under the FIRST parent that found it (ParentId).
// SetUpTree is public static so the tree logic is easy to test.
// =============================================================================
namespace WebCrawler.Core.Models;

public class PageNode
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public string Url { get; set; } = "";
    public string FinalUrl { get; set; } = "";
    public int Depth { get; set; }
    public string Status { get; set; } = "";
    public int HttpCode { get; set; }
    public string Title { get; set; } = "";
    public int InternalLinks { get; set; }
    public int ExternalLinks { get; set; }
    public int TotalLinks => InternalLinks + ExternalLinks;
    public double LinkRatio { get; set; }            // rounded to 3 for display
    public string Error { get; set; } = "";
    public List<PageNode> Children { get; set; } = new();

    // SetUpTree - pages whose parent is missing become roots; children in crawl order
    public static List<PageNode> SetUpTree(List<PageResult> pages)
    {
        List<PageResult> ordered = pages.OrderBy(p => p.Depth).ThenBy(p => p.Id).ToList();
        Dictionary<int, PageNode> nodesById = ordered.ToDictionary(p => p.Id, SetUpNode);
        List<PageNode> roots = new List<PageNode>();

        foreach (PageResult page in ordered)
        {
            PageNode node = nodesById[page.Id];
            if (page.ParentId != null && page.ParentId != page.Id && nodesById.TryGetValue(page.ParentId.Value, out PageNode? parent))
                parent.Children.Add(node);
            else
                roots.Add(node);
        }
        return roots;
    }

    private static PageNode SetUpNode(PageResult page)
    {
        return new PageNode
        {
            Id = page.Id,
            ParentId = page.ParentId,
            Url = page.Url,
            FinalUrl = page.FinalUrl,
            Depth = page.Depth,
            Status = page.Status,
            HttpCode = page.HttpCode,
            Title = page.Title,
            InternalLinks = page.InternalLinks,
            ExternalLinks = page.ExternalLinks,
            LinkRatio = Math.Round(page.LinkRatio, 3),
            Error = page.Error
        };
    }
}
