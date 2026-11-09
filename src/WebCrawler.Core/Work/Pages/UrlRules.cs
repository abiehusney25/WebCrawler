// =============================================================================
// UrlRules
// Pure url logic, all public static so tests are trivial: normalize, http only,
// same host, and the SSRF guard (no loopback / private / link-local addresses).
// =============================================================================
using System.Net;
using System.Net.Sockets;
using WebCrawler.Core.Models;

namespace WebCrawler.Core.Work.Pages;

public static class UrlRules
{
    // Normalize - an absolute url in one standard form, or null if it is not a usable http(s) link
    public static string? Normalize(string url)
    {
        return Normalize(null, url);
    }

    // Normalize - resolve href against the page url first (relative links)
    public static string? Normalize(Uri? pageUri, string href)
    {
        href = href.Trim();
        if (href == "")
            return null;

        Uri? uri;
        bool parsed = pageUri == null
            ? Uri.TryCreate(href, UriKind.Absolute, out uri)
            : Uri.TryCreate(pageUri, href, out uri);
        if (!parsed || uri == null || !IsHttp(uri) || uri.Host == "")
            return null;                                                     // mailto:, javascript:, data:, ftp:, broken

        string path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : "/";   // keep "/" on the root only
        string schemeAndHost = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);   // lowercase, no default port
        return schemeAndHost + path + uri.Query;                             // fragment dropped, query kept
    }

    public static bool IsHttp(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    public static bool IsSameHost(Uri first, Uri second)
    {
        return string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase);
    }

    // CheckUserUrl - validate a url typed by the user; returns the error text, or null when ok
    public static string? CheckUserUrl(string? input, bool blockPrivate, out string normalizedUrl)
    {
        normalizedUrl = "";
        if (string.IsNullOrWhiteSpace(input))
            return "Url is required.";
        if (input.Length > Job.InputMaxLength)
            return $"Url is too long (max {Job.InputMaxLength} characters).";
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out Uri? uri) || !IsHttp(uri) || uri.Host == "")
            return "Url must be an absolute http or https address, like https://example.com.";
        if (blockPrivate && !IsAllowedHost(uri.Host))
            return "Url points to a local or private address, which is blocked.";

        normalizedUrl = Normalize(uri.AbsoluteUri) ?? "";
        return null;
    }

    // IsAllowedHost - quick check on the host text (names are checked again after DNS in PageFetcher)
    public static bool IsAllowedHost(string host)
    {
        host = host.Trim('[', ']').ToLowerInvariant();
        if (host == "localhost" || host.EndsWith(".localhost"))
            return false;
        return !IPAddress.TryParse(host, out IPAddress? address) || IsAllowedAddress(address);
    }

    // IsAllowedAddress - false for loopback, private, link-local (incl. cloud metadata), CGNAT, multicast
    public static bool IsAllowedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return false;

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bool isBlocked = bytes[0] == 0
                || bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)                     // link-local + 169.254.169.254 metadata
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)  // carrier-grade NAT
                || bytes[0] >= 224;                                         // multicast and reserved
            return !isBlocked;
        }

        bool isUniqueLocal = (bytes[0] & 0xFE) == 0xFC;                     // fc00::/7
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                 || isUniqueLocal || address.Equals(IPAddress.IPv6Any));
    }
}
