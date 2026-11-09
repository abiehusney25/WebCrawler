// =============================================================================
// JobApiTests
// The real API in memory (WebApplicationFactory): create -> 202 + id, bad input
// -> 400, unknown id -> 404, history paging, cancel. Uses a temp SQLite file and
// WorkerMode=Separate so no worker runs and no network is touched.
// =============================================================================
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WebCrawler.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebCrawler.Tests;

[Collection("Api")]                                            // env vars are process-wide: api test classes run one at a time
public class JobApiTests : IClassFixture<JobApiTests.ApiFactory>
{
    private readonly HttpClient client;

    public JobApiTests(ApiFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateJob_ReturnsAcceptedWithIdAndLocation()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/jobs", new { url = "https://example.com", maxDepth = 1 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        string id = body.GetProperty("jobId").GetString()!;
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal("/api/jobs/" + id, response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com")]
    [InlineData("http://localhost:5000")]
    [InlineData("")]
    public async Task CreateJob_RejectsBadUrlWith400(string url)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/jobs", new { url });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("url", out _));
    }

    [Fact]
    public async Task CreateJob_RejectsDepthOutOfRange()
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/jobs", new { url = "https://example.com", maxDepth = 99 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetJob_ReturnsNotFoundForUnknownId()
    {
        HttpResponseMessage response = await client.GetAsync("/api/jobs/" + Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetJobs_ReturnsNewestFirstWithPaging()
    {
        for (int i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/page" + i });

        JsonElement history = await client.GetFromJsonAsync<JsonElement>("/api/jobs?page=1&pageSize=2");

        JsonElement items = history.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.True(history.GetProperty("totalCount").GetInt32() >= 3);
        Assert.True(items[0].GetProperty("createdAt").GetDateTime() >= items[1].GetProperty("createdAt").GetDateTime());
    }

    [Fact]
    public async Task CancelJob_CancelsQueuedJobThenReturns409()
    {
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/cancel" });
        string id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        HttpResponseMessage first = await client.PostAsync($"/api/jobs/{id}/cancel", null);
        HttpResponseMessage second = await client.PostAsync($"/api/jobs/{id}/cancel", null);
        JsonElement job = await client.GetFromJsonAsync<JsonElement>("/api/jobs/" + id);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("Canceled", job.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetJob_ReturnsUrlPageCountsAndProgress()
    {
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/counts" });
        string id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;

        JsonElement job = await client.GetFromJsonAsync<JsonElement>("/api/jobs/" + id);

        Assert.Equal("https://example.com/counts", job.GetProperty("url").GetString());
        Assert.Equal(2, job.GetProperty("maxDepth").GetInt32());                               // default depth
        Assert.Equal(0, job.GetProperty("pageCounts").GetProperty("total").GetInt32());
        Assert.Equal(0, job.GetProperty("progress").GetDouble());
    }

    [Fact]
    public async Task GetTreeAndLinks_ReturnNotFoundForUnknownIds()
    {
        HttpResponseMessage tree = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}/tree");
        HttpResponseMessage links = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}/pages/123/links");

        Assert.Equal(HttpStatusCode.NotFound, tree.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, links.StatusCode);
    }

    [Fact]
    public async Task JobEvents_StreamsTheJobAndEndsWhenFinished()
    {
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/events" });
        string id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
        await client.PostAsync($"/api/jobs/{id}/cancel", null);

        HttpResponseMessage response = await client.GetAsync($"/api/jobs/{id}/events");
        string body = await response.Content.ReadAsStringAsync();                             // returns: the stream ended

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("event: job\ndata: {", body);
        Assert.Contains("\"status\":\"Canceled\"", body);
    }

    [Fact]
    public async Task JobEvents_ReturnsNotFoundForUnknownJob()
    {
        HttpResponseMessage response = await client.GetAsync($"/api/jobs/{Guid.NewGuid()}/events");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_ReturnOk(string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReportsDbAndQueueOk()
    {
        JsonElement health = await client.GetFromJsonAsync<JsonElement>("/api/health");

        Assert.Equal("ok", health.GetProperty("status").GetString());
    }

    // ApiFactory - environment variables win over settings.csv, so the test gets its own db
    public class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string testFolder = Path.Combine(Path.GetTempPath(), "webcrawler_api_" + Guid.NewGuid().ToString("N"));

        public ApiFactory()
        {
            Environment.SetEnvironmentVariable("WEBCRAWLER_DbMode", "Sqlite");
            Environment.SetEnvironmentVariable("WEBCRAWLER_QueueMode", "InMemory");
            Environment.SetEnvironmentVariable("WEBCRAWLER_WorkerMode", "Separate");
            Environment.SetEnvironmentVariable("WEBCRAWLER_SqliteFile", Path.Combine(testFolder, "test.db"));
            Environment.SetEnvironmentVariable("WEBCRAWLER_CreateJobsPerMinute", "1000");
        }
    }
}

// RateLimitTests - its own app with CreateJobsPerMinute=2: the third create in a minute gets 429
[Collection("Api")]
public class RateLimitTests : IClassFixture<RateLimitTests.LimitedApiFactory>
{
    private readonly HttpClient client;

    public RateLimitTests(LimitedApiFactory factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateJob_Returns429PastTheLimit()
    {
        HttpResponseMessage first = await client.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/1" });
        HttpResponseMessage second = await client.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/2" });
        HttpResponseMessage third = await client.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/3" });
        HttpResponseMessage history = await client.GetAsync("/api/jobs");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);                                   // only creating is limited
    }

    public class LimitedApiFactory : JobApiTests.ApiFactory
    {
        public LimitedApiFactory()
        {
            Environment.SetEnvironmentVariable("WEBCRAWLER_CreateJobsPerMinute", "2");
        }
    }
}
