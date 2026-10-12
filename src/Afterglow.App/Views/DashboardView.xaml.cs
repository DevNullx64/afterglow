using System.Windows;
using System.Windows.Controls;
using Afterglow.App.ViewModels;

namespace Afterglow.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel vm || sender is not FrameworkElement { Tag: string key })
        {
            return;
        }

        // With nothing presenting there are no frametimes to expand: the tile's empty
        // state points at the FPS page, so the click goes there.
        if (key == "fps" && !vm.HasFps && Window.GetWindow(this)?.DataContext is MainViewModel main)
        {
            main.NavigateTo("metrics");
            return;
        }

        vm.ToggleExpand(key);
    }
}
