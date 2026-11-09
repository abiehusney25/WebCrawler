// =============================================================================
// PageResult
// One page of a crawl job. Rows are inserted as Queued (the crawl frontier lives
// in the database), then claimed (Running) and finished as Done / Skipped / Failed.
// UrlHash (SHA-256 of the normalized url) + a unique (JobId, UrlHash) index =
// each url is crawled once per job, even with 2 workers or a duplicate message.
// FinalUrlHash (hash of the normalized url after redirects) catches the same page
// reached through a redirect (example.com -> www.example.com/, /old -> /new).
// =============================================================================
using System.Security.Cryptography;
using System.Text;

namespace WebCrawler.Core.Models;

public class PageResult
{
    public const int TitleMaxLength = 500;
    public const string StatusQueued = "Queued";
    public const string StatusRunning = "Running";      // claimed by a worker
    public const string StatusDone = "Done";
    public const string StatusSkipped = "Skipped";      // not html, too big, redirected off the site or onto a known page, job canceled
    public const string StatusFailed = "Failed";        // http error, network, timeout

    public int Id { get; set; }
    public Guid JobId { get; set; }
    public string Url { get; set; } = "";               // normalized
    public string UrlHash { get; set; } = "";
    public string FinalUrl { get; set; } = "";          // after redirects
    public string FinalUrlHash { get; set; } = "";      // "" until fetched
    public int Depth { get; set; }
    public int? ParentId { get; set; }                  // null for the start page; the first parent that found it
    public string Status { get; set; } = StatusQueued;
    public int HttpCode { get; set; }
    public string Title { get; set; } = "";
    public int InternalLinks { get; set; }
    public int ExternalLinks { get; set; }
    public double LinkRatio { get; set; }               // raw internal / all; 0 when the page has no links
    public string Error { get; set; } = "";
    public DateTime? ClaimedAt { get; set; }            // a Running page older than the page timeout is taken over
    public DateTime? FetchedAt { get; set; }

    public List<PageLink> Links { get; set; } = new();

    public bool IsFinished => Status is StatusDone or StatusSkipped or StatusFailed;

    // GetUrlHash - 64 hex chars; fixed size, so any url length fits the unique index
    public static string GetUrlHash(string url)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
    }

    // GetLine - one readable line, for logs (modern GetProd)
    public string GetLine()
    {
        return $"{Status} {Url} depth:{Depth} internal:{InternalLinks} external:{ExternalLinks} ratio:{Math.Round(LinkRatio, 3)} {Error}";
    }
}
