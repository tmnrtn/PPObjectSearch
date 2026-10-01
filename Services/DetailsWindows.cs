using System.Windows;

namespace PPObjectSearch.Services;

/// <summary>
/// The object details windows currently open, so each new one can be placed where it is seen -
/// stepped down and right from the last, rather than exactly over it - and so the main window can
/// close them all at once.
/// </summary>
public static class DetailsWindows
{
    /// <summary>How far each new window steps from the last.</summary>
    public const double CascadeStep = 28;

    private static readonly List<Window> Open = new();

    /// <summary>Raised whenever a details window opens or closes.</summary>
    public static event EventHandler? Changed;

    public static int Count => Open.Count;

    /// <summary>
    /// Places <paramref name="window"/> (before it is shown) and tracks it until it closes.
    /// The first opens centred on its owner as before; each one after steps on from the last.
    /// </summary>
    public static void Show(Window window)
    {
        if (Open.LastOrDefault() is { } last)
        {
            var previous = last.WindowState == WindowState.Normal
                ? new Rect(last.Left, last.Top, last.ActualWidth, last.ActualHeight)
                : last.RestoreBounds;

            var (left, top) = Cascade(previous, new Size(window.Width, window.Height), SystemParameters.WorkArea);

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
        }

        Open.Add(window);
        window.Closed += (_, _) =>
        {
            Open.Remove(window);
            Changed?.Invoke(null, EventArgs.Empty);
        };

        window.Show();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Closes every open details window.</summary>
    public static void CloseAll()
    {
        foreach (var window in Open.ToList()) window.Close();
    }

    /// <summary>
    /// Where the next window goes: one step down and right from the previous, or - when that would
    /// run off the work area - back to its top-left corner, so a long run of windows never walks
    /// off the screen.
    /// </summary>
    public static (double Left, double Top) Cascade(Rect previous, Size size, Rect workArea, double step = CascadeStep)
    {
        var left = previous.Left + step;
        var top = previous.Top + step;

        var width = double.IsNaN(size.Width) ? previous.Width : size.Width;
        var height = double.IsNaN(size.Height) ? previous.Height : size.Height;

        if (left + width > workArea.Right || top + height > workArea.Bottom ||
            double.IsNaN(left) || double.IsNaN(top))
        {
            left = workArea.Left + step;
            top = workArea.Top + step;
        }

        return (Math.Max(workArea.Left, left), Math.Max(workArea.Top, top));
    }
}
