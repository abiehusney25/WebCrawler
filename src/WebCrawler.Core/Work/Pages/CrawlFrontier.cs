// =============================================================================
// CrawlFrontier
// The crawl queue of ONE job lives in the database, not in memory: pages are
// inserted as Queued, a worker claims the next one atomically (lowest depth
// first), and the unique (JobId, UrlHash) index drops duplicates. So a re-sent
// message, a restarted worker or a second worker continue the same crawl
// without fetching any url twice.
// =============================================================================
using WebCrawler.Core.Data;
using WebCrawler.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace WebCrawler.Core.Work.Pages;

public static class CrawlFrontier
{
    private const int ChunkSize = 500;                                       // SQL Server allows ~2100 parameters per query

    // AddRoot - the start page as Queued; a no-op when it is already there (resumed job)
    public static async Task AddRoot(AppDbContext db, Job job, CancellationToken cancel)
    {
        string urlHash = PageResult.GetUrlHash(job.Input);
        if (await db.Pages.AnyAsync(p => p.JobId == job.Id && p.UrlHash == urlHash, cancel))
            return;

        db.Pages.Add(new PageResult { JobId = job.Id, Url = job.Input, UrlHash = urlHash, Depth = 0 });
        await SaveIgnoringDuplicates(db, cancel);
    }

    // GetNextPage - claim the next Queued page (or one whose worker died: Running past staleAfter)
    public static async Task<PageResult?> GetNextPage(AppDbContext db, Guid jobId, TimeSpan staleAfter, CancellationToken cancel)
    {
        for (int attempt = 0; attempt < 5; attempt++)                        // another worker may win the race
        {
            DateTime now = DateTime.UtcNow;
            DateTime staleBefore = now - staleAfter;
            PageResult? next = await db.Pages.AsNoTracking()
                .Where(p => p.JobId == jobId && (p.Status == PageResult.StatusQueued
                            || (p.Status == PageResult.StatusRunning && p.ClaimedAt < staleBefore)))
                .OrderBy(p => p.Depth).ThenBy(p => p.Id)
                .FirstOrDefaultAsync(cancel);
            if (next == null)
                return null;

            int claimed = await db.Pages
                .Where(p => p.Id == next.Id && p.Status == next.Status && p.ClaimedAt == next.ClaimedAt)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(p => p.Status, PageResult.StatusRunning)
                    .SetProperty(p => p.ClaimedAt, now), cancel);
            if (claimed == 1)
                return await db.Pages.FirstAsync(p => p.Id == next.Id, cancel);   // tracked, so the caller can save it
        }
        return null;
    }

    // AddChildren - new internal urls as Queued pages at depth + 1, within the page budget
    public static async Task<int> AddChildren(AppDbContext db, PageResult parent, List<string> childUrls, int maxPages,
        CancellationToken cancel)
    {
        int budget = maxPages - await db.Pages.CountAsync(p => p.JobId == parent.JobId, cancel);
        if (budget <= 0 || childUrls.Count == 0)
            return 0;

        Dictionary<string, string> urlsByHash = childUrls.Distinct().ToDictionary(PageResult.GetUrlHash, url => url);
        HashSet<string> knownHashes = await GetKnownHashes(db, parent.JobId, urlsByHash.Keys.ToList(), cancel);
        List<PageResult> children = urlsByHash
            .Where(pair => !knownHashes.Contains(pair.Key))
            .Take(budget)
            .Select(pair => new PageResult
            {
                JobId = parent.JobId, Url = pair.Value, UrlHash = pair.Key, Depth = parent.Depth + 1, ParentId = parent.Id
            })
            .ToList();

        db.Pages.AddRange(children);
        return await SaveIgnoringDuplicates(db, cancel);
    }

    // AddEdges - parent -> child rows for every page of the job this parent links to (new or already
    // known, also through a redirect); existing edges are left alone, so running it twice adds nothing
    public static async Task<int> AddEdges(AppDbContext db, PageResult parent, List<string> childUrls, CancellationToken cancel)
    {
        List<string> hashes = childUrls.Distinct().Select(PageResult.GetUrlHash).ToList();
        HashSet<int> childIds = new HashSet<int>();
        foreach (List<string> chunk in hashes.Chunk(ChunkSize).Select(part => part.ToList()))
            childIds.UnionWith(await db.Pages
                .Where(p => p.JobId == parent.JobId && (chunk.Contains(p.UrlHash) || chunk.Contains(p.FinalUrlHash)))
                .Select(p => p.Id)
                .ToListAsync(cancel));
        childIds.Remove(parent.Id);                                          // a link back to itself is not an edge

        List<int> knownIds = await db.Edges.Where(e => e.ParentPageId == parent.Id).Select(e => e.ChildPageId).ToListAsync(cancel);
        List<PageEdge> edges = childIds.Except(knownIds)
            .Select(childId => new PageEdge { JobId = parent.JobId, ParentPageId = parent.Id, ChildPageId = childId })
            .ToList();
        if (edges.Count == 0)
            return 0;

        db.Edges.AddRange(edges);
        try
        {
            await db.SaveChangesAsync(cancel);
            return edges.Count;
        }
        catch (DbUpdateException ex) when (DbSetup.IsDuplicateKey(ex))
        {
            foreach (PageEdge edge in edges)
                db.Entry(edge).State = EntityState.Detached;                 // a taken-over page raced us: the unique index kept one copy
            return 0;
        }
    }

    // HasRunningPages - another worker is still busy on this job (fresh claim, not stale)
    public static async Task<bool> HasRunningPages(AppDbContext db, Guid jobId, CancellationToken cancel)
    {
        return await db.Pages.AnyAsync(p => p.JobId == jobId && p.Status == PageResult.StatusRunning, cancel);
    }

    // SkipPages - on cancel mid-batch: pages this worker claimed but will not save become Skipped
    public static async Task SkipPages(AppDbContext db, List<int> pageIds, string reason, CancellationToken cancel)
    {
        await db.Pages
            .Where(p => pageIds.Contains(p.Id) && p.Status == PageResult.StatusRunning)
            .ExecuteUpdateAsync(set => set
                .SetProperty(p => p.Status, PageResult.StatusSkipped)
                .SetProperty(p => p.Error, reason), cancel);
    }

    // SkipQueued - on cancel: what was never crawled becomes Skipped with the reason
    public static async Task SkipQueued(AppDbContext db, Guid jobId, string reason, CancellationToken cancel)
    {
        await db.Pages
            .Where(p => p.JobId == jobId && p.Status == PageResult.StatusQueued)
            .ExecuteUpdateAsync(set => set
                .SetProperty(p => p.Status, PageResult.StatusSkipped)
                .SetProperty(p => p.Error, reason), cancel);
    }

    // GetKnownHashes - which of these urls the job already has, as a url or as a page's final url
    // after a redirect (checked in chunks)
    private static async Task<HashSet<string>> GetKnownHashes(AppDbContext db, Guid jobId, List<string> hashes,
        CancellationToken cancel)
    {
        HashSet<string> known = new HashSet<string>();
        foreach (List<string> chunk in hashes.Chunk(ChunkSize).Select(part => part.ToList()))   // List, not array: the newest C# binds array.Contains to a Span overload EF can't translate
        {
            var found = await db.Pages
                .Where(p => p.JobId == jobId && (chunk.Contains(p.UrlHash) || chunk.Contains(p.FinalUrlHash)))
                .Select(p => new { p.UrlHash, p.FinalUrlHash })
                .ToListAsync(cancel);
            known.UnionWith(found.SelectMany(row => new[] { row.UrlHash, row.FinalUrlHash }));
        }
        return known;
    }

    // SaveIgnoringDuplicates - batch insert; if another worker inserted one of them first
    // (unique index), fall back to one by one and skip the duplicates
    private static async Task<int> SaveIgnoringDuplicates(AppDbContext db, CancellationToken cancel)
    {
        List<PageResult> added = db.ChangeTracker.Entries<PageResult>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .ToList();
        try
        {
            await db.SaveChangesAsync(cancel);
            return added.Count;
        }
        catch (DbUpdateException ex) when (DbSetup.IsDuplicateKey(ex))
        {
            foreach (PageResult page in added)
                db.Entry(page).State = EntityState.Detached;
        }

        int countSaved = 0;
        foreach (PageResult page in added)
        {
            page.Id = 0;
            db.Pages.Add(page);
            try
            {
                await db.SaveChangesAsync(cancel);
                countSaved++;
            }
            catch (DbUpdateException ex) when (DbSetup.IsDuplicateKey(ex))
            {
                db.Entry(page).State = EntityState.Detached;                  // already there: the other worker has it
            }
        }
        return countSaved;
    }
}
