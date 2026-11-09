// =============================================================================
// ConfigEndpoints
// GET /api/config - the few settings the UI needs (poll interval, page size, depth)
// GET /api/sites  - sample inputs from Config/sites.csv (Enabled rows only)
// So the frontend has nothing hardcoded either.
// =============================================================================
using WebCrawler.Core;
using WebCrawler.Core.Models;

namespace WebCrawler.Api.Endpoints;

public static class ConfigEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/config", GetConfig).WithTags("Config");
        app.MapGet("/api/sites", GetSites).WithTags("Config");
    }

    private static IResult GetConfig(Settings settings)
    {
        return Results.Ok(new
        {
            appName = settings.Get("AppName", "WebCrawler"),
            pollIntervalMs = settings.GetInt("PollIntervalMs", 1500),
            historyPageSize = settings.GetInt("HistoryPageSize", 20),
            defaultMaxDepth = settings.GetInt("DefaultMaxDepth", 2),
            maxDepthAllowed = settings.GetInt("MaxDepthAllowed", 5),
            liveUpdates = settings.GetBool("LiveUpdates", true)
        });
    }

    private static IResult GetSites(Settings settings)
    {
        List<SiteRow> sites = SiteRow.GetSites(settings.GetPath("SitesFile", Path.Combine("Config", "sites.csv")));
        return Results.Ok(sites.Select(site => new { site.Name, site.Url }));
    }
}
