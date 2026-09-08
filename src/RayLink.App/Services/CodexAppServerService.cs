using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace RayLink.App.Services;

/// <summary>Small JSON-RPC client for a Codex app-server process owned by AgentLink.</summary>
public sealed class CodexAppServerService : IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _requests = new();
    private Process? _process;
    private StreamWriter? _input;
    private CancellationTokenSource? _stop;
    private string _activeThreadId = "";
    private int _nextId;

    public event EventHandler<string>? AgentMessage;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ConversationError;

    public async Task<string> EnsureThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(threadId) && string.Equals(threadId, _activeThreadId, StringComparison.Ordinal))
            return threadId;
        if (string.IsNullOrWhiteSpace(threadId))
            return await StartThreadAsync(cancellationToken);
        try
        {
            await RequestAsync("thread/resume", new { threadId }, cancellationToken);
            await SetThreadNameAsync(threadId, cancellationToken);
            _activeThreadId = threadId;
            return threadId;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("no rollout found", StringComparison.OrdinalIgnoreCase))
        {
            StatusChanged?.Invoke(this, "原 Codex 会话已失效，正在自动创建新会话。");
            return await StartThreadAsync(cancellationToken);
        }
    }

    public Task SendAsync(string threadId, string text, CancellationToken cancellationToken = default) =>
        RequestAsync("turn/start", new { threadId, input = new[] { new { type = "text", text } } }, cancellationToken);

    private async Task<string> StartThreadAsync(CancellationToken cancellationToken)
    {
        var response = await RequestAsync("thread/start", new { serviceName = "agentlink" }, cancellationToken);
        _activeThreadId = response.GetProperty("thread").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Codex 没有返回线程 ID。");
        await SetThreadNameAsync(_activeThreadId, cancellationToken);
        return _activeThreadId;
    }

    private async Task SetThreadNameAsync(string threadId, CancellationToken cancellationToken)
    {
        try { await RequestAsync("thread/name/set", new { threadId, name = "AgentLink · Codex" }, cancellationToken); }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"Codex 会话已创建，但设置会话名称失败：{ex.Message}"); }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return;
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false }) return;
            var executable = CodexTaskAdapter.ResolveExecutable();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("app-server");
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += (_, _) => FailPending(new InvalidOperationException("Codex App Server 已退出。"));
            if (!_process.Start()) throw new InvalidOperationException("无法启动 Codex App Server。");
            _input = _process.StandardInput;
            _input.AutoFlush = true;
            _stop = new CancellationTokenSource();
            _ = ReadStdoutAsync(_process.StandardOutput, _stop.Token);
            _ = ReadStderrAsync(_process.StandardError, _stop.Token);
            await RequestAsync("initialize", new { clientInfo = new { name = "agentlink", title = "AgentLink", version = "0.3.4" } }, cancellationToken);
            await NotifyAsync("initialized", new { }, cancellationToken);
            StatusChanged?.Invoke(this, "Codex 已连接，准备接收消息。");
        }
        catch { await StopAsync(); throw; }
        finally { _startLock.Release(); }
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.TryAdd(id, pending)) throw new InvalidOperationException("Codex 请求编号冲突。");
        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);
            return await pending.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally { _requests.TryRemove(id, out _); }
    }

    private Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var input = _input ?? throw new InvalidOperationException("Codex App Server 尚未启动。");
        await _writeLock.WaitAsync(cancellationToken);
        try { await input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadStdoutAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                var parsed = false;
                foreach (var json in ExtractJsonObjects(line))
                {
                    try { ProcessMessage(json); parsed = true; }
                    catch (JsonException ex) { StatusChanged?.Invoke(this, $"忽略一条格式异常的 Codex 事件：{ex.Message}"); }
                }
                if (!parsed && !string.IsNullOrWhiteSpace(line))
                    StatusChanged?.Invoke(this, "忽略一条非 JSON 的 Codex 输出，事件监听仍在继续。");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FailPending(ex); StatusChanged?.Invoke(this, $"读取 Codex 事件失败：{ex.Message}"); }
    }

    private void ProcessMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out var requestId) &&
            (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)) &&
            _requests.TryGetValue(requestId, out var pending))
        {
            if (root.TryGetProperty("error", out var error)) pending.TrySetException(new InvalidOperationException(error.GetProperty("message").GetString() ?? "Codex 请求失败。"));
            else if (root.TryGetProperty("result", out var result)) pending.TrySetResult(result.Clone());
            return;
        }
        if (!root.TryGetProperty("method", out var method) || !root.TryGetProperty("params", out var parameters)) return;
        if (method.GetString() == "item/completed" && parameters.TryGetProperty("item", out var item) &&
            item.TryGetProperty("type", out var type) && type.GetString() == "agentMessage" && item.TryGetProperty("text", out var text))
            AgentMessage?.Invoke(this, text.GetString() ?? "");
        else if (method.GetString() == "turn/completed")
        {
            if (parameters.TryGetProperty("turn", out var turn) && turn.TryGetProperty("status", out var status) && status.GetString() == "failed")
            {
                var message = turn.TryGetProperty("error", out var turnError) && turnError.ValueKind == JsonValueKind.Object &&
                              turnError.TryGetProperty("message", out var errorMessage)
                    ? errorMessage.GetString() ?? "Codex 本轮处理失败。"
                    : "Codex 本轮处理失败。";
                ConversationError?.Invoke(this, message);
            }
            else StatusChanged?.Invoke(this, "Codex 已完成本轮回复。");
        }
        else if (method.GetString() == "error" && parameters.TryGetProperty("error", out var eventError) &&
                 eventError.TryGetProperty("message", out var eventMessage))
            StatusChanged?.Invoke(this, $"Codex：{eventMessage.GetString()}");
    }

    private static IEnumerable<string> ExtractJsonObjects(string line)
    {
        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];
            if (start < 0)
            {
                if (current != '{') continue;
                start = i;
                depth = 1;
                continue;
            }
            if (inString)
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == '"') inString = false;
                continue;
            }
            if (current == '"') inString = true;
            else if (current == '{') depth++;
            else if (current == '}' && --depth == 0)
            {
                yield return line[start..(i + 1)];
                start = -1;
            }
        }
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken token)
    {
        try { while (await reader.ReadLineAsync(token) is { } line) if (!string.IsNullOrWhiteSpace(line)) StatusChanged?.Invoke(this, $"Codex：{line}"); }
        catch (OperationCanceledException) { }
    }

    private void FailPending(Exception error) { foreach (var request in _requests.Values) request.TrySetException(error); }

    private async Task StopAsync()
    {
        var process = _process; _process = null; _activeThreadId = "";
        if (_stop is not null) { await _stop.CancelAsync(); _stop.Dispose(); _stop = null; }
        _input?.Dispose(); _input = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
        finally { process.Dispose(); }
    }

    public async ValueTask DisposeAsync() { await StopAsync(); _startLock.Dispose(); _writeLock.Dispose(); }
}
