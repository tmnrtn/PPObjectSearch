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
