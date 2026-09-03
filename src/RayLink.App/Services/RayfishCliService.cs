using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace RayLink.App.Services;

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Success => ExitCode == 0;
    public string Combined => string.Join(Environment.NewLine,
        new[] { Output.Trim(), Error.Trim() }.Where(static x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>
/// Locates and controls the Rayfish CLI bundled/installed by the RayLink setup.
/// The desktop app no longer requires users to configure a ray executable path.
/// An explicit legacy path is still accepted for development and upgrades.
/// </summary>
public sealed class RayfishCliService
{
    // Accept both fully expanded and compressed IPv6 forms printed by
    // `ray status` (for example 200:db8::1).
    private static readonly Regex Ipv6Candidate = new(
        @"(?<![0-9A-Fa-f:])[0-9A-Fa-f:]{2,}(?![0-9A-Fa-f:])",
        RegexOptions.Compiled);

    private readonly string? _configuredPath;

    public string ExecutablePath { get; }
    public bool IsBundledOrInstalled => !string.Equals(ExecutablePath, "ray", StringComparison.OrdinalIgnoreCase);
    public bool IsAvailable => File.Exists(ExecutablePath) || !Path.IsPathRooted(ExecutablePath);

    public RayfishCliService(string? configuredPath = null)
    {
        _configuredPath = string.IsNullOrWhiteSpace(configuredPath) || configuredPath.Trim() == "ray"
            ? null
            : configuredPath.Trim();
        ExecutablePath = ResolveExecutable(_configuredPath);
    }

    /// <summary>
    /// Runs a command through UAC when the current desktop user has not yet been
    /// registered as a Rayfish operator. The normal path remains unelevated.
    /// </summary>
    public async Task<bool> TryRunElevatedAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathRooted(ExecutablePath) || !File.Exists(ExecutablePath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (System.ComponentModel.Win32Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            // User cancelled UAC or the platform does not allow elevation.
            return false;
        }
    }

    public async Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new CommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, string.Empty,
                $"无法启动 Rayfish 服务：{ex.Message}（当前路径：{ExecutablePath}）");
        }
    }

    public Task<CommandResult> VersionAsync(CancellationToken ct = default) => RunAsync(["--version"], ct);
    public Task<CommandResult> UpAsync(CancellationToken ct = default) => RunAsync(["up"], ct);
    public Task<CommandResult> StatusAsync(CancellationToken ct = default) => RunAsync(["status"], ct);
    public Task<CommandResult> StatusJsonAsync(CancellationToken ct = default) => RunAsync(["status", "--json"], ct);
    public Task<CommandResult> DownAsync(CancellationToken ct = default) => RunAsync(["down"], ct);

    public Task<CommandResult> AllowTcpPortAsync(int port, CancellationToken ct = default) =>
        RunAsync(["firewall", "add", "in", "allow", "--proto", "tcp", "--port", port.ToString()], ct);

    public Task<CommandResult> CreateAsync(string networkName, string hostname, CancellationToken ct = default) =>
        RunAsync(["create", "--name", networkName, "--hostname", hostname], ct);

    public Task<CommandResult> InviteAsync(string network, CancellationToken ct = default) =>
        RunAsync(["invite", network], ct);

    public Task<CommandResult> JoinAsync(string inviteCode, string networkName, string hostname, CancellationToken ct = default) =>
        RunAsync(["join", inviteCode, "--name", networkName, "--hostname", hostname, "--auto-accept-firewall"], ct);

    public static string? FindRayfishIpv6(string output)
    {
        foreach (Match match in Ipv6Candidate.Matches(output))
        {
            var candidate = match.Value.Trim('[', ']', '(', ')', ',', ';', '"');
            if (!IPAddress.TryParse(candidate, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                continue;
            }

            var bytes = address.GetAddressBytes();
            // Rayfish addresses are derived from identity and currently live in 0200::/7.
            if ((bytes[0] & 0xFE) == 0x02)
            {
                return address.ToString();
            }
        }

        return null;
    }

    private static string ResolveExecutable(string? configuredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
        }

        var appDir = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(appDir, "ray.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rayfish", "ray.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Rayfish", "ray.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(Path.Combine(appDir, "ray"));
            candidates.Add("/usr/local/bin/ray");
            candidates.Add("/opt/homebrew/bin/ray");
        }
        else
        {
            candidates.Add(Path.Combine(appDir, "ray"));
            candidates.Add("/usr/local/bin/ray");
        }

        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Let the OS resolve a PATH installation for developer builds. A packaged
        // installation should fail with a clear error instead of silently invoking
        // an unrelated executable from PATH.
        return OperatingSystem.IsWindows() ? "ray.exe" : "ray";
    }
}
