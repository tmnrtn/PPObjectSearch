using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class TableDataView : ScreenView
{
    public TableDataView() => InitializeComponent();

    protected override async void OnFirstShown()
    {
        if (DataContext is not TableDataViewModel viewModel) return;

        viewModel.PropertyChanged += OnViewModelChanged;
        await viewModel.LoadAsync();
    }

    protected override void OnScreenClosed()
    {
        if (DataContext is not TableDataViewModel viewModel) return;

        viewModel.PropertyChanged -= OnViewModelChanged;
        viewModel.Dispose();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableDataViewModel.ShownColumns) && sender is TableDataViewModel viewModel)
        {
            BuildColumns(viewModel.ShownColumns);
        }
    }

    /// <summary>
    /// Tells the view model which cell the copy commands act on. A column's index in the grid's
    /// own list is its position in the row's cells, whatever order the columns were dragged into.
    /// </summary>
    private void RowsGrid_CurrentCellChanged(object? sender, EventArgs e)
    {
        if (DataContext is not TableDataViewModel viewModel) return;

        var cell = RowsGrid.CurrentCell;
        viewModel.SetCurrentCell(cell.Item as TableDataRow, cell.Column is null ? -1 : RowsGrid.Columns.IndexOf(cell.Column));
    }

    /// <summary>A right-click makes the cell under the mouse current, so the menu copies that one.</summary>
    private void RowsGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not DataGridCell) element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is not DataGridCell cell) return;

        if (!cell.IsSelected)
        {
            RowsGrid.SelectedCells.Clear();
            cell.IsSelected = true;
        }
        cell.Focus();
        RowsGrid.CurrentCell = new DataGridCellInfo(cell);
    }

    /// <summary>A double-click on a row - not its column header or the scroll bar - opens it in Dynamics 365.</summary>
    private void RowsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left || DataContext is not TableDataViewModel viewModel) return;
        if (ItemsControl.ContainerFromElement(RowsGrid, e.OriginalSource as DependencyObject) is not DataGridRow { Item: TableDataRow row }) return;

        if (viewModel.OpenRecordCommand.CanExecute(row)) viewModel.OpenRecordCommand.Execute(row);
        e.Handled = true;
    }

    /// <summary>
    /// A link's click - a lookup's name, or a row's related rows - opens what it points at. The
    /// cell it is in says which row and which column.
    /// </summary>
    private void RowsGrid_LinkClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TableDataViewModel viewModel) return;

        // The link is a content element: its logical parent leads back into the visual tree.
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not DataGridCell)
        {
            element = element is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        if (element is not DataGridCell { DataContext: TableDataRow row } cell) return;

        viewModel.FollowLink(row, RowsGrid.Columns.IndexOf(cell.Column));
        e.Handled = true;
    }

    /// <summary>
    /// One text column per column read, bound to the row's cell at the same position, and sorted by
    /// the row's sort key there - a date or a number rather than the text shown. A lookup's name is
    /// a link to its row, and a column of related rows a link to them.
    /// </summary>
    private void BuildColumns(IReadOnlyList<TableDataColumn> columns)
    {
        RowsGrid.Columns.Clear();

        for (var i = 0; i < columns.Count; i++)
        {
            var path = $"Cells[{i}]";

            // The header stays plain text so copying with headers copies the name; the logical
            // name and type go in its tooltip.
            var headerStyle = new Style(typeof(DataGridColumnHeader),
                TryFindResource(i == 0 ? "FirstHeader" : typeof(DataGridColumnHeader)) as Style);
            headerStyle.Setters.Add(new Setter(ToolTipProperty, columns[i].ToolTip));

            var cellStyle = (Style)FindResource(i == 0 ? "CellFirst" : "Cell");

            if (columns[i].IsLink)
            {
                // No address: the click is handled here, not navigated. Related rows have no value,
                // so they show the same words in every row, and copy as nothing.
                var text = columns[i].IsRelationship
                    ? new Binding { Source = "View rows", Mode = BindingMode.OneTime }
                    : new Binding(path) { Mode = BindingMode.OneTime };

                RowsGrid.Columns.Add(new DataGridHyperlinkColumn
                {
                    Header = columns[i].Header,
                    HeaderStyle = headerStyle,
                    ContentBinding = text,
                    ClipboardContentBinding = new Binding(path) { Mode = BindingMode.OneTime },
                    SortMemberPath = $"SortKeys[{i}]",
                    CanUserSort = !columns[i].IsRelationship,
                    ElementStyle = cellStyle,
                    MaxWidth = 420
                });
                continue;
            }

            RowsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i].Header,
                HeaderStyle = headerStyle,
                Binding = new Binding(path) { Mode = BindingMode.OneTime },
                SortMemberPath = $"SortKeys[{i}]",
                ElementStyle = cellStyle,
                MaxWidth = 420
            });
        }
    }
}
