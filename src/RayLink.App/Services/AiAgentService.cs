using System.Text.Json;
using RayLink.App.Models;

namespace RayLink.App.Services;

// Shared by the desktop and its --mcp stdio processes. Never discovers OS processes.
public sealed class AiAgentService
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(35);
    private static string DirectoryPath => Environment.GetEnvironmentVariable("AGENTLINK_MCP_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RayLink", "mcp");

    private static T Access<T>(Func<Registry, T> action)
    {
        Directory.CreateDirectory(DirectoryPath);
        var lockPath = Path.Combine(DirectoryPath, "registry.lock");
        FileStream? gate = null;
        for (var attempt = 0; gate is null; attempt++)
        {
            try { gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(10); }
        }
        using (gate)
        {
            var path = Path.Combine(DirectoryPath, "registry.json");
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize<Registry>(File.ReadAllText(path)) ?? throw new IOException("MCP 注册数据为空。")
                : new Registry();
            var result = action(state);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, path, true);
            return result;
        }
    }

    public IReadOnlyList<AgentProfile> Discover() => Access(state => state.Agents.Values
        .Where(IsLive).OrderBy(a => a.Id, StringComparer.Ordinal)
        .Select(a => new AgentProfile(a.Id, a.Provider, a.Name, "", "") { IsOnline = true }).ToList());

    private static bool IsLive(Registration agent) => DateTimeOffset.UtcNow - agent.LastSeen < Lease;

    public void Register(string owner, string id, string provider, string name) => Access(state =>
    {
        if (state.Agents.TryGetValue(id, out var existing) && IsLive(existing) && existing.Owner != owner)
            throw new InvalidOperationException("此 instance_id 已被另一个 MCP 会话使用，请为不同实例设置不同 ID。");
        // One instance per MCP connection; changing ID releases its old registration.
        foreach (var key in state.Agents.Where(x => x.Value.Owner == owner && x.Key != id).Select(x => x.Key).ToArray())
            state.Agents.Remove(key);
        state.Agents[id] = new Registration { Id = id, Owner = owner, Provider = provider, Name = name, LastSeen = DateTimeOffset.UtcNow };
        return true;
    });

    public void Heartbeat(string owner) => Access(state =>
    {
        foreach (var agent in state.Agents.Values.Where(a => a.Owner == owner)) agent.LastSeen = DateTimeOffset.UtcNow;
        return true;
    });

    public void Unregister(string owner) => Access(state =>
    {
        foreach (var id in state.Agents.Where(x => x.Value.Owner == owner).Select(x => x.Key).ToArray()) state.Agents.Remove(id);
        return true;
    });

    public IReadOnlyList<AgentMessage> ReadMessages(string id) => Access(state =>
        state.Messages.TryGetValue(id, out var messages) ? messages.ToList() : new List<AgentMessage>());

    public AgentMessage Send(string id, string text) => Access(state =>
    {
        if (!state.Agents.TryGetValue(id, out var agent) || !IsLive(agent))
            throw new InvalidOperationException("Agent 的 MCP 会话已断开，请重新注册后再发送。");
        return AddMessage(state, id, text, true);
    });

    public IReadOnlyList<AgentMessage> Receive(string owner, long after) => Access(state =>
    {
        var agent = RequireAgent(state, owner);
        agent.LastSeen = DateTimeOffset.UtcNow;
        return state.Messages.TryGetValue(agent.Id, out var messages)
            ? messages.Where(m => m.IsLocal && m.Sequence > after).ToList() : new List<AgentMessage>();
    });

    public AgentMessage Reply(string owner, string text) => Access(state =>
    {
        var agent = RequireAgent(state, owner);
        agent.LastSeen = DateTimeOffset.UtcNow;
        return AddMessage(state, agent.Id, text, false);
    });

    private static Registration RequireAgent(Registry state, string owner) =>
        state.Agents.Values.FirstOrDefault(a => a.Owner == owner)
        ?? throw new InvalidOperationException("请先调用 agentlink_register 注册当前 Agent 实例。");

    private static AgentMessage AddMessage(Registry state, string id, string text, bool local)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32000) throw new ArgumentException("消息长度必须在 1–32000 字符之间。");
        if (!state.Messages.TryGetValue(id, out var messages)) state.Messages[id] = messages = [];
        var message = new AgentMessage(++state.Sequence, text, DateTimeOffset.UtcNow, local);
        messages.Add(message);
        if (messages.Count > 1000) messages.RemoveRange(0, messages.Count - 1000);
        return message;
    }

    public sealed class Registry
    {
        public Dictionary<string, Registration> Agents { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<AgentMessage>> Messages { get; set; } = new(StringComparer.Ordinal);
        public long Sequence { get; set; }
    }
    public sealed class Registration
    {
        public string Id { get; set; } = "";
        public string Owner { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTimeOffset LastSeen { get; set; }
    }
}

public sealed record AgentMessage(long Sequence, string Text, DateTimeOffset Timestamp, bool IsLocal)
{
    public string Sender => IsLocal ? "我" : "Agent";
    public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss");
}
