using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

/// <summary>
/// The environments are only known once a check has run, so their columns are built here: one per
/// environment, in sidebar order, between each grid's name columns and its Difference column.
/// </summary>
public partial class TableSettingsWindow : Window
{
    /// <summary>Columns ahead of the environments in each matrix grid.</summary>
    private const int LeadingColumns = 2;

    private readonly Dictionary<DataGrid, List<DataGridColumn>> _environmentColumns = new();

    public TableSettingsWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnViewModelChanged;
            BuildEnvironmentColumns();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableSettingsViewModel.ResultEnvironments)) BuildEnvironmentColumns();
    }

    private void BuildEnvironmentColumns()
    {
        var environments = (DataContext as TableSettingsViewModel)?.ResultEnvironments ?? [];

        foreach (var grid in new[] { TablesGrid, ColumnsGrid, EnvironmentGrid })
        {
            if (_environmentColumns.TryGetValue(grid, out var old))
            {
                foreach (var column in old) grid.Columns.Remove(column);
            }

            var added = environments.Select((name, i) => (DataGridColumn)new DataGridTemplateColumn
            {
                Header = name,
                Width = new DataGridLength(Math.Max(90, name.Length * 7 + 24)),
                CellTemplate = CellTemplate(i),
                SortMemberPath = $"Cells[{i}].Text"
            }).ToList();

            for (var i = 0; i < added.Count; i++) grid.Columns.Insert(LeadingColumns + i, added[i]);
            _environmentColumns[grid] = added;
        }
    }

    /// <summary>
    /// One environment's cell: its text, coloured by what it means - green on, muted off, faint where
    /// it has no effect or the table is missing, amber where something is wrong.
    /// </summary>
    private static DataTemplate CellTemplate(int index) => (DataTemplate)XamlReader.Parse($$"""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <TextBlock Text="{Binding Cells[{{index}}].Text}" ToolTip="{Binding Cells[{{index}}].ToolTip}"
                       Margin="8,0" VerticalAlignment="Center" FontSize="12">
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Setter Property="Foreground" Value="{DynamicResource Text}" />
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Cells[{{index}}].State}" Value="On">
                                <Setter Property="Foreground" Value="{DynamicResource Green}" />
                                <Setter Property="FontWeight" Value="SemiBold" />
                            </DataTrigger>
                            <DataTrigger Binding="{Binding Cells[{{index}}].State}" Value="Off">
                                <Setter Property="Foreground" Value="{DynamicResource Muted}" />
                            </DataTrigger>
                            <DataTrigger Binding="{Binding Cells[{{index}}].State}" Value="Inactive">
                                <Setter Property="Foreground" Value="{DynamicResource Faint}" />
                                <Setter Property="FontStyle" Value="Italic" />
                            </DataTrigger>
                            <DataTrigger Binding="{Binding Cells[{{index}}].State}" Value="Problem">
                                <Setter Property="Foreground" Value="{DynamicResource Amber}" />
                                <Setter Property="FontWeight" Value="SemiBold" />
                            </DataTrigger>
                            <DataTrigger Binding="{Binding Cells[{{index}}].State}" Value="Unknown">
                                <Setter Property="Foreground" Value="{DynamicResource Amber}" />
                            </DataTrigger>
                            <DataTrigger Binding="{Binding Cells[{{index}}].State}" Value="Absent">
                                <Setter Property="Foreground" Value="{DynamicResource Faint}" />
                            </DataTrigger>
                            <DataTrigger Binding="{Binding Cells[{{index}}].State}" Value="Text">
                                <Setter Property="FontFamily" Value="{DynamicResource MonoFont}" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
        </DataTemplate>
        """);

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is INotifyPropertyChanged vm) vm.PropertyChanged -= OnViewModelChanged;
        (DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
