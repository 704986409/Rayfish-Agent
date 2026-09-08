using System.Text.Json;
using System.Diagnostics;
using RayLink.App.Models;

namespace RayLink.App.Services;

// Persistent local broker shared by the desktop process and its --mcp children.
// It deliberately never discovers operating-system processes or executes messages.
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
            catch (IOException) when (attempt < 50) { Thread.Sleep(10); }
        }
        using (gate)
        {
            var path = Path.Combine(DirectoryPath, "registry.json");
            var state = File.Exists(path) ? JsonSerializer.Deserialize<Registry>(File.ReadAllText(path)) ?? new Registry() : new Registry();
            foreach (var item in state.Agents) if (string.IsNullOrWhiteSpace(item.Value.InstanceId)) item.Value.InstanceId = item.Key;
            var result = action(state);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, path, true);
            return result;
        }
    }

    public void ConfigureNode(string nodeId, string nodeName) => Access(state =>
    {
        if (!string.Equals(state.NodeId, nodeId, StringComparison.Ordinal)) { state.NodeId = nodeId; state.DirectoryVersion++; }
        state.NodeName = nodeName; return true;
    });

    public IReadOnlyList<AgentProfile> Discover() => Access(state =>
    {
        var agents = state.Agents.Values.Where(IsLive).OrderBy(a => a.InstanceId, StringComparer.Ordinal).Select(a => ToProfile(state, a)).ToList();
        foreach (var process in Process.GetProcessesByName("codex"))
        {
            if (agents.Any(a => a.Provider == "Codex")) break;
            agents.Add(new AgentProfile($"codex-process-{process.Id}", "Codex", "Codex", "已检测到，尚未接入 AgentLink", "请点击激活，创建一个 AgentLink Codex 任务。") { IsOnline = true, CanActivate = true });
        }
        return agents;
    });
    public IReadOnlyList<DirectoryAgent> ListAgents(string owner) => Access(state => ListAgentsCore(state, owner).OrderBy(a => a.AgentId, StringComparer.Ordinal).ToList());
    public DirectoryAgent GetAgent(string owner, string agentId) => Access(state => ListAgentsCore(state, owner).FirstOrDefault(a => a.AgentId == agentId) ?? throw new InvalidOperationException("找不到此 Agent 或尚未获授权访问。"));

    public string Register(string owner, string instanceId, string provider, string name, string role = "", IReadOnlyList<string>? capabilities = null) => Access(state =>
    {
        if (state.Agents.TryGetValue(instanceId, out var existing) && IsLive(existing) && existing.Owner != owner) throw new InvalidOperationException("此 instance_id 已被另一个 MCP 会话使用，请为不同实例设置不同 ID。");
        foreach (var key in state.Agents.Where(x => x.Value.Owner == owner && x.Key != instanceId).Select(x => x.Key).ToArray()) state.Agents.Remove(key);
        state.Agents[instanceId] = new Registration { InstanceId = instanceId, Owner = owner, Provider = provider, Name = name, Role = role, Capabilities = capabilities?.Distinct(StringComparer.Ordinal).ToList() ?? [], Shared = existing?.Shared ?? false, LastSeen = DateTimeOffset.UtcNow };
        state.DirectoryVersion++; return LocalAgentId(state, instanceId);
    });

    // A desktop installation represents one local Codex Agent. New MCP
    // connections take over this stable entry instead of creating duplicate
    // cards for every Codex task or reconnect.
    public string RegisterDefaultCodexSession(string owner, string name) => Access(state =>
    {
        const string instanceId = "codex-mcp-client";
        var previous = state.Agents.GetValueOrDefault(instanceId);
        foreach (var key in state.Agents.Where(x => x.Value.Owner == owner && x.Key != instanceId).Select(x => x.Key).ToArray()) state.Agents.Remove(key);
        state.Agents[instanceId] = new Registration
        {
            InstanceId = instanceId,
            Owner = owner,
            Provider = "Codex",
            Name = string.IsNullOrWhiteSpace(name) ? "Codex" : name,
            Role = previous?.Role ?? "",
            Capabilities = previous?.Capabilities ?? [],
            Shared = previous?.Shared ?? false,
            LastSeen = DateTimeOffset.UtcNow
        };
        state.DirectoryVersion++;
        return LocalAgentId(state, instanceId);
    });

    public bool HasLiveCodexAgent() => Access(state => state.Agents.Values.Any(a => IsLive(a) && string.Equals(a.Provider, "Codex", StringComparison.OrdinalIgnoreCase)));

    public void SetSharing(string owner, bool shared) => Access(state => { var agent = RequireAgent(state, owner); agent.Shared = shared; state.DirectoryVersion++; return true; });
    public void Heartbeat(string owner) => Access(state => { foreach (var a in state.Agents.Values.Where(a => a.Owner == owner)) a.LastSeen = DateTimeOffset.UtcNow; return true; });
    public void Unregister(string owner) => Access(state => { foreach (var id in state.Agents.Where(x => x.Value.Owner == owner).Select(x => x.Key).ToArray()) state.Agents.Remove(id); state.DirectoryVersion++; return true; });
    public IReadOnlyList<AgentMessage> ReadMessages(string instanceId) => Access(state => state.Messages.TryGetValue(instanceId, out var messages) ? messages.ToList() : []);
    public AgentMessage Send(string instanceId, string text) => Access(state => { if (!state.Agents.TryGetValue(instanceId, out var agent) || !IsLive(agent)) throw new InvalidOperationException("Agent 的 MCP 会话已断开，请重新注册后再发送。"); return AddIncoming(state, instanceId, text, "desktop", "", ""); });
    public IReadOnlyList<AgentMessage> Receive(string owner, long after) => Access(state => { var agent = RequireAgent(state, owner); agent.LastSeen = DateTimeOffset.UtcNow; return state.Messages.TryGetValue(agent.InstanceId, out var messages) ? messages.Where(m => m.Sequence > after).ToList() : []; });

    public AgentMessage SendToAgent(string owner, string targetAgentId, string text, string conversationId, string? replyTo = null) => Access(state =>
    {
        var sender = RequireAgent(state, owner); sender.LastSeen = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(state.NodeId)) throw new InvalidOperationException("本机 Iroh 节点尚未启动。");
        var target = state.RemoteAgents.GetValueOrDefault(targetAgentId);
        if (target is null || !target.Reachable || !target.CanMessage) throw new InvalidOperationException("目标远程 Agent 不可达或未授予消息权限。");
        return QueueOutbound(state, sender, targetAgentId, text, conversationId, replyTo ?? "");
    });

    public AgentMessage Reply(string owner, string text) => Access(state =>
    {
        var agent = RequireAgent(state, owner); agent.LastSeen = DateTimeOffset.UtcNow;
        var last = state.Messages.TryGetValue(agent.InstanceId, out var messages) ? messages.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.FromAgentId)) : null;
        return last is null ? AddIncoming(state, agent.InstanceId, text, "agent", "", "") : QueueOutbound(state, agent, last.FromAgentId, text, last.ConversationId, last.MessageId);
    });

    public DirectorySnapshot GetSharedSnapshot() => Access(state => new DirectorySnapshot(state.NodeId, state.NodeName, state.DirectoryVersion, state.Agents.Values.Where(a => IsLive(a) && a.Shared).Select(a => ToDirectoryAgent(state, a, "remote")).ToList()));
    public string LocalNodeId() => Access(state => state.NodeId);
    public void TrustNode(string nodeId, string displayName) => Access(state => { state.TrustedNodes[nodeId] = displayName; return true; });
    public void RevokeTrust(string nodeId) => Access(state => { state.TrustedNodes.Remove(nodeId); foreach (var id in state.RemoteAgents.Where(x => x.Value.NodeId == nodeId).Select(x => x.Key).ToArray()) state.RemoteAgents.Remove(id); return true; });
    public bool IsTrusted(string nodeId) => Access(state => state.TrustedNodes.ContainsKey(nodeId));
    public void ApplyRemoteSnapshot(string nodeId, DirectorySnapshot snapshot) => Access(state =>
    {
        if (!state.TrustedNodes.ContainsKey(nodeId) || snapshot.NodeId != nodeId) return false;
        foreach (var id in state.RemoteAgents.Where(x => x.Value.NodeId == nodeId).Select(x => x.Key).ToArray()) state.RemoteAgents.Remove(id);
        foreach (var agent in snapshot.Agents.Where(a => a.NodeId == nodeId && a.Shared)) state.RemoteAgents[agent.AgentId] = agent with { Location = "remote", Reachable = true };
        return true;
    });
    public void MarkNodeDisconnected(string nodeId) => Access(state => { foreach (var item in state.RemoteAgents.Where(x => x.Value.NodeId == nodeId).ToArray()) state.RemoteAgents[item.Key] = item.Value with { Reachable = false }; return true; });
    public IReadOnlyList<OutboundMessage> TakeOutbound(string nodeId) => Access(state => state.Outbound.Where(x => x.ToAgentId.StartsWith(nodeId + "/", StringComparison.Ordinal) && !x.Delivered).ToList());
    public void MarkDelivered(string messageId) => Access(state => { var item = state.Outbound.FirstOrDefault(x => x.MessageId == messageId); if (item is not null) item.Delivered = true; return true; });
    public bool ReceiveRemoteMessage(string nodeId, RoutedMessage incoming) => Access(state =>
    {
        if (!state.TrustedNodes.ContainsKey(nodeId) || incoming.ToAgentId.StartsWith(nodeId + "/", StringComparison.Ordinal)) return false;
        var prefix = state.NodeId + "/";
        if (!incoming.ToAgentId.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var instanceId = incoming.ToAgentId[prefix.Length..];
        if (!state.Agents.TryGetValue(instanceId, out var target) || !IsLive(target) || !target.Shared) return false;
        if (state.SeenMessageIds.Contains(incoming.MessageId)) return true;
        state.SeenMessageIds.Add(incoming.MessageId); if (state.SeenMessageIds.Count > 5000) state.SeenMessageIds.RemoveRange(0, state.SeenMessageIds.Count - 5000);
        AddIncoming(state, instanceId, incoming.Text, incoming.FromAgentId, incoming.ConversationId, incoming.ReplyTo, incoming.MessageId); return true;
    });

    private static IReadOnlyList<DirectoryAgent> ListAgentsCore(Registry state, string owner) { _ = RequireAgent(state, owner); var list = state.Agents.Values.Where(IsLive).Select(a => ToDirectoryAgent(state, a, "local")).ToList(); list.AddRange(state.RemoteAgents.Values.Where(a => a.Reachable && a.CanMessage)); return list; }
    private static AgentProfile ToProfile(Registry state, Registration a) => new(a.InstanceId, a.Provider, a.Name, a.Role, "") { IsOnline = true, IsShared = a.Shared, AgentId = LocalAgentId(state, a.InstanceId), Capabilities = a.Capabilities };
    private static DirectoryAgent ToDirectoryAgent(Registry state, Registration a, string location) => new(LocalAgentId(state, a.InstanceId), state.NodeId, a.InstanceId, a.Name, a.Provider, a.Role, a.Capabilities, location, true, a.Shared, a.Shared);
    private static string LocalAgentId(Registry state, string instanceId) => string.IsNullOrWhiteSpace(state.NodeId) ? instanceId : state.NodeId + "/" + instanceId;
    private static bool IsLive(Registration agent) => DateTimeOffset.UtcNow - agent.LastSeen < Lease;
    private static Registration RequireAgent(Registry state, string owner) => state.Agents.Values.FirstOrDefault(a => a.Owner == owner) ?? throw new InvalidOperationException("请先调用 agentlink_register 注册当前 Agent 实例。");
    private static string ValidateText(string text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 32000 ? text : throw new ArgumentException("消息长度必须在 1–32000 字符之间。");
    private static string ValidateConversation(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 ? value : throw new ArgumentException("conversation_id 必须在 1–200 字符之间。");
    private static AgentMessage QueueOutbound(Registry state, Registration agent, string target, string text, string conversation, string replyTo) { var item = new OutboundMessage { MessageId = Guid.NewGuid().ToString("N"), FromAgentId = LocalAgentId(state, agent.InstanceId), ToAgentId = target, Text = ValidateText(text), ConversationId = ValidateConversation(conversation), ReplyTo = replyTo, CreatedAt = DateTimeOffset.UtcNow }; state.Outbound.Add(item); return new AgentMessage(++state.Sequence, item.MessageId, item.Text, item.CreatedAt, false, target, item.ConversationId, item.ReplyTo, "queued"); }
    private static AgentMessage AddIncoming(Registry state, string instanceId, string text, string fromAgent, string conversation, string replyTo, string? id = null) { if (!state.Messages.TryGetValue(instanceId, out var messages)) state.Messages[instanceId] = messages = []; var item = new AgentMessage(++state.Sequence, id ?? Guid.NewGuid().ToString("N"), ValidateText(text), DateTimeOffset.UtcNow, fromAgent == "desktop", fromAgent, conversation, replyTo, "delivered"); messages.Add(item); if (messages.Count > 1000) messages.RemoveRange(0, messages.Count - 1000); return item; }

    public sealed class Registry { public string NodeId { get; set; } = ""; public string NodeName { get; set; } = ""; public long DirectoryVersion { get; set; } public Dictionary<string, Registration> Agents { get; set; } = new(StringComparer.Ordinal); public Dictionary<string, DirectoryAgent> RemoteAgents { get; set; } = new(StringComparer.Ordinal); public Dictionary<string, string> TrustedNodes { get; set; } = new(StringComparer.Ordinal); public Dictionary<string, List<AgentMessage>> Messages { get; set; } = new(StringComparer.Ordinal); public List<OutboundMessage> Outbound { get; set; } = []; public List<string> SeenMessageIds { get; set; } = []; public long Sequence { get; set; } }
    public sealed class Registration { public string InstanceId { get; set; } = ""; public string Owner { get; set; } = ""; public string Provider { get; set; } = ""; public string Name { get; set; } = ""; public string Role { get; set; } = ""; public List<string> Capabilities { get; set; } = []; public bool Shared { get; set; } public DateTimeOffset LastSeen { get; set; } }
}

public sealed record DirectoryAgent(string AgentId, string NodeId, string InstanceId, string Name, string Provider, string Role, IReadOnlyList<string> Capabilities, string Location, bool Reachable, bool Shared, bool CanMessage);
public sealed record DirectorySnapshot(string NodeId, string NodeName, long Version, IReadOnlyList<DirectoryAgent> Agents);
public sealed record RoutedMessage(string MessageId, string FromAgentId, string ToAgentId, string Text, string ConversationId, string ReplyTo);
public sealed class OutboundMessage { public string MessageId { get; set; } = ""; public string FromAgentId { get; set; } = ""; public string ToAgentId { get; set; } = ""; public string Text { get; set; } = ""; public string ConversationId { get; set; } = ""; public string ReplyTo { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } public bool Delivered { get; set; } }
public sealed record AgentMessage(long Sequence, string MessageId, string Text, DateTimeOffset Timestamp, bool IsLocal, string FromAgentId, string ConversationId, string ReplyTo, string Status) { public string Sender => IsLocal ? "我" : string.IsNullOrWhiteSpace(FromAgentId) ? "Agent" : FromAgentId; public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss"); }
