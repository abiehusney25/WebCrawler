// =============================================================================
// PageFetcher
// DownloadPage: SSRF check -> HTTP GET (retries/timeouts from the resilience
// handler) -> html only -> size limit. Never throws for a bad page: it returns
// a FAIL FetchResult with the reason, so one bad page never stops a job.
// Not html / too big = Skipped (not an error). The handler refuses HTTPS -> HTTP
// redirects (.NET default), and every connection re-runs the SSRF check.
// =============================================================================
using System.Net;
using System.Net.Sockets;
using System.Text;
using WebCrawler.Core.Models;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace WebCrawler.Core.Work.Pages;

public interface IPageFetcher
{
    Task<FetchResult> DownloadPage(string url, CancellationToken cancel);
}

public class PageFetcher : IPageFetcher
{
    public const string ClientName = "web";

    private readonly IHttpClientFactory httpFactory;
    private readonly Settings settings;
    private readonly ILogger<PageFetcher> logger;

    public PageFetcher(IHttpClientFactory httpFactory, Settings settings, ILogger<PageFetcher> logger)
    {
        this.httpFactory = httpFactory;
        this.settings = settings;
        this.logger = logger;
    }

    // DownloadPage - fetch one page; the whole page (incl. retries) is capped by PageTimeoutSeconds
    public async Task<FetchResult> DownloadPage(string url, CancellationToken cancel)
    {
        FetchResult result = new FetchResult { FinalUrl = url };
        logger.LogInformation("BEGIN- URL: {Url}", url);

        using CancellationTokenSource pageTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        pageTimeout.CancelAfter(TimeSpan.FromSeconds(settings.GetInt("PageTimeoutSeconds", 45)));
        int maxBytes = settings.GetInt("MaxPageBytes", 2_000_000);

        try
        {
            string? blockReason = await CheckAddress(new Uri(url), pageTimeout.Token);
            if (blockReason != null)
                return Fail(result, blockReason);

            HttpClient client = httpFactory.CreateClient(ClientName);
            using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, pageTimeout.Token);
            result.HttpCode = (int)response.StatusCode;
            result.FinalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;

            if (!response.IsSuccessStatusCode)
                return Fail(result, GetHttpError(response, url));

            string? contentReason = CheckContent(response.Content.Headers, maxBytes);
            if (contentReason != null)
                return Fail(result, contentReason, skipped: true);

            string? html = await ReadLimited(response.Content, maxBytes, pageTimeout.Token);
            if (html == null)
                return Fail(result, $"Too big (over {maxBytes} bytes)", skipped: true);

            result.Html = html;
            result.Ok = true;
            return result;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return Fail(result, "Timeout");
        }
        catch (TimeoutRejectedException)
        {
            return Fail(result, "Timeout");
        }
        catch (BrokenCircuitException)
        {
            return Fail(result, "Host keeps failing (circuit open)");
        }
        catch (SocketException ex)
        {
            return Fail(result, "DNS lookup failed: " + ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Fail(result, "Request Exception: " + ex.Message);
        }
    }

    // GetHttpError - a 3xx that was not followed says why (https -> http downgrade is refused on purpose)
    public static string GetHttpError(HttpResponseMessage response, string url)
    {
        int httpCode = (int)response.StatusCode;
        Uri? location = response.Headers.Location;
        if (httpCode < 300 || httpCode > 399 || location == null)
            return "HTTP " + httpCode;
        Uri target = location.IsAbsoluteUri ? location : new Uri(new Uri(url), location);
        if (url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) && target.Scheme == Uri.UriSchemeHttp)
            return $"HTTP {httpCode}: refused https -> http redirect to {target.AbsoluteUri}";
        return $"HTTP {httpCode}: redirect not followed to {target.AbsoluteUri}";
    }

    // CheckAddress - SSRF guard before the request: resolve the host, refuse private addresses
    private async Task<string?> CheckAddress(Uri uri, CancellationToken cancel)
    {
        if (!settings.GetBool("BlockPrivateAddresses", true))
            return null;

        IPAddress[] addresses = IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancel);

        IPAddress? blocked = addresses.FirstOrDefault(address => !UrlRules.IsAllowedAddress(address));
        return blocked == null ? null : "Blocked private address " + blocked;
    }

    // CheckContent - html only, and refuse early when Content-Length is already over the limit
    public static string? CheckContent(System.Net.Http.Headers.HttpContentHeaders headers, int maxBytes)
    {
        string mediaType = headers.ContentType?.MediaType ?? "";
        if (mediaType != "text/html" && mediaType != "application/xhtml+xml")
            return "Not html (" + (mediaType == "" ? "no content type" : mediaType) + ")";
        if (headers.ContentLength > maxBytes)
            return $"Too big ({headers.ContentLength} bytes)";
        return null;
    }

    // ReadLimited - read the body but stop past maxBytes (Content-Length can be missing or wrong)
    private static async Task<string?> ReadLimited(HttpContent content, int maxBytes, CancellationToken cancel)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancel);
        using MemoryStream buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return GetEncoding(content.Headers.ContentType?.CharSet).GetString(buffer.ToArray());
    }

    private static Encoding GetEncoding(string? charset)
    {
        try
        {
            return string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;                                            // unknown charset name
        }
    }

    // SetUpHandler - the real socket handler: redirects capped, and every connection
    // re-checks the address so a redirect or DNS rebinding can't reach a private ip
    public static SocketsHttpHandler SetUpHandler(Settings settings)
    {
        SocketsHttpHandler handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = Math.Max(1, settings.GetInt("MaxRedirects", 5)),
            AutomaticDecompression = DecompressionMethods.All
        };
        if (settings.GetBool("BlockPrivateAddresses", true))
            handler.ConnectCallback = ConnectSafe;
        return handler;
    }

    private static async ValueTask<Stream> ConnectSafe(SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancel);
        IPAddress? blocked = addresses.FirstOrDefault(address => !UrlRules.IsAllowedAddress(address));
        if (blocked != null || addresses.Length == 0)
            throw new HttpRequestException("Blocked private address " + blocked);

        Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancel);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // Fail - one place to mark a fetch as failed, so every failure looks the same
    private FetchResult Fail(FetchResult result, string reason, bool skipped = false)
    {
        logger.LogWarning("FAIL {Url} {Reason}", result.FinalUrl, reason);
        result.Ok = false;
        result.Skipped = skipped;
        result.Error = reason;
        return result;
    }
}
