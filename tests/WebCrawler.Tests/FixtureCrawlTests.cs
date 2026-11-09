// =============================================================================
// FixtureCrawlTests
// Integration-style test: a whole crawl over real HTML files on disk
// (Fixtures/site), through the real JobRunner + PageWork + PageParser +
// CrawlFrontier and a real SQL database (in-memory SQLite). Only the network is
// replaced: FixtureFetcher reads https://fixture.example/<path> from the folder.
// The fixtures cover relative and root-relative links, fragments, <base href>,
// mailto/tel/javascript, host case, a non-html file, a 404 and a page with no links.
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

public class FixtureCrawlTests : IDisposable
{
    private const string Root = "https://fixture.example/";

    private readonly SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
    private readonly AppDbContext db;
    private readonly Settings settings = new Settings { RootFolder = Path.Combine(Path.GetTempPath(), "webcrawler_fixtures_" + Guid.NewGuid().ToString("N")) };

    public FixtureCrawlTests()
    {
        connection.Open();
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        settings.Set("PerHostDelayMs", "0");
        Directory.CreateDirectory(settings.RootFolder);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        Directory.Delete(settings.RootFolder, recursive: true);
    }

    [Fact]
    public async Task RunJob_CrawlsTheFixtureSite()
    {
        Job job = await RunCrawl(maxDepth: 2);

        Assert.Equal(JobStatus.Completed, job.Status);
        Dictionary<string, PageResult> pages = await GetPages(job.Id);
        Assert.Equal(new[] { "/", "/about.html", "/docs/guide.html", "/docs/deep.html", "/missing.html", "/report.pdf" },
            pages.Values.OrderBy(p => p.Depth).ThenBy(p => p.Status).ThenBy(p => p.Url).Select(p => p.Url.Replace(Root, "/")));
        Assert.Equal("Fixture Home", pages["/"].Title);
        Assert.Equal(PageResult.StatusSkipped, pages["/report.pdf"].Status);
        Assert.Equal(PageResult.StatusFailed, pages["/missing.html"].Status);
        Assert.Equal(2, pages["/docs/deep.html"].Depth);                                       // found through <base href="/docs/">
    }

    [Theory]
    [InlineData("/", 4, 1, 0.8)]                          // about x2 (fragment dropped), guide, #top (this page); mailto, tel, javascript ignored
    [InlineData("/about.html", 4, 1, 0.8)]                // HOST CASE is still the same site
    [InlineData("/docs/guide.html", 2, 0, 1.0)]           // relative to <base href>
    [InlineData("/docs/deep.html", 0, 0, 0.0)]            // no links -> ratio 0
    public async Task RunJob_CountsLinksAndDomainLinkRatio(string path, int internalLinks, int externalLinks, double ratio)
    {
        Job job = await RunCrawl(maxDepth: 2);

        PageResult page = (await GetPages(job.Id))[path];
        Assert.Equal(internalLinks, page.InternalLinks);
        Assert.Equal(externalLinks, page.ExternalLinks);
        Assert.Equal(ratio, page.LinkRatio, 3);
    }

    [Fact]
    public async Task RunJob_StoresUniqueLinksAndEdges()
    {
        Job job = await RunCrawl(maxDepth: 2);

        Dictionary<string, PageResult> pages = await GetPages(job.Id);
        List<PageLink> rootLinks = await db.Links.Where(l => l.PageResultId == pages["/"].Id).ToListAsync();
        Assert.Equal(4, rootLinks.Count);                                                      // home (#top), about, guide, external (unique)
        Assert.Equal(2, rootLinks.Single(l => l.Url == Root + "about.html").TimesFound);
        Assert.False(rootLinks.Single(l => l.Url.Contains("external")).IsInternal);

        Dictionary<int, string> paths = pages.ToDictionary(pair => pair.Value.Id, pair => pair.Key);
        List<string> edges = (await db.Edges.Where(e => e.JobId == job.Id).ToListAsync())
            .Select(e => paths[e.ParentPageId] + " -> " + paths[e.ChildPageId]).OrderBy(line => line).ToList();
        Assert.Equal(new[]
        {
            "/ -> /about.html", "/ -> /docs/guide.html",
            "/about.html -> /", "/about.html -> /docs/guide.html", "/about.html -> /missing.html", "/about.html -> /report.pdf",
            "/docs/guide.html -> /about.html", "/docs/guide.html -> /docs/deep.html"
        }, edges);
    }

    // ---- helpers ----------------------------------------------------------------

    private async Task<Job> RunCrawl(int maxDepth)
    {
        Job job = new Job { Input = Root, InputName = Root, MaxDepth = maxDepth };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        List<IJobWork> works = new List<IJobWork> { new PageWork(db, new FixtureFetcher(), settings, NullLogger<PageWork>.Instance) };
        await new JobRunner(db, works, settings, NullLogger<JobRunner>.Instance).RunJob(job.Id, CancellationToken.None);
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
    }

    private async Task<Dictionary<string, PageResult>> GetPages(Guid jobId)
    {
        List<PageResult> pages = await db.Pages.AsNoTracking().Where(p => p.JobId == jobId).ToListAsync();
        return pages.ToDictionary(p => p.Url.Replace(Root, "/"));
    }

    // FixtureFetcher - serves Fixtures/site like a tiny web server: "/" = index.html,
    // a missing file = 404, anything that is not .html = skipped as not html
    private class FixtureFetcher : IPageFetcher
    {
        private static readonly string SiteFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "site");

        public async Task<FetchResult> DownloadPage(string url, CancellationToken cancel)
        {
            string path = new Uri(url).AbsolutePath.TrimStart('/');
            string file = Path.Combine(SiteFolder, path == "" ? "index.html" : path);
            if (!file.EndsWith(".html"))
                return new FetchResult { Ok = false, Skipped = true, HttpCode = 200, FinalUrl = url, Error = "Not html" };
            if (!File.Exists(file))
                return new FetchResult { Ok = false, HttpCode = 404, FinalUrl = url, Error = "HTTP 404" };
            return new FetchResult { Ok = true, HttpCode = 200, FinalUrl = url, Html = await File.ReadAllTextAsync(file, cancel) };
        }
    }
}
