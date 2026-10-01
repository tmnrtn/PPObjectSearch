using System.Windows.Controls;
using System.Windows.Input;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class FlowDiagramView : UserControl
{
    public FlowDiagramView() => InitializeComponent();

    /// <summary>Ctrl+wheel zooms the diagram; the wheel alone scrolls it as usual.</summary>
    private void Canvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || DataContext is not FlowDiagramViewModel diagram) return;

        diagram.Zoom += e.Delta > 0 ? 0.1 : -0.1;
        e.Handled = true;
    }
}
