// =============================================================================
// DbSetup
// Picks SQLite or SQL Server from DbMode (one switch in settings.csv) and creates
// the database with a short retry loop (SQL Server in Docker takes a few seconds).
// SQLite only: an out-of-date file (model changed since it was made) is kept as a
// timestamped backup and a fresh one is created, instead of every query failing.
// =============================================================================
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WebCrawler.Core.Data;

public static class DbSetup
{
    // AddDatabase - register AppDbContext for the chosen DbMode
    public static void AddDatabase(IServiceCollection services, Settings settings)
    {
        if (settings.IsMode("DbMode", "SqlServer", "Sqlite"))
        {
            string connection = GetSqlServerConnection(settings);
            services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connection));
            return;
        }

        string sqliteFile = settings.GetPath("SqliteFile", Path.Combine("Output", "webcrawler.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(sqliteFile)!);
        services.AddDbContext<AppDbContext>(options => options.UseSqlite("Data Source=" + sqliteFile));
    }

    // GetSqlServerConnection - connection string from settings, password from secrets/env
    public static string GetSqlServerConnection(Settings settings)
    {
        string connection = settings.Get("SqlServerConnection",
            "Server=localhost;Database=WebCrawler;User Id=sa;TrustServerCertificate=True");
        string password = settings.Get("SqlServerPassword", "");
        return password == "" ? connection : connection.TrimEnd(';') + ";Password=" + password;
    }

    // CreateDatabase - EnsureCreated (no migrations to run), retried while the server starts
    public static async Task CreateDatabase(IServiceProvider services, Settings settings, ILogger logger)
    {
        int maxTries = Math.Max(1, settings.GetInt("DbStartupRetries", 10));
        for (int tryNumber = 1; ; tryNumber++)
        {
            try
            {
                using IServiceScope scope = services.CreateScope();
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                await CheckSchema(db, logger);
                logger.LogInformation("Database ready ({Provider})", db.Database.ProviderName);
                return;
            }
            catch (Exception ex) when (tryNumber < maxTries && ex is DbException or InvalidOperationException)
            {
                logger.LogWarning("FAIL database not ready, try {Try}/{Max}: {Reason}", tryNumber, maxTries, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    // IsDuplicateKey - true only for a unique-index violation (another worker saved the same row first);
    // any other database error (timeout, connection lost, bad data) is NOT swallowed by the callers
    public static bool IsDuplicateKey(DbUpdateException ex)
    {
        return ex.InnerException switch
        {
            Microsoft.Data.SqlClient.SqlException sql => sql.Number is 2601 or 2627,          // duplicate key row / unique constraint
            SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                      && sqlite.SqliteExtendedErrorCode is 2067 or 1555,      // UNIQUE / PRIMARY KEY constraint
            _ => false
        };
    }

    // CheckSchema - EnsureCreated never changes an existing file, so after a model change every query
    // fails ("no such column"). Read one row of each table; if that fails on SQLite, keep the old file
    // as Output/<name>_old_<timestamp>.db and start a fresh database.
    public static async Task CheckSchema(AppDbContext db, ILogger logger)
    {
        if (!db.Database.IsSqlite())
            return;                                                          // SQL Server in Docker: docker compose down -v
        try
        {
            await db.Jobs.AsNoTracking().Take(1).ToListAsync();
            await db.Pages.AsNoTracking().Take(1).ToListAsync();
            await db.Links.AsNoTracking().Take(1).ToListAsync();
        }
        catch (SqliteException ex)
        {
            string dbFile = db.Database.GetDbConnection().DataSource;
            string backupFile = Path.Combine(Path.GetDirectoryName(dbFile) ?? "",
                Path.GetFileNameWithoutExtension(dbFile) + "_old_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".db");
            logger.LogWarning("FAIL database file is out of date ({Reason}); kept as {Backup}, creating a fresh one", ex.Message, backupFile);

            await db.Database.CloseConnectionAsync();
            SqliteConnection.ClearAllPools();                                // Windows keeps the file locked while pooled
            File.Move(dbFile, backupFile);
            foreach (string suffix in new[] { "-wal", "-shm" })
                if (File.Exists(dbFile + suffix))
                    File.Move(dbFile + suffix, backupFile + suffix);
            await db.Database.EnsureCreatedAsync();
            logger.LogInformation("Saved -> {Backup}", backupFile);
        }
    }
}
