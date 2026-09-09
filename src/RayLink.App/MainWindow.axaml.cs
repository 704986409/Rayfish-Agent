using Avalonia.Input;
using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Primitives;
using RayLink.App.Models;
using RayLink.App.Services;

namespace RayLink.App;

public partial class MainWindow : Window
{
    private bool _closing;
    private bool _disposed;
    private AgentChatWindow? _agentChatWindow;
    private readonly DispatcherTimer _chatScrollbarTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        _chatScrollbarTimer.Tick += (_, _) =>
        {
            _chatScrollbarTimer.Stop();
            this.FindControl<ScrollViewer>("MessageScroll")?.SetCurrentValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            this.FindControl<ScrollViewer>("AgentChatScroll")?.SetCurrentValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        };
        DataContext = new MainViewModel(async text =>
        {
            var clipboard = Clipboard ?? throw new InvalidOperationException("当前无法访问剪贴板。");
            await clipboard.SetTextAsync(text);
        });
        this.FindControl<TextBox>("MessageInput")!.AddHandler(InputElement.KeyDownEvent, OnMessageKeyDown, RoutingStrategies.Tunnel);
        var vm = (MainViewModel)DataContext;
        vm.Messages.CollectionChanged += (_, _) => QueueScrollToLatest();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsMessagesPage) && vm.IsMessagesPage) QueueScrollToLatest();
            if (e.PropertyName == nameof(MainViewModel.IsAgentChatOpen) && vm.IsAgentChatOpen) OpenAgentChatWindow(vm);
        };
        Closing += async (_, e) =>
        {
            if (_disposed) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            IsEnabled = false;
            try
            {
                if (DataContext is MainViewModel viewModel) await viewModel.DisposeAsync();
                // Closing the desktop app is a full shutdown: terminate MCP
                // workers and any remaining Iroh bridge from this installation.
                var remaining = await Task.Run(AgentLinkProcessManager.StopSiblingProcesses);
                if (remaining.Count > 0) Debug.WriteLine($"AgentLink background processes still running: {string.Join(", ", remaining)}");
            }
            finally { _disposed = true; Close(); }
        };
    }




    private void OpenAgentChatWindow(MainViewModel vm)
    {
        if (_agentChatWindow is { IsVisible: true }) { _agentChatWindow.Activate(); return; }
        _agentChatWindow = new AgentChatWindow(vm);
        _agentChatWindow.Closed += (_, _) => _agentChatWindow = null;
        _agentChatWindow.Show(this);
    }

    private static AgentProfile? GetAgentFromMenu(object? sender)
    {
        if (sender is not MenuItem item) return null;
        // ContextMenu is hosted in a popup, so it does not reliably inherit the
        // card's data context. Prefer the context set when the menu opened.
        if (item.DataContext is AgentProfile agent) return agent;

        for (Control? current = item; current is not null; current = current.Parent as Control)
            if (current is ContextMenu menu)
                return (menu.PlacementTarget as Control)?.DataContext as AgentProfile;
        return null;
    }

    private static void OnAgentMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.PlacementTarget is Control target)
            menu.DataContext = target.DataContext;
    }

    private void OnAgentChatMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && GetAgentFromMenu(sender) is AgentProfile agent) vm.OpenAgentChat(agent);
    }

    private void OnAgentSettingsMenuClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && GetAgentFromMenu(sender) is AgentProfile agent)
            vm.EditAgentCommand.Execute(agent);
    }

    private void OnMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || sender is not TextBox input) return;
        // Let the IME consume Enter while composing Chinese/Japanese text.
        if (input.GetVisualDescendants().OfType<TextPresenter>().Any(p => !string.IsNullOrEmpty(p.PreeditText))) return;
        e.Handled = true;
        if (DataContext is MainViewModel vm && !vm.IsConnectionDialogOpen && vm.CanSend && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }

    private void OnMessageScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) QueueScrollToLatest();
    }

    private void OnChatScrollInteraction(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        viewer.SetCurrentValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _chatScrollbarTimer.Stop();
        _chatScrollbarTimer.Start();
    }

    private bool _scrollQueued;
    private void QueueScrollToLatest()
    {
        if (_scrollQueued || _closing) return;
        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (!_closing && DataContext is MainViewModel { IsMessagesPage: true })
                this.FindControl<ScrollViewer>("MessageScroll")?.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
