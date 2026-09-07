using System.Text.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RayLink.App.Models;

public sealed class AppSettings : INotifyPropertyChanged
{
    public string TransportExecutable { get; set; } = "";
    private string _displayName = Environment.MachineName;
    private string _localEndpointId = "";
    private string _localEndpointAddress = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string LocalEndpointId { get => _localEndpointId; set => Set(ref _localEndpointId, value); }
    public string LocalEndpointAddress { get => _localEndpointAddress; set => Set(ref _localEndpointAddress, value); }

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
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
