using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RobloxPriceTracker.Gui;

public partial class MainWindow
{
    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(DataGrid),
            Control.MouseDoubleClickEvent,
            new MouseButtonEventHandler(OnDataGridMouseDoubleClick));
    }

    private static async void OnDataGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.Name != "DashboardWatchlistList") return;
        if (Window.GetWindow(grid) is not MainWindow window) return;
        if (GetDoubleClickedWatchlistRow(grid, e) is not { } row) return;

        e.Handled = true;
        await window.OpenItemDetailsAsync(row);
    }
}
