using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ChangesView : ScreenView
{
    public ChangesView() => InitializeComponent();

    protected override async void OnFirstShown()
    {
        if (DataContext is ChangesViewModel viewModel) await viewModel.LoadAsync();
    }

    /// <summary>A right-click selects the row under it, so the menu acts on that row.</summary>
    private void EntriesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (ItemsControl.ContainerFromElement(grid, (DependencyObject)e.OriginalSource) is DataGridRow row) grid.SelectedItem = row.Item;
    }

    protected override void OnScreenClosed()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
