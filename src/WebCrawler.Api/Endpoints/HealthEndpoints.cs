// =============================================================================
// HealthEndpoints
// GET /api/health    - is the database reachable and can the queue take messages?
//                      200 when both are ok, 503 otherwise (the UI badge goes red).
// GET /health/live   - the process is up (no dependencies; for a container restart policy)
// GET /health/ready  - same checks as /api/health (for a load balancer / orchestrator)
// =============================================================================
using WebCrawler.Core;
using WebCrawler.Core.Data;
using WebCrawler.Core.Queue;

namespace WebCrawler.Api.Endpoints;

public static class HealthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/health", CheckHealth).WithTags("Health");
        app.MapGet("/health/ready", CheckHealth).WithTags("Health");
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).WithTags("Health");
    }

    // CheckHealth - db + queue checks, plus which modes are on (handy when reviewing)
    private static async Task<IResult> CheckHealth(AppDbContext db, IJobQueue queue, Settings settings)
    {
        bool dbOk = await db.Database.CanConnectAsync();
        bool queueOk = queue.CheckReady();

        var body = new
        {
            status = dbOk && queueOk ? "ok" : "fail",
            db = dbOk ? "ok" : "fail",
            queue = queueOk ? "ok" : "fail",
            dbMode = settings.Get("DbMode", "Sqlite"),
            queueMode = settings.Get("QueueMode", "InMemory"),
            workerMode = settings.Get("WorkerMode", "InProcess"),
            time = DateTime.UtcNow
        };
        return dbOk && queueOk ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
