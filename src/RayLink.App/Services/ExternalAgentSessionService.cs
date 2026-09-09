using System.Diagnostics;
using System.Text;

namespace RayLink.App.Services;

/// Starts a short-lived external Agent turn. The turn is instructed to use the
/// AgentLink MCP session, so registration, receiving and replying happen
/// through the same broker instead of relying on an already-open UI session.
public sealed class ExternalAgentSessionService
{
    public async Task<string> SendAsync(string provider, string text, CancellationToken token = default)
    {
        var command = ResolveCommand(provider);
        var prompt = $"You are the {provider} Agent connected to AgentLink. Treat the following as a user message. " +
            "First call agentlink_register with a stable instance_id for this client (use the provider name if needed). " +
            "Then process the message, call agentlink_reply with your complete answer, and finally return the same answer briefly. " +
            "Do not ask the user to perform any setup. Message:\n\n" + text;
        var psi = CreateStartInfo(command, prompt);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {provider} 客户端：{command}");
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{provider} 返回错误：{error.Trim()}");
        return output.Trim();
    }

    private static ProcessStartInfo CreateStartInfo(string command, string prompt)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("text");
        psi.ArgumentList.Add("--no-session-persistence");
        return psi;
    }

    private static string ResolveCommand(string provider)
    {
        var key = provider.Replace(" ", "").ToUpperInvariant();
        var configured = Environment.GetEnvironmentVariable($"AGENTLINK_{key}_COMMAND");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return provider.Contains("Claude", StringComparison.OrdinalIgnoreCase) ? "claude.cmd" : "cursor-agent.cmd";
    }
}
