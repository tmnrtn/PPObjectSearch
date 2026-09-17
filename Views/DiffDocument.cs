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
/// </summary>
public static class DiffDocument
{
    private static readonly Brush RemovedLine = Frozen("#FDECEA");
    private static readonly Brush RemovedWord = Frozen("#F5A9A0");
    private static readonly Brush AddedLine = Frozen("#E7F6E9");
    private static readonly Brush AddedWord = Frozen("#9EDBA6");

    /// <summary>Marks the rows where this side has no line at all, so the two panes stay aligned.</summary>
    private static readonly Brush Absent = Frozen("#F1F2F4");

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
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            PagePadding = new Thickness(6, 4, 6, 4),

            // Wrapping would let the two panes drift out of step, so lines are kept whole and
            // the panes scroll sideways together instead.
            PageWidth = 4000
        };

        foreach (var row in (GetRows(box) ?? Array.Empty<object>()).OfType<DiffRow>())
        {
            document.Blocks.Add(BuildLine(row, side));
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("(no value)")) { Foreground = Brushes.Gray, Margin = default });
        }

        box.Document = document;
    }

    private static Paragraph BuildLine(DiffRow row, DiffSide side)
    {
        var runs = side == DiffSide.Left ? row.Left : row.Right;

        var paragraph = new Paragraph
        {
            Margin = default,
            LineHeight = 16,
            Background = BackgroundFor(row, side, runs is null)
        };

        if (runs is null)
        {
            // An empty paragraph still occupies its line, which is the point - it holds the
            // other pane's added or removed line in place opposite this one.
            paragraph.Inlines.Add(new Run(string.Empty));
            return paragraph;
        }

        var wordBrush = side == DiffSide.Left ? RemovedWord : AddedWord;

        foreach (var run in runs)
        {
            paragraph.Inlines.Add(new Run(run.Text)
            {
                Background = run.Changed && row.Kind != DiffKind.Unchanged ? wordBrush : null
            });
        }

        return paragraph;
    }

    private static Brush? BackgroundFor(DiffRow row, DiffSide side, bool missing)
    {
        if (missing) return Absent;

        return row.Kind switch
        {
            DiffKind.Unchanged => null,
            DiffKind.Removed => RemovedLine,
            DiffKind.Added => AddedLine,
            _ => side == DiffSide.Left ? RemovedLine : AddedLine
        };
    }

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
