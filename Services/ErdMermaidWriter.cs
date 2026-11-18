using System.Text;
using System.Text.RegularExpressions;

namespace PPObjectSearch.Services;

/// <summary>
/// Writes an entity relationship diagram as a Mermaid erDiagram, for a wiki, README or pull
/// request - wherever Mermaid renders. Tables are named by logical name, which Mermaid takes as it
/// is; each line is labelled with its lookup column, or a many-to-many's schema name.
/// </summary>
public static partial class ErdMermaidWriter
{
    public static string ToMermaid(ErdDiagram diagram, string? title = null)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) text.AppendLine($"%% {OneLine(title)} - drawn by PPObjectSearch");
        text.AppendLine("erDiagram");

        foreach (var link in diagram.Links)
        {
            text.AppendLine($"    {Name(link.One)} {Left(link.OneEnd)}--{Right(link.ManyEnd)} {Name(link.Many)} : \"{Quoted(link.Label)}\"");
        }

        foreach (var box in diagram.Boxes)
        {
            // A box with no columns needs no block: a table on a line is declared by it, and one
            // alone - nothing drawn to it - is declared bare.
            if (box.Rows.Count == 0)
            {
                if (!diagram.Links.Any(l => l.One == box.LogicalName || l.Many == box.LogicalName)) text.AppendLine($"    {Name(box.LogicalName)}");
                continue;
            }

            text.AppendLine($"    {Name(box.LogicalName)} {{");
            foreach (var row in box.Rows)
            {
                var keys = row.IsPrimaryKey ? " PK" : row.IsForeignKey ? " FK" : string.Empty;
                var comment = row.References is { } references ? $" \"{Quoted(references)}\"" : string.Empty;
                text.AppendLine($"        {Type(row.Type)} {Name(row.Name)}{keys}{comment}");
            }

            text.AppendLine("    }");
        }

        return text.ToString();
    }

    /// <summary>The one end, read leftwards from the line: |o zero or one, || exactly one.</summary>
    private static string Left(ErdCardinality end) => end switch
    {
        ErdCardinality.ExactlyOne => "||",
        ErdCardinality.ZeroOrMany => "}o",
        _ => "|o"
    };

    private static string Right(ErdCardinality end) => end switch
    {
        ErdCardinality.ExactlyOne => "||",
        ErdCardinality.ZeroOrMany => "o{",
        _ => "o|"
    };

    /// <summary>Mermaid's names take letters, digits, hyphens and underscores, starting with a letter or underscore.</summary>
    private static string Name(string name)
    {
        var safe = Unsafe().Replace(name, "_");
        return safe.Length == 0 || char.IsDigit(safe[0]) ? "_" + safe : safe;
    }

    private static string Type(string type) => Name(type);

    private static string Quoted(string text) => OneLine(text).Replace("\"", "'");

    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex Unsafe();
}
