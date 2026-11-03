using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ReferenceDataCompareView : ScreenView
{
    public ReferenceDataCompareView() => InitializeComponent();

    /// <summary>
    /// SelectedItems is not a bindable dependency property, so the grid hands its selection to the
    /// view model here. Reconciling acts on all of it, so this has to stay in step.
    /// </summary>
    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ReferenceDataCompareViewModel viewModel) return;

        viewModel.SetSelectedRows(ResultsGrid.SelectedItems.OfType<Services.RecordComparison>());

        // The header box mirrors the row boxes: ticked, clear, or mixed.
        var selected = ResultsGrid.SelectedItems.Count;
        if (selected == 0) SelectAllBox.IsChecked = false;
        else if (selected == ResultsGrid.Items.Count) SelectAllBox.IsChecked = true;
        else SelectAllBox.IsChecked = null;
    }

    /// <summary>Anything short of every row ticked becomes every row; every row becomes none.</summary>
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItems.Count == ResultsGrid.Items.Count) ResultsGrid.UnselectAll();
        else ResultsGrid.SelectAll();
    }

    /// <summary>
    /// A row's tick adds or removes that row alone. Left to the grid, the click would first select
    /// only this row - the cell handles it even when the check box already has - and the check box
    /// would then toggle it straight back off. Handled here, as a preview, the grid never sees it.
    /// </summary>
    private void RowCheck_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridCell cell || DataGridRow.GetRowContainingElement(cell) is not { } row) return;

        row.IsSelected = !row.IsSelected;
        e.Handled = true;
    }

    /// <summary>Double-clicking a table opens the settings that decide how it is compared, which is
    /// where the work of configuring one actually happens.</summary>
    private void Entity_DoubleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ReferenceDataCompareViewModel viewModel) return;
        if (sender is not ListBoxItem { DataContext: ReferenceEntityViewModel entity }) return;

        if (viewModel.EditEntityCommand.CanExecute(entity)) viewModel.EditEntityCommand.Execute(entity);
    }

    protected override void OnScreenClosed()
    {
        (DataContext as IDisposable)?.Dispose();
    }
}
