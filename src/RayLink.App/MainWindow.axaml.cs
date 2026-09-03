using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RayLink.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
