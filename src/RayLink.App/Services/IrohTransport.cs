using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RayLink.App.Models;

namespace RayLink.App.Services;

public sealed record PeerMessage(string Type, string Sender, string Text, string MessageId, DateTimeOffset Timestamp);

public sealed class PeerMessageEventArgs(PeerMessage message) : EventArgs
{
    public PeerMessage Message { get; } = message;
}

public sealed class IrohReadyEventArgs(string endpointId, string endpointAddress) : EventArgs
{
    public string EndpointId { get; } = endpointId;
    public string EndpointAddress { get; } = endpointAddress;
}

/// <summary>
/// Owns the bundled Rust bridge process. Commands/events use newline-delimited JSON.
/// All network traffic is handled by Iroh (QUIC, NAT traversal and relay fallback).
/// </summary>
public sealed class IrohTransport : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<IrohReadyEventArgs>? _readySource;
    private bool _stopping;

    public event EventHandler<PeerMessageEventArgs>? MessageReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<IrohReadyEventArgs>? Ready;
    public bool IsRunning => _process is { HasExited: false };
    public bool IsConnected { get; private set; }
    public string ExecutablePath => ResolveExecutable(_settings.TransportExecutable);

    public IrohTransport(AppSettings settings) => _settings = settings;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) return;
            var executable = ExecutablePath;
            if (!File.Exists(executable))
                throw new FileNotFoundException("未找到内置 Iroh 通信组件。请使用完整安装包，或先构建 native/iroh-transport。", executable);
            _stopping = false;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readySource = new TaskCompletionSource<IrohReadyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
                StandardInputEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("--identity-key");
            startInfo.ArgumentList.Add(_settings.GetIdentityKeyPath());
            startInfo.ArgumentList.Add("--display-name");
            startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(_settings.DisplayName) ? Environment.MachineName : _settings.DisplayName.Trim());
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;
            if (!_process.Start()) throw new InvalidOperationException("无法启动 Iroh 通信组件。");
            _input = _process.StandardInput;
            _input.AutoFlush = true;
            _ = ReadStdoutAsync(_process.StandardOutput, _cts.Token);
            _ = ReadStderrAsync(_process.StandardError, _cts.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try { await _readySource.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Iroh 通信组件在 90 秒内没有完成联网初始化。"); }
        }
        catch { await StopProcessAsync(); throw; }
        finally { _startLock.Release(); }
    }

    public async Task StartServerAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);
        await SendCommandAsync(new { type = "start" }, cancellationToken);
    }

    public async Task ConnectAsync(string endpointAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpointAddress)) throw new InvalidOperationException("请粘贴对方完整的 Iroh EndpointAddr JSON。");
        JsonElement address;
        try
        {
            using var document = JsonDocument.Parse(endpointAddress);
            address = document.RootElement.Clone();
            if (address.ValueKind != JsonValueKind.Object || !address.TryGetProperty("id", out _)) throw new JsonException("缺少 id 字段");
        }
        catch (JsonException ex) { throw new InvalidOperationException($"远程 Iroh 地址格式不正确：{ex.Message}", ex); }
        await StartAsync(cancellationToken);
        StatusChanged?.Invoke(this, "正在通过 Iroh 连接远程节点…");
        await SendCommandAsync(new { type = "connect", endpoint_addr = address }, cancellationToken);
    }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("当前没有可用的 Iroh 连接。");
        await SendCommandAsync(new { type = "send", text }, cancellationToken);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) await SendCommandAsync(new { type = "disconnect" }, cancellationToken);
        IsConnected = false;
    }

    private async Task SendCommandAsync(object command, CancellationToken cancellationToken)
    {
        if (!IsRunning || _input is null) throw new InvalidOperationException("Iroh 通信组件尚未启动。");
        var json = JsonSerializer.Serialize(command);
        await _writeLock.WaitAsync(cancellationToken);
        try { await _input.WriteLineAsync(json.AsMemory(), cancellationToken); await _input.FlushAsync(cancellationToken); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadStdoutAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try { while (!cancellationToken.IsCancellationRequested) { var line = await reader.ReadLineAsync(cancellationToken); if (line is null) break; HandleEvent(line); } }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"读取 Iroh 组件事件失败：{ex.Message}"); }
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try { while (!cancellationToken.IsCancellationRequested) { var line = await reader.ReadLineAsync(cancellationToken); if (line is null) break; if (!string.IsNullOrWhiteSpace(line)) StatusChanged?.Invoke(this, $"Iroh：{line}"); } }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void HandleEvent(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
            var message = root.TryGetProperty("message", out var messageNode) ? messageNode.GetString() ?? "" : "";
            switch (type)
            {
                case "ready":
                    var ready = new IrohReadyEventArgs(root.GetProperty("endpoint_id").GetString() ?? "", root.GetProperty("endpoint_addr").GetRawText());
                    _readySource?.TrySetResult(ready); Ready?.Invoke(this, ready);
                    StatusChanged?.Invoke(this, "Iroh 节点已上线，支持 NAT 穿透和 Relay 回退。"); break;
                case "connected":
                    IsConnected = true;
                    var remoteId = root.TryGetProperty("remote_id", out var remoteNode) ? remoteNode.GetString() : null;
                    StatusChanged?.Invoke(this, string.IsNullOrWhiteSpace(remoteId) ? message : $"{message} 远程 ID：{remoteId}"); break;
                case "disconnected": IsConnected = false; StatusChanged?.Invoke(this, string.IsNullOrWhiteSpace(message) ? "Iroh 连接已断开。" : message); break;
                case "message":
                    var sender = root.TryGetProperty("sender", out var senderNode) ? senderNode.GetString() ?? "远程节点" : "远程节点";
                    var text = root.TryGetProperty("text", out var textNode) ? textNode.GetString() ?? "" : "";
                    var id = root.TryGetProperty("message_id", out var idNode) ? idNode.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                    var timestamp = root.TryGetProperty("timestamp", out var timestampNode) && DateTimeOffset.TryParse(timestampNode.GetString(), out var parsed) ? parsed : DateTimeOffset.UtcNow;
                    MessageReceived?.Invoke(this, new PeerMessageEventArgs(new PeerMessage("chat", sender, text, id, timestamp))); break;
                case "error": StatusChanged?.Invoke(this, string.IsNullOrWhiteSpace(message) ? "Iroh 通信组件发生错误。" : message); break;
                case "status": StatusChanged?.Invoke(this, message); break;
            }
        }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"忽略无法解析的 Iroh 组件事件：{ex.Message}"); }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        IsConnected = false;
        var exitCode = _process?.ExitCode;
        _readySource?.TrySetException(new InvalidOperationException($"Iroh 通信组件提前退出（退出码 {exitCode}）。"));
        if (!_stopping) StatusChanged?.Invoke(this, $"Iroh 通信组件已退出（退出码 {exitCode}）。");
    }

    private async Task StopProcessAsync()
    {
        _stopping = true;
        var process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited && _input is not null)
            {
                try { await SendCommandAsync(new { type = "shutdown" }, CancellationToken.None); } catch { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(timeout.Token); } catch { }
            }
            if (!process.HasExited) process.Kill(true);
        }
        catch { }
        finally
        {
            if (_cts is not null) { await _cts.CancelAsync(); _cts.Dispose(); }
            _cts = null; _input?.Dispose(); _input = null; process.Dispose(); _process = null; IsConnected = false;
        }
    }

    private static string ResolveExecutable(string? configuredPath)
    {
        var fileName = OperatingSystem.IsWindows() ? "RayLink.Transport.exe" : "RayLink.Transport";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, fileName));
        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "native", "iroh-transport", "target", "release", fileName)));
        candidates.Add(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "native", "iroh-transport", "target", "release", fileName)));
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public async ValueTask DisposeAsync() => await StopProcessAsync();
}
