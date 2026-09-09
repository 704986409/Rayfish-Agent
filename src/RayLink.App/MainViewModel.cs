using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Diagnostics;
using RayLink.App.Models;
using RayLink.App.Services;

namespace RayLink.App;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private IrohTransport? _transport;
    private AgentLinkSyncService? _sync;
    private readonly Func<string, Task> _copyTextAsync;
    private string _nodeStatus = "Iroh 节点未启动";
    private string _connectionStatus = "未连接";
    private string _messageDraft = "";
    private string _log = "";
    private bool _isBusy;
    private bool _isServerStarted;
    private int _currentPageIndex;
    private bool _isConnecting;
    private bool _isConnectionDialogOpen;
    private bool _hasUnreadMessages;
    private string _connectionPrompt = "";
    private AgentProfile? _selectedAgent;
    private bool _isProfileDialogOpen;
    private string _profileName = "";
    private string _profileRole = "";
    private string _profileDescription = "";
    private string _profileInstallPath = "";
    private int _profileAvatarIndex;
    private bool _isAgentChatOpen;
    private string _agentChatDraft = "";
    private string _pendingTrustNode = "";
    private bool _isAgentIntegrationPromptOpen;
    private AgentProfile? _activationTarget;
    private bool _isActivationPromptOpen;
    private readonly AiAgentService _aiAgents = new();
    private readonly CodexAppServerService _codex = new();
    private readonly ExternalAgentSessionService _externalAgents = new();

    public AppSettings Settings { get; }
    public ObservableCollection<ChatEntry> Messages { get; } = [];
    public ObservableCollection<AgentProfile> Agents { get; } = [];
    public ObservableCollection<InstalledDesktopClient> InstalledClients { get; } = [];
    public ObservableCollection<ChatEntry> AgentChatMessages { get; } = [];
    public int[] AvatarChoices { get; } = [0, 1, 2, 3, 4, 5];
    public AgentProfile? SelectedAgent { get => _selectedAgent; set { Set(ref _selectedAgent, value); if (value != null) LoadProfile(value); } }
    public bool IsProfileDialogOpen { get => _isProfileDialogOpen; private set => Set(ref _isProfileDialogOpen, value); }
    public bool IsAgentChatOpen { get => _isAgentChatOpen; private set => Set(ref _isAgentChatOpen, value); }
    public string AgentChatDraft { get => _agentChatDraft; set { Set(ref _agentChatDraft, value); OnPropertyChanged(nameof(CanSendAgentChat)); } }
    public bool CanSendAgentChat => IsAgentChatOpen && SelectedAgent != null && !string.IsNullOrWhiteSpace(AgentChatDraft);
    public string ProfileName { get => _profileName; set => Set(ref _profileName,value); }
    public string ProfileRole { get => _profileRole; set => Set(ref _profileRole,value); }
    public string ProfileDescription { get => _profileDescription; set => Set(ref _profileDescription,value); }
    public string ProfileInstallPath { get => _profileInstallPath; private set { Set(ref _profileInstallPath,value); OnPropertyChanged(nameof(HasProfileInstallPath)); } }
    public bool HasProfileInstallPath => !string.IsNullOrWhiteSpace(ProfileInstallPath);
    public int ProfileAvatarIndex { get => _profileAvatarIndex; set => Set(ref _profileAvatarIndex,value); }
    public ICommand NavigateCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand StartServerCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand CopyEndpointCommand { get; }
    public ICommand CloseConnectionDialogCommand { get; }
    public ICommand RefreshAgentsCommand { get; }
    public ICommand StartDesktopClientCommand { get; }
    public ICommand InjectMcpCommand { get; }
    public ICommand EditAgentCommand { get; }
    public ICommand SaveAgentProfileCommand { get; }
    public ICommand CloseProfileDialogCommand { get; }
    public ICommand SendAgentChatCommand { get; }
    public ICommand CloseAgentChatCommand { get; }
    public ICommand TrustNodeCommand { get; }
    public ICommand RejectNodeCommand { get; }
    public ICommand EnableCodexMcpCommand { get; }
    public ICommand EnableClaudeMcpCommand { get; }
    public ICommand EnableCursorMcpCommand { get; }
    public ICommand DeclineCodexMcpCommand { get; }
    public ICommand ActivateCodexCommand { get; }
    public ICommand ConfirmActivationCommand { get; }
    public ICommand CancelActivationCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            var nextPage = Math.Clamp(value, 0, 5);
            if (_currentPageIndex == nextPage) return;
            _currentPageIndex = nextPage;
            if (IsMessagesPage) HasUnreadMessages = false;
            OnPropertyChanged(nameof(CurrentPageIndex));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
            OnPropertyChanged(nameof(IsWorkspacePage));
            OnPropertyChanged(nameof(IsRemoteNodesPage));
            OnPropertyChanged(nameof(IsMessagesPage));
            OnPropertyChanged(nameof(IsStatusPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(IsRolesPage));
            if (IsRolesPage && !Settings.AgentIntegrationPromptHandled) IsAgentIntegrationPromptOpen = true;
        }
    }
    public string PageTitle => CurrentPageIndex switch
    {
        1 => "远程节点",
        2 => "消息发送",
        3 => "节点状态",
        4 => "应用设置",
        5 => "角色设定",
        _ => "通信工作区"
    };
    public string PageSubtitle => CurrentPageIndex switch
    {
        1 => "管理本机节点，连接远程 Agent。",
        2 => "发送文字消息，查看本次会话的双向通信记录。",
        3 => "查看 Iroh 节点、连接与运行日志。",
        4 => "绑定并管理本机可用的 AI Agent。",
        5 => "为每个 Agent 建立独立的角色聊天空间。",
        _ => "管理节点、连接远程 Agent，并进行双向消息测试。"
    };
    public bool IsWorkspacePage => CurrentPageIndex == 0;
    public bool IsRemoteNodesPage => CurrentPageIndex == 1;
    public bool IsMessagesPage => CurrentPageIndex == 2;
    public bool IsStatusPage => CurrentPageIndex == 3;
    public bool IsSettingsPage => CurrentPageIndex == 4;
    public bool IsRolesPage => CurrentPageIndex == 5;
    public string NodeStatus { get => _nodeStatus; private set => Set(ref _nodeStatus, value); }
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            Set(ref _connectionStatus, value);
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(StartServerText));
            OnPropertyChanged(nameof(CanSend));
        }
    }
    public string MessageDraft
    {
        get => _messageDraft;
        set { Set(ref _messageDraft, value); OnPropertyChanged(nameof(CanSend)); }
    }
    public string Log { get => _log; private set => Set(ref _log, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (EqualityComparer<bool>.Default.Equals(_isBusy, value)) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStartServer));
            OnPropertyChanged(nameof(CanConnect));
        }
    }
    public bool IsServerStarted
    {
        get => _isServerStarted;
        private set
        {
            if (EqualityComparer<bool>.Default.Equals(_isServerStarted, value)) return;
            _isServerStarted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ServerStatus));
            OnPropertyChanged(nameof(LocalNodeName));
            OnPropertyChanged(nameof(CanStartServer));
            OnPropertyChanged(nameof(CanConnect));
        }
    }
    public string ServerStatus => IsServerStarted ? "已启动" : "未启动";
    public string StartServerText => IsServerStarted ? "服务已开启" : "开启服务";
    public bool CanStartServer => !IsServerStarted && !IsBusy;
    public bool IsConnected => ConnectionStatus == "已连接";
    public bool CanConnect => IsServerStarted && !IsBusy && !IsConnected;
    public string LocalNodeName => IsServerStarted ? Settings.DisplayName : "";
    public bool IsConnecting { get => _isConnecting; private set => Set(ref _isConnecting, value); }
    public bool IsConnectionDialogOpen { get => _isConnectionDialogOpen; private set => Set(ref _isConnectionDialogOpen, value); }
    public string ConnectionPrompt { get => _connectionPrompt; private set => Set(ref _connectionPrompt, value); }
    public bool HasUnreadMessages { get => _hasUnreadMessages; private set => Set(ref _hasUnreadMessages, value); }
    public bool CanSend => IsConnected && !string.IsNullOrWhiteSpace(MessageDraft);
    public bool HasMessages => Messages.Count > 0;
    public int MessageCount => Messages.Count;
    public bool HasAgents => Agents.Count > 0;
    public string PendingTrustNode { get => _pendingTrustNode; private set { Set(ref _pendingTrustNode, value); OnPropertyChanged(nameof(HasPendingTrust)); } }
    public bool HasPendingTrust => !string.IsNullOrWhiteSpace(PendingTrustNode);
    public bool IsAgentIntegrationPromptOpen { get => _isAgentIntegrationPromptOpen; private set => Set(ref _isAgentIntegrationPromptOpen, value); }
    public bool IsActivationPromptOpen { get => _isActivationPromptOpen; private set => Set(ref _isActivationPromptOpen, value); }

    public MainViewModel(Func<string, Task> copyTextAsync)
    {
        _copyTextAsync = copyTextAsync;
        Settings = AppSettings.Load();
        _codex.AgentMessage += (_, text) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var entry = new ChatEntry("Codex", text, DateTimeOffset.Now, false);
            SaveManagedCodexEntry(entry);
            if (IsAgentChatOpen && SelectedAgent?.Id == "managed-codex")
                AgentChatMessages.Add(entry);
        });
        _codex.StatusChanged += (_, status) => Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendLog(status));
        _codex.ConversationError += (_, error) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AgentChatMessages.Add(new ChatEntry("AgentLink", $"Codex 回复失败：{error}", DateTimeOffset.Now, false));
        });
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.DisplayName)) OnPropertyChanged(nameof(LocalNodeName));
        };
        Messages.CollectionChanged += (_, e) =>
        {
            if (!IsMessagesPage && e.NewItems is not null && e.NewItems.Cast<ChatEntry>().Any(m => !m.IsLocal))
                HasUnreadMessages = true;
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(MessageCount));
        };
        NavigateCommand = new RelayCommand(parameter =>
        {
            if (int.TryParse(parameter?.ToString(), out var page))
            {
                CurrentPageIndex = page;
            }
        });
        SaveSettingsCommand = new RelayCommand(_ =>
        {
            try { Settings.Save(); AppendLog("应用设置已保存。"); }
            catch (Exception ex) { AppendLog($"保存设置失败：{ex.Message}"); }
        });
        StartServerCommand = new AsyncCommand(StartServerAsync);
        ConnectCommand = new AsyncCommand(ConnectAsync);
        DisconnectCommand = new AsyncCommand(DisconnectAsync);
        SendCommand = new AsyncCommand(SendAsync);
        CopyEndpointCommand = new AsyncCommand(CopyEndpointAsync);
        CloseConnectionDialogCommand = new RelayCommand(_ =>
        {
            if (!IsConnecting) IsConnectionDialogOpen = false;
        });
        RefreshAgentsCommand = new RelayCommand(_ => RefreshAgents());
        StartDesktopClientCommand = new RelayCommand(p =>
        {
            // Keep Codex on the existing managed-session activation path.
            if (p is AgentProfile { IsCodex: true } codex)
            {
                _activationTarget = codex;
                IsActivationPromptOpen = true;
                return;
            }
            var client = p is InstalledDesktopClient installed ? installed : p is AgentProfile agent
                ? InstalledClients.FirstOrDefault(x => x.Id == agent.Id || x.Name.Equals(agent.Provider, StringComparison.OrdinalIgnoreCase)) : null;
            if (client == null) { AppendLog("未找到客户端启动程序，请确认客户端已安装后刷新。"); return; }
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c start \"\" /b \"{client.Command}\"") { UseShellExecute = false, CreateNoWindow = true });
                if (p is AgentProfile profile) profile.IsOnline = true;
            }
            catch (Exception ex) { AppendLog($"启动失败：{ex.Message}"); }
        });
        InjectMcpCommand = new RelayCommand(p =>
        {
            if (p is not AgentProfile profile) return;
            try
            {
                var integration = new McpClientIntegrationService();
                if (profile.Provider.Contains("Claude", StringComparison.OrdinalIgnoreCase)) integration.EnableClaudeCode();
                else if (profile.Provider.Contains("Cursor", StringComparison.OrdinalIgnoreCase)) integration.EnableCursor();
                else if (profile.Provider.Contains("Codex", StringComparison.OrdinalIgnoreCase)) new CodexMcpIntegrationService().Enable();
                else throw new InvalidOperationException("暂不支持此客户端的一键 MCP 注入。");
                profile.IsMcpInjected = true;
                profile.IsInstalled = true;
                OnPropertyChanged(nameof(Agents));
                AppendLog($"已为 {profile.Provider} 注入 AgentLink MCP。");
            }
            catch (Exception ex) { AppendLog($"注入 {profile.Provider} MCP 失败：{ex.Message}"); }
        });
        EditAgentCommand = new RelayCommand(p => { if (p is AgentProfile a) { SelectedAgent=a; IsProfileDialogOpen=true; } });
        SaveAgentProfileCommand = new RelayCommand(_ => SaveAgentProfile());
        CloseProfileDialogCommand = new RelayCommand(_ => IsProfileDialogOpen=false);
        SendAgentChatCommand = new AsyncCommand(SendAgentChatAsync);
        CloseAgentChatCommand = new RelayCommand(_ => IsAgentChatOpen = false);
        TrustNodeCommand = new RelayCommand(_ => TrustPendingNode());
        RejectNodeCommand = new RelayCommand(_ => { if (!string.IsNullOrWhiteSpace(PendingTrustNode)) { AppendLog($"未信任远程节点：{PendingTrustNode}"); PendingTrustNode = ""; } });
        EnableCodexMcpCommand = new RelayCommand(_ => EnableCodexMcp());
        EnableClaudeMcpCommand = new RelayCommand(_ => EnableJsonMcp("Claude Code", new McpClientIntegrationService().EnableClaudeCode));
        EnableCursorMcpCommand = new RelayCommand(_ => EnableJsonMcp("Cursor", new McpClientIntegrationService().EnableCursor));
        DeclineCodexMcpCommand = new RelayCommand(_ => { Settings.AgentIntegrationPromptHandled = true; Settings.Save(); IsAgentIntegrationPromptOpen = false; });
        ActivateCodexCommand = new RelayCommand(p => { if (p is AgentProfile agent && agent.Provider == "Codex") { _activationTarget = agent; IsActivationPromptOpen = true; } });
        CancelActivationCommand = new RelayCommand(_ => { _activationTarget = null; IsActivationPromptOpen = false; });
        ConfirmActivationCommand = new AsyncCommand(ActivateCodexAsync);
        RefreshAgents();
    }


    private void RefreshAgents()
    {
        try
        {
            InstalledClients.Clear();
            foreach (var client in new DesktopClientScanner().Scan()) InstalledClients.Add(client);
            AppendLog($"已扫描到 {InstalledClients.Count} 个桌面端工具。 ");
            var selectedId = SelectedAgent?.Id;
            var discovered = _aiAgents.Discover()
                // A Codex MCP child can use either the old stable id or the
                // provider/name reported by older AgentLink builds.  These
                // are transport registrations, not user-facing agents.
                .Where(a => !IsLegacyCodexMcpRegistration(a))
                .GroupBy(a => a.Id, StringComparer.Ordinal)
                .Select(g => g.First()).ToDictionary(a => a.Id, StringComparer.Ordinal);
            foreach (var c in InstalledClients)
            {
                if (!discovered.ContainsKey(c.Id)) discovered[c.Id] = new AgentProfile(c.Id, c.Name, c.Name, "桌面端工具", "已安装的 MCP 客户端");
                discovered[c.Id].IsInstalled = true; discovered[c.Id].IsMcpInjected = c.IsInjected; discovered[c.Id].InstallPath = c.Command;
            }
            var hasManagedCodex = !string.IsNullOrWhiteSpace(Settings.ManagedCodexThreadId);
            if (hasManagedCodex)
            {
                foreach (var id in discovered.Keys.Where(id => id.StartsWith("codex-process-", StringComparison.Ordinal)).ToArray())
                    discovered.Remove(id);
                foreach (var id in discovered.Values
                             .Where(a => string.Equals(a.Provider, "Codex", StringComparison.OrdinalIgnoreCase) && a.Id != "managed-codex")
                             .Select(a => a.Id).ToArray())
                    discovered.Remove(id);
                discovered["managed-codex"] = new AgentProfile("managed-codex", "Codex", "Codex", "AgentLink 受管会话", "桌面与远程消息将直接追加到同一 Codex 会话。") { IsOnline = true };
            }
            // Session IDs differ from installation IDs (for example managed-codex).
            foreach (var agent in discovered.Values)
            {
                var client = InstalledClients.FirstOrDefault(c => c.Name.Equals(agent.Provider, StringComparison.OrdinalIgnoreCase));
                agent.IsInstalled = client != null;
                agent.IsMcpInjected = DesktopClientScanner.IsMcpConfigured(agent.Provider);
                agent.InstallPath = client?.Command ?? "";
            }
            for (var i = Agents.Count - 1; i >= 0; i--)
                if (!discovered.ContainsKey(Agents[i].Id)) Agents.RemoveAt(i);
            foreach (var a in discovered.Values)
            {
                var current = Agents.FirstOrDefault(x => x.Id == a.Id);
                if (current == null) Agents.Add(a);
                else { current.IsOnline = a.IsOnline; current.IsInstalled = a.IsInstalled; current.IsMcpInjected = a.IsMcpInjected; current.InstallPath = a.InstallPath; }
                var item = current ?? a;
                var saved = Settings.AgentProfiles.FirstOrDefault(x => x.Id == item.Id);
                if (saved != null) { item.Name=saved.Name; item.Role=saved.Role; item.Description=saved.Description; item.AvatarIndex=saved.AvatarIndex; }
            }
            if (selectedId != null) SelectedAgent = Agents.FirstOrDefault(a => a.Id == selectedId);
        }
        catch (Exception ex) { AppendLog($"刷新 Agent 失败：{ex.Message}"); }
    }

    private static bool IsLegacyCodexMcpRegistration(AgentProfile agent)
    {
        static bool Matches(string value) => string.Equals(value.Trim(), "codex-mcp-client", StringComparison.OrdinalIgnoreCase);
        return Matches(agent.Id) || Matches(agent.Provider) || Matches(agent.Name);
    }

    public void OpenAgentChat(AgentProfile agent)
    {
        SelectedAgent = agent;
        AgentChatMessages.Clear();
        AgentChatDraft = "";
        if (agent.Id == "managed-codex")
        {
            foreach (var message in Settings.ManagedCodexChatHistory)
                AgentChatMessages.Add(new ChatEntry(message.Sender, message.Text, message.Timestamp, message.IsLocal));
        }
        else
        {
            foreach (var message in _aiAgents.ReadMessages(agent.Id))
                AgentChatMessages.Add(new ChatEntry(message.Sender, message.Text, message.Timestamp, message.IsLocal));
        }
        IsAgentChatOpen = true;
        OnPropertyChanged(nameof(CanSendAgentChat));
    }

    private async Task SendAgentChatAsync()
    {
        if (!CanSendAgentChat || SelectedAgent == null) return;
        var text = AgentChatDraft.Trim();
        try
        {
            if (SelectedAgent.Id.StartsWith("codex-process-", StringComparison.Ordinal))
            {
                await Task.Run(() => new CodexTaskAdapter().RestartAndStart(text));
                AgentChatMessages.Add(new ChatEntry("AgentLink", "已重启 Codex，并启动新的任务以激活 AgentLink MCP。", DateTimeOffset.Now, false));
                AgentChatDraft = "";
                await Task.Delay(1200);
                RefreshAgents();
                return;
            }
            if (SelectedAgent.Id == "managed-codex")
            {
                if (string.IsNullOrWhiteSpace(Settings.ManagedCodexThreadId)) throw new InvalidOperationException("请先激活 Codex 会话。");
                var entry = new ChatEntry("我", text, DateTimeOffset.Now, true);
                AgentChatMessages.Add(entry);
                SaveManagedCodexEntry(entry);
                AgentChatDraft = "";
                Settings.ManagedCodexThreadId = await _codex.EnsureThreadAsync(Settings.ManagedCodexThreadId);
                Settings.Save();
                var submittedThreadId = await _codex.SendAsync(Settings.ManagedCodexThreadId, text);
                if (!string.Equals(submittedThreadId, Settings.ManagedCodexThreadId, StringComparison.Ordinal))
                {
                    Settings.ManagedCodexThreadId = submittedThreadId;
                    Settings.Save();
                }
                AgentChatMessages.Add(new ChatEntry("AgentLink", "消息已提交到 Codex 会话“AgentLink · Codex”，正在等待回复。该会话也会出现在 Codex 最近会话中。", DateTimeOffset.Now, false));
            }
            else
            {
                if (SelectedAgent.Provider.Contains("Claude", StringComparison.OrdinalIgnoreCase) || SelectedAgent.Provider.Contains("Cursor", StringComparison.OrdinalIgnoreCase))
                {
                    AgentChatMessages.Add(new ChatEntry("我", text, DateTimeOffset.Now, true));
                    AgentChatDraft = "";
                    var reply = await _externalAgents.SendAsync(SelectedAgent.Provider, text);
                    AgentChatMessages.Add(new ChatEntry(SelectedAgent.Provider, string.IsNullOrWhiteSpace(reply) ? "Agent 已完成处理，但没有返回文本。" : reply, DateTimeOffset.Now, false));
                    return;
                }
                // UI profile ids may be friendly names (for example
                // claude-code), while MCP registers a stable mcp-<provider>
                // id. Resolve the live registration before queueing.
                var targetId = SelectedAgent.Id;
                if (targetId.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                    targetId.Contains("cursor", StringComparison.OrdinalIgnoreCase))
                {
                    var live = _aiAgents.Discover().FirstOrDefault(a =>
                        a.IsOnline && (a.Provider.Contains(SelectedAgent.Provider, StringComparison.OrdinalIgnoreCase) ||
                                       a.Name.Contains(SelectedAgent.Provider, StringComparison.OrdinalIgnoreCase)));
                    if (live != null) targetId = live.Id;
                }
                var message = await Task.Run(() => _aiAgents.Send(targetId, text));
                AgentChatMessages.Add(new ChatEntry("我", text, DateTimeOffset.Now, true));
                AgentChatDraft = "";
                // MCP clients (Claude, Cursor, and future clients) receive the
                // queued message through agentlink_receive. Keep watching the
                // shared inbox so their agentlink_reply appears in this chat.
                _ = Task.Run(async () =>
                {
                    var seen = message.Sequence;
                    for (var i = 0; i < 60; i++)
                    {
                        await Task.Delay(1000);
                        var replies = _aiAgents.ReadMessages(targetId).Where(x => x.Sequence > seen && !x.IsLocal).ToList();
                        foreach (var reply in replies)
                        {
                            seen = Math.Max(seen, reply.Sequence);
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => AgentChatMessages.Add(new ChatEntry(reply.Sender, reply.Text, reply.Timestamp, false)));
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppendLog($"发送 Agent 消息失败：{ex.Message}");
            AgentChatMessages.Add(new ChatEntry("AgentLink", $"发送失败：{ex.Message}", DateTimeOffset.Now, false));
        }
    }

    private void SaveManagedCodexEntry(ChatEntry entry)
    {
        Settings.ManagedCodexChatHistory.Add(new StoredChatEntry
        {
            Sender = entry.Sender,
            Text = entry.Text,
            Timestamp = entry.Timestamp,
            IsLocal = entry.IsLocal
        });
        if (Settings.ManagedCodexChatHistory.Count > 500)
            Settings.ManagedCodexChatHistory.RemoveRange(0, Settings.ManagedCodexChatHistory.Count - 500);
        try { Settings.Save(); }
        catch (Exception ex) { AppendLog($"保存 Codex 聊天历史失败：{ex.Message}"); }
    }

    private void LoadProfile(AgentProfile a) { ProfileName=a.Name; ProfileRole=a.Role; ProfileDescription=a.Description; ProfileAvatarIndex=a.AvatarIndex; ProfileInstallPath=a.InstallPath; }
    private void SaveAgentProfile()
    {
        if (SelectedAgent == null) return;
        SelectedAgent.Name=string.IsNullOrWhiteSpace(ProfileName)?SelectedAgent.Provider:ProfileName.Trim(); SelectedAgent.Role=ProfileRole.Trim(); SelectedAgent.Description=ProfileDescription.Trim(); SelectedAgent.AvatarIndex=ProfileAvatarIndex;
        var saved=Settings.AgentProfiles.FirstOrDefault(x=>x.Id==SelectedAgent.Id);
        if(saved==null) Settings.AgentProfiles.Add(new StoredAgentProfile { Id=SelectedAgent.Id, Provider=SelectedAgent.Provider });
        saved=Settings.AgentProfiles.First(x=>x.Id==SelectedAgent.Id); saved.Name=SelectedAgent.Name; saved.Role=SelectedAgent.Role; saved.Description=SelectedAgent.Description; saved.AvatarIndex=SelectedAgent.AvatarIndex; Settings.Save(); IsProfileDialogOpen=false;
    }

    private async Task StartServerAsync()
    {
        if (!CanStartServer) return;
        try
        {
            IsBusy = true; Settings.Save();
            await DisposeTransportAsync();
            _transport = CreateTransport();
            await _transport.StartServerAsync();
            IsServerStarted = true;
            ConnectionStatus = _transport.IsConnected ? "已连接" : "未连接";
        }
        catch (Exception ex) { IsServerStarted = false; AppendLog($"启动 Iroh 服务失败：{ex.Message}"); }
        finally { IsBusy = false; }
    }

    private async Task ConnectAsync()
    {
        if (!CanConnect || _transport is null) return;
        IsBusy = true;
        IsConnecting = true;
        ConnectionPrompt = "正在连接";
        IsConnectionDialogOpen = true;
        try
        {
            var transport = _transport;
            await transport.ConnectAsync(Settings.RemoteEndpointAddress);
            ConnectionStatus = transport.IsConnected ? "已连接" : "未连接";
            IsConnectionDialogOpen = false;
        }
        catch (Exception ex)
        {
            ConnectionPrompt = $"连接失败：{ex.Message}";
            AppendLog(ConnectionPrompt);
            if (_transport is null || !_transport.IsRunning)
            {
                IsServerStarted = false;
                NodeStatus = "Iroh 节点未启动";
                ConnectionStatus = "未连接";
            }
        }
        finally { IsConnecting = false; IsBusy = false; }
    }

    private IrohTransport CreateTransport()
    {
        var transport = new IrohTransport(Settings);
        transport.StatusChanged += (_, status) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_transport, transport)) return;
                ConnectionStatus = transport.IsConnected ? "已连接" : "未连接";
                if (!transport.IsRunning)
                {
                    IsServerStarted = false;
                    NodeStatus = "Iroh 节点未启动";
                }
                AppendLog(status);
            });
        };
        transport.Ready += (_, ready) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_transport, transport)) return;
                IsServerStarted = true;
                Settings.LocalEndpointId = ready.EndpointId;
                Settings.LocalEndpointAddress = ready.EndpointAddress;
                _aiAgents.ConfigureNode(ready.EndpointId, Settings.DisplayName);
                NodeStatus = "Iroh 已上线";
                AppendLog($"本机 EndpointAddr 已更新：{ready.EndpointAddress}");
            });
        };
        transport.MessageReceived += (_, args) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_transport, transport)) return;
                if (AgentLinkSyncService.IsProtocolMessage(args.Message.Text)) return;
                Messages.Add(new ChatEntry(args.Message.Sender, args.Message.Text, args.Message.Timestamp, false));
            });
        };
        Agents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAgents));
        _sync = new AgentLinkSyncService(_aiAgents, transport);
        _sync.TrustRequired += (_, nodeId) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_transport, transport)) return;
            PendingTrustNode = nodeId;
            AppendLog($"远程节点 {nodeId} 等待信任确认，尚未同步任何 Agent。 ");
        });
        _sync.StatusChanged += (_, status) => Avalonia.Threading.Dispatcher.UIThread.Post(() => AppendLog(status));
        return transport;
    }

    private void TrustPendingNode()
    {
        if (string.IsNullOrWhiteSpace(PendingTrustNode) || _sync is null) return;
        _sync.TrustConnectedNode(PendingTrustNode, PendingTrustNode);
        AppendLog($"已信任远程节点：{PendingTrustNode}。正在同步获共享的 Agent 目录。");
        PendingTrustNode = "";
    }

    private void EnableCodexMcp()
    {
        try { AppendLog(new CodexMcpIntegrationService().Enable()); Settings.AgentIntegrationPromptHandled = true; Settings.Save(); IsAgentIntegrationPromptOpen = false; }
        catch (Exception ex) { AppendLog($"接入 Codex MCP 失败：{ex.Message}"); }
    }

    private void EnableJsonMcp(string clientName, Func<string> enable)
    {
        try { AppendLog(enable()); }
        catch (Exception ex) { AppendLog($"接入 {clientName} MCP 失败：{ex.Message}"); }
    }

    private async Task ActivateCodexAsync()
    {
        if (_activationTarget is null) return;
        try
        {
            IsActivationPromptOpen = false;
            Settings.ManagedCodexThreadId = await _codex.EnsureThreadAsync(Settings.ManagedCodexThreadId);
            Settings.Save();
            AppendLog("Codex 受管会话已激活，后续消息会直接追加到该会话。");
            RefreshAgents();
        }
        catch (Exception ex) { AppendLog($"激活 Codex 失败：{ex.Message}"); }
        finally { _activationTarget = null; }
    }

    private async Task CopyEndpointAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.LocalEndpointAddress)) { AppendLog("请先点击“启动服务”，等待本机 EndpointAddr 生成。"); return; }
        try
        {
            await _copyTextAsync(Settings.LocalEndpointAddress);
            AppendLog("本机 Endpoint 地址已复制。");
        }
        catch (Exception ex) { AppendLog($"复制失败：{ex.Message}"); }
    }

    private async Task SendAsync()
    {
        var text = MessageDraft.Trim();
        if (!CanSend || _transport is null) return;
        try
        {
            await _transport.SendTextAsync(text);
            Messages.Add(new ChatEntry("我", text, DateTimeOffset.Now, true));
            if (MessageDraft.Trim() == text) MessageDraft = "";
        }
        catch (Exception ex) { AppendLog($"发送失败：{ex.Message}"); }
    }

    private async Task DisconnectAsync()
    {
        try
        {
            if (_transport is not null) await _transport.DisconnectAsync();
            ConnectionStatus = "未连接";
        }
        catch (Exception ex) { AppendLog($"断开连接失败：{ex.Message}"); }
    }

    private async Task DisposeTransportAsync()
    {
        var sync = _sync;
        _sync = null;
        if (sync is not null) await sync.DisposeAsync();
        var transport = _transport;
        _transport = null;
        if (transport is not null) await transport.DisposeAsync();
        IsServerStarted = false;
        NodeStatus = "Iroh 节点未启动";
        ConnectionStatus = "未连接";
    }

    private void AppendLog(string message)
    {
        const int maxLogCharacters = 64 * 1024;
        var next = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}{Log}";
        Log = next.Length <= maxLogCharacters ? next : next[..maxLogCharacters];
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; OnPropertyChanged(name); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public async ValueTask DisposeAsync()
    {
        await DisposeTransportAsync();
        await _codex.DisposeAsync();
    }
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
