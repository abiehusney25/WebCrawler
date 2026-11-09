// =============================================================================
// JobChecks
// Input validation at the edge: url format/scheme/SSRF and the depth range.
// Public static so tests can call them directly. Empty dictionary = all good;
// otherwise it becomes a 400 ValidationProblem with field -> messages.
// =============================================================================
using WebCrawler.Core;
using WebCrawler.Core.Models;
using WebCrawler.Core.Work.Pages;

namespace WebCrawler.Api.Endpoints;

public static class JobChecks
{
    // CheckUrlRequest - url format/scheme/SSRF + depth range
    public static Dictionary<string, string[]> CheckUrlRequest(CreateJobRequest request, Settings settings,
        out string inputUrl, out int maxDepth)
    {
        Dictionary<string, string[]> errors = new Dictionary<string, string[]>();
        string? urlError = UrlRules.CheckUserUrl(request.Url, settings.GetBool("BlockPrivateAddresses", true), out inputUrl);
        if (urlError != null)
            errors["url"] = new[] { urlError };

        int maxDepthAllowed = settings.GetInt("MaxDepthAllowed", 5);
        maxDepth = request.MaxDepth ?? settings.GetInt("DefaultMaxDepth", 2);
        if (maxDepth < 0 || maxDepth > maxDepthAllowed)
            errors["maxDepth"] = new[] { $"Max depth must be between 0 and {maxDepthAllowed}." };
        return errors;
    }
}
