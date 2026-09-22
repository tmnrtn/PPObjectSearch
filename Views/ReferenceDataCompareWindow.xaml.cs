using System.Windows;
using System.Windows.Controls;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ReferenceDataCompareWindow : Window
{
    public ReferenceDataCompareWindow() => InitializeComponent();

    /// <summary>Double-clicking a table opens the settings that decide how it is compared, which is
    /// where the work of configuring one actually happens.</summary>
    private void Entity_DoubleClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ReferenceDataCompareViewModel viewModel) return;
        if (sender is not ListBoxItem { DataContext: ReferenceEntityViewModel entity }) return;

        if (viewModel.EditEntityCommand.CanExecute(entity)) viewModel.EditEntityCommand.Execute(entity);
    }
}
