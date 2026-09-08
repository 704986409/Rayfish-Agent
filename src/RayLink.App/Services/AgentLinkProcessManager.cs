using System.ComponentModel;
using System.Diagnostics;

namespace RayLink.App.Services;

/// <summary>
/// Stops sibling AgentLink processes when the desktop application exits.
/// MCP processes use the same executable, but belong to Codex/ChatGPT rather
/// than the window; closing AgentLink is intentionally a full local shutdown.
/// </summary>
public static class AgentLinkProcessManager
{
    public static IReadOnlyList<int> StopSiblingProcesses()
    {
        var ownPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(ownPath)) return [];

        var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(ownPath));
        if (string.IsNullOrWhiteSpace(executableDirectory)) return [];

        var remaining = new List<int>();
        foreach (var executableName in new[] { "AgentLink.exe", "AgentLink.Transport.exe" })
        {
            var expectedPath = Path.Combine(executableDirectory, executableName);
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executableName)))
            {
                try
                {
                    if (process.Id == Environment.ProcessId || !IsExpectedProcess(process, expectedPath)) continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                    if (!process.HasExited) remaining.Add(process.Id);
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { remaining.Add(process.Id); }
                finally { process.Dispose(); }
            }
        }
        return remaining;
    }

    private static bool IsExpectedProcess(Process process, string expectedPath)
    {
        try
        {
            var processPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(processPath) &&
                   string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception) { return false; }
    }
}
