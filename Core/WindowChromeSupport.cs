using System.Windows;
using System.Windows.Input;

namespace PPObjectSearch.Core;

/// <summary>
/// Plumbing for the custom title bar in <c>Themes/Controls.xaml</c>. The caption buttons use the
/// stock <see cref="SystemCommands"/>; binding them once at class level means no window needs its
/// own CommandBindings.
/// </summary>
public static partial class WindowChromeSupport
{
    /// <summary>Dialogs show only a Close button.</summary>
    public static readonly DependencyProperty IsDialogProperty = DependencyProperty.RegisterAttached(
        "IsDialog", typeof(bool), typeof(WindowChromeSupport), new PropertyMetadata(false));

    public static bool GetIsDialog(DependencyObject d) => (bool)d.GetValue(IsDialogProperty);
    public static void SetIsDialog(DependencyObject d, bool value) => d.SetValue(IsDialogProperty, value);

    /// <summary>A screen in a window of its own, which shows a button to move it back into the main window.</summary>
    public static readonly DependencyProperty IsScreenProperty = DependencyProperty.RegisterAttached(
        "IsScreen", typeof(bool), typeof(WindowChromeSupport), new PropertyMetadata(false));

    public static bool GetIsScreen(DependencyObject d) => (bool)d.GetValue(IsScreenProperty);
    public static void SetIsScreen(DependencyObject d, bool value) => d.SetValue(IsScreenProperty, value);

    /// <summary>A window with the AI assistant beside its screen, which shows the title bar's Ask AI button.</summary>
    public static readonly DependencyProperty CanAskAiProperty = DependencyProperty.RegisterAttached(
        "CanAskAi", typeof(bool), typeof(WindowChromeSupport), new PropertyMetadata(false));

    public static bool GetCanAskAi(DependencyObject d) => (bool)d.GetValue(CanAskAiProperty);
    public static void SetCanAskAi(DependencyObject d, bool value) => d.SetValue(CanAskAiProperty, value);

    /// <summary>Ask AI is showing beside the window's screen: its title-bar button reads as pressed.</summary>
    public static readonly DependencyProperty IsAiOpenProperty = DependencyProperty.RegisterAttached(
        "IsAiOpen", typeof(bool), typeof(WindowChromeSupport), new PropertyMetadata(false));

    public static bool GetIsAiOpen(DependencyObject d) => (bool)d.GetValue(IsAiOpenProperty);
    public static void SetIsAiOpen(DependencyObject d, bool value) => d.SetValue(IsAiOpenProperty, value);

    /// <summary>The title bar's Ask AI button, and Ctrl+Shift+I: shows or hides the assistant beside the screen.</summary>
    public static readonly RoutedUICommand AskAiCommand = new("Ask AI", "AskAi", typeof(WindowChromeSupport),
        new InputGestureCollection { new KeyGesture(Key.I, ModifierKeys.Control | ModifierKeys.Shift) });

    /// <summary>The screen window's title-bar button: back into the main window as a tab.</summary>
    public static readonly RoutedUICommand MoveToMainWindowCommand =
        new("Move to the main window", "MoveToMainWindow", typeof(WindowChromeSupport));

    /// <summary>
    /// A maximised WindowChrome window overhangs the screen by its resize border, so the template
    /// pads itself back in by that much.
    /// </summary>
    public static Thickness MaximizedMargin
    {
        get
        {
            var resize = SystemParameters.WindowResizeBorderThickness;
            var padded = GetSystemMetrics(SM_CXPADDEDBORDER) / GetDpiScale();
            return new Thickness(resize.Left + padded, resize.Top + padded, resize.Right + padded, resize.Bottom + padded);
        }
    }

    private const int SM_CXPADDEDBORDER = 92;

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    private static double GetDpiScale()
    {
        var source = Application.Current?.MainWindow is { } w ? PresentationSource.FromVisual(w) : null;
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    public static void Register()
    {
        Bind(SystemCommands.CloseWindowCommand, w => SystemCommands.CloseWindow(w));
        Bind(SystemCommands.MinimizeWindowCommand, w => SystemCommands.MinimizeWindow(w));
        Bind(SystemCommands.MaximizeWindowCommand, w =>
        {
            if (w.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(w);
            else SystemCommands.MaximizeWindow(w);
        });
    }

    private static void Bind(RoutedCommand command, Action<Window> action)
    {
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(
            command,
            (s, _) => action((Window)s),
            (_, e) => e.CanExecute = true));
    }
}
