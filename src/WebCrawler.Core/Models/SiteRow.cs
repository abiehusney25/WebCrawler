// =============================================================================
// SiteRow
// One row from Config/sites.csv (sample inputs for the UI), with an Enabled switch.
// =============================================================================
using CsvHelper;

namespace WebCrawler.Core.Models;

public class SiteRow
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;

    // GetSites - enabled rows only; a missing file is an empty list, not a crash
    public static List<SiteRow> GetSites(string sitesFile)
    {
        if (!File.Exists(sitesFile))
            return new List<SiteRow>();

        using StreamReader reader = new StreamReader(sitesFile);
        using CsvReader csv = new CsvReader(reader, Settings.CsvSetup());
        return csv.GetRecords<SiteRow>().Where(site => site.Enabled && site.Url != "").ToList();
    }
}
