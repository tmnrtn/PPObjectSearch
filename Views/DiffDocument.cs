using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PPObjectSearch.Core;

namespace PPObjectSearch.Views;

public enum DiffSide
{
    Left,
    Right
}

/// <summary>
/// Renders <see cref="DiffRow"/>s into a RichTextBox, which keeps the text selectable and
/// copyable while still allowing per-line and per-word highlighting - neither a DataGrid cell
/// nor a plain TextBox can do both.
///
/// Colours are resource references rather than brushes, so a theme change repaints an open diff.
/// </summary>
public static class DiffDocument
{
    private const double LineHeight = 19;
    private const double GutterWidth = 36;

    public static readonly DependencyProperty RowsProperty = DependencyProperty.RegisterAttached(
        "Rows",
        typeof(IEnumerable),
        typeof(DiffDocument),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty SideProperty = DependencyProperty.RegisterAttached(
        "Side",
        typeof(DiffSide),
        typeof(DiffDocument),
        new PropertyMetadata(DiffSide.Left, OnChanged));

    public static void SetRows(DependencyObject element, IEnumerable? value) => element.SetValue(RowsProperty, value);

    public static IEnumerable? GetRows(DependencyObject element) => (IEnumerable?)element.GetValue(RowsProperty);

    public static void SetSide(DependencyObject element, DiffSide value) => element.SetValue(SideProperty, value);

    public static DiffSide GetSide(DependencyObject element) => (DiffSide)element.GetValue(SideProperty);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not RichTextBox box) return;

        var side = GetSide(box);
        var document = new FlowDocument
        {
            FontSize = 11.5,
            PagePadding = new Thickness(0, 4, 8, 4),
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,

            // Wrapping would let the two panes drift out of step, so lines are kept whole and
            // the panes scroll sideways together instead.
            PageWidth = 4000
        };
        document.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
        document.SetResourceReference(TextElement.ForegroundProperty, "Text");

        var number = 0;
        foreach (var row in (GetRows(box) ?? Array.Empty<object>()).OfType<DiffRow>())
        {
            var present = (side == DiffSide.Left ? row.Left : row.Right) is not null;
            if (present) number++;

            document.Blocks.Add(BuildLine(row, side, present ? number : null));
        }

        if (document.Blocks.Count == 0)
        {
            var empty = new Paragraph(new Run("(no value)")) { Margin = new Thickness(GutterWidth, 0, 0, 0) };
            empty.SetResourceReference(TextElement.ForegroundProperty, "Faint");
            document.Blocks.Add(empty);
        }

        box.Document = document;
    }

    private static Paragraph BuildLine(DiffRow row, DiffSide side, int? lineNumber)
    {
        var runs = side == DiffSide.Left ? row.Left : row.Right;
        var paragraph = new Paragraph { Margin = default };

        paragraph.Inlines.Add(Gutter(lineNumber));

        if (runs is null)
        {
            // A gap: this side has no line where the other has one. The hatched, numberless row
            // holds the other pane's added or removed line in place opposite it.
            paragraph.SetResourceReference(TextElement.BackgroundProperty, "HatchBrush");
            paragraph.Inlines.Add(new Run(string.Empty));
            return paragraph;
        }

        var lineBrush = row.Kind switch
        {
            DiffKind.Unchanged => null,
            DiffKind.Removed => "DiffRemovedBg",
            DiffKind.Added => "DiffAddedBg",
            _ => side == DiffSide.Left ? "DiffRemovedBg" : "DiffAddedBg"
        };

        if (lineBrush is null)
        {
            paragraph.SetResourceReference(TextElement.ForegroundProperty, "Muted");
        }
        else
        {
            paragraph.SetResourceReference(TextElement.BackgroundProperty, lineBrush);
        }

        var wordBrush = side == DiffSide.Left ? "DiffRemovedWord" : "DiffAddedWord";

        foreach (var part in runs)
        {
            var run = new Run(part.Text);
            if (part.Changed && row.Kind != DiffKind.Unchanged) run.SetResourceReference(TextElement.BackgroundProperty, wordBrush);
            paragraph.Inlines.Add(run);
        }

        return paragraph;
    }

    /// <summary>
    /// The line number, as a UI element rather than text so that selecting and copying lines
    /// takes only the value, never the numbers beside it.
    /// </summary>
    private static InlineUIContainer Gutter(int? lineNumber)
    {
        var text = new TextBlock
        {
            Text = lineNumber?.ToString() ?? string.Empty,
            Width = GutterWidth,
            Padding = new Thickness(0, 0, 10, 0),
            TextAlignment = TextAlignment.Right,
            FontSize = 11,
            IsHitTestVisible = false
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Faint");

        return new InlineUIContainer(text) { BaselineAlignment = BaselineAlignment.TextBottom };
    }
}
