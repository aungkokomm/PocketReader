using System.Text.Json;

namespace PocketReader.Services;

public class AppSettings
{
    public string Theme { get; set; } = "Default";   // "Default" (System) | "Light" | "Dark"
    public string ViewMode { get; set; } = "List";    // "List" | "Card"
    public int BatchSize { get; set; } = 150;          // articles cached per "Download Offline" click
    public int Concurrency { get; set; } = 8;          // parallel downloads
    public string LastSyncUpdate { get; set; } = "";   // watermark: newest lastUpdate seen (ISO 8601)
    public string ReaderTheme { get; set; } = "Light"; // reader content theme: "Light" | "Sepia" | "Dark"
    public string SortOrder { get; set; } = "DateDesc"; // "DateDesc" | "DateAsc" | "Title"
    public string Density { get; set; } = "Comfortable"; // "Comfortable" | "Compact"
}

public static class AppSettingsService
{
    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "data", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
