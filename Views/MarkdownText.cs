using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PPObjectSearch.Views;

/// <summary>
/// Shows an answer's Markdown in a read-only RichTextBox, so it reads formatted and still selects
/// and copies as text. Only what answers use: paragraphs, headings, bullet and numbered lists,
/// fenced code, tables (as monospace text), and **bold**, *italic* and `code` within a line.
/// </summary>
public static partial class MarkdownText
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached(
        "Markdown", typeof(string), typeof(MarkdownText),
        new PropertyMetadata(null, (d, e) =>
        {
            if (d is RichTextBox box) box.Document = Render(e.NewValue as string ?? string.Empty, box);
        }));

    public static string? GetMarkdown(DependencyObject d) => (string?)d.GetValue(MarkdownProperty);
    public static void SetMarkdown(DependencyObject d, string? value) => d.SetValue(MarkdownProperty, value);

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`|(?<![\*\w])\*(?!\s)(.+?)(?<!\s)\*(?!\w)")]
    private static partial Regex Inline();

    [GeneratedRegex(@"^\s*(?:[-*+]|(\d+)[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}")]
    private static partial Regex TableRule();

    internal static FlowDocument Render(string markdown, FrameworkElement owner)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        document.SetBinding(FlowDocument.FontFamilyProperty, new System.Windows.Data.Binding(nameof(Control.FontFamily)) { Source = owner });
        document.SetBinding(FlowDocument.FontSizeProperty, new System.Windows.Data.Binding(nameof(Control.FontSize)) { Source = owner });
        document.SetBinding(FlowDocument.ForegroundProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = owner });

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        Paragraph? paragraph = null;
        List? list = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                paragraph = null;
                list = null;
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                document.Blocks.Add(Monospace(string.Join("\n", code), owner));
                continue;
            }

            if (line.TrimStart().StartsWith('|'))
            {
                paragraph = null;
                list = null;
                var table = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++)
                {
                    if (!TableRule().IsMatch(lines[i].Replace("|", " |"))) table.Add(lines[i].Trim());
                }
                i--;
                document.Blocks.Add(Monospace(string.Join("\n", table), owner));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                paragraph = null;
                list = null;
                continue;
            }

            if (line.StartsWith('#'))
            {
                paragraph = null;
                list = null;
                var heading = new Paragraph { Margin = new Thickness(0, 6, 0, 2), FontWeight = FontWeights.SemiBold };
                AddInlines(heading.Inlines, line.TrimStart('#').Trim(), owner);
                document.Blocks.Add(heading);
                continue;
            }

            if (ListItem().Match(line) is { Success: true } item)
            {
                paragraph = null;
                var numbered = item.Groups[1].Success;
                if (list is null || (list.MarkerStyle == TextMarkerStyle.Decimal) != numbered)
                {
                    list = new List
                    {
                        MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 6),
                        Padding = new Thickness(18, 0, 0, 0)
                    };
                    if (numbered && int.TryParse(item.Groups[1].Value, out var start)) list.StartIndex = start;
                    document.Blocks.Add(list);
                }

                var content = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                AddInlines(content.Inlines, item.Groups[2].Value, owner);
                list.ListItems.Add(new System.Windows.Documents.ListItem(content));
                continue;
            }

            // A continuation of a list item's text, indented under it.
            if (list is not null && line.StartsWith("  ", StringComparison.Ordinal) && list.ListItems.LastListItem?.Blocks.LastBlock is Paragraph last)
            {
                last.Inlines.Add(new LineBreak());
                AddInlines(last.Inlines, line.Trim(), owner);
                continue;
            }

            list = null;
            if (paragraph is null)
            {
                paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                document.Blocks.Add(paragraph);
            }
            else
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            AddInlines(paragraph.Inlines, line, owner);
        }

        return document;
    }

    private static Paragraph Monospace(string text, FrameworkElement owner)
    {
        var block = new Paragraph(new Run(text))
        {
            Margin = new Thickness(0, 2, 0, 8),
            Padding = new Thickness(8, 6, 8, 6)
        };
        block.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
        block.SetResourceReference(Block.BackgroundProperty, "Surface2");
        block.SetResourceReference(Block.BorderBrushProperty, "Border");
        block.BorderThickness = new Thickness(1);
        return block;
    }

    private static void AddInlines(InlineCollection inlines, string text, FrameworkElement owner)
    {
        var at = 0;
        foreach (Match match in Inline().Matches(text))
        {
            if (match.Index > at) inlines.Add(new Run(text[at..match.Index]));

            if (match.Groups[1].Success)
            {
                inlines.Add(new Bold(new Run(match.Groups[1].Value)));
            }
            else if (match.Groups[2].Success)
            {
                var code = new Run(match.Groups[2].Value);
                code.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                code.SetResourceReference(TextElement.BackgroundProperty, "Surface2");
                inlines.Add(code);
            }
            else
            {
                inlines.Add(new Italic(new Run(match.Groups[3].Value)));
            }

            at = match.Index + match.Length;
        }

        if (at < text.Length) inlines.Add(new Run(text[at..]));
    }
}
