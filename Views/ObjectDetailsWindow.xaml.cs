using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.Models;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ObjectDetailsWindow : Window
{
    public ObjectDetailsWindow() => InitializeComponent();

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as ObjectDetailsViewModel)?.Detach();
        base.OnClosed(e);
    }

    /// <summary>
    /// Copies the clicked property's value. Which row the mouse landed on is a view question, so
    /// the view answers it and hands the property itself to the view model.
    /// </summary>
    private void OnPropertyClicked(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;

        // Null for the header row or the empty space below the rows - nothing to copy there.
        if (ItemsControl.ContainerFromElement(grid, (DependencyObject)e.OriginalSource) is not DataGridRow row) return;

        if (DataContext is ObjectDetailsViewModel viewModel && row.Item is ComponentProperty property)
        {
            viewModel.CopyPropertyCommand.Execute(property);
        }
    }
}
