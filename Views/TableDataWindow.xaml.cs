using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class TableDataWindow : Window
{
    public TableDataWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is not TableDataViewModel viewModel) return;

            viewModel.PropertyChanged += OnViewModelChanged;
            await viewModel.LoadAsync();
        };
        Closed += (_, _) =>
        {
            if (DataContext is not TableDataViewModel viewModel) return;

            viewModel.PropertyChanged -= OnViewModelChanged;
            viewModel.Dispose();
        };
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

    /// <summary>One text column per column read, bound to the row's cell at the same position.</summary>
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

            RowsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = columns[i].Header,
                HeaderStyle = headerStyle,
                Binding = new Binding(path) { Mode = BindingMode.OneTime },
                SortMemberPath = path,
                ElementStyle = (Style)FindResource(i == 0 ? "CellFirst" : "Cell"),
                MaxWidth = 420
            });
        }
    }
}
