using System.Diagnostics;

namespace RayLink.App.Services;

public sealed record InstalledDesktopClient(string Id, string Name, string Command, string Source, bool IsInjected);

public sealed class DesktopClientScanner
{
    private static readonly (string Id,string Name,string Command)[] Known =
    [ ("claude-code", "Claude Code", "claude"), ("cursor", "Cursor", "cursor-agent"), ("codex", "Codex", "codex") ];
    public static bool IsMcpConfigured(string provider)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = provider.Contains("Codex", StringComparison.OrdinalIgnoreCase) ? Path.Combine(home, ".codex", "config.toml")
            : provider.Contains("Claude", StringComparison.OrdinalIgnoreCase) ? Path.Combine(home, ".claude.json")
            : provider.Contains("Cursor", StringComparison.OrdinalIgnoreCase) ? Path.Combine(home, ".cursor", "mcp.json") : null;
        if (path == null || !File.Exists(path)) return false;
        try
        {
            var text = File.ReadAllText(path);
            if (path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                return System.Text.RegularExpressions.Regex.IsMatch(text, @"(?m)^\[mcp_servers\.agentlink\]\s*$");
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty("mcpServers", out var servers)
                && servers.ValueKind == System.Text.Json.JsonValueKind.Object
                && servers.TryGetProperty("agentlink", out var entry)
                && entry.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch { return false; }
    }
    public IReadOnlyList<InstalledDesktopClient> Scan()
    {
        var result = new List<InstalledDesktopClient>();
        foreach (var (id,name,command) in Known)
        {
            try { if (Process.Start(new ProcessStartInfo("where.exe", command) { RedirectStandardOutput=true, UseShellExecute=false, CreateNoWindow=true }) is { } p) { p.WaitForExit(1500); var path=p.StandardOutput.ReadLine(); if (!string.IsNullOrWhiteSpace(path)) { var cfg = id == "claude-code" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json") : id == "cursor" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "mcp.json") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml"); result.Add(new(id,name,path.Trim(),"PATH", File.Exists(cfg) && File.ReadAllText(cfg).Contains("agentlink", StringComparison.OrdinalIgnoreCase))); } } } catch { }
        }
        return result;
    }
}
