using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace PPObjectSearch.Core;

/// <summary>
/// A value in a details pane - an email address, an id - that copies itself when clicked, or when
/// it has keyboard focus and Enter, Space or Ctrl+C is pressed, and says so beside it. On hover and
/// on focus it is underlined with a copy glyph after it, so it can be seen to copy before it is
/// tried. Set <c>IsEnabled</c> on a TextBlock to copy its text; set <c>Text</c> as well to copy
/// something else, such as the address behind a "No email address" placeholder.
/// </summary>
public static class CopyOnClick
{
    /// <summary>What a value reads when there is none; it is never copied.</summary>
    public const string Placeholder = "—";

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(CopyOnClick), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    /// <summary>The value to copy in place of the element's text; null when there is nothing to copy.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(CopyOnClick), new PropertyMetadata(null));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(1.2);

    /// <summary>The text worth copying: none for an empty value or the placeholder.</summary>
    public static string? ValueToCopy(string? text)
    {
        var value = text?.Trim();
        return string.IsNullOrEmpty(value) || value == Placeholder ? null : value;
    }

    /// <summary>What the pointer says after a click: the value copied, shortened, or why it was not.</summary>
    public static string Notice(string value, bool copied, string? failure) => copied
        ? "Copied " + (value.Length > 60 ? value[..57] + "…" : value)
        : "Could not copy - the clipboard is held by another application" + (failure is null ? "." : $" ({failure}).");

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.MouseLeftButtonUp -= OnClick;
        element.MouseEnter -= OnEnter;
        element.MouseLeave -= OnLeave;
        element.KeyDown -= OnKey;
        element.GotKeyboardFocus -= OnFocus;
        element.LostKeyboardFocus -= OnBlur;
        if (e.NewValue is not true) return;

        element.MouseLeftButtonUp += OnClick;
        element.MouseEnter += OnEnter;
        element.MouseLeave += OnLeave;
        element.KeyDown += OnKey;
        element.GotKeyboardFocus += OnFocus;
        element.LostKeyboardFocus += OnBlur;

        // Reachable by Tab, and named for what it does.
        element.Focusable = true;
        KeyboardNavigation.SetIsTabStop(element, true);
        if (Application.Current?.TryFindResource("FocusRing") is Style ring) element.FocusVisualStyle = ring;
        System.Windows.Automation.AutomationProperties.SetHelpText(element, "Press Enter to copy");
        if (element.ReadLocalValue(FrameworkElement.ToolTipProperty) == DependencyProperty.UnsetValue)
        {
            element.ToolTip = "Click to copy";
        }
    }

    private static string? Value(FrameworkElement element) =>
        ValueToCopy(element.ReadLocalValue(TextProperty) != DependencyProperty.UnsetValue
            ? GetText(element)
            : (element as TextBlock)?.Text);

    // Only a value there is something to copy from looks clickable.
    private static void OnEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element || Value(element) is null) return;

        element.Cursor = Cursors.Hand;
        Highlight(element, true);
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        element.ClearValue(FrameworkElement.CursorProperty);
        if (!element.IsKeyboardFocused) Highlight(element, false);
    }

    private static void OnFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement element && Value(element) is not null) Highlight(element, true);
    }

    private static void OnBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement element && !element.IsMouseOver) Highlight(element, false);
    }

    private static void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || Value(element) is not { } value) return;

        var copied = ClipboardText.TryCopy(value, out var failure);
        Show(element, Notice(value, copied, failure), PlacementMode.Mouse);
        e.Handled = true;
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        var copyKey = e.Key is Key.Enter or Key.Space ||
                      (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control);
        if (!copyKey || sender is not FrameworkElement element || Value(element) is not { } value) return;

        var copied = ClipboardText.TryCopy(value, out var failure);
        Show(element, Notice(value, copied, failure), PlacementMode.Bottom);
        e.Handled = true;
    }

    /// <summary>Underlines the value and puts a copy glyph after it - or takes both away.</summary>
    private static void Highlight(FrameworkElement element, bool on)
    {
        if (element is TextBlock text)
        {
            if (on) text.TextDecorations = TextDecorations.Underline;
            else text.ClearValue(TextBlock.TextDecorationsProperty);
        }

        if (AdornerLayer.GetAdornerLayer(element) is not { } layer) return;

        foreach (var old in layer.GetAdorners(element)?.OfType<CopyGlyph>() ?? []) layer.Remove(old);
        if (on) layer.Add(new CopyGlyph(element));
    }

    /// <summary>The copy glyph drawn just after a value that copies.</summary>
    private sealed class CopyGlyph : Adorner
    {
        public CopyGlyph(UIElement element) : base(element) => IsHitTestVisible = false;

        protected override void OnRender(System.Windows.Media.DrawingContext drawing)
        {
            var element = (FrameworkElement)AdornedElement;
            var font = Application.Current?.TryFindResource("IconFont") as System.Windows.Media.FontFamily
                       ?? new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            var brush = Application.Current?.TryFindResource("Muted") as System.Windows.Media.Brush
                        ?? System.Windows.Media.Brushes.Gray;

            // The end of the text itself, not of the space the element was given.
            var width = element is TextBlock text
                ? Math.Min(element.ActualWidth, Measure(text))
                : element.ActualWidth;

            var glyph = new System.Windows.Media.FormattedText("\uE8C8", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new System.Windows.Media.Typeface(font.Source), 10, brush,
                System.Windows.Media.VisualTreeHelper.GetDpi(element).PixelsPerDip);
            drawing.DrawText(glyph, new Point(width + 5, (element.ActualHeight - glyph.Height) / 2));
        }

        private static double Measure(TextBlock text) => new System.Windows.Media.FormattedText(
            text.Text, System.Globalization.CultureInfo.CurrentUICulture, text.FlowDirection,
            new System.Windows.Media.Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, System.Windows.Media.Brushes.Black,
            System.Windows.Media.VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
    }

    private static void Show(FrameworkElement element, string message, PlacementMode placement)
    {
        var notice = new ToolTip { Content = message, PlacementTarget = element, Placement = placement, IsOpen = true };
        var timer = new DispatcherTimer { Interval = NoticeTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            notice.IsOpen = false;
        };
        timer.Start();
    }
}
