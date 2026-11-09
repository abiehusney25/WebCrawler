// =============================================================================
// WebCrawler Worker
// The worker on its own (WorkerMode=Separate). Same steps as the API's Main,
// minus the UI and the job API: it only serves /health/live and /health/ready
// (WorkerHealthUrl) so Docker can see it. Needs QueueMode=RabbitMq to receive jobs.
// =============================================================================
using WebCrawler.Core;
using WebCrawler.Core.Data;
using WebCrawler.Core.Queue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WebCrawler.Worker;

public class Program
{
    private static Settings settings = new Settings();
    private static WebApplication host = null!;
    private static ILogger logger = null!;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("Hello World! WebCrawler Worker starting...");

        Init();
        LoadConfig();
        SetUpServices(args);
        MapHealth();
        CheckModes();
        await CheckDatabase();
        await RunWorker();      // blocks until Ctrl+C
        CleanUp();
    }

    // Init - find the project root and make sure folders and starter CSVs exist
    private static void Init()
    {
        settings = new Settings { RootFolder = Settings.FindRootFolder() };
        settings.CheckFiles();
    }

    // LoadConfig - Config/settings.csv, then secrets.csv on top (env vars still win)
    private static void LoadConfig()
    {
        settings.Load(settings.SettingsFile);
        settings.Load(settings.SecretsFile);
    }

    // SetUpServices - the shared pipeline with the worker switched on, plus a tiny web host for health
    private static void SetUpServices(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory               // finds appsettings.json from any folder
        });
        builder.WebHost.UseUrls(settings.Get("WorkerHealthUrl", "http://localhost:5001"));
        PipelineSetup.SetUpPipeline(builder.Services, settings, runWorker: true);

        host = builder.Build();
        logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WebCrawler.Worker");
    }

    // MapHealth - live = the process is up; ready = database + RabbitMQ reachable (503 otherwise)
    private static void MapHealth()
    {
        host.MapGet("/health/live", () => Results.Ok(new { status = "ok", role = "worker" }));
        host.MapGet("/health/ready", async (AppDbContext db, IJobQueue queue) =>
        {
            bool dbOk = await db.Database.CanConnectAsync();
            bool queueOk = queue.CheckReady();
            var body = new { status = dbOk && queueOk ? "ok" : "fail", role = "worker", db = dbOk ? "ok" : "fail", queue = queueOk ? "ok" : "fail" };
            return dbOk && queueOk ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }

    // CheckModes - an in-memory queue can't cross processes, so say so loudly
    private static void CheckModes()
    {
        if (!settings.IsMode("QueueMode", "RabbitMq", "InMemory"))
            logger.LogWarning("FAIL QueueMode is InMemory: this worker will only see stuck jobs re-queued at startup. Use QueueMode=RabbitMq.");
    }

    // CheckDatabase - create tables if missing (retries while SQL Server starts)
    private static async Task CheckDatabase()
    {
        await DbSetup.CreateDatabase(host.Services, settings, logger);
    }

    // RunWorker - run until Ctrl+C / docker stop
    private static async Task RunWorker()
    {
        logger.LogInformation("Worker running ({DbMode}, {QueueMode})", settings.Get("DbMode", "Sqlite"), settings.Get("QueueMode", "InMemory"));
        await host.RunAsync();
    }

    // CleanUp - anything to close or flush on the way out
    private static void CleanUp()
    {
        Console.WriteLine("THE end");
    }
}
