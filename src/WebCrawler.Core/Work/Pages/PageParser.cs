// =============================================================================
// PageParser
// SortPage: pull the title and links out of html (AngleSharp, not Regex), count
// internal vs external and work out the Domain Link Ratio (internal / all).
// Links come from <a href> and <area href>, resolved against <base href> or the
// page's final url. Self-links (#top, a link to the page itself) are not counted.
// Every other link counts as it appears; the stored list is unique with TimesFound.
// =============================================================================
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using WebCrawler.Core.Models;

namespace WebCrawler.Core.Work.Pages;

public static class PageParser
{
    // SortPage - internal = same host as the page itself (used when there is no start site)
    public static ParsedPage SortPage(string html, Uri pageUri)
    {
        return SortPage(html, pageUri, pageUri.Host);
    }

    // SortPage - title + links of one page; pageUri must be the FINAL url (after redirects),
    // siteHost is the start site's host (internal = same host as the start site)
    public static ParsedPage SortPage(string html, Uri pageUri, string siteHost)
    {
        return SortPage(html, pageUri, new[] { siteHost });
    }

    // SortPage - siteHosts = the starting domain: the job url's host, plus the start page's
    // final host when it redirected (example.com -> www.example.com); internal = any of them
    public static ParsedPage SortPage(string html, Uri pageUri, IReadOnlyCollection<string> siteHosts)
    {
        IDocument document = new HtmlParser().ParseDocument(html);
        ParsedPage page = new ParsedPage { Title = Shorten(document.Title ?? "", PageResult.TitleMaxLength) };
        Dictionary<string, PageLink> uniqueLinks = new Dictionary<string, PageLink>();
        Uri baseUri = GetBaseUri(document, pageUri);

        foreach (IElement anchor in document.QuerySelectorAll("a[href], area[href]"))
        {
            string? linkUrl = UrlRules.Normalize(baseUri, anchor.GetAttribute("href") ?? "");
            if (linkUrl == null)
                continue;                                                    // mailto:, tel:, javascript:, broken

            // every http(s) link counts, links back to this page (#top) included
            bool isInternal = siteHosts.Contains(new Uri(linkUrl).Host, StringComparer.OrdinalIgnoreCase);
            if (isInternal)
                page.InternalLinks++;
            else
                page.ExternalLinks++;

            if (linkUrl.Length > PageLink.UrlMaxLength)
                continue;                                                    // counted, but too long to store or crawl
            if (uniqueLinks.TryGetValue(linkUrl, out PageLink? seen))
                seen.TimesFound++;
            else
                uniqueLinks[linkUrl] = new PageLink { Url = linkUrl, IsInternal = isInternal };
        }

        page.Links = uniqueLinks.Values.ToList();
        page.LinkRatio = GetLinkRatio(page.InternalLinks, page.ExternalLinks);
        return page;
    }

    // GetBaseUri - <base href> wins over the page url when it is a usable http(s) address
    public static Uri GetBaseUri(IDocument document, Uri pageUri)
    {
        string baseHref = document.QuerySelector("base[href]")?.GetAttribute("href")?.Trim() ?? "";
        if (baseHref != "" && Uri.TryCreate(pageUri, baseHref, out Uri? baseUri) && UrlRules.IsHttp(baseUri))
            return baseUri;
        return pageUri;
    }

    // GetLinkRatio - internal / all; 0 when the page has no links (documented choice)
    public static double GetLinkRatio(int internalLinks, int externalLinks)
    {
        int allLinks = internalLinks + externalLinks;
        return allLinks == 0 ? 0 : (double)internalLinks / allLinks;
    }

    // Shorten - keep text within a db column size; collapses whitespace from the html
    public static string Shorten(string text, int maxLength)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= maxLength ? text : text.Substring(0, maxLength);
    }
}
