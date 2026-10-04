using System.Windows;
using System.Windows.Threading;
using PPObjectSearch.Core;
using PPObjectSearch.Services;

namespace PPObjectSearch;

public partial class App : Application
{
    /// <summary>The last error shown, and when, so one that repeats does not stack up dialogs.</summary>
    private string? _lastShown;
    private DateTime _lastShownAt;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // An exception from a task nobody awaited - one of the background loads - would otherwise
        // vanish without trace; one that kills the process is at least written down first.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error($"Unhandled exception (terminating: {args.IsTerminating})", args.ExceptionObject as Exception);

        Log.Info($"Started {AppVersion.Label}");

        WindowChromeSupport.Register();
        ThemeManager.Apply(AppSettings.Load().Theme);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Exited");
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log.Error("Unhandled exception on the UI thread", e.Exception);

        // The same error raised again and again - from a binding or layout, say - is logged each
        // time but shown once, rather than as an endless queue of message boxes.
        var message = e.Exception.Message;
        if (message == _lastShown && DateTime.Now - _lastShownAt < TimeSpan.FromSeconds(10)) return;

        _lastShown = message;
        _lastShownAt = DateTime.Now;

        var copy = MessageBox.Show(
            $"{message}\n\nThe details were written to the log ({Log.CurrentFile}).\n\n" +
            "Copy the details to the clipboard?",
            "PPObjectSearch", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No);

        if (copy == MessageBoxResult.Yes) ClipboardText.TryCopy(e.Exception.ToString(), out _);
    }
}
