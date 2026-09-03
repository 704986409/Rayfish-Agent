using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RayLink.App.Models;

namespace RayLink.App.Services;

public sealed record PeerMessage(string Type, string Sender, string Text, string MessageId, DateTimeOffset Timestamp);

public sealed class PeerMessageEventArgs(PeerMessage message) : EventArgs
{
    public PeerMessage Message { get; } = message;
}

/// <summary>
/// MVP transport over Rayfish's virtual IPv6 network. It intentionally keeps the
/// app protocol small: newline-delimited JSON over TCP, with a periodic heartbeat.
/// This MVP does not yet implement application-layer peer authentication.
/// </summary>
public sealed class PeerTransport : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _lastReceivedUtcTicks;

    public event EventHandler<PeerMessageEventArgs>? MessageReceived;
    public event EventHandler<string>? StatusChanged;
    public bool IsConnected => _stream is not null && (_client?.Connected ?? false);

    public PeerTransport(AppSettings settings) => _settings = settings;

    public async Task StartServerAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listenAddress = IPAddress.IPv6Any;
        if (!string.IsNullOrWhiteSpace(_settings.LocalAddress) &&
            IPAddress.TryParse(_settings.LocalAddress.Trim('[', ']'), out var configuredAddress) &&
            configuredAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            listenAddress = configuredAddress;
        }
        try
        {
            _listener = new TcpListener(listenAddress, _settings.Port);
            _listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            _listener.Start();
            StatusChanged?.Invoke(this, $"服务端已启动，监听 Rayfish IPv6:{_settings.Port}");
            _ = AcceptLoopAsync(_listener, _cts.Token);
        }
        catch
        {
            _listener?.Stop();
            _listener = null;
            _cts.Dispose();
            _cts = null;
            throw;
        }
    }

    public async Task ConnectAsync(string address, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        if (!IPAddress.TryParse(address.Trim('[', ']'), out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new InvalidOperationException("请输入 Rayfish 分配的 IPv6 地址，例如 200:...，不要填写域名或本地地址。");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _client = new TcpClient(AddressFamily.InterNetworkV6)
        {
            NoDelay = true
        };
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        await _client.ConnectAsync(ip, _settings.Port, _cts.Token);
        await AttachAsync(_client, _cts.Token);
        StatusChanged?.Invoke(this, $"已连接到 [{ip}]:{_settings.Port}");
        _ = ReadLoopAsync(_cts.Token);
        _ = HeartbeatLoopAsync(_cts.Token);
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("当前没有可用的远程连接。");
        }

        var message = new PeerMessage("chat", _settings.DisplayName, text, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        await SendAsync(message, cancellationToken);
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
        _listener?.Stop();
        _listener = null;
        StatusChanged?.Invoke(this, "连接已断开");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                // Only one active connection is needed in the MVP. Replace the previous one.
                _stream?.Dispose();
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                _client = client;
                await AttachAsync(client, cancellationToken);
                StatusChanged?.Invoke(this, $"客户端已连接：{client.Client.RemoteEndPoint}");
                _ = ReadLoopAsync(cancellationToken);
                _ = HeartbeatLoopAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"服务端监听异常：{ex.Message}");
        }
    }

    private async Task AttachAsync(TcpClient client, CancellationToken cancellationToken)
    {
        _stream = client.GetStream();
        Interlocked.Exchange(ref _lastReceivedUtcTicks, DateTime.UtcNow.Ticks);
        await SendAsync(new PeerMessage("hello", _settings.DisplayName, "RayLink hello", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), cancellationToken);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_stream is null) return;
        try
        {
            using var reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                PeerMessage? message;
                try { message = JsonSerializer.Deserialize<PeerMessage>(line); }
                catch { continue; }
                if (message is null) continue;
                Interlocked.Exchange(ref _lastReceivedUtcTicks, DateTime.UtcNow.Ticks);
                if (message.Type == "heartbeat") continue;
                MessageReceived?.Invoke(this, new PeerMessageEventArgs(message));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"读取消息失败：{ex.Message}"); }
        finally
        {
            StatusChanged?.Invoke(this, "对端连接已关闭");
            _stream?.Dispose();
            _stream = null;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_stream is null) break;
                var lastReceived = new DateTime(Interlocked.Read(ref _lastReceivedUtcTicks), DateTimeKind.Utc);
                if (DateTime.UtcNow - lastReceived > TimeSpan.FromSeconds(60))
                {
                    StatusChanged?.Invoke(this, "心跳超时，远程连接已失效");
                    break;
                }
                await SendAsync(new PeerMessage("heartbeat", _settings.DisplayName, "", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"心跳失败：{ex.Message}"); }
    }

    private async Task SendAsync(PeerMessage message, CancellationToken cancellationToken)
    {
        if (_stream is null) throw new InvalidOperationException("当前没有可用的远程连接。");
        var json = JsonSerializer.Serialize(message) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        await _writeLock.WaitAsync(cancellationToken);
        try { await _stream.WriteAsync(bytes, cancellationToken); await _stream.FlushAsync(cancellationToken); }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
