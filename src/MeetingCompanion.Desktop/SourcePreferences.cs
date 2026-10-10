using System.IO;
using System.Text.Json;
using MeetingCompanion.Platform;

namespace MeetingCompanion.Desktop;

/// <summary>Only operator settings persist; never transcripts, snapshots, audio or API credentials.</summary>
public sealed class SourcePreferences
{
    public CallSourceKind Kind { get; set; }
    public string? MicrophoneId { get; set; }
    public string? LastSource { get; set; }
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cintra", "source-preferences.json");
    public static SourcePreferences Load()
    {
        try
        {
            if (new FileInfo(SettingsPath).Length > 4096) return new();
            var settings = JsonSerializer.Deserialize<SourcePreferences>(File.ReadAllText(SettingsPath));
            return settings is not null && Enum.IsDefined(settings.Kind) && settings.MicrophoneId?.Length is not > 1024 && settings.LastSource?.Length is not > 512 ? settings : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Selection still works in memory. */ }
    }
}
