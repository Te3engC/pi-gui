using System.IO;
using System.Text.Json;

namespace PiGui;

public sealed record UiPreferences(bool DarkTheme, string FontFamily, double FontSize, bool Bold, double WindowWidth, double WindowHeight)
{
    public double UiFontSize { get; init; } = 12;
    public static UiPreferences Default { get; } = new(false, "Microsoft YaHei UI", 14, false, 1200, 800);
}

public static class UiPreferencesStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiGui");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "ui-preferences.json");

    public static UiPreferences Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(FilePath));
            return settings is { UiFontSize: >= 11 and <= 16, FontSize: >= 10 and <= 32, WindowWidth: >= 860 and <= 5000, WindowHeight: >= 620 and <= 3000 } && !string.IsNullOrWhiteSpace(settings.FontFamily)
                ? settings
                : UiPreferences.Default;
        }
        catch (Exception)
        {
            return UiPreferences.Default;
        }
    }

    public static void Save(UiPreferences preferences)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, FilePath, overwrite: true);
    }
}
