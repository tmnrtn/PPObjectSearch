using System.Windows;

namespace PPObjectSearch.Services;

/// <summary>
/// The object details screens open across every tab, so the main window can close them all at
/// once, and so each one opened as a window is placed where it is seen - stepped down and right
/// from the last, rather than exactly over it.
/// </summary>
public static class DetailsWindows
{
    /// <summary>How far each new window steps from the last.</summary>
    public const double CascadeStep = 28;

    /// <summary>Raised whenever a screen - details or not - opens or closes.</summary>
    public static event EventHandler? Changed
    {
        add => ScreenHost.Current.Changed += value;
        remove => ScreenHost.Current.Changed -= value;
    }

    public static int Count => ScreenHost.Current.DetailsCount;

    /// <summary>Closes every open details screen, in tabs and in windows.</summary>
    public static void CloseAll() => ScreenHost.Current.CloseAll(s => s.IsDetails);

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
