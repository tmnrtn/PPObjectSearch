using System.Windows;

namespace PPObjectSearch.Core;

/// <summary>
/// Attached properties read by the control templates in <c>Themes/Controls.xaml</c>, so a view can
/// ask for a placeholder, an inline label or the "filter is active" look without a template of
/// its own.
/// </summary>
public static class Ui
{
    /// <summary>Faint text shown in an empty text box.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);

    /// <summary>A key hint at the right of a text box, e.g. "Esc".</summary>
    public static readonly DependencyProperty KeyHintProperty = DependencyProperty.RegisterAttached(
        "KeyHint", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetKeyHint(DependencyObject d) => (string?)d.GetValue(KeyHintProperty);
    public static void SetKeyHint(DependencyObject d, string? value) => d.SetValue(KeyHintProperty, value);

    /// <summary>Muted text at the right of a text box, e.g. "Environment names match too".</summary>
    public static readonly DependencyProperty TrailingTextProperty = DependencyProperty.RegisterAttached(
        "TrailingText", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetTrailingText(DependencyObject d) => (string?)d.GetValue(TrailingTextProperty);
    public static void SetTrailingText(DependencyObject d, string? value) => d.SetValue(TrailingTextProperty, value);

    /// <summary>A Segoe Fluent Icons glyph drawn at the left of a text box.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string? value) => d.SetValue(IconProperty, value);

    /// <summary>A muted label drawn inside a dropdown, before its value - "Solution", "Type".</summary>
    public static readonly DependencyProperty InlineLabelProperty = DependencyProperty.RegisterAttached(
        "InlineLabel", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetInlineLabel(DependencyObject d) => (string?)d.GetValue(InlineLabelProperty);
    public static void SetInlineLabel(DependencyObject d, string? value) => d.SetValue(InlineLabelProperty, value);

    /// <summary>Replaces the dropdown's selection box text, for when the list wording is longer
    /// than the closed control needs ("All types (4,812)" reads as "All").</summary>
    public static readonly DependencyProperty SelectionTextProperty = DependencyProperty.RegisterAttached(
        "SelectionText", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetSelectionText(DependencyObject d) => (string?)d.GetValue(SelectionTextProperty);
    public static void SetSelectionText(DependencyObject d, string? value) => d.SetValue(SelectionTextProperty, value);

    /// <summary>The accent "a filter is applied" look on a dropdown.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(Ui), new PropertyMetadata(false));

    public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject d, bool value) => d.SetValue(IsActiveProperty, value);

    /// <summary>A small count shown after a segmented item or tab header.</summary>
    public static readonly DependencyProperty CountProperty = DependencyProperty.RegisterAttached(
        "Count", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetCount(DependencyObject d) => (string?)d.GetValue(CountProperty);
    public static void SetCount(DependencyObject d, string? value) => d.SetValue(CountProperty, value);

    /// <summary>A left click opens the button's ContextMenu beneath it - account and overflow menus.</summary>
    public static readonly DependencyProperty OpensMenuProperty = DependencyProperty.RegisterAttached(
        "OpensMenu", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnOpensMenuChanged));

    public static bool GetOpensMenu(DependencyObject d) => (bool)d.GetValue(OpensMenuProperty);
    public static void SetOpensMenu(DependencyObject d, bool value) => d.SetValue(OpensMenuProperty, value);

    private static void OnOpensMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Primitives.ButtonBase button) return;

        button.Click -= OpenMenu;
        if (e.NewValue is true) button.Click += OpenMenu;
    }

    private static void OpenMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element) return;

        menu.PlacementTarget = element;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = element.DataContext;
        menu.IsOpen = true;
    }

    /// <summary>The colour of the dot drawn before a segmented item.</summary>
    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.RegisterAttached(
        "DotBrush", typeof(System.Windows.Media.Brush), typeof(Ui), new PropertyMetadata(null));

    public static System.Windows.Media.Brush? GetDotBrush(DependencyObject d) => (System.Windows.Media.Brush?)d.GetValue(DotBrushProperty);
    public static void SetDotBrush(DependencyObject d, System.Windows.Media.Brush? value) => d.SetValue(DotBrushProperty, value);
}
