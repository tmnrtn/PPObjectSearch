using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using PPObjectSearch.Core;
using PPObjectSearch.Services;

namespace PPObjectSearch.Views;

/// <summary>
/// A screen in a window of its own. The window is not owned by the main window, so either can be
/// in front; the main window closes every screen window as it closes.
/// </summary>
public sealed class ScreenWindow : Window
{
    public ScreenWindow(OpenScreen screen, ScreenView view)
    {
        Screen = screen;
        Style = (Style)Application.Current.FindResource("AppWindow");
        WindowChromeSupport.SetIsScreen(this, true);
        SetBinding(TitleProperty, new Binding(nameof(OpenScreen.Title)) { Source = screen });

        Width = view.WindowWidth;
        Height = view.WindowHeight;
        MinWidth = view.WindowMinWidth;
        MinHeight = view.WindowMinHeight;

        // Code that finds a screen's window by its view model still finds this one.
        DataContext = view.DataContext;
        Content = view;

        CommandBindings.Add(new CommandBinding(WindowChromeSupport.MoveToMainWindowCommand,
            (_, _) => ScreenHost.Current.PopIn(screen)));
    }

    public OpenScreen Screen { get; }
}

/// <summary>The screen windows the app has open, for <see cref="ScreenHost"/>.</summary>
public sealed class ScreenWindows : IScreenWindows
{
    private readonly ScreenHost _host;
    private readonly Dictionary<OpenScreen, ScreenWindow> _windows = new();

    public ScreenWindows(ScreenHost host) => _host = host;

    public void Open(OpenScreen screen)
    {
        if (screen.Content is not ScreenView view) return;

        var window = new ScreenWindow(screen, view);
        Place(window, screen);

        _windows[screen] = window;
        window.Closed += (_, _) =>
        {
            // Closed by the user; a window the host closed or released has already gone from the list.
            if (_windows.Remove(screen)) _host.WindowClosed(screen);
        };

        window.Show();
        window.Activate();
    }

    public void Close(OpenScreen screen)
    {
        if (_windows.Remove(screen, out var window)) window.Close();
    }

    public void Release(OpenScreen screen)
    {
        if (!_windows.Remove(screen, out var window)) return;

        // The view is about to go into a tab, and an element can have only one parent.
        window.Content = null;
        window.Close();
    }

    public void Activate(OpenScreen screen)
    {
        if (_windows.TryGetValue(screen, out var window)) Bring(window);
    }

    public void ActivateMain()
    {
        if (Application.Current?.MainWindow is { IsVisible: true } main) Bring(main);
    }

    private static void Bring(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Details windows step on from the last one open, so several are seen at once; anything
    /// else - and the first details window - opens over the middle of the main window.
    /// </summary>
    private void Place(Window window, OpenScreen screen)
    {
        var lastDetails = screen.IsDetails
            ? _windows.Values.LastOrDefault(w => w.Screen.IsDetails && w.IsVisible)
            : null;

        if (lastDetails is not null)
        {
            var previous = lastDetails.WindowState == WindowState.Normal
                ? new Rect(lastDetails.Left, lastDetails.Top, lastDetails.ActualWidth, lastDetails.ActualHeight)
                : lastDetails.RestoreBounds;

            var (left, top) = DetailsWindows.Cascade(previous, new Size(window.Width, window.Height), SystemParameters.WorkArea);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
            return;
        }

        // A maximised main window fills the screen, so its middle is the screen's.
        if (Application.Current?.MainWindow is not { IsVisible: true, WindowState: WindowState.Normal } main)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var work = SystemParameters.WorkArea;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Max(work.Left, main.Left + (main.ActualWidth - window.Width) / 2);
        window.Top = Math.Max(work.Top, main.Top + (main.ActualHeight - window.Height) / 2);
    }
}
