using System.Text.Json;
using System.Text.Json.Nodes;

namespace RayLink.App.Services;

/// <summary>Writes the standard stdio MCP entry for clients that use JSON config.</summary>
public sealed class McpClientIntegrationService
{
    public sealed record ClientDefinition(string Name, string ConfigPath, string ServersProperty = "mcpServers");

    public static IReadOnlyList<ClientDefinition> SupportedClients => new[]
    {
        new ClientDefinition("Claude Code", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")),
        new ClientDefinition("Cursor", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "mcp.json"))
    };
    private static string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path) || !Path.GetFileName(path).Equals("AgentLink.exe", StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(AppContext.BaseDirectory, "AgentLink.exe");
            return path;
        }
    }

    public string EnableClaudeCode() => Enable("Claude Code");

    public string EnableCursor() => Enable("Cursor");

    public string Enable(string clientName)
    {
        var definition = SupportedClients.FirstOrDefault(x => x.Name.Equals(clientName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"未配置 MCP 客户端：{clientName}");
        return EnableJsonClient(definition.ConfigPath, definition.Name, definition.ServersProperty);
    }

    private static string EnableJsonClient(string configPath, string clientName, string serversProperty)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        JsonObject root;
        if (File.Exists(configPath))
        {
            try { root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { throw new InvalidOperationException($"无法解析 {clientName} 配置文件：{configPath}"); }
            File.Copy(configPath, configPath + ".agentlink-backup", true);
        }
        else root = new JsonObject();

        var servers = root[serversProperty] as JsonObject;
        if (servers is null) root[serversProperty] = servers = new JsonObject();
        servers["agentlink"] = new JsonObject
        {
            ["command"] = ExecutablePath,
            ["args"] = new JsonArray("--mcp")
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(configPath, root.ToJsonString(options));
        return $"已将 AgentLink MCP 写入 {clientName} 配置：{configPath}。请重启或新建一个 {clientName} 会话。";
    }
}
