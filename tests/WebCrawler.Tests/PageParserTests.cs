// =============================================================================
// PageParserTests
// Link counts, DLR, zero-links case, bad hrefs, duplicates, title, <base>,
// <area>, links back to the same page, very long urls, start-site host, page tree.
// Fixed html strings only, no network.
// =============================================================================
using WebCrawler.Core.Models;
using WebCrawler.Core.Work.Pages;

namespace WebCrawler.Tests;

public class PageParserTests
{
    private static readonly Uri pageUri = new Uri("https://example.com/docs/");

    [Fact]
    public void SortPage_CountsInternalAndExternalLinks()
    {
        string html = "<a href='/about'>a</a>"
                    + "<a href='https://example.com/contact'>b</a>"
                    + "<a href='https://other.com'>c</a>";

        ParsedPage page = PageParser.SortPage(html, pageUri);

        Assert.Equal(2, page.InternalLinks);
        Assert.Equal(1, page.ExternalLinks);
        Assert.Equal(0.667, Math.Round(page.LinkRatio, 3));
    }

    [Fact]
    public void SortPage_IgnoresMailtoJavascriptAndEmptyLinks()
    {
        string html = "<a href='mailto:me@example.com'>m</a>"
                    + "<a href='tel:123'>t</a>"
                    + "<a href='javascript:void(0)'>j</a>"
                    + "<a href=''>empty</a>"
                    + "<a>no href</a>"
                    + "<a href='https://other.com'>ok</a>";

        ParsedPage page = PageParser.SortPage(html, pageUri);

        Assert.Equal(0, page.InternalLinks);
        Assert.Equal(1, page.ExternalLinks);
        Assert.Single(page.Links);
    }

    [Fact]
    public void SortPage_ResolvesRelativeLinksAgainstPageUrl()
    {
        ParsedPage page = PageParser.SortPage("<a href='intro'>i</a>", pageUri);

        Assert.Equal("https://example.com/docs/intro", page.Links[0].Url);
        Assert.True(page.Links[0].IsInternal);
    }

    [Fact]
    public void SortPage_CountsDuplicatesButStoresUniqueList()
    {
        string html = "<a href='/a'>1</a><a href='/a#top'>2</a><a href='/a/'>3</a><a href='/b'>4</a>";

        ParsedPage page = PageParser.SortPage(html, pageUri);

        Assert.Equal(4, page.InternalLinks);                    // every link counts as it appears
        Assert.Equal(2, page.Links.Count);                      // /a and /b
        Assert.Equal(3, page.Links.Single(l => l.Url.EndsWith("/a")).TimesFound);
    }

    [Fact]
    public void SortPage_ReadsAndCleansTitle()
    {
        ParsedPage page = PageParser.SortPage("<html><head><title>  Hello \n  World </title></head></html>", pageUri);

        Assert.Equal("Hello World", page.Title);
    }

    [Fact]
    public void SortPage_ZeroLinksGivesZeroRatio()
    {
        ParsedPage page = PageParser.SortPage("<p>no links here</p>", pageUri);

        Assert.Equal(0, page.InternalLinks + page.ExternalLinks);
        Assert.Equal(0, page.LinkRatio);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 0, 1)]
    [InlineData(0, 5, 0)]
    [InlineData(1, 3, 0.25)]
    public void GetLinkRatio_IsInternalOverAll(int internalLinks, int externalLinks, double expected)
    {
        Assert.Equal(expected, PageParser.GetLinkRatio(internalLinks, externalLinks));
    }

    [Fact]
    public void SortPage_ResolvesAgainstBaseHref()
    {
        string html = "<base href='https://example.com/shop/'><a href='item1'>x</a>";

        ParsedPage page = PageParser.SortPage(html, pageUri);

        Assert.Equal("https://example.com/shop/item1", page.Links[0].Url);
    }

    [Fact]
    public void SortPage_CountsAreaLinks()
    {
        string html = "<map><area href='/zone' alt='z'></map><a href='https://other.com'>o</a>";

        ParsedPage page = PageParser.SortPage(html, pageUri);

        Assert.Equal(1, page.InternalLinks);
        Assert.Equal(1, page.ExternalLinks);
    }

    [Fact]
    public void SortPage_CountsLinksBackToTheSamePage()
    {
        // #top, ./ and /docs all point at this page: still outgoing http(s) links (fragment dropped)
        string html = "<a href='#top'>t</a><a href='./'>s</a><a href='/docs'>same page</a><a href='/other'>o</a>";

        ParsedPage page = PageParser.SortPage(html, pageUri);

        Assert.Equal(4, page.InternalLinks);
        Assert.Equal(3, page.Links.Single(l => l.Url == "https://example.com/docs").TimesFound);
        Assert.Equal(2, page.Links.Count);
    }

    [Fact]
    public void SortPage_CountsVeryLongUrlsButDoesNotStoreThem()
    {
        string longUrl = "https://other.com/" + new string('x', PageLink.UrlMaxLength);
        string html = $"<a href='{longUrl}'>long</a><a href='/a'>a</a>";

        ParsedPage page = PageParser.SortPage(html, pageUri);

        Assert.Equal(1, page.InternalLinks);
        Assert.Equal(1, page.ExternalLinks);                    // counted in the DLR
        Assert.Equal("https://example.com/a", Assert.Single(page.Links).Url);
    }

    [Fact]
    public void SortPage_InternalMeansSameHostAsTheStartSite()
    {
        string html = "<a href='https://www.example.com/a'>a</a><a href='https://example.com/b'>b</a>";

        ParsedPage page = PageParser.SortPage(html, new Uri("https://www.example.com/"), "www.example.com");

        Assert.Equal(1, page.InternalLinks);
        Assert.Equal(1, page.ExternalLinks);
    }

    [Fact]
    public void SetUpTree_PutsChildrenUnderTheirParent()
    {
        List<PageResult> pages = new List<PageResult>
        {
            new PageResult { Id = 1, Url = "https://example.com/", Depth = 0 },
            new PageResult { Id = 2, Url = "https://example.com/a", Depth = 1, ParentId = 1 },
            new PageResult { Id = 3, Url = "https://example.com/a/b", Depth = 2, ParentId = 2 },
            new PageResult { Id = 4, Url = "https://example.com/c", Depth = 1, ParentId = 1 }
        };

        List<PageNode> roots = PageNode.SetUpTree(pages);

        Assert.Single(roots);
        Assert.Equal(2, roots[0].Children.Count);
        Assert.Equal("https://example.com/a/b", roots[0].Children[0].Children[0].Url);
    }

    [Theory]
    [InlineData("https://a.com/x", 308, "http://a.com/x/", "refused https -> http redirect to http://a.com/x/")]
    [InlineData("https://a.com/x", 301, "/y", "redirect not followed to https://a.com/y")]
    [InlineData("https://a.com/x", 404, null, "HTTP 404")]
    public void GetHttpError_ExplainsRedirectsThatWereNotFollowed(string url, int httpCode, string? location, string expected)
    {
        using HttpResponseMessage response = new HttpResponseMessage((System.Net.HttpStatusCode)httpCode);
        if (location != null)
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);

        Assert.EndsWith(expected, PageFetcher.GetHttpError(response, url));
    }
}
