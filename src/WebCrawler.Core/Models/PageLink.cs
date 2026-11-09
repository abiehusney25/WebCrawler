// =============================================================================
// PageLink
// One unique outgoing link of a page. TimesFound counts duplicates on the page.
// =============================================================================
namespace WebCrawler.Core.Models;

public class PageLink
{
    public const int UrlMaxLength = 2048;

    public int Id { get; set; }
    public int PageResultId { get; set; }
    public string Url { get; set; } = "";
    public bool IsInternal { get; set; }
    public int TimesFound { get; set; } = 1;

    public string GetLine()
    {
        return $"{(IsInternal ? "internal" : "external")} {Url} x{TimesFound}";
    }
}
