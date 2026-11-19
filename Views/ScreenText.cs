using System.Collections;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using PPObjectSearch.Ai;
using PPObjectSearch.ViewModels;

namespace PPObjectSearch.Views;

/// <summary>
/// A screen as text, for the AI assistant: what the user can see on it, read from its elements
/// rather than its view model, so every screen works without code of its own. Labels and values
/// read in layout order, a grid row's cells on one line; a table is its visible columns and the
/// rows its filters let through, in its sort order, read from the items rather than the realised
/// rows so scrolling makes no difference. Collapsed parts and buttons are left out. View models
/// with more to say than their screen shows - <see cref="IAiContext"/> - add it.
/// </summary>
public static class ScreenText
{
    internal const int MaxTableRows = 300;
    internal const int MaxCodeChars = 60_000;
    internal const int MaxChars = 200_000;
    private const int MaxCellChars = 400;

    /// <summary>A screen as the assistant is shown it: its text, and the environments its content comes from.</summary>
    public static AiScreen Read(DependencyObject root, string title)
    {
        var writer = new Writer();
        var reading = new Reading();
        if (root is FrameworkElement { DataContext: { } context }) reading.Environments.Add(context);
        Walk(root, writer, reading, isRoot: true);
        return new AiScreen(title, writer.ToString()) { Environments = reading.Environments.Found };
    }

    public static string Capture(DependencyObject root) => Read(root, string.Empty).Content;

    /// <summary>What a reading has met so far: view models already described, and the environments shown.</summary>
    private sealed class Reading
    {
        public HashSet<object> Described { get; } = new(ReferenceEqualityComparer.Instance);
        public AiEnvironmentFinder Environments { get; } = new();
    }

    private static void Walk(DependencyObject element, Writer writer, Reading reading, bool isRoot = false)
    {
        if (writer.IsFull) return;

        if (element is UIElement ui && !isRoot && ui.Visibility != Visibility.Visible) return;

        if (element is FrameworkElement { DataContext: { } dataContext }) reading.Environments.Add(dataContext);

        if (element is FrameworkElement { DataContext: IAiContext context } fe &&
            reading.Described.Add(context) &&
            (fe.Parent is not FrameworkElement parent || !ReferenceEquals(parent.DataContext, context)) &&
            context.DescribeForAi() is { Length: > 0 } extra)
        {
            writer.Block(extra);
        }

        switch (element)
        {
            case DataGrid grid:
                WriteTable(grid, writer, reading);
                return;

            case TextEditor editor:
                WriteCode(editor.Text, writer);
                return;

            case PasswordBox:
                return;

            case TextBox box:
                if (!string.IsNullOrWhiteSpace(box.Text))
                {
                    if (box.AcceptsReturn || box.Text.Length > 200) WriteCode(box.Text, writer);
                    else writer.Line(box.Text);
                }
                return;

            case TextBlock text:
                writer.Line(text.Text);
                return;

            case ComboBox combo:
                if ((Text(combo.SelectedItem) ?? combo.Text) is { Length: > 0 } chosen) writer.Line(chosen);
                return;

            case ToggleButton toggle when toggle is not RadioButton || toggle.IsChecked == true:
                if (Content(toggle) is { Length: > 0 } label)
                {
                    writer.Line(toggle.IsChecked == true ? $"[x] {label}" : $"[ ] {label}");
                }
                return;

            case ButtonBase button:
                // Actions are not content - but a link button shows a name or a value.
                if (IsLink(button) && Content(button) is { Length: > 0 } link) writer.Line(link);
                return;

            case ListBoxItem { IsSelected: true } selected:
                var inner = new Writer();
                foreach (var child in Children(selected)) Walk(child, inner, reading);
                writer.Line("» " + inner.ToString().Replace('\n', ' ').Trim());
                return;

            case Grid layout when layout.ColumnDefinitions.Count > 1:
                WriteGridRows(layout, writer, reading);
                return;

            case StackPanel { Orientation: Orientation.Horizontal } or WrapPanel:
                WriteOneLine(element, writer, reading);
                return;
        }

        foreach (var child in Children(element)) Walk(child, writer, reading);
    }

    /// <summary>A grid's cells, row by row: a row of short cells reads as one line - a label and its value.</summary>
    private static void WriteGridRows(Grid grid, Writer writer, Reading reading)
    {
        var rows = grid.Children.OfType<UIElement>()
            .GroupBy(Grid.GetRow)
            .OrderBy(g => g.Key);

        foreach (var row in rows)
        {
            var cells = row.OrderBy(Grid.GetColumn).Select(cell =>
            {
                var cellWriter = new Writer();
                Walk(cell, cellWriter, reading);
                return cellWriter.ToString().Trim();
            }).Where(c => c.Length > 0).ToList();

            if (cells.Count > 1 && cells.All(c => !c.Contains('\n') && c.Length < 200)) writer.Line(string.Join("  ", cells));
            else foreach (var cell in cells) writer.Block(cell);
        }
    }

    /// <summary>A row of short pieces - a chip, a count and a date - reads as one line.</summary>
    private static void WriteOneLine(DependencyObject panel, Writer writer, Reading reading)
    {
        var inner = new Writer();
        foreach (var child in Children(panel)) Walk(child, inner, reading);
        var text = inner.ToString().Trim();

        var lines = text.Split('\n');
        if (lines.Length <= 8 && lines.All(l => l.Length < 120)) writer.Line(string.Join("  ", lines));
        else writer.Block(text);
    }

    private static void WriteTable(DataGrid grid, Writer writer, Reading reading)
    {
        var columns = grid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)
            .ToList();
        if (columns.Count == 0) return;

        var items = grid.Items.Cast<object>().Where(i => i != CollectionView.NewItemPlaceholder).ToList();
        var selected = new HashSet<object>(grid.SelectedItems.Cast<object>(), ReferenceEqualityComparer.Instance);

        writer.Line(items.Count > MaxTableRows
            ? $"Table, {items.Count:N0} rows (the first {MaxTableRows:N0} follow):"
            : $"Table, {items.Count:N0} row{(items.Count == 1 ? "" : "s")}:");
        writer.Line(string.Join('\t', columns.Select(c => Cell(Text(c.Header) ?? c.SortMemberPath ?? string.Empty))));

        foreach (var item in items.Take(MaxTableRows))
        {
            if (writer.IsFull) return;

            reading.Environments.Add(item);
            var cells = columns.Select(c => Cell(CellText(c, item)));
            writer.Line((selected.Contains(item) ? "» " : "") + string.Join('\t', cells), dedupe: false);
        }

        if (selected.Count > 0) writer.Line("(» marks the selected rows)");
        writer.Line(string.Empty, dedupe: false);
    }

    private static string CellText(DataGridColumn column, object item)
    {
        try
        {
            // What Ctrl+C copies for the cell: the column's binding, through its converter and format.
            if (column.ClipboardContentBinding is not null && column.OnCopyingCellClipboardContent(item) is { } copied)
            {
                return Text(copied) ?? string.Empty;
            }

            // A template column has no binding of its own; its sort key is usually what it shows.
            if (!string.IsNullOrEmpty(column.SortMemberPath)) return Text(Resolve(item, column.SortMemberPath)) ?? string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Reflection.TargetInvocationException)
        {
            // A cell that cannot be read is left blank rather than losing the table.
        }

        return string.Empty;
    }

    /// <summary>A property path's value - dotted, no indexers - or null where it leads nowhere.</summary>
    internal static object? Resolve(object? item, string path)
    {
        foreach (var name in path.Split('.'))
        {
            if (item is null) return null;
            var property = item.GetType().GetProperty(name);
            if (property is null || property.GetIndexParameters().Length > 0) return null;
            item = property.GetValue(item);
        }

        return item;
    }

    private static void WriteCode(string text, Writer writer)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var cut = text.Length > MaxCodeChars;
        writer.Block("```\n" + (cut ? text[..MaxCodeChars] : text).TrimEnd() + "\n```" +
                     (cut ? $"\n(the first {MaxCodeChars:N0} of {text.Length:N0} characters)" : string.Empty));
    }

    private static string? Content(ContentControl control) => control.Content switch
    {
        null => null,
        string s => s,
        DependencyObject d => CaptureInline(d),
        var other => Text(other)
    };

    private static string CaptureInline(DependencyObject d)
    {
        var inner = new Writer();
        Walk(d, inner, new Reading(), isRoot: true);
        return inner.ToString().Replace('\n', ' ').Trim();
    }

    private static bool IsLink(ButtonBase button) =>
        Application.Current?.TryFindResource("LinkButton") is Style link && IsBasedOn(button.Style, link);

    private static bool IsBasedOn(Style? style, Style target)
    {
        for (; style is not null; style = style.BasedOn)
        {
            if (ReferenceEquals(style, target)) return true;
        }

        return false;
    }

    /// <summary>A value as it reads: text as it is, dates and numbers in the user's format, a view model's own ToString only if it has one.</summary>
    private static string? Text(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "Yes" : "No",
        DateTime d => d.ToString("g", CultureInfo.CurrentCulture),
        DateTimeOffset d => d.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        TextBlock t => t.Text,
        IEnumerable list and not DependencyObject => string.Join(", ", list.Cast<object>().Select(Text)),
        _ when value.GetType().GetMethod("ToString", Type.EmptyTypes)?.DeclaringType != typeof(object) => value.ToString(),
        _ => null
    };

    private static string Cell(string text)
    {
        var flat = string.Join(' ', text.Split(['\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length > MaxCellChars ? flat[..MaxCellChars] + "…" : flat;
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject element)
    {
        // Visual children where there are any; content not yet given a template is reached logically.
        var count = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(element) : 0;
        if (count > 0)
        {
            for (var i = 0; i < count; i++) yield return VisualTreeHelper.GetChild(element, i);
            yield break;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) yield return child;
    }

    /// <summary>Collects lines, dropping blanks and repeats, up to <see cref="MaxChars"/>.</summary>
    private sealed class Writer
    {
        private readonly StringBuilder _text = new();
        private string? _last;

        public bool IsFull { get; private set; }

        public void Line(string? line, bool dedupe = true)
        {
            if (IsFull || line is null) return;

            var trimmed = line.TrimEnd();
            if (dedupe && (trimmed.Trim().Length == 0 || trimmed == _last)) return;

            _last = trimmed;
            _text.Append(trimmed).Append('\n');

            if (_text.Length > MaxChars)
            {
                _text.Append("(the screen goes on, but the rest is left out to keep this short)\n");
                IsFull = true;
            }
        }

        public void Block(string text)
        {
            foreach (var line in text.Split('\n')) Line(line, dedupe: false);
        }

        public override string ToString() => _text.ToString().Trim('\n');
    }
}
