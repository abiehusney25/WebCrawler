// =============================================================================
// Settings
// Loads Config/settings.csv and Config/secrets.csv (Key,Value). Lookup order:
// environment variable WEBCRAWLER_<Key> -> CSV -> the fallback in code, so a
// missing or broken file never crashes the app and Docker can override anything.
// =============================================================================
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace WebCrawler.Core;

public class Settings
{
    public const string EnvPrefix = "WEBCRAWLER_";

    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    // RootFolder - the folder that holds Config/; every relative path is resolved against it
    public string RootFolder { get; set; } = Directory.GetCurrentDirectory();

    public string ConfigFolder => Path.Combine(RootFolder, "Config");
    public string SettingsFile => Path.Combine(ConfigFolder, "settings.csv");
    public string SecretsFile => Path.Combine(ConfigFolder, "secrets.csv");

    // CheckFiles - create missing folders and starter CSVs so a fresh clone always runs
    public void CheckFiles()
    {
        Directory.CreateDirectory(ConfigFolder);
        Directory.CreateDirectory(GetPath("OutputFolder", "Output"));

        if (!File.Exists(SettingsFile))
            File.WriteAllText(SettingsFile, "Key,Value\n# starter file, every key has a default in code\nAppName,WebCrawler\n");
    }

    // Load - read one Key,Value CSV into the dictionary (a later file wins)
    public void Load(string csvFile)
    {
        if (!File.Exists(csvFile))
        {
            Console.WriteLine("No settings file, using defaults: " + csvFile);
            return;
        }

        try
        {
            using StreamReader reader = new StreamReader(csvFile);
            using CsvReader csv = new CsvReader(reader, CsvSetup());
            int countRead = 0;
            foreach (SettingRow row in csv.GetRecords<SettingRow>())
            {
                if (row.Key.Trim() == "")
                    continue;
                values[row.Key.Trim()] = row.Value.Trim();
                countRead++;
            }
            Console.WriteLine($"Settings loaded: {countRead} from {csvFile}");
        }
        catch (Exception ex) when (ex is CsvHelperException or IOException)
        {
            Console.WriteLine("FAIL reading " + csvFile + ", using defaults: " + ex.Message);
        }
    }

    public string Get(string key, string fallback)
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(EnvPrefix + key);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();

        return values.TryGetValue(key, out string? value) && value != "" ? value : fallback;
    }

    public int GetInt(string key, int fallback)
    {
        return int.TryParse(Get(key, ""), out int number) ? number : fallback;
    }

    public bool GetBool(string key, bool fallback)
    {
        return bool.TryParse(Get(key, ""), out bool flag) ? flag : fallback;
    }

    // GetPath - a path setting made absolute against RootFolder (absolute values stay as they are)
    public string GetPath(string key, string fallback)
    {
        return Path.GetFullPath(Path.Combine(RootFolder, Get(key, fallback)));
    }

    // IsMode - true when a mode switch (DbMode, QueueMode, WorkerMode) has this value
    public bool IsMode(string key, string mode, string fallback)
    {
        return Get(key, fallback).Equals(mode, StringComparison.OrdinalIgnoreCase);
    }

    // Set - used by tests and code that needs to force a value
    public void Set(string key, string value)
    {
        values[key] = value;
    }

    // FindRootFolder - walk up from the current folder, then the app folder, until Config/settings.csv shows up
    public static string FindRootFolder()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? folder = new DirectoryInfo(start);
            while (folder != null)
            {
                if (File.Exists(Path.Combine(folder.FullName, "Config", "settings.csv")))
                    return folder.FullName;
                folder = folder.Parent;
            }
        }
        return Directory.GetCurrentDirectory();   // fresh folder: CheckFiles creates Config here
    }

    // CsvSetup - shared CSV options: allow # comments, don't fail on extra/missing columns
    public static CsvConfiguration CsvSetup()
    {
        return new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            AllowComments = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim
        };
    }

    private class SettingRow
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }
}
