using System.Windows;
using System.Windows.Threading;
using PPObjectSearch.Core;
using PPObjectSearch.Services;

namespace PPObjectSearch;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        WindowChromeSupport.Register();
        ThemeManager.Apply(AppSettings.Load().Theme);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "PPObjectSearch", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
