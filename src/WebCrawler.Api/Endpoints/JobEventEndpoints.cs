// =============================================================================
// JobEventEndpoints
// GET /api/jobs/{id}/events - live progress as server-sent events (SSE).
// The API checks the job every PollIntervalMs and pushes an "job" event only when
// something changed; the stream ends when the job is finished. One check per
// open screen, no extra broker or SignalR hub. The UI falls back to polling.
// =============================================================================
using System.Text.Json;
using WebCrawler.Core;
using WebCrawler.Core.Data;
using WebCrawler.Core.Models;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace WebCrawler.Api.Endpoints;

public static class JobEventEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/jobs/{id:guid}/events", StreamJob).WithTags("Jobs");
    }

    // StreamJob - send the job when it changes, until it is finished or the browser leaves
    private static async Task StreamJob(Guid id, HttpContext http, AppDbContext db, Settings settings,
        IOptions<JsonOptions> jsonOptions, CancellationToken cancel)
    {
        JobDto? job = await JobEndpoints.GetJobDto(id, db, cancel);
        if (job == null)
        {
            await JobEndpoints.NotFoundJob(id).ExecuteAsync(http);
            return;
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        TimeSpan interval = TimeSpan.FromMilliseconds(Math.Max(250, settings.GetInt("PollIntervalMs", 1500)));
        string lastSent = "";

        try
        {
            while (job != null)
            {
                string json = JsonSerializer.Serialize(job, jsonOptions.Value.SerializerOptions);
                if (json != lastSent)
                {
                    await http.Response.WriteAsync("event: job\ndata: " + json + "\n\n", cancel);
                    await http.Response.Body.FlushAsync(cancel);
                    lastSent = json;
                }
                if (job.Status is not (JobStatus.Pending or JobStatus.Running))
                    break;                                                  // finished: the browser closes the stream

                await Task.Delay(interval, cancel);
                job = await JobEndpoints.GetJobDto(id, db, cancel);
            }
        }
        catch (OperationCanceledException)
        {
            // the browser left the page; nothing to clean up
        }
    }
}
