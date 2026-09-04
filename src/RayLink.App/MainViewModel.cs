using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using RayLink.App.Models;
using RayLink.App.Services;

namespace RayLink.App;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private IrohTransport? _transport;
    private string _nodeStatus = "Iroh 节点未启动";
    private string _connectionStatus = "未连接";
    private string _messageDraft = "";
    private string _log = "";
    private bool _isBusy;
    private int _currentPageIndex;

    public AppSettings Settings { get; }
    public ObservableCollection<ChatEntry> Messages { get; } = [];
    public ICommand NavigateCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand StartServerCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand CopyEndpointCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            var nextPage = Math.Clamp(value, 0, 4);
            if (_currentPageIndex == nextPage) return;
            _currentPageIndex = nextPage;
            OnPropertyChanged(nameof(CurrentPageIndex));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            OnPropertyChanged(nameof(IsWorkspacePage));
            OnPropertyChanged(nameof(IsRemoteNodesPage));
            OnPropertyChanged(nameof(IsMessagesPage));
            OnPropertyChanged(nameof(IsStatusPage));
            OnPropertyChanged(nameof(IsSettingsPage));
        }
    }
    public string PageTitle => CurrentPageIndex switch
    {
        1 => "远程节点",
        2 => "消息记录",
        3 => "节点状态",
        4 => "应用设置",
        _ => "通信工作区"
    };
    public string PageSubtitle => CurrentPageIndex switch
    {
        1 => "管理本机节点，连接远程 Agent。",
        2 => "查看本次会话中的双向通信记录。",
        3 => "查看 Iroh 节点、连接与运行日志。",
        4 => "配置设备名称和本地应用选项。",
        _ => "管理节点、连接远程 Agent，并进行双向消息测试。"
    };
    public bool IsWorkspacePage => CurrentPageIndex == 0;
    public bool IsRemoteNodesPage => CurrentPageIndex == 1;
    public bool IsMessagesPage => CurrentPageIndex == 2;
    public bool IsStatusPage => CurrentPageIndex == 3;
    public bool IsSettingsPage => CurrentPageIndex == 4;
    public string NodeStatus { get => _nodeStatus; private set => Set(ref _nodeStatus, value); }
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string MessageDraft { get => _messageDraft; set => Set(ref _messageDraft, value); }
    public string Log { get => _log; private set => Set(ref _log, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool HasMessages => Messages.Count > 0;

    public MainViewModel()
    {
        Settings = AppSettings.Load();
        NavigateCommand = new RelayCommand(parameter =>
        {
            if (int.TryParse(parameter?.ToString(), out var page))
            {
                CurrentPageIndex = page;
            }
        });
        SaveSettingsCommand = new RelayCommand(_ =>
        {
            Settings.Save();
            AppendLog("应用设置已保存。");
        });
        StartServerCommand = new AsyncCommand(StartServerAsync);
        ConnectCommand = new AsyncCommand(ConnectAsync);
        DisconnectCommand = new AsyncCommand(DisconnectAsync);
        SendCommand = new AsyncCommand(SendAsync);
        CopyEndpointCommand = new AsyncCommand(CopyEndpointAsync);
    }

    private async Task StartServerAsync()
    {
        try
        {
            IsBusy = true; Settings.Save();
            await DisposeTransportAsync();
            _transport = CreateTransport();
            await _transport.StartServerAsync();
        }
        catch (Exception ex) { AppendLog($"启动 Iroh 服务失败：{ex.Message}"); }
        finally { IsBusy = false; }
    }

    private async Task ConnectAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Settings.RemoteEndpointAddress)) { AppendLog("请先粘贴远程节点的完整 Iroh EndpointAddr JSON。"); return; }
            IsBusy = true; Settings.Save();
            if (_transport is null) _transport = CreateTransport();
            await _transport.ConnectAsync(Settings.RemoteEndpointAddress);
        }
        catch (Exception ex) { AppendLog($"连接失败：{ex.Message}"); }
        finally { IsBusy = false; }
    }

    private IrohTransport CreateTransport()
    {
        var transport = new IrohTransport(Settings);
        transport.StatusChanged += (_, status) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { ConnectionStatus = status; AppendLog(status); });
        };
        transport.Ready += (_, ready) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Settings.LocalEndpointId = ready.EndpointId;
                Settings.LocalEndpointAddress = ready.EndpointAddress;
                Settings.Save();
                NodeStatus = $"Iroh 已上线 · EndpointId：{ready.EndpointId}";
                AppendLog($"本机 EndpointAddr 已更新：{ready.EndpointAddress}");
            });
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

    private async Task CopyEndpointAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.LocalEndpointAddress)) { AppendLog("请先点击“启动服务”，等待本机 EndpointAddr 生成。"); return; }
        AppendLog("请手动复制上方的本机 EndpointAddr JSON。");
        await Task.CompletedTask;
    }

    private async Task SendAsync()
    {
        var text = MessageDraft.Trim();
        if (string.IsNullOrWhiteSpace(text) || _transport is null) return;
        try { await _transport.SendTextAsync(text); Messages.Add(new ChatEntry("我", text, DateTimeOffset.Now, true)); MessageDraft = ""; OnPropertyChanged(nameof(HasMessages)); }
        catch (Exception ex) { AppendLog($"发送失败：{ex.Message}"); }
    }

    private async Task DisconnectAsync() { if (_transport is not null) await _transport.DisconnectAsync(); }

    private async Task DisposeTransportAsync()
    {
        if (_transport is not null) { await _transport.DisposeAsync(); _transport = null; }
        ConnectionStatus = "未连接";
    }

    private void AppendLog(string message) => Log = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}{Log}";
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; OnPropertyChanged(name); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public async ValueTask DisposeAsync() => await DisposeTransportAsync();
}

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
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
        try { await execute(); }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
