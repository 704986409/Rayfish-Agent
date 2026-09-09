using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RayLink.App.Models;

public sealed class AgentProfile : INotifyPropertyChanged
{
    private string _name;
    private string _role;
    private string _description;
    private int _avatarIndex;
    private bool _isOnline;
    private bool _isInstalled;
    private bool _isMcpInjected;
    public AgentProfile(string id, string provider, string name, string role, string description, int avatarIndex = 0)
    { Id=id; Provider=provider; _name=name; _role=role; _description=description; _avatarIndex=avatarIndex; }
    public string Id { get; }
    public string AgentId { get; set; } = "";
    public string Provider { get; }
    public bool IsCodex => string.Equals(Provider, "Codex", StringComparison.OrdinalIgnoreCase);
    public bool CanActivate { get; init; }
    public string Name { get => _name; set => Set(ref _name,value); }
    public string Role { get => _role; set => Set(ref _role,value); }
    public string Description { get => _description; set => Set(ref _description,value); }
    public int AvatarIndex { get => _avatarIndex; set => Set(ref _avatarIndex,value); }
    public bool IsOnline { get => _isOnline; set => Set(ref _isOnline,value); }
    public bool IsInstalled { get => _isInstalled; set => Set(ref _isInstalled,value); }
    public bool IsMcpInjected { get => _isMcpInjected; set => Set(ref _isMcpInjected,value); }
    public string InstallPath { get; set; } = "";
    public bool CanInject => !IsMcpInjected;
    public bool CanStart => IsMcpInjected && !IsOnline;
    public string InstallationStatus => IsMcpInjected ? "已注入" : IsInstalled ? "已安装" : "未安装";
    public bool IsShared { get; set; }
    public IReadOnlyList<string> Capabilities { get; set; } = [];
    public string Status => IsOnline ? "在线" : "未运行";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field,T value,[CallerMemberName]string? n=null){if(EqualityComparer<T>.Default.Equals(field,value))return;field=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(n)); if(n==nameof(IsOnline)){PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(Status)));PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(CanStart)));} if(n==nameof(IsInstalled)||n==nameof(IsMcpInjected)){PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(InstallationStatus)));PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(CanInject)));PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(CanStart)));}}
}
