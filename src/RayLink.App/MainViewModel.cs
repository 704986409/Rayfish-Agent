using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using RayLink.App.Models;
using RayLink.App.Services;

namespace RayLink.App;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly RayfishCliService _rayfish;
    private PeerTransport? _transport;
    private string _rayStatus = "未检查 Rayfish";
    private string _connectionStatus = "未连接";
    private string _messageDraft = "";
    private string _log = "";
    private string _networkStatus = "尚未配置网络";
    private string _inviteCode = "";
    private string _networkName = "team";
    private bool _isBusy;

    public AppSettings Settings { get; }
    public ObservableCollection<ChatEntry> Messages { get; } = [];
    public ICommand CheckRayfishCommand { get; }
    public ICommand StartServerCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand CreateNetworkCommand { get; }
    public ICommand InviteCommand { get; }
    public ICommand JoinNetworkCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public string RayStatus { get => _rayStatus; private set => Set(ref _rayStatus, value); }
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string MessageDraft { get => _messageDraft; set => Set(ref _messageDraft, value); }
    public string Log { get => _log; private set => Set(ref _log, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool HasMessages => Messages.Count > 0;
    public string NetworkStatus { get => _networkStatus; private set => Set(ref _networkStatus, value); }
    public string InviteCode { get => _inviteCode; set => Set(ref _inviteCode, value); }
    public string NetworkName { get => _networkName; set => Set(ref _networkName, value); }

    public MainViewModel()
    {
        Settings = AppSettings.Load();
        _rayfish = new RayfishCliService(Settings.RayExecutable);
        CheckRayfishCommand = new AsyncCommand(CheckRayfishAsync);
        StartServerCommand = new AsyncCommand(StartServerAsync);
        ConnectCommand = new AsyncCommand(ConnectAsync);
        DisconnectCommand = new AsyncCommand(DisconnectAsync);
        SendCommand = new AsyncCommand(SendAsync);
        CreateNetworkCommand = new AsyncCommand(CreateNetworkAsync);
        InviteCommand = new AsyncCommand(InviteAsync);
        JoinNetworkCommand = new AsyncCommand(JoinNetworkAsync);
        NetworkName = Settings.NetworkName;
        _ = CheckRayfishAsync();
    }

    private async Task CheckRayfishAsync()
    {
        IsBusy = true;
        var result = await _rayfish.VersionAsync();
        RayStatus = result.Success ? $"Rayfish：{result.Combined}" : $"Rayfish 不可用：{result.Combined}";
        AppendLog(RayStatus);
        IsBusy = false;
    }

    private async Task CreateNetworkAsync()
    {
        try
        {
            await EnsureRayfishUpAsync();
            var name = string.IsNullOrWhiteSpace(NetworkName) ? "team" : NetworkName.Trim();
            var host = string.IsNullOrWhiteSpace(Settings.DisplayName) ? Environment.MachineName : Settings.DisplayName.Trim();
            var result = await _rayfish.CreateAsync(name, host);
            if (!result.Success) throw new InvalidOperationException(result.Combined);
            Settings.NetworkName = name;
            Settings.Save();
            NetworkStatus = $"网络已创建：{name}";
            AppendLog($"Rayfish 网络创建成功：{result.Combined}");
            await RefreshRayfishAddressAsync();
        }
        catch (Exception ex) { AppendLog($"创建网络失败：{ex.Message}"); }
    }

    private async Task InviteAsync()
    {
        try
        {
            await EnsureRayfishUpAsync();
            var name = string.IsNullOrWhiteSpace(NetworkName) ? Settings.NetworkName : NetworkName.Trim();
            var result = await _rayfish.InviteAsync(name);
            if (!result.Success) throw new InvalidOperationException(result.Combined);
            InviteCode = ExtractInviteCode(result.Combined);
            NetworkStatus = $"已生成邀请：{InviteCode}";
            AppendLog($"邀请代码已生成：{InviteCode}");
        }
        catch (Exception ex) { AppendLog($"生成邀请失败：{ex.Message}"); }
    }

    private async Task JoinNetworkAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(InviteCode)) { AppendLog("请先粘贴 Rayfish 邀请代码。"); return; }
            await EnsureRayfishUpAsync();
            var name = string.IsNullOrWhiteSpace(NetworkName) ? "team" : NetworkName.Trim();
            var host = string.IsNullOrWhiteSpace(Settings.DisplayName) ? Environment.MachineName : Settings.DisplayName.Trim();
            var result = await _rayfish.JoinAsync(InviteCode.Trim(), name, host);
            if (!result.Success) throw new InvalidOperationException(result.Combined);
            Settings.NetworkName = name;
            Settings.Save();
            NetworkStatus = $"已加入网络：{name}";
            AppendLog($"Rayfish 网络加入成功：{result.Combined}");
            await RefreshRayfishAddressAsync();
        }
        catch (Exception ex) { AppendLog($"加入网络失败：{ex.Message}"); }
    }

    private async Task RefreshRayfishAddressAsync()
    {
        var status = await _rayfish.StatusJsonAsync();
        var address = status.Success ? RayfishCliService.FindRayfishIpv6(status.Combined) : null;

        if (address is null)
        {
            status = await _rayfish.StatusAsync();
            address = status.Success ? RayfishCliService.FindRayfishIpv6(status.Combined) : null;
        }

        if (address is null)
        {
            NetworkStatus = status.Success
                ? "Rayfish 已启动，但尚未分配虚拟 IPv6 地址"
                : $"无法读取网络状态：{status.Combined}";
            return;
        }

        Settings.LocalAddress = address;
        Settings.Save();
        NetworkStatus = $"Rayfish 地址：{address}";
        AppendLog($"已获取本机 Rayfish IPv6：{address}");
    }

    private static string ExtractInviteCode(string output)
    {
        var lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(line => line.Length >= 12 && !line.Contains(' ') && !line.Contains(':')) ?? output.Trim();
    }

    private async Task StartServerAsync()
    {
        try
        {
            Settings.Save();
            await EnsureRayfishUpAsync();
            var firewall = await _rayfish.AllowTcpPortAsync(Settings.Port);
            if (!firewall.Success)
            {
                throw new InvalidOperationException($"无法放行 Rayfish TCP 端口 {Settings.Port}：{firewall.Combined}");
            }
            AppendLog($"已允许 Rayfish 网络访问 TCP {Settings.Port}。");
            await DisposeTransportAsync();
            _transport = CreateTransport();
            await _transport.StartServerAsync();
        }
        catch (Exception ex) { AppendLog($"启动服务端失败：{ex.Message}"); }
    }

    private async Task ConnectAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Settings.RemoteAddress))
            {
                AppendLog("请先填写远程设备的 Rayfish IPv6 地址。");
                return;
            }
            Settings.Save();
            await EnsureRayfishUpAsync();
            await DisposeTransportAsync();
            _transport = CreateTransport();
            await _transport.ConnectAsync(Settings.RemoteAddress);
        }
        catch (Exception ex) { AppendLog($"连接失败：{ex.Message}"); }
    }

    private async Task EnsureRayfishUpAsync()
    {
        var result = await _rayfish.UpAsync();
        if (!result.Success && OperatingSystem.IsWindows())
        {
            AppendLog("正在请求系统权限启动 Rayfish 服务…");
            if (await _rayfish.TryRunElevatedAsync(["up"]))
            {
                result = await _rayfish.StatusAsync();
            }
        }
        if (!result.Success)
        {
            throw new InvalidOperationException($"Rayfish 启动失败：{result.Combined}");
        }

        AppendLog("Rayfish 网络已启用。");
        await RefreshRayfishAddressAsync();
    }
    private PeerTransport CreateTransport()
    {
        var transport = new PeerTransport(Settings);
        transport.StatusChanged += (_, status) =>
        {
            ConnectionStatus = status;
            AppendLog(status);
        };
        transport.MessageReceived += (_, args) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Messages.Add(new ChatEntry(args.Message.Sender, args.Message.Text, args.Message.Timestamp, false));
                OnPropertyChanged(nameof(HasMessages));
            });
        };
        return transport;
    }

    private async Task SendAsync()
    {
        var text = MessageDraft.Trim();
        if (string.IsNullOrWhiteSpace(text) || _transport is null) return;
        try
        {
            await _transport.SendTextAsync(text);
            Messages.Add(new ChatEntry("我", text, DateTimeOffset.Now, true));
            MessageDraft = "";
            OnPropertyChanged(nameof(HasMessages));
        }
        catch (Exception ex) { AppendLog($"发送失败：{ex.Message}"); }
    }

    private async Task DisconnectAsync() => await DisposeTransportAsync();

    private async Task DisposeTransportAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync();
            _transport = null;
        }
        ConnectionStatus = "未连接";
    }

    private void AppendLog(string message) => Log = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}{Log}";
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public async ValueTask DisposeAsync() => await DisposeTransportAsync();
}

public sealed class AsyncCommand(Func<Task> execute) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;
    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); } finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
