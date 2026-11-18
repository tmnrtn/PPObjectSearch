using System.Windows;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class ErdView : ScreenView
{
    public ErdView() => InitializeComponent();

    protected override void OnScreenClosed()
    {
        (DataContext as IDisposable)?.Dispose();
    }

    /// <summary>Ctrl+wheel zooms the diagram; the wheel alone scrolls it as usual.</summary>
    private void Canvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || DataContext is not ErdViewModel erd) return;

        erd.Zoom *= e.Delta > 0 ? 1.1 : 1 / 1.1;
        e.Handled = true;
    }

    /// <summary>The zoom that shows the whole diagram in the space it has, never past 100%.</summary>
    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ErdViewModel { Placed: { } placed } erd) return;

        // The page's margin and border, and room for the scroll bars.
        var width = Canvas.ActualWidth - 70;
        var height = Canvas.ActualHeight - 70;
        if (width <= 0 || height <= 0) return;

        erd.Zoom = Math.Min(1, Math.Min(width / placed.Width, height / placed.Height));
    }

    /// <summary>A double-click on a table opens its details; the point is in the diagram's own units, whatever the zoom.</summary>
    private void Diagram_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || DataContext is not ErdViewModel erd) return;

        var point = e.GetPosition(Diagram);
        erd.OpenTableAt(point.X, point.Y);
        e.Handled = true;
    }
}
