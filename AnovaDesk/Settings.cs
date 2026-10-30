using System.IO;
using System.Text.Json;

namespace AnovaDesk;

/// <summary>
/// Non-secret UI preferences (%APPDATA%\AnovaDesk\settings.json).
/// The access token deliberately lives in Windows Credential Manager, not here.
/// </summary>
internal static class Settings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnovaDesk");

    private static string FilePath => Path.Combine(Folder, AppRuntime.SettingsFileName);

    public static string? LastCookerId { get; set; }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath));
            LastCookerId = model?.LastCookerId;
        }
        catch
        {
            // Preferences are optional; a corrupt file must never block startup.
        }
    }

    public static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Model { LastCookerId = LastCookerId }));
        }
        catch
        {
            // Ignore: losing a UI preference is not worth interrupting a cook.
        }
    }

    private sealed class Model
    {
        public string? LastCookerId { get; set; }
    }
}
