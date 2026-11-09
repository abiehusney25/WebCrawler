// =============================================================================
// FetchResult - what PageFetcher hands back: the html, or a FAIL/SKIP with the reason
// ParsedPage  - what PageParser hands back: title, link counts, unique links
// =============================================================================
namespace WebCrawler.Core.Models;

public class FetchResult
{
    public bool Ok { get; set; }
    public bool Skipped { get; set; }              // not a failure: not html or too big
    public int HttpCode { get; set; }
    public string FinalUrl { get; set; } = "";     // after redirects; links resolve against this
    public string Html { get; set; } = "";
    public string Error { get; set; } = "";

    public string GetLine()
    {
        return $"{(Ok ? "OK" : "FAIL")} {FinalUrl} http:{HttpCode} bytes:{Html.Length} {Error}";
    }
}

public class ParsedPage
{
    public string Title { get; set; } = "";
    public int InternalLinks { get; set; }
    public int ExternalLinks { get; set; }
    public double LinkRatio { get; set; }
    public List<PageLink> Links { get; set; } = new();

    public string GetLine()
    {
        return $"{Title} internal:{InternalLinks} external:{ExternalLinks} ratio:{Math.Round(LinkRatio, 3)} unique:{Links.Count}";
    }
}
