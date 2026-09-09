using System.Diagnostics;
using System.Text;

namespace RayLink.App.Services;

/// Drives external MCP clients in one-shot mode. Commands can be overridden
/// with AGENTLINK_<CLIENT>_COMMAND for installations using another launcher.
public sealed class ExternalAgentSessionService
{
    public async Task<string> SendAsync(string provider, string text, CancellationToken token = default)
    {
        var key = provider.Replace(" ", "").ToUpperInvariant();
        var command = Environment.GetEnvironmentVariable($"AGENTLINK_{key}_COMMAND")
            ?? (provider.Contains("Claude", StringComparison.OrdinalIgnoreCase) ? "claude" : "cursor-agent");
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
            psi = new ProcessStartInfo("cmd.exe", "/d /c " + Quote(command + " -p " + Quote(text)));
        else
            psi = new ProcessStartInfo(command, "-p " + Quote(text));
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false; psi.CreateNoWindow = true;
        // Node-based CLIs write UTF-8 even when Windows uses a legacy console code page.
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {provider} 客户端：{command}");
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{provider} 返回错误：{error.Trim()}");
        return output.Trim();
    }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";
}
