using System.Text.Json;

namespace RayLink.App.Services;

// Runs only in the desktop application. MCP child processes write to AiAgentService;
// this service is the sole component allowed to move queued data over Iroh.
public sealed class AgentLinkSyncService : IAsyncDisposable
{
    private const string Protocol = "agentlink/1";
    private readonly AiAgentService _agents;
    private readonly IrohTransport _transport;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _outbox;
    private string _remoteNodeId = "";
    private long _lastSharedVersion = -1;

    public event EventHandler<string>? TrustRequired;
    public event EventHandler<string>? StatusChanged;

    public AgentLinkSyncService(AiAgentService agents, IrohTransport transport)
    {
        _agents = agents; _transport = transport;
        _transport.Connected += (_, nodeId) => OnConnected(nodeId);
        _transport.Disconnected += (_, nodeId) => { if (!string.IsNullOrWhiteSpace(nodeId)) _agents.MarkNodeDisconnected(nodeId); };
        _transport.MessageReceived += (_, args) => HandleIncoming(args.Message);
        _outbox = FlushOutboxAsync(_stop.Token);
    }

    public static bool IsProtocolMessage(string text)
    {
        try { using var document = JsonDocument.Parse(text); return document.RootElement.TryGetProperty("Protocol", out var protocol) && protocol.GetString() == Protocol; }
        catch (JsonException) { return false; }
    }

    public void TrustConnectedNode(string nodeId, string displayName)
    {
        _agents.TrustNode(nodeId, displayName);
        if (_remoteNodeId == nodeId) _ = SendSnapshotAsync();
    }

    private void OnConnected(string nodeId)
    {
        _remoteNodeId = nodeId;
        if (_agents.IsTrusted(nodeId)) _ = SendSnapshotAsync();
        else TrustRequired?.Invoke(this, nodeId);
    }

    private void HandleIncoming(PeerMessage message)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(message.Text);
            if (envelope?.Protocol != Protocol || string.IsNullOrWhiteSpace(envelope.NodeId) || envelope.NodeId != _remoteNodeId) return;
            if (!_agents.IsTrusted(envelope.NodeId)) { TrustRequired?.Invoke(this, envelope.NodeId); return; }
            if (envelope.Kind == "directory_snapshot" && envelope.Snapshot is not null)
            {
                _agents.ApplyRemoteSnapshot(envelope.NodeId, envelope.Snapshot);
                StatusChanged?.Invoke(this, "已同步远程 Agent 目录。");
            }
            else if (envelope.Kind == "agent_message" && envelope.Message is not null)
            {
                if (_agents.ReceiveRemoteMessage(envelope.NodeId, envelope.Message))
                {
                    _ = SendAsync(new Envelope(Protocol, "receipt_delivered", "", _agents.LocalNodeId(), null, new Receipt(envelope.Message.MessageId)));
                    StatusChanged?.Invoke(this, "远程 Agent 消息已保存到本地收件箱。");
                }
            }
            else if (envelope.Kind == "receipt_delivered" && envelope.Receipt is not null) _agents.MarkDelivered(envelope.Receipt.MessageId);
        }
        catch (JsonException) { /* Plain desktop chat is intentionally ignored here. */ }
    }

    private async Task FlushOutboxAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var nodeId = _remoteNodeId;
                if (string.IsNullOrWhiteSpace(nodeId) || !_transport.IsConnected || !_agents.IsTrusted(nodeId)) continue;
                var snapshot = _agents.GetSharedSnapshot();
                if (snapshot.Version != _lastSharedVersion)
                {
                    _lastSharedVersion = snapshot.Version;
                    await SendAsync(new Envelope(Protocol, "directory_snapshot", snapshot.NodeName, snapshot.NodeId, snapshot), cancellationToken);
                }
                foreach (var item in _agents.TakeOutbound(nodeId))
                    await SendAsync(new Envelope(Protocol, "agent_message", "", _agents.LocalNodeId(), null, null, new RoutedMessage(item.MessageId, item.FromAgentId, item.ToAgentId, item.Text, item.ConversationId, item.ReplyTo)), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private Task SendSnapshotAsync()
    {
        var snapshot = _agents.GetSharedSnapshot();
        _lastSharedVersion = snapshot.Version;
        return SendAsync(new Envelope(Protocol, "directory_snapshot", snapshot.NodeName, snapshot.NodeId, snapshot));
    }
    private async Task SendAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        try { await _transport.SendTextAsync(JsonSerializer.Serialize(envelope), cancellationToken); }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"同步发送失败：{ex.Message}"); }
    }

    public async ValueTask DisposeAsync() { _stop.Cancel(); try { await _outbox; } catch (OperationCanceledException) { } _stop.Dispose(); }

    private sealed record Envelope(string Protocol, string Kind, string NodeName, string NodeId, DirectorySnapshot? Snapshot = null, Receipt? Receipt = null, RoutedMessage? Message = null);
    private sealed record Receipt(string MessageId);
}
