// =============================================================================
// CrashRecoveryTests
// A worker "crashes" in the worst spot: after saving a page, before its edges
// are saved. Because page + children + edges are one transaction, nothing of
// that page is kept: it stays Running, and the redelivered job crawls it again
// and ends with every child page and every edge. (Real crash = process dies;
// here a SaveChanges interceptor throws once at that moment.)
// =============================================================================
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WebCrawler.Core;
using WebCrawler.Core.Data;
using WebCrawler.Core.Models;
using WebCrawler.Core.Work;
using WebCrawler.Core.Work.Pages;

namespace WebCrawler.Tests;

public class CrashRecoveryTests : IDisposable
{
    private const string Root = "https://example.com/";

    private readonly SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
    private readonly CrashOnEdges crash = new CrashOnEdges();
    private readonly AppDbContext db;
    private readonly Settings settings = new Settings { RootFolder = Path.Combine(Path.GetTempPath(), "webcrawler_crash_" + Guid.NewGuid().ToString("N")) };

    public CrashRecoveryTests()
    {
        connection.Open();
        db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).AddInterceptors(crash).Options);
        db.Database.EnsureCreated();
        settings.Set("PerHostDelayMs", "0");
        settings.Set("PageTimeoutSeconds", "0");                 // a page left Running is taken over right away
        Directory.CreateDirectory(settings.RootFolder);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        Directory.Delete(settings.RootFolder, recursive: true);
    }

    [Fact]
    public async Task RunJob_CrashBetweenSavingAPageAndItsEdgesLosesNothing()
    {
        Job job = new Job { Input = Root, InputName = Root, MaxDepth = 1 };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        // first delivery: the worker dies while saving the start page's edges
        await NewRunner().RunJob(job.Id, CancellationToken.None);
        db.ChangeTracker.Clear();
        PageResult root = await db.Pages.AsNoTracking().SingleAsync(p => p.Url == Root);
        Assert.Equal(PageResult.StatusRunning, root.Status);                                   // NOT Done: the save was rolled back
        Assert.Equal(0, await db.Pages.CountAsync(p => p.ParentId != null));                   // no half-queued children
        Assert.Equal(0, await db.Edges.CountAsync());

        // redelivery: the job runs again and the start page is crawled again
        await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(set => set.SetProperty(j => j.Status, JobStatus.Pending));
        await NewRunner().RunJob(job.Id, CancellationToken.None);

        List<PageResult> pages = await db.Pages.AsNoTracking().Where(p => p.JobId == job.Id).ToListAsync();
        Job saved = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.True(saved.Status == JobStatus.Completed, saved.FailureReason);
        Assert.Equal(3, pages.Count);                                                          // /, /a, /b
        Assert.All(pages, page => Assert.Equal(PageResult.StatusDone, page.Status));
        Assert.Equal(2, await db.Edges.CountAsync());                                          // / -> /a, / -> /b
    }

    [Fact]
    public async Task IsDuplicateKey_OnlyForUniqueIndexErrors()
    {
        Job job = new Job { Input = Root, InputName = Root };
        db.Jobs.Add(job);
        db.Pages.Add(new PageResult { JobId = job.Id, Url = Root, UrlHash = PageResult.GetUrlHash(Root) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.Pages.Add(new PageResult { JobId = job.Id, Url = Root, UrlHash = PageResult.GetUrlHash(Root) });   // same url again
        DbUpdateException duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Pages.Add(new PageResult { JobId = Guid.NewGuid(), Url = Root, UrlHash = "x" });                    // job does not exist
        DbUpdateException other = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.True(DbSetup.IsDuplicateKey(duplicate));
        Assert.False(DbSetup.IsDuplicateKey(other));                                           // real errors are not swallowed
    }

    private JobRunner NewRunner()
    {
        List<IJobWork> works = new List<IJobWork> { new PageWork(db, new MiniSite(), settings, NullLogger<PageWork>.Instance) };
        return new JobRunner(db, works, settings, NullLogger<JobRunner>.Instance);
    }

    // CrashOnEdges - throws the first time edges are about to be saved (the "crash")
    private class CrashOnEdges : SaveChangesInterceptor
    {
        private bool isArmed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancel = default)
        {
            if (isArmed && eventData.Context!.ChangeTracker.Entries<PageEdge>().Any(entry => entry.State == EntityState.Added))
            {
                isArmed = false;
                throw new InvalidOperationException("worker crashed");
            }
            return base.SavingChangesAsync(eventData, result, cancel);
        }
    }

    // MiniSite - / links to /a and /b
    private class MiniSite : IPageFetcher
    {
        public Task<FetchResult> DownloadPage(string url, CancellationToken cancel)
        {
            string html = url == Root ? "<a href='/a'>a</a><a href='/b'>b</a>" : "<p>leaf</p>";
            return Task.FromResult(new FetchResult { Ok = true, HttpCode = 200, FinalUrl = url, Html = html });
        }
    }
}
