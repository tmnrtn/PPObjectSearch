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

        // Assigning SelectedItem replaces an extended selection, so the menu acts on one known row.
        if (!ReferenceEquals(grid.SelectedItem, row.Item)) grid.SelectedItem = row.Item;
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
