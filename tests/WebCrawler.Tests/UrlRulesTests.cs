// =============================================================================
// UrlRulesTests
// Normalize, same host, scheme filter and the SSRF guard. Names: Function_WhatItShouldDo.
// =============================================================================
using System.Net;
using WebCrawler.Core.Work.Pages;

namespace WebCrawler.Tests;

public class UrlRulesTests
{
    [Fact]
    public void Normalize_LowercasesHostDropsFragmentAndDefaultPort()
    {
        Assert.Equal("https://example.com/About", UrlRules.Normalize("HTTPS://Example.COM:443/About#team"));
        Assert.Equal("http://example.com:8080/", UrlRules.Normalize("http://example.com:8080"));
    }

    [Theory]
    [InlineData("https://bulbapedia.bulbagarden.net/wiki/Charizard_(Pok%C3%A9mon)")]
    [InlineData("https://bulbapedia.bulbagarden.net/wiki/Charizard_(Pokémon)")]
    [InlineData("https://bulbapedia.bulbagarden.net/wiki/Charizard_(Pok%c3%a9mon)")]
    public void Normalize_EncodedAndReadableUrlsAreTheSame(string url)
    {
        Assert.Equal("https://bulbapedia.bulbagarden.net/wiki/Charizard_(Pok%C3%A9mon)", UrlRules.Normalize(url));
    }

    [Fact]
    public void Normalize_KeepsRootSlashButTrimsOthers()
    {
        Assert.Equal("https://example.com/", UrlRules.Normalize("https://example.com"));
        Assert.Equal("https://example.com/docs", UrlRules.Normalize("https://example.com/docs/"));
    }

    [Fact]
    public void Normalize_KeepsQueryString()
    {
        Assert.Equal("https://example.com/search?q=edi&page=2", UrlRules.Normalize("https://example.com/search/?q=edi&page=2#top"));
    }

    [Fact]
    public void Normalize_ResolvesRelativeLinksAgainstPage()
    {
        Uri page = new Uri("https://example.com/docs/intro");
        Assert.Equal("https://example.com/docs/next", UrlRules.Normalize(page, "next"));
        Assert.Equal("https://example.com/about", UrlRules.Normalize(page, "/about"));
        Assert.Equal("https://cdn.other.com/x", UrlRules.Normalize(page, "//cdn.other.com/x"));
    }

    [Theory]
    [InlineData("mailto:me@example.com")]
    [InlineData("tel:+15551234")]
    [InlineData("javascript:void(0)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ftp://example.com/file")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void Normalize_ReturnsNullForNonHttpLinks(string href)
    {
        Assert.Null(UrlRules.Normalize(href));
    }

    [Fact]
    public void IsSameHost_IgnoresCaseButNotSubdomain()
    {
        Assert.True(UrlRules.IsSameHost(new Uri("https://Example.com/a"), new Uri("http://example.COM/b")));
        Assert.False(UrlRules.IsSameHost(new Uri("https://www.example.com"), new Uri("https://example.com")));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("93.184.215.14", true)]
    [InlineData("2606:4700::6810:84e5", true)]
    public void IsAllowedAddress_BlocksPrivateAndLocal(string ip, bool expected)
    {
        Assert.Equal(expected, UrlRules.IsAllowedAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("https://example.com", null)]
    [InlineData("http://localhost:5000", "local or private")]
    [InlineData("http://192.168.0.1/admin", "local or private")]
    [InlineData("http://[::1]/", "local or private")]
    [InlineData("ftp://example.com", "absolute http or https")]
    [InlineData("example.com", "absolute http or https")]
    [InlineData("", "required")]
    public void CheckUserUrl_ValidatesSchemeAndBlocksLocal(string input, string? expectedError)
    {
        string? error = UrlRules.CheckUserUrl(input, blockPrivate: true, out string normalizedUrl);

        if (expectedError == null)
        {
            Assert.Null(error);
            Assert.Equal("https://example.com/", normalizedUrl);
        }
        else
        {
            Assert.Contains(expectedError, error);
        }
    }

    [Fact]
    public void CheckUserUrl_AllowsLocalWhenGuardIsOff()
    {
        Assert.Null(UrlRules.CheckUserUrl("http://localhost:5000", blockPrivate: false, out _));
    }
}
