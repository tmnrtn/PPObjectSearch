using System.Windows;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class MembershipApplyWindow : Window
{
    public MembershipApplyWindow() => InitializeComponent();

    /// <summary>
    /// True means membership may have changed, so whatever opened this window should read it
    /// again. Closing without applying reports false so nothing is re-read for no reason.
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e) => CloseReporting();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        CloseReporting();
    }

    private void CloseReporting()
    {
        // A run in progress keeps the window: closing it would not stop the writes, only hide them.
        if (DataContext is MembershipApplyViewModel { IsRunning: true }) return;

        DialogResult = DataContext is MembershipApplyViewModel { AnyWritesAttempted: true };
    }
}
