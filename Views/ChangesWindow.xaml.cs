using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ChangesWindow : Window
{
    public ChangesWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is ChangesViewModel viewModel) await viewModel.LoadAsync();
        };
    }

    /// <summary>A right-click selects the row under it, so the menu acts on that row.</summary>
    private void EntriesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (ItemsControl.ContainerFromElement(grid, (DependencyObject)e.OriginalSource) is DataGridRow row) grid.SelectedItem = row.Item;
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
