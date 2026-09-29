using System.Windows;
using System.Windows.Input;

namespace PPObjectSearch.Core;

/// <summary>
/// Plumbing for the custom title bar in <c>Themes/Controls.xaml</c>. The caption buttons use the
/// stock <see cref="SystemCommands"/>; binding them once at class level means no window needs its
/// own CommandBindings.
/// </summary>
public static class WindowChromeSupport
{
    /// <summary>Dialogs show only a Close button.</summary>
    public static readonly DependencyProperty IsDialogProperty = DependencyProperty.RegisterAttached(
        "IsDialog", typeof(bool), typeof(WindowChromeSupport), new PropertyMetadata(false));

    public static bool GetIsDialog(DependencyObject d) => (bool)d.GetValue(IsDialogProperty);
    public static void SetIsDialog(DependencyObject d, bool value) => d.SetValue(IsDialogProperty, value);

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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

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
