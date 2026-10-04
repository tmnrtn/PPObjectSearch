using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class EnvironmentSessionView : UserControl
{
    public EnvironmentSessionView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusMostUsefulBox();
        DataContextChanged += (_, _) => FocusMostUsefulBox();
    }

    /// <summary>
    /// The DataGrid does not select on right-click, so without this the context menu would act on
    /// whichever row was left-clicked last rather than the one under the cursor.
    /// </summary>
    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;

        // Null for the header row or the empty space below the rows - leave the selection alone.
        if (ItemsControl.ContainerFromElement(grid, (DependencyObject)e.OriginalSource) is not DataGridRow row) return;

        // A row already in the selection keeps it, so the menu can act on all of them; any other
        // row becomes the only one selected, so the menu acts on the row under the cursor.
        if (!row.IsSelected) grid.SelectedItem = row.Item;
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not EnvironmentSessionViewModel session || sender is not DataGrid grid) return;

        var selected = grid.SelectedItems.OfType<PPObjectSearch.Models.SolutionComponentItem>().ToList();

        // In grid order, so a copied list reads as the grid does. A single row needs no ordering,
        // which spares walking a large solution's rows on every click.
        if (selected.Count <= 1)
        {
            session.SetSelection(selected);
            return;
        }

        var set = selected.ToHashSet();
        session.SetSelection(grid.Items.OfType<PPObjectSearch.Models.SolutionComponentItem>().Where(set.Contains));
    }

    /// <summary>
    /// The solution picker's list closed without a pick: put the loaded solution's name back.
    /// </summary>
    private void SolutionPicker_SearchEnded(object? sender, EventArgs e)
    {
        if (DataContext is EnvironmentSessionViewModel session) session.EndSolutionSearch();
    }

    /// <summary>
    /// Focus moving between the picker's own text box and its list is still the same search; only
    /// leaving the picker altogether ends it.
    /// </summary>
    private void SolutionPicker_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ComboBox { IsKeyboardFocusWithin: false, IsDropDownOpen: false }) SolutionPicker_SearchEnded(sender, e);
    }

    /// <summary>
    /// Double-clicking a row opens its details. Only a row: a double-click on a column header (to
    /// resize it) or on the empty space below the rows is left alone.
    /// </summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement((DataGrid)sender, (DependencyObject)e.OriginalSource) is not DataGridRow row) return;
        if (DataContext is not EnvironmentSessionViewModel session) return;

        if (session.ShowDetailsCommand.CanExecute(row.Item))
        {
            session.ShowDetailsCommand.Execute(row.Item);
            e.Handled = true;
        }
    }

    /// <summary>Profiles may have come and gone since the menu last opened.</summary>
    private void AccountMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (DataContext is EnvironmentSessionViewModel session) session.RaiseBrowserProfile();
    }

    /// <summary>
    /// A connected tab is there to be searched; a new tab needs its URL first.
    /// </summary>
    private void FocusMostUsefulBox()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var target = DataContext is EnvironmentSessionViewModel { IsConnected: true }
                ? (Control)SearchBox
                : EnvironmentBox;

            target.Focus();
            Keyboard.Focus(target);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }
}
