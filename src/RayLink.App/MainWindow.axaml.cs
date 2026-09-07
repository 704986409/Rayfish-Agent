using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;

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

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
