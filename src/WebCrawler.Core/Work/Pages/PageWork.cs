// =============================================================================
// PageWork  (InputType = Url)
// The crawler. RunWork: add the start page -> loop { cancel check -> claim the
// next pages (lowest depth first, FetchesPerJob at a time) -> download them
// (overlapping, but each request STARTS PerHostDelayMs after the last one, so
// the site sees the same rate) -> one by one: sort -> save -> queue its internal
// links one level deeper + save the parent -> child edges } -> summary.
// DLR "internal" = the job url's host (the brief's starting domain). Pages followed = that host + the
// start page's FINAL host (example.com -> www.example.com still crawls).
// Saving a page, its children and its edges is one transaction (crash-safe).
// A failed or skipped child never fails the job; a failed or non-html start page does.
// =============================================================================
using WebCrawler.Core.Data;
using WebCrawler.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WebCrawler.Core.Work.Pages;

public class PageWork : IJobWork
{
    private readonly AppDbContext db;
    private readonly IPageFetcher fetcher;
    private readonly Settings settings;
    private readonly ILogger<PageWork> logger;
    private DateTime lastFetchAt = DateTime.MinValue;
    private List<string> siteHosts = new List<string>();                    // hosts the crawl follows, read once per run

    public PageWork(AppDbContext db, IPageFetcher fetcher, Settings settings, ILogger<PageWork> logger)
    {
        this.db = db;
        this.fetcher = fetcher;
        this.settings = settings;
        this.logger = logger;
    }

    public JobInputType InputType => JobInputType.Url;

    // RunWork - crawl until the frontier is empty, the job is canceled, or the start page fails
    public async Task<WorkResult> RunWork(Job job, CancellationToken cancel)
    {
        TimeSpan staleAfter = TimeSpan.FromSeconds(settings.GetInt("PageTimeoutSeconds", 45) * 2);
        await CrawlFrontier.AddRoot(db, job, cancel);

        while (true)
        {
            if (await CheckCanceled(job, cancel))
                break;

            List<PageResult> pages = await GetNextPages(job, staleAfter, cancel);
            if (pages.Count == 0)
            {
                if (!await CrawlFrontier.HasRunningPages(db, job.Id, cancel))
                    break;                                                   // frontier empty: done
                await Task.Delay(settings.GetInt("FrontierWaitMs", 500), cancel);   // another worker is finishing a page of this job
                continue;
            }

            FetchResult[] fetched = await DownloadPages(job, pages, cancel);
            bool keepGoing = true;
            for (int i = 0; i < pages.Count && keepGoing; i++)
            {
                if (await JobRunner.IsCanceled(db, job.Id, cancel))
                {
                    // canceled while downloading: the rest of this batch is not saved
                    await CrawlFrontier.SkipPages(db, pages.Skip(i).Select(p => p.Id).ToList(), "Job canceled", cancel);
                    break;                                                   // the loop's CheckCanceled skips the rest
                }
                keepGoing = await CrawlPage(job, pages[i], fetched[i], cancel);   // saving stays one page at a time (one DbContext)
            }
            if (!keepGoing)
                break;
        }

        return await GetSummary(job, cancel);
    }

    // GetNextPages - claim up to FetchesPerJob pages (each claim is atomic, see CrawlFrontier)
    private async Task<List<PageResult>> GetNextPages(Job job, TimeSpan staleAfter, CancellationToken cancel)
    {
        int batchSize = Math.Clamp(settings.GetInt("FetchesPerJob", 2), 1, 8);
        List<PageResult> pages = new List<PageResult>();
        while (pages.Count < batchSize)
        {
            PageResult? page = await CrawlFrontier.GetNextPage(db, job.Id, staleAfter, cancel);
            if (page == null || pages.Any(p => p.Id == page.Id))
                break;                                                       // nothing left, or a stale page this batch already holds
            pages.Add(page);
        }
        return pages;
    }

    // DownloadPages - the downloads overlap (no idling on slow responses), but each one starts
    // PerHostDelayMs after the previous start, so the request rate on the site does not go up
    private async Task<FetchResult[]> DownloadPages(Job job, List<PageResult> pages, CancellationToken cancel)
    {
        List<Task<FetchResult>> downloads = new List<Task<FetchResult>>();
        foreach (PageResult page in pages)
        {
            await WaitPolitely(cancel);
            logger.LogInformation(":TRYING: {Url} job {JobId} depth {Depth}", page.Url, job.Id, page.Depth);
            downloads.Add(fetcher.DownloadPage(page.Url, cancel));
        }
        return await Task.WhenAll(downloads);
    }

    // CrawlPage - one downloaded page: sort, then save it + queue its children + save its edges in ONE
    // transaction, so a crash can never leave a Done page whose children were not queued (the page stays
    // Running and is taken over). Returns false when the start page failed.
    private async Task<bool> CrawlPage(Job job, PageResult page, FetchResult fetched, CancellationToken cancel)
    {
        if (db.Entry(page).State == EntityState.Detached)
            db.Pages.Attach(page);                                           // the previous page of the batch cleared the context
        await GetSiteHosts(job, page, fetched, cancel);
        bool isDuplicate = await CheckRedirectDuplicate(page, fetched, cancel);
        ParsedPage? parsed = SortPage(job, page, fetched, isDuplicate);
        List<string> childUrls = parsed == null || page.Depth >= job.MaxDepth ? new List<string>() : GetChildUrls(parsed);

        await using var transaction = await db.Database.BeginTransactionAsync(cancel);
        await SavePage(page, parsed, cancel);
        db.ChangeTracker.Clear();                                            // keep the context small on big crawls
        int countAdded = 0;
        if (childUrls.Count > 0)
        {
            countAdded = await CrawlFrontier.AddChildren(db, page, childUrls, settings.GetInt("MaxPagesPerJob", 200), cancel);
            db.ChangeTracker.Clear();
            await CrawlFrontier.AddEdges(db, page, childUrls, cancel);
            db.ChangeTracker.Clear();
        }
        await transaction.CommitAsync(cancel);

        if (countAdded > 0)
            logger.LogInformation("Queued {Count} new pages at depth {Depth} job {JobId}", countAdded, page.Depth + 1, job.Id);
        return parsed != null || page.ParentId != null;                      // a failed child is fine, a failed start page ends the crawl
    }

    // GetChildUrls - links to follow: on the starting site (the job's host or where the start page redirected)
    private List<string> GetChildUrls(ParsedPage parsed)
    {
        return parsed.Links
            .Where(link => siteHosts.Contains(new Uri(link.Url).Host, StringComparer.OrdinalIgnoreCase))
            .Select(link => link.Url)
            .ToList();
    }

    // CheckRedirectDuplicate - a redirect can land on a page the job already has (example.com ->
    // www.example.com/, /old -> /new); compare the normalized FINAL url's hash with the job's pages
    private async Task<bool> CheckRedirectDuplicate(PageResult page, FetchResult fetched, CancellationToken cancel)
    {
        page.FinalUrlHash = PageResult.GetUrlHash(UrlRules.Normalize(fetched.FinalUrl) ?? page.Url);
        if (!fetched.Ok || page.FinalUrlHash == page.UrlHash)
            return false;                                                    // no redirect (or /about -> /about/: same url once normalized)
        return await db.Pages.AnyAsync(p => p.JobId == page.JobId && p.Id != page.Id
            && (p.UrlHash == page.FinalUrlHash || p.FinalUrlHash == page.FinalUrlHash), cancel);
    }

    // SortPage - the fetch becomes the page's status, counts and links; null when there is nothing to parse
    private ParsedPage? SortPage(Job job, PageResult page, FetchResult fetched, bool isDuplicate)
    {
        page.FinalUrl = fetched.FinalUrl;
        page.HttpCode = fetched.HttpCode;
        page.FetchedAt = DateTime.UtcNow;

        if (!fetched.Ok)
            return Fail(page, fetched.Error, fetched.Skipped);
        Uri finalUri = new Uri(fetched.FinalUrl);
        if (page.ParentId != null && !siteHosts.Contains(finalUri.Host, StringComparer.OrdinalIgnoreCase))
            return Fail(page, "Redirected off the site to " + finalUri.Host, skipped: true);
        if (isDuplicate)
            return Fail(page, "Duplicate after redirect: same page as " + fetched.FinalUrl, skipped: true);

        ParsedPage parsed = PageParser.SortPage(fetched.Html, finalUri, new Uri(job.Input).Host);   // DLR: the brief's starting domain only
        page.Title = parsed.Title;
        page.InternalLinks = parsed.InternalLinks;
        page.ExternalLinks = parsed.ExternalLinks;
        page.LinkRatio = parsed.LinkRatio;
        page.Status = PageResult.StatusDone;
        page.Error = "";
        logger.LogInformation("{Line}", page.GetLine());
        return parsed;
    }

    // SavePage - status + counts + at most MaxLinksPerPage links (counts and DLR use them all)
    private async Task SavePage(PageResult page, ParsedPage? parsed, CancellationToken cancel)
    {
        await db.Links.Where(l => l.PageResultId == page.Id).ExecuteDeleteAsync(cancel);   // a taken-over page starts clean
        if (parsed != null)
            page.Links = parsed.Links.Take(Math.Max(0, settings.GetInt("MaxLinksPerPage", 1000))).ToList();
        await db.SaveChangesAsync(cancel);
    }

    // GetSiteHosts - which hosts the crawl FOLLOWS: the job url's host plus the start page's final host
    // after redirects (example.com -> www.example.com would otherwise stop the crawl at the root).
    // The DLR still counts only the job url's host as internal (the brief's definition).
    // Read from the db once per run (a resumed job did not fetch the root).
    private async Task GetSiteHosts(Job job, PageResult page, FetchResult fetched, CancellationToken cancel)
    {
        if (page.ParentId != null && siteHosts.Count > 0)
            return;
        string rootFinalUrl = page.ParentId == null ? fetched.FinalUrl
            : await db.Pages.AsNoTracking()
                .Where(p => p.JobId == job.Id && p.ParentId == null)
                .Select(p => p.FinalUrl)
                .FirstOrDefaultAsync(cancel) ?? "";
        siteHosts = new List<string> { new Uri(job.Input).Host };
        if (Uri.TryCreate(rootFinalUrl, UriKind.Absolute, out Uri? rootUri) && !siteHosts.Contains(rootUri.Host, StringComparer.OrdinalIgnoreCase))
            siteHosts.Add(rootUri.Host);
    }

    // WaitPolitely - at least PerHostDelayMs between two requests (one job = one site)
    private async Task WaitPolitely(CancellationToken cancel)
    {
        TimeSpan delay = TimeSpan.FromMilliseconds(settings.GetInt("PerHostDelayMs", 250));
        TimeSpan waited = DateTime.UtcNow - lastFetchAt;
        if (waited < delay)
            await Task.Delay(delay - waited, cancel);
        lastFetchAt = DateTime.UtcNow;
    }

    // CheckCanceled - before every page; pages never crawled become Skipped, crawled ones are kept
    private async Task<bool> CheckCanceled(Job job, CancellationToken cancel)
    {
        bool isCanceled = await JobRunner.IsCanceled(db, job.Id, cancel);
        if (!isCanceled)
            return false;
        logger.LogInformation("Job {JobId} was canceled, stopping the crawl", job.Id);
        await CrawlFrontier.SkipQueued(db, job.Id, "Job canceled", cancel);
        return true;
    }

    // GetSummary - counts by page status; the job fails only when the start page did not make it
    private async Task<WorkResult> GetSummary(Job job, CancellationToken cancel)
    {
        Dictionary<string, int> counts = await db.Pages.Where(p => p.JobId == job.Id)
            .GroupBy(p => p.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancel);
        int countDone = counts.GetValueOrDefault(PageResult.StatusDone);
        int countSkipped = counts.GetValueOrDefault(PageResult.StatusSkipped);
        int countFailed = counts.GetValueOrDefault(PageResult.StatusFailed);
        int countAll = counts.Values.Sum();
        string summary = $"{countAll} {(countAll == 1 ? "page" : "pages")}: {countDone} done, {countSkipped} skipped, {countFailed} failed";

        PageResult? root = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.JobId == job.Id && p.ParentId == null, cancel);
        if (root == null || root.Status != PageResult.StatusDone)
        {
            WorkResult failed = WorkResult.Fail("Start page " + (root?.Status ?? "missing") + ": " + root?.Error, countFailed);
            failed.Summary = summary;
            return failed;
        }
        return new WorkResult { Ok = true, ItemsOk = countDone, ItemsFailed = countFailed, Summary = summary };
    }


    // Fail - one place to mark a page as failed or skipped, so every failure looks the same
    private ParsedPage? Fail(PageResult page, string reason, bool skipped = false)
    {
        logger.LogWarning("FAIL {Url} {Reason}", page.Url, reason);
        page.Status = skipped ? PageResult.StatusSkipped : PageResult.StatusFailed;
        page.Error = PageParser.Shorten(reason, 1000);
        return null;
    }
}
