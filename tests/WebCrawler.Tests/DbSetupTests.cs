// =============================================================================
// DbSetupTests
// An SQLite file made by an older model (a column missing) must not break the
// app: startup keeps it as a timestamped backup and creates a fresh database.
// =============================================================================
using WebCrawler.Core;
using WebCrawler.Core.Data;
using WebCrawler.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace WebCrawler.Tests;

public class DbSetupTests : IDisposable
{
    private readonly string testFolder = Path.Combine(Path.GetTempPath(), "webcrawler_db_" + Guid.NewGuid().ToString("N"));

    public DbSetupTests()
    {
        Directory.CreateDirectory(testFolder);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(testFolder, recursive: true);
    }

    [Fact]
    public async Task CreateDatabase_ReplacesAnOutOfDateSqliteFileAndKeepsABackup()
    {
        string dbFile = Path.Combine(testFolder, "webcrawler.db");
        using (SqliteConnection old = new SqliteConnection("Data Source=" + dbFile))   // an "old model" file: Pages without FinalUrlHash
        {
            old.Open();
            using SqliteCommand create = old.CreateCommand();
            create.CommandText = "CREATE TABLE Jobs (Id TEXT PRIMARY KEY); CREATE TABLE Pages (Id INTEGER PRIMARY KEY, JobId TEXT);";
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Settings settings = new Settings { RootFolder = testFolder };
        using ServiceProvider provider = SetUpDatabase(dbFile);

        await DbSetup.CreateDatabase(provider, settings, NullLogger.Instance);

        Assert.Single(Directory.GetFiles(testFolder, "webcrawler_old_*.db"));
        using IServiceScope scope = provider.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Pages.Where(p => p.FinalUrlHash != "").ToListAsync());          // the new column exists
    }

    [Fact]
    public async Task CreateDatabase_LeavesAnUpToDateFileAlone()
    {
        Settings settings = new Settings { RootFolder = testFolder };
        using ServiceProvider provider = SetUpDatabase(Path.Combine(testFolder, "webcrawler.db"));

        await DbSetup.CreateDatabase(provider, settings, NullLogger.Instance);
        using (IServiceScope scope = provider.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Jobs.Add(new Job { Input = "https://example.com/", InputName = "https://example.com/" });
            await db.SaveChangesAsync();
        }
        await DbSetup.CreateDatabase(provider, settings, NullLogger.Instance);

        Assert.Empty(Directory.GetFiles(testFolder, "webcrawler_old_*.db"));
        using IServiceScope check = provider.CreateScope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.CountAsync());
    }

    // SetUpDatabase - this test's own SQLite file (not via settings: other test classes set
    // WEBCRAWLER_SqliteFile for the whole process, and environment variables win)
    private static ServiceProvider SetUpDatabase(string dbFile)
    {
        ServiceCollection services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite("Data Source=" + dbFile));
        return services.BuildServiceProvider();
    }
}
