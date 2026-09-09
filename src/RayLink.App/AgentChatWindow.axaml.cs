using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using RayLink.App.Models;
using Avalonia.Markup.Xaml;

namespace RayLink.App;

public partial class AgentChatWindow : Window
{
    private readonly DispatcherTimer _scrollbarTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public AgentChatWindow() { InitializeComponent(); }

    public AgentChatWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, _) => viewModel.CloseAgentChatCommand.Execute(null);
        viewModel.AgentChatMessages.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("AgentChatScroll")?.ScrollToEnd());
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsAgentChatOpen) && !viewModel.IsAgentChatOpen)
                Dispatcher.UIThread.Post(Close);
        };
        var input = this.FindControl<TextBox>("AgentChatInput")!;
        input.AddHandler(InputElement.KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        _scrollbarTimer.Tick += (_, _) =>
        {
            _scrollbarTimer.Stop();
            this.FindControl<ScrollViewer>("AgentChatScroll")?.SetCurrentValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        };
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || sender is not TextBox input) return;
        if (input.GetVisualDescendants().OfType<TextPresenter>().Any(p => !string.IsNullOrEmpty(p.PreeditText))) return;
        if (DataContext is MainViewModel vm && vm.CanSendAgentChat && vm.SendAgentChatCommand.CanExecute(null))
        {
            e.Handled = true;
            vm.SendAgentChatCommand.Execute(null);
        }
    }

    private void OnChatScrollInteraction(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;
        viewer.SetCurrentValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _scrollbarTimer.Stop();
        _scrollbarTimer.Start();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
