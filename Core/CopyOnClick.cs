using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace PPObjectSearch.Core;

/// <summary>
/// A value in a details pane - an email address, an id - that copies itself when clicked, and says
/// so beside the pointer. Set <c>IsEnabled</c> on a TextBlock to copy its text; set <c>Text</c> as well
/// to copy something else, such as the address behind a "No email address" placeholder.
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
        if (e.NewValue is not true) return;

        element.MouseLeftButtonUp += OnClick;
        element.MouseEnter += OnEnter;
        element.MouseLeave += OnLeave;
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
        if (element is TextBlock text) text.TextDecorations = TextDecorations.Underline;
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        element.ClearValue(FrameworkElement.CursorProperty);
        if (element is TextBlock text) text.ClearValue(TextBlock.TextDecorationsProperty);
    }

    private static void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || Value(element) is not { } value) return;

        var copied = ClipboardText.TryCopy(value, out var failure);
        Show(element, Notice(value, copied, failure));
        e.Handled = true;
    }

    private static void Show(FrameworkElement element, string message)
    {
        var notice = new ToolTip { Content = message, PlacementTarget = element, Placement = PlacementMode.Mouse, IsOpen = true };
        var timer = new DispatcherTimer { Interval = NoticeTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            notice.IsOpen = false;
        };
        timer.Start();
    }
}
