using System.Windows;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ReconcileWindow : Window
{
    public ReconcileWindow() => InitializeComponent();

    /// <summary>
    /// True means something was written, which the comparison behind this window is now out of
    /// date about. Closing without applying reports false so nothing is re-read for no reason.
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
        DialogResult = DataContext is ReconcileViewModel { AnyWritesSucceeded: true };
    }
}
