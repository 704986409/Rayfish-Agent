using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Primitives;
using RayLink.App.Models;

namespace RayLink.App;

public partial class MainWindow : Window
{
    private bool _closing;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
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
            }
            finally { _disposed = true; Close(); }
        };
    }



    private static AgentProfile? GetAgentFromMenu(object? sender)
    {
        if (sender is not MenuItem item) return null;
        var menu = item.Parent as ContextMenu;
        return (menu?.PlacementTarget as Control)?.DataContext as AgentProfile;
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
