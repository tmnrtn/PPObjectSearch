using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class EnvironmentSessionView : UserControl
{
    private EnvironmentSessionViewModel? _viewModel;

    public EnvironmentSessionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => FocusMostUsefulBox();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as EnvironmentSessionViewModel;

        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        UpdateOptionalColumns();
        FocusMostUsefulBox();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EnvironmentSessionViewModel.ShowSubTypeColumn)
                           or nameof(EnvironmentSessionViewModel.ShowRelatedTableColumn))
        {
            UpdateOptionalColumns();
        }
    }

    /// <summary>
    /// DataGrid columns live outside the visual tree, so they inherit no DataContext and cannot
    /// bind their Visibility - it has to be pushed to them.
    /// </summary>
    private void UpdateOptionalColumns()
    {
        if (_viewModel is null) return;

        SubTypeColumn.Visibility = _viewModel.ShowSubTypeColumn ? Visibility.Visible : Visibility.Collapsed;
        RelatedTableColumn.Visibility = _viewModel.ShowRelatedTableColumn ? Visibility.Visible : Visibility.Collapsed;
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
