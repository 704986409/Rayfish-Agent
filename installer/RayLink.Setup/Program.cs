using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using System.Windows.Forms;

namespace RayLink.Setup;

internal static class Program
{
    private const string ProductName = "RayLink";
    private const string ProductVersion = "0.1.0";
    private static readonly string InstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Uninstall();
            }
            else
            {
                Install();
            }

            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"RayLink 安装失败：{Environment.NewLine}{ex.Message}",
                ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Install()
    {
        using var progress = new SetupProgressForm("正在安装 RayLink…");
        progress.Show();
        progress.SetStatus("准备安装文件…");

        Directory.CreateDirectory(InstallDirectory);
        var appPath = Path.Combine(InstallDirectory, "RayLink.exe");
        ExtractResource("payload\\RayLink.exe", appPath);
        var setupPath = Path.Combine(InstallDirectory, "RayLink.Setup.exe");
        var currentSetup = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentSetup) || !File.Exists(currentSetup))
        {
            throw new InvalidOperationException("无法确定安装程序路径。");
        }
        if (!string.Equals(Path.GetFullPath(currentSetup), Path.GetFullPath(setupPath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(currentSetup, setupPath, true);
        }

        var workDirectory = Path.Combine(Path.GetTempPath(), "RayLinkSetup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        try
        {
            progress.SetStatus("正在安装内置 Rayfish 网络服务…");
            var rayfishMsi = Path.Combine(workDirectory, "ray-windows-x86_64.msi");
            ExtractResource("payload\\ray-windows-x86_64.msi", rayfishMsi);
            var msiResult = RunProcess("msiexec.exe", $"/i \"{rayfishMsi}\" /qn /norestart", 180_000, captureOutput: true);
            if (msiResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"内置 Rayfish 安装失败（退出码 {msiResult.ExitCode}）：{msiResult.Output}");
            }

            progress.SetStatus("正在初始化 Rayfish 服务…");
            var rayExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rayfish", "ray.exe");
            if (!File.Exists(rayExe))
            {
                throw new FileNotFoundException("Rayfish 安装完成后没有找到 ray.exe。", rayExe);
            }

            // The installer is elevated. This first `ray up` registers the
            // current Windows user as the Rayfish operator and activates the
            // TUN service, so the normal desktop app can run as a standard user.
            var rayResult = RunProcess(rayExe, "up", 120_000, captureOutput: true);
            if (rayResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Rayfish 服务初始化失败（退出码 {rayResult.ExitCode}）：{rayResult.Output}");
            }

            progress.SetStatus("正在创建快捷方式…");
            CreateShortcut(appPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RayLink.lnk"));
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName);
            Directory.CreateDirectory(startMenu);
            CreateShortcut(appPath, Path.Combine(startMenu, "RayLink.lnk"));

            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RayLink");
            key?.SetValue("DisplayName", ProductName);
            key?.SetValue("DisplayVersion", ProductVersion);
            key?.SetValue("Publisher", ProductName);
            key?.SetValue("InstallLocation", InstallDirectory);
            key?.SetValue("DisplayIcon", appPath);
            key?.SetValue("UninstallString", $"\"{setupPath}\" /uninstall");

            progress.SetStatus("安装完成，正在启动 RayLink…");
            Process.Start(new ProcessStartInfo(appPath) { UseShellExecute = true });
            progress.Close();
            MessageBox.Show(
                "RayLink 已安装。内置 Rayfish 服务已启动。\n\n以后直接打开 RayLink 即可，不需要另外安装 Rayfish。",
                ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private static void Uninstall()
    {
        var answer = MessageBox.Show(
            "确定要卸载 RayLink 和内置 Rayfish 服务吗？",
            ProductName,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        var rayExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rayfish", "ray.exe");
        if (File.Exists(rayExe))
        {
            try { RunProcess(rayExe, "down", 30_000); } catch { }
        }

        DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RayLink.lnk"));
        DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName, "RayLink.lnk"));
        try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName), true); } catch { }
        try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RayLink", false); } catch { }

        // Do not delete the running setup/app in-place. Schedule removal after
        // the process exits and remove Rayfish via its registered MSI.
        var cleanup = Path.Combine(Path.GetTempPath(), $"RayLink-uninstall-{Guid.NewGuid():N}.cmd");
        var rayfishUninstall = FindRayfishUninstallCommand();
        File.WriteAllText(cleanup, $"@echo off\r\ntimeout /t 2 /nobreak >nul\r\n{(rayfishUninstall is null ? "" : rayfishUninstall + " /qn /norestart\r\n")}rmdir /s /q \"{InstallDirectory}\"\r\ndel \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cleanup}\"") { UseShellExecute = false, CreateNoWindow = true });
        MessageBox.Show("RayLink 卸载已开始。", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string? FindRayfishUninstallCommand()
    {
        const string productCode = "{B350B8C4-9812-4DEE-A933-94B669C309E9}";
        return $"msiexec.exe /x {productCode}";
    }
    private static void ExtractResource(string resourceName, string destination)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var input = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"安装包缺少内置文件：{resourceName}");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    private static ProcessResult RunProcess(string fileName, string arguments, int timeoutMs, bool captureOutput = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = captureOutput,
                WorkingDirectory = InstallDirectory
            }
        };
        process.Start();
        var stdout = captureOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
        var stderr = captureOutput ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"等待 {Path.GetFileName(fileName)} 完成超时。");
        }
        var output = string.Join(Environment.NewLine, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult()).Trim();
        return new ProcessResult(process.ExitCode, output);
    }

    private static void CreateShortcut(string target, string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统不支持创建快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = InstallDirectory;
        shortcut.Description = "RayLink Agent 通信";
        shortcut.IconLocation = $"{target},0";
        shortcut.Save();
    }

    private static void DeleteShortcut(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);

    private sealed class SetupProgressForm : Form
    {
        private readonly Label _label;
        public SetupProgressForm(string title)
        {
            Text = title;
            Width = 440;
            Height = 130;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            _label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = title };
            Controls.Add(_label);
        }
        public void SetStatus(string text) { _label.Text = text; Application.DoEvents(); }
    }
}
