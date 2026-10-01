using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

public partial class FlowDiagramView : UserControl
{
    private FlowDiagramViewModel? _diagram;

    public FlowDiagramView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch(DataContext as FlowDiagramViewModel);
    }

    /// <summary>Ctrl+wheel zooms the diagram; the wheel alone scrolls it as usual.</summary>
    private void Canvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || DataContext is not FlowDiagramViewModel diagram) return;

        diagram.Zoom += e.Delta > 0 ? 0.1 : -0.1;
        e.Handled = true;
    }

    private void Watch(FlowDiagramViewModel? diagram)
    {
        if (_diagram is not null) _diagram.PropertyChanged -= OnDiagramChanged;
        _diagram = diagram;
        if (_diagram is not null) _diagram.PropertyChanged += OnDiagramChanged;
    }

    /// <summary>
    /// A step chosen from outside the diagram - the first failure of a run - is scrolled into
    /// view. After the containers holding it have opened and laid out, hence the dispatcher.
    /// </summary>
    private void OnDiagramChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FlowDiagramViewModel.Selected) || _diagram?.Selected is not { } selected) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (FindCard(Canvas, selected) is { } card) card.BringIntoView(new Rect(-40, -60, card.ActualWidth + 80, card.ActualHeight + 120));
        }, DispatcherPriority.Loaded);
    }

    private static FrameworkElement? FindCard(DependencyObject root, FlowCardViewModel target)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is FrameworkElement { Name: "Card" } element && ReferenceEquals(element.DataContext, target)) return element;

            if (FindCard(child, target) is { } found) return found;
        }

        return null;
    }
}
