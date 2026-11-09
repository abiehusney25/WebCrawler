// =============================================================================
// WebCrawler API
// Main is the table of contents: config from Config/settings.csv, services,
// endpoints, database, then run until Ctrl+C. With WorkerMode=InProcess the
// worker runs here too, so one "dotnet run" is the whole app.
// =============================================================================
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using WebCrawler.Api.Endpoints;
using WebCrawler.Core;
using WebCrawler.Core.Data;
using Microsoft.Extensions.FileProviders;

namespace WebCrawler.Api;

public class Program
{
    private static Settings settings = new Settings();
    private static WebApplication app = null!;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("Hello World! WebCrawler API starting...");

        Init();
        LoadConfig();
        SetUpServices(args);
        MapEndpoints();
        await CheckDatabase();
        await RunApp();     // blocks until Ctrl+C
        CleanUp();
    }

    // Init - find the project root and make sure folders and starter CSVs exist
    private static void Init()
    {
        settings = new Settings { RootFolder = Settings.FindRootFolder() };
        settings.CheckFiles();
    }

    // LoadConfig - every "hardcoded" value from Config/settings.csv, then secrets.csv on top
    private static void LoadConfig()
    {
        settings.Load(settings.SettingsFile);
        settings.Load(settings.SecretsFile);
    }

    // SetUpServices - the shared pipeline (db, queue, http, worker if InProcess) + web extras
    private static void SetUpServices(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(settings.Get("Url", "http://localhost:5000"));

        bool runWorker = settings.IsMode("WorkerMode", "InProcess", "InProcess");
        PipelineSetup.SetUpPipeline(builder.Services, settings, runWorker);

        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));   // "Running", not 1
        SetUpRateLimit(builder.Services);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        app = builder.Build();
    }

    // SetUpRateLimit - POST /api/jobs: CreateJobsPerMinute per client ip, then 429 (crawls are expensive)
    private static void SetUpRateLimit(IServiceCollection services)
    {
        int jobsPerMinute = Math.Max(1, settings.GetInt("CreateJobsPerMinute", 30));
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(JobEndpoints.CreateJobPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = jobsPerMinute, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    // MapEndpoints - error handling, the frontend (static files), Swagger, then the JSON API
    private static void MapEndpoints()
    {
        app.UseExceptionHandler();      // unexpected errors -> logged + ProblemDetails
        app.UseStatusCodePages();       // also turns the limiter's bare 429 into ProblemDetails

        string webFolder = settings.GetPath("WebFolder", "wwwroot");
        if (!File.Exists(Path.Combine(webFolder, "index.html")) && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
            webFolder = Path.Combine(AppContext.BaseDirectory, "wwwroot");   // published build (Docker)
        if (!File.Exists(Path.Combine(webFolder, "index.html")))
            app.Logger.LogWarning("FAIL web page not built ({Folder}): install Node.js and build again, or run \"npm ci\" + \"npm run build\" in web/ (the API works without it)", webFolder);
        Directory.CreateDirectory(webFolder);
        PhysicalFileProvider webFiles = new PhysicalFileProvider(webFolder);
        StaticFileOptions webOptions = new StaticFileOptions { FileProvider = webFiles };
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
        app.UseStaticFiles(webOptions);
        app.UseRouting();               // after static files, or the React fallback route would answer /assets/*.js
        app.UseRateLimiter();           // after routing: it reads the endpoint's policy

        if (settings.GetBool("Swagger", true))
        {
            app.UseSwagger();
            app.UseSwaggerUI();         // /swagger
        }

        HealthEndpoints.Map(app);
        ConfigEndpoints.Map(app);
        JobEndpoints.Map(app);
        JobEventEndpoints.Map(app);

        // React routes (/jobs/{id}, /history) -> index.html on reload; unknown /api paths stay 404.
        // No web page built yet -> say how to build it instead of a bare 404.
        const string pagePattern = "{*path:regex(^(?!api/|health/|swagger).*$)}";
        if (File.Exists(Path.Combine(webFolder, "index.html")))
            app.MapFallbackToFile(pagePattern, "index.html", webOptions);
        else
        {
            app.MapGet("/", () => Results.Content(WebPageMissing, "text/html", statusCode: 503)).ExcludeFromDescription();
            app.MapFallback(pagePattern, () => Results.Content(WebPageMissing, "text/html", statusCode: 503));
        }
    }

    private const string WebPageMissing = """
        <!DOCTYPE html><html><head><meta charset="utf-8"><title>Web Crawler</title></head>
        <body style="font-family: Segoe UI, Arial, sans-serif; max-width: 40rem; margin: 3rem auto; line-height: 1.5">
        <h1>Web Crawler</h1>
        <p>The API is running, but the web page has not been built yet.</p>
        <p><b>Fix:</b> install <a href="https://nodejs.org">Node.js 20+</a>, then build again
        (<code>dotnet build</code> builds the page the first time), or double-click <code>RunCrawler.bat</code>,
        or run <code>npm ci</code> and <code>npm run build</code> in the <code>web</code> folder. Then restart.</p>
        <p>The API itself works: <a href="/swagger">/swagger</a>, <a href="/health/ready">/health/ready</a>.</p>
        </body></html>
        """;

    // CheckDatabase - create tables if they are missing (retries while SQL Server starts)
    private static async Task CheckDatabase()
    {
        await DbSetup.CreateDatabase(app.Services, settings, app.Logger);
    }

    // RunApp - start the web server
    private static async Task RunApp()
    {
        app.Logger.LogInformation("Open {Url}  (Swagger: /swagger)", settings.Get("Url", "http://localhost:5000"));
        await app.RunAsync();
    }

    // CleanUp - anything to close or flush on the way out
    private static void CleanUp()
    {
        Console.WriteLine("THE end");
    }
}
