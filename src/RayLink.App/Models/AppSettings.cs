using System.Text.Json;

namespace RayLink.App.Models;

public sealed class AppSettings
{
    public string RayExecutable { get; set; } = "";
    public string DisplayName { get; set; } = Environment.MachineName;
    public string LocalAddress { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public int Port { get; set; } = 42821;
    public string NetworkName { get; set; } = "team";

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RayLink",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
