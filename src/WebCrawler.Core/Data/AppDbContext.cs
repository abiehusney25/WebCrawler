// =============================================================================
// AppDbContext
// EF Core model. Same context for SQLite and SQL Server (DbSetup picks one).
// Jobs is generic; Pages/Links/Edges belong to PageWork. New work = new result tables here.
// Unique (JobId, UrlHash) makes "fetch each url once per job" safe with many workers.
// =============================================================================
using WebCrawler.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace WebCrawler.Core.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<PageResult> Pages => Set<PageResult>();
    public DbSet<PageLink> Links => Set<PageLink>();
    public DbSet<PageEdge> Edges => Set<PageEdge>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Job>(job =>
        {
            job.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);   // readable in the db
            job.Property(j => j.InputType).HasConversion<string>().HasMaxLength(20);
            job.Property(j => j.Input).HasMaxLength(Job.InputMaxLength);
            job.Property(j => j.InputName).HasMaxLength(Job.InputMaxLength);
            job.Property(j => j.FailureReason).HasMaxLength(1000);
            job.Property(j => j.Summary).HasMaxLength(Job.SummaryMaxLength);
            job.HasIndex(j => j.CreatedAt);                                          // history paging
            job.HasIndex(j => j.Status);                                             // stuck job sweep
        });

        model.Entity<PageResult>(page =>
        {
            page.Property(p => p.Url).HasMaxLength(PageLink.UrlMaxLength);
            page.Property(p => p.UrlHash).HasMaxLength(64);
            page.Property(p => p.FinalUrlHash).HasMaxLength(64);
            page.Property(p => p.FinalUrl).HasMaxLength(PageLink.UrlMaxLength);
            page.Property(p => p.Status).HasMaxLength(10);
            page.Property(p => p.Title).HasMaxLength(PageResult.TitleMaxLength);
            page.Property(p => p.Error).HasMaxLength(1000);
            page.HasIndex(p => new { p.JobId, p.UrlHash }).IsUnique();               // dedupe per job, any url length
            page.HasIndex(p => new { p.JobId, p.Status, p.Depth });                  // "next Queued page, lowest depth"
            page.HasIndex(p => new { p.JobId, p.FinalUrlHash });                     // "already have the page this redirect landed on?"
            page.HasOne<Job>().WithMany().HasForeignKey(p => p.JobId).OnDelete(DeleteBehavior.Cascade);
            page.HasMany(p => p.Links).WithOne().HasForeignKey(l => l.PageResultId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PageLink>(link =>
        {
            link.Property(l => l.Url).HasMaxLength(PageLink.UrlMaxLength);
        });

        model.Entity<PageEdge>(edge =>
        {
            edge.HasIndex(e => new { e.JobId, e.ParentPageId, e.ChildPageId }).IsUnique();   // idempotent edges
            edge.HasIndex(e => e.ChildPageId);                                              // "who links to this page?"
            // one cascade path only (job -> pages -> edges); SQL Server refuses two
            edge.HasOne<PageResult>().WithMany().HasForeignKey(e => e.ParentPageId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
