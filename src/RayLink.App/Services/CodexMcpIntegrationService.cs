using System.Text;
using System.Text.RegularExpressions;

namespace RayLink.App.Services;

public sealed class CodexMcpIntegrationService
{
    private static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
    public string Enable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var current = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : "";
        if (Regex.IsMatch(current, @"(?m)^\[mcp_servers\.agentlink\]\s*$")) return "Codex already has the AgentLink MCP. Start a new Codex session to use it.";
        if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigPath + ".agentlink-backup", true);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !Path.GetFileName(executable).Equals("AgentLink.exe", StringComparison.OrdinalIgnoreCase)) executable = Path.Combine(AppContext.BaseDirectory, "AgentLink.exe");
        var quotedExecutable = executable.Replace("\\", "\\\\").Replace("\"", "\\\"");
        File.AppendAllText(ConfigPath, $"{Environment.NewLine}[mcp_servers.agentlink]{Environment.NewLine}command = \"{quotedExecutable}\"{Environment.NewLine}args = [\"--mcp\"]{Environment.NewLine}", new UTF8Encoding(false));
        return "AgentLink has been added to Codex. Start a new Codex session, then call agentlink_register.";
    }
}
