using System.Text.Json;

namespace RayLink.App.Models;

public sealed class AppSettings
{
    public string TransportExecutable { get; set; } = "";
    public string DisplayName { get; set; } = Environment.MachineName;
    public string LocalEndpointId { get; set; } = "";
    public string LocalEndpointAddress { get; set; } = "";
    public string RemoteEndpointAddress { get; set; } = "";

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RayLink", "settings.json");

    public string GetIdentityKeyPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RayLink", "iroh-secret-key");

    public static AppSettings Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings() : new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
