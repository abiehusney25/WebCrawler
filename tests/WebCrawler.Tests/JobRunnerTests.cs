// =============================================================================
// JobRunnerTests
// The whole crawl with the real JobRunner + PageWork, a fake fetcher that serves a
// canned mini-site (no real HTTP) and an in-memory SQLite database (real SQL, so
// the atomic claims, ExecuteUpdate and unique indexes are tested too).
// =============================================================================
using WebCrawler.Core;
using WebCrawler.Core.Data;
using WebCrawler.Core.Models;
using WebCrawler.Core.Work;
using WebCrawler.Core.Work.Pages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace WebCrawler.Tests;

public class JobRunnerTests : IDisposable
{
    private const string Root = "https://example.com/";

    private readonly SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
    private readonly AppDbContext db;
    private readonly FakeSite site = new FakeSite();
    private readonly Settings settings = new Settings { RootFolder = Path.Combine(Path.GetTempPath(), "webcrawler_tests_" + Guid.NewGuid().ToString("N")) };

    public JobRunnerTests()
    {
        connection.Open();                                     // in-memory db lives as long as the connection
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        settings.Set("PerHostDelayMs", "0");
        Directory.CreateDirectory(settings.RootFolder);

        // the mini-site: / -> a, b, external; a -> a1, b (dup); b -> a (dup), pdf, missing; a1 -> a2 (depth 3)
        site.AddPage(Root, "<title>Home</title><a href='/a'>a</a><a href='/b'>b</a><a href='https://other.com/x'>x</a>");
        site.AddPage(Root + "a", "<a href='/a/1'>a1</a><a href='/b'>b</a>");
        site.AddPage(Root + "b", "<a href='/a'>a</a><a href='/file.pdf'>pdf</a><a href='/missing'>gone</a>");
        site.AddPage(Root + "a/1", "<a href='/a/2'>a2</a>");
        site.AddPage(Root + "a/2", "<p>too deep</p>");
        site.Add(Root + "file.pdf", new FetchResult { Ok = false, Skipped = true, HttpCode = 200, Error = "Not html (application/pdf)" });
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        Directory.Delete(settings.RootFolder, recursive: true);
    }

    [Fact]
    public async Task RunJob_CrawlsTheSiteUpToMaxDepth()
    {
        Job job = await AddJob(maxDepth: 2);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        Job saved = await GetJob(job.Id);
        List<PageResult> pages = await GetPages(job.Id);
        Assert.True(saved.Status == JobStatus.Completed, saved.FailureReason);
        Assert.Equal(new[] { Root, Root + "a", Root + "b", Root + "a/1", Root + "file.pdf", Root + "missing" },
            pages.Select(p => p.Url));
        Assert.DoesNotContain(site.Calls, url => url == Root + "a/2");                         // depth 3 never fetched
        Assert.Equal(2, pages.Single(p => p.Url == Root + "a/1").Depth);
        Assert.Contains("6 pages: 4 done, 1 skipped, 1 failed", saved.Summary);
    }

    [Fact]
    public async Task RunJob_FetchesEachUrlOnceAndUsesTheFirstParent()
    {
        Job job = await AddJob(maxDepth: 2);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        List<PageResult> pages = await GetPages(job.Id);
        Assert.Equal(site.Calls.Count, site.Calls.Distinct().Count());
        PageResult root = pages.Single(p => p.Url == Root);
        Assert.Equal(root.Id, pages.Single(p => p.Url == Root + "b").ParentId);                // found by / first, not by /a
    }

    [Fact]
    public async Task RunJob_DepthZeroCrawlsOnlyTheStartPage()
    {
        Job job = await AddJob(maxDepth: 0);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        PageResult page = Assert.Single(await GetPages(job.Id));
        Assert.Equal(2, page.InternalLinks);
        Assert.Equal(1, page.ExternalLinks);
        Assert.Equal(0.667, Math.Round(page.LinkRatio, 3));
    }

    [Fact]
    public async Task RunJob_StopsAtMaxPagesPerJob()
    {
        settings.Set("MaxPagesPerJob", "3");
        Job job = await AddJob(maxDepth: 5);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        Assert.Equal(3, (await GetPages(job.Id)).Count);
        Assert.Equal(JobStatus.Completed, (await GetJob(job.Id)).Status);
    }

    [Fact]
    public async Task RunJob_FailedAndSkippedChildrenDoNotFailTheJob()
    {
        Job job = await AddJob(maxDepth: 2);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        List<PageResult> pages = await GetPages(job.Id);
        Assert.Equal(PageResult.StatusSkipped, pages.Single(p => p.Url == Root + "file.pdf").Status);
        PageResult missing = pages.Single(p => p.Url == Root + "missing");
        Assert.Equal(PageResult.StatusFailed, missing.Status);
        Assert.Contains("404", missing.Error);
        Job saved = await GetJob(job.Id);
        Assert.True(saved.Status == JobStatus.Completed, saved.FailureReason);
        Assert.Equal(1, saved.ItemsFailed);
    }

    [Theory]
    [InlineData(false, "HTTP 404")]
    [InlineData(true, "Not html")]
    public async Task RunJob_FailsWithReasonWhenStartPageFailsOrIsNotHtml(bool skipped, string reason)
    {
        site.Add(Root, new FetchResult { Ok = false, Skipped = skipped, HttpCode = skipped ? 200 : 404, FinalUrl = Root, Error = reason });
        Job job = await AddJob();

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        Job saved = await GetJob(job.Id);
        Assert.Equal(JobStatus.Failed, saved.Status);
        Assert.Contains(reason, saved.FailureReason);
        Assert.Single(await GetPages(job.Id));                                                 // the failure is saved too
    }

    [Fact]
    public async Task RunJob_DlrUsesTheJobHostButTheCrawlFollowsTheRedirectHost()
    {
        site.Add(Root, new FetchResult { Ok = true, HttpCode = 200, FinalUrl = "https://www.example.com/", Html = "<a href='/a'>a</a><a href='https://example.com/b'>b</a>" });
        site.AddPage("https://www.example.com/a", "<p>a</p>");
        site.AddPage("https://example.com/b", "<p>b</p>");
        Job job = await AddJob(maxDepth: 1);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        List<PageResult> pages = await GetPages(job.Id);
        Assert.Equal(1, pages[0].InternalLinks);                                               // example.com/b: the job url's host (the brief's starting domain)
        Assert.Equal(1, pages[0].ExternalLinks);                                               // www.example.com/a: another host for the DLR...
        Assert.Equal(PageResult.StatusDone, pages.Single(p => p.Url == "https://example.com/b").Status);
        Assert.Equal(PageResult.StatusDone, pages.Single(p => p.Url == "https://www.example.com/a").Status);   // ...but still crawled
    }

    [Fact]
    public async Task RunJob_RootReachedAgainAfterRedirectIsNotCrawledTwice()
    {
        // example.com -> www.example.com/, and a child links back to www.example.com/ (the same page)
        site.Add(Root, new FetchResult { Ok = true, HttpCode = 200, FinalUrl = "https://www.example.com/", Html = "<a href='/a'>a</a>" });
        site.AddPage("https://www.example.com/a", "<a href='https://www.example.com/'>home</a>");
        Job job = await AddJob(maxDepth: 2);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        Assert.DoesNotContain("https://www.example.com/", site.Calls);
        Assert.Equal(2, (await GetPages(job.Id)).Count);                                        // root + /a, the root is not in the tree twice
    }

    [Fact]
    public async Task RunJob_ChildRedirectingToAKnownPageIsSkippedAsDuplicate()
    {
        site.AddPage(Root, "<a href='/a'>a</a><a href='/old'>old</a>");
        site.Add(Root + "old", new FetchResult { Ok = true, HttpCode = 200, FinalUrl = Root + "a", Html = "<a href='/a/1'>a1</a>" });
        Job job = await AddJob(maxDepth: 1);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        List<PageResult> pages = await GetPages(job.Id);
        PageResult old = pages.Single(p => p.Url == Root + "old");
        Assert.Equal(PageResult.StatusSkipped, old.Status);
        Assert.Contains("Duplicate after redirect", old.Error);
        Assert.Equal(PageResult.StatusDone, pages.Single(p => p.Url == Root + "a").Status);
    }

    [Fact]
    public async Task RunJob_ChildRedirectedOffTheSiteIsSkipped()
    {
        site.Add(Root + "a", new FetchResult { Ok = true, HttpCode = 200, FinalUrl = "https://elsewhere.com/", Html = "<a href='/z'>z</a>" });
        Job job = await AddJob(maxDepth: 2);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        PageResult page = (await GetPages(job.Id)).Single(p => p.Url == Root + "a");
        Assert.Equal(PageResult.StatusSkipped, page.Status);
        Assert.DoesNotContain(site.Calls, url => url.Contains("elsewhere"));
    }

    [Fact]
    public async Task RunJob_StoresAtMostMaxLinksPerPageButCountsThemAll()
    {
        settings.Set("MaxLinksPerPage", "2");
        Job job = await AddJob(maxDepth: 0);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        PageResult page = await db.Pages.Include(p => p.Links).SingleAsync();
        Assert.Equal(2, page.Links.Count);
        Assert.Equal(3, page.InternalLinks + page.ExternalLinks);
    }

    [Fact]
    public async Task RunJob_CancelMidCrawlKeepsCrawledPages()
    {
        settings.Set("FetchesPerJob", "1");                                                    // one at a time: cancel lands between two pages
        Job job = await AddJob(maxDepth: 2);
        site.OnFetch = url =>
        {
            if (url == Root + "a")                                                             // the user cancels while /a is fetched
                db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdate(set => set.SetProperty(j => j.Status, JobStatus.Canceled));
        };

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        List<PageResult> pages = await GetPages(job.Id);
        Assert.Equal(JobStatus.Canceled, (await GetJob(job.Id)).Status);
        Assert.Equal(2, site.Calls.Count);                                                     // / and /a, then it stopped
        Assert.Equal(PageResult.StatusDone, pages.Single(p => p.Url == Root).Status);                    // crawled before the cancel: kept
        Assert.Equal(PageResult.StatusSkipped, pages.Single(p => p.Url == Root + "a").Status);           // downloading during the cancel: not saved
        Assert.Equal("Job canceled", pages.Single(p => p.Url == Root + "b").Error);
    }

    [Fact]
    public async Task RunJob_OverlapsDownloadsButKeepsStartsSpaced()
    {
        settings.Set("FetchesPerJob", "2");
        settings.Set("PerHostDelayMs", "100");
        site.DelayMs = 300;                                                                    // a slow site
        Job job = await AddJob(maxDepth: 1);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Completed, (await GetJob(job.Id)).Status);
        Assert.Equal(3, site.Calls.Count);                                                     // /, /a, /b
        Assert.Equal(2, site.MaxInFlight);                                                     // /a and /b overlapped
        List<TimeSpan> gaps = site.StartTimes.Zip(site.StartTimes.Skip(1), (first, next) => next - first).ToList();
        Assert.All(gaps, gap => Assert.True(gap >= TimeSpan.FromMilliseconds(90), "starts too close: " + gap));
    }

    [Fact]
    public async Task RunJob_SameMessageTwiceDoesNothingTheSecondTime()
    {
        Job job = await AddJob(maxDepth: 2);

        await NewRunner().RunJob(job.Id, CancellationToken.None);
        int pagesAfterFirst = await db.Pages.CountAsync();
        int edgesAfterFirst = await db.Edges.CountAsync();
        int callsAfterFirst = site.Calls.Count;
        await NewRunner().RunJob(job.Id, CancellationToken.None);

        Assert.Equal(pagesAfterFirst, await db.Pages.CountAsync());
        Assert.Equal(edgesAfterFirst, await db.Edges.CountAsync());
        Assert.Equal(callsAfterFirst, site.Calls.Count);
    }

    [Fact]
    public async Task RunJob_SavesEveryParentToChildEdge()
    {
        Job job = await AddJob(maxDepth: 2);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        Dictionary<int, string> urls = (await GetPages(job.Id)).ToDictionary(p => p.Id, p => p.Url.Replace(Root, "/"));
        List<string> edges = (await db.Edges.Where(e => e.JobId == job.Id).ToListAsync())
            .Select(e => urls[e.ParentPageId] + " -> " + urls[e.ChildPageId]).OrderBy(line => line).ToList();
        // /b has two parents (/ and /a) and /a has two (/ and /b); /a/1 is at max depth, so it has no children
        Assert.Equal(new[] { "/ -> /a", "/ -> /b", "/a -> /a/1", "/a -> /b", "/b -> /a", "/b -> /file.pdf", "/b -> /missing" }, edges);
    }

    [Fact]
    public async Task AddEdges_SamePageTwiceAddsNoDuplicateEdges()
    {
        // a taken-over page (its worker died) saves its edges again
        Job job = await AddJob(maxDepth: 1);
        await NewRunner().RunJob(job.Id, CancellationToken.None);
        PageResult root = (await GetPages(job.Id)).Single(p => p.Url == Root);
        int countBefore = await db.Edges.CountAsync();

        int countAdded = await CrawlFrontier.AddEdges(db, root, new List<string> { Root + "a", Root + "b" }, CancellationToken.None);

        Assert.Equal(0, countAdded);
        Assert.Equal(countBefore, await db.Edges.CountAsync());
    }

    [Fact]
    public async Task RunJob_ResumedJobContinuesWithoutDuplicates()
    {
        // a worker died after the start page: root Done, its children Queued, job back to Queued
        Job job = await AddJob(maxDepth: 1);
        PageResult root = new PageResult { JobId = job.Id, Url = Root, UrlHash = PageResult.GetUrlHash(Root), Status = PageResult.StatusDone, FinalUrl = Root };
        db.Pages.Add(root);
        await db.SaveChangesAsync();
        db.Pages.Add(new PageResult { JobId = job.Id, Url = Root + "a", UrlHash = PageResult.GetUrlHash(Root + "a"), Depth = 1, ParentId = root.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        List<PageResult> pages = await GetPages(job.Id);
        Assert.Equal(new[] { Root + "a" }, site.Calls);                                        // the start page was not fetched again
        Assert.Equal(2, pages.Count);
        Assert.Equal(JobStatus.Completed, (await GetJob(job.Id)).Status);
    }

    [Theory]
    [InlineData(false, JobStatus.Running, 0)]
    [InlineData(true, JobStatus.Completed, 1)]
    public async Task RunJob_RunningJobIsOnlyTakenOverOnRedelivery(bool takeOver, JobStatus expected, int expectedCalls)
    {
        Job job = await AddJob(maxDepth: 0, status: JobStatus.Running);

        await NewRunner().RunJob(job.Id, CancellationToken.None, takeOver);

        Assert.Equal(expected, (await GetJob(job.Id)).Status);
        Assert.Equal(expectedCalls, site.Calls.Count);
    }

    [Fact]
    public async Task RunJob_SkipsCanceledJob()
    {
        Job job = await AddJob(status: JobStatus.Canceled);

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        Assert.Equal(JobStatus.Canceled, (await GetJob(job.Id)).Status);
        Assert.Empty(site.Calls);
    }

    [Fact]
    public async Task RunJob_UnexpectedErrorFailsTheJobNotTheWorker()
    {
        site.Throw = true;
        Job job = await AddJob();

        await NewRunner().RunJob(job.Id, CancellationToken.None);

        Job saved = await GetJob(job.Id);
        Assert.Equal(JobStatus.Failed, saved.Status);
        Assert.Contains("Unexpected error", saved.FailureReason);
    }

    // ---- helpers ----------------------------------------------------------------

    private JobRunner NewRunner()
    {
        List<IJobWork> works = new List<IJobWork> { new PageWork(db, site, settings, NullLogger<PageWork>.Instance) };
        return new JobRunner(db, works, settings, NullLogger<JobRunner>.Instance);
    }

    private async Task<Job> AddJob(int maxDepth = 2, JobStatus status = JobStatus.Pending)
    {
        Job job = new Job { Input = Root, InputName = Root, MaxDepth = maxDepth, Status = status };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private async Task<Job> GetJob(Guid jobId)
    {
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);   // fresh read, not the tracked copy
    }

    private async Task<List<PageResult>> GetPages(Guid jobId)
    {
        return await db.Pages.AsNoTracking().Where(p => p.JobId == jobId).OrderBy(p => p.Id).ToListAsync();
    }

    // FakeSite - canned pages by url; anything unknown is a 404
    private class FakeSite : IPageFetcher
    {
        private readonly Dictionary<string, FetchResult> pages = new Dictionary<string, FetchResult>();
        public List<string> Calls { get; } = new List<string>();
        public Action<string>? OnFetch { get; set; }
        public bool Throw { get; set; }
        public int DelayMs { get; set; }
        public List<DateTime> StartTimes { get; } = new List<DateTime>();
        public int MaxInFlight { get; private set; }
        private int inFlight;

        public void AddPage(string url, string html)
        {
            Add(url, new FetchResult { Ok = true, HttpCode = 200, FinalUrl = url, Html = html });
        }

        public void Add(string url, FetchResult result)
        {
            result.FinalUrl = result.FinalUrl == "" ? url : result.FinalUrl;
            pages[url] = result;
        }

        public async Task<FetchResult> DownloadPage(string url, CancellationToken cancel)
        {
            Calls.Add(url);
            StartTimes.Add(DateTime.UtcNow);
            OnFetch?.Invoke(url);
            if (Throw)
                throw new InvalidOperationException("boom");
            MaxInFlight = Math.Max(MaxInFlight, Interlocked.Increment(ref inFlight));
            if (DelayMs > 0)
                await Task.Delay(DelayMs, cancel);
            Interlocked.Decrement(ref inFlight);
            FetchResult result = pages.TryGetValue(url, out FetchResult? page)
                ? page
                : new FetchResult { Ok = false, HttpCode = 404, FinalUrl = url, Error = "HTTP 404" };
            return new FetchResult
            {
                Ok = result.Ok, Skipped = result.Skipped, HttpCode = result.HttpCode, FinalUrl = result.FinalUrl,
                Html = result.Html, Error = result.Error
            };
        }
    }
}
