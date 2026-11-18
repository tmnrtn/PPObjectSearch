using System.Globalization;
using System.Security;
using System.Text;

namespace PPObjectSearch.Services;

/// <summary>
/// The colours a diagram is drawn in. Light only: an exported diagram goes into documents and
/// slides, which are mostly white, and the preview shows it as it will be saved.
/// </summary>
public static class ErdPalette
{
    public const string Paper = "#FFFFFF";
    public const string Text = "#18181B";
    public const string Muted = "#62626B";
    public const string Faint = "#8E8E96";
    public const string Border = "#A9B4C8";
    public const string Header = "#E9EFFC";
    public const string OutsideBorder = "#B4B4BC";
    public const string OutsideHeader = "#F4F4F5";
    public const string Line = "#5B6475";
    public const string PrimaryKey = "#9A6400";
    public const string ForeignKey = "#1F52C4";
    public const string FontFamily = "Segoe UI";
}

/// <summary>
/// Writes a placed entity relationship diagram as SVG - a vector image for documents, wikis and
/// slides that stays sharp at any size. Each box and line carries a title, which most viewers show
/// on hover.
/// </summary>
public static class ErdSvgWriter
{
    public static string ToSvg(ErdPlacedDiagram diagram)
    {
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(diagram.Width)}\" height=\"{N(diagram.Height)}\" viewBox=\"0 0 {N(diagram.Width)} {N(diagram.Height)}\"");
        svg.AppendLine($" font-family=\"{ErdPalette.FontFamily}, 'Segoe UI Variable', Helvetica, Arial, sans-serif\">");

        if (!string.IsNullOrWhiteSpace(diagram.Title)) svg.AppendLine($"  <title>{X(diagram.Title)}</title>");
        svg.AppendLine($"  <rect width=\"100%\" height=\"100%\" fill=\"{ErdPalette.Paper}\" />");

        if (!string.IsNullOrWhiteSpace(diagram.Title))
        {
            svg.AppendLine($"  <text x=\"{N(ErdLayout.Margin)}\" y=\"{N(ErdLayout.Margin + 18)}\" font-size=\"{N(ErdLayout.HeadingSize)}\" font-weight=\"600\" fill=\"{ErdPalette.Text}\">{X(diagram.Title)}</text>");
            if (!string.IsNullOrWhiteSpace(diagram.Caption))
            {
                svg.AppendLine($"  <text x=\"{N(ErdLayout.Margin)}\" y=\"{N(ErdLayout.Margin + 38)}\" font-size=\"{N(ErdLayout.CaptionSize)}\" fill=\"{ErdPalette.Muted}\">{X(diagram.Caption)}</text>");
            }
        }

        // Lines first, so the boxes sit on top of where they meet them.
        svg.AppendLine("  <g fill=\"none\" stroke-width=\"1.2\" stroke-linecap=\"round\">");
        foreach (var link in diagram.Links)
        {
            var dash = link.Link.IsManyToMany ? " stroke-dasharray=\"5 4\"" : string.Empty;
            svg.AppendLine($"    <g stroke=\"{ErdPalette.Line}\">");
            svg.AppendLine($"      <title>{X(Describe(link.Link))}</title>");
            svg.AppendLine($"      <path d=\"{PathData(link.Path)}\"{dash} />");
            foreach (var (from, to) in link.Strokes)
            {
                svg.AppendLine($"      <line x1=\"{N(from.X)}\" y1=\"{N(from.Y)}\" x2=\"{N(to.X)}\" y2=\"{N(to.Y)}\" />");
            }

            foreach (var (centre, radius) in link.Circles)
            {
                svg.AppendLine($"      <circle cx=\"{N(centre.X)}\" cy=\"{N(centre.Y)}\" r=\"{N(radius)}\" fill=\"{ErdPalette.Paper}\" />");
            }

            svg.AppendLine("    </g>");
        }

        svg.AppendLine("  </g>");

        foreach (var box in diagram.Boxes) Box(svg, box);

        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    /// <summary>"account (parentcustomerid) to contact" - a line's hover text.</summary>
    internal static string Describe(ErdLink link) => link.IsManyToMany
        ? $"{link.One} many-to-many {link.Many} ({link.Label})"
        : $"{link.Many}.{link.Label} looks up {link.One}";

    private static void Box(StringBuilder svg, ErdPlacedBox box)
    {
        var outside = box.Box.IsOutside;
        var border = outside ? ErdPalette.OutsideBorder : ErdPalette.Border;
        var dash = outside ? " stroke-dasharray=\"4 3\"" : string.Empty;
        var r = N(ErdLayout.CornerRadius);

        svg.AppendLine($"  <g transform=\"translate({N(box.X)},{N(box.Y)})\">");
        svg.AppendLine($"    <title>{X(box.Box.LogicalName + (outside ? " - outside the solutions" : string.Empty))}</title>");
        svg.AppendLine($"    <rect width=\"{N(box.Width)}\" height=\"{N(box.Height)}\" rx=\"{r}\" fill=\"{ErdPalette.Paper}\" />");

        // The header's fill, its top corners rounded with the box's and its bottom square.
        var header = box.Rows.Count == 0 ? box.Height : box.HeaderHeight;
        svg.AppendLine($"    <path d=\"{HeaderPath(box.Width, header, box.Rows.Count == 0)}\" fill=\"{(outside ? ErdPalette.OutsideHeader : ErdPalette.Header)}\" />");
        if (box.Rows.Count > 0)
        {
            svg.AppendLine($"    <line x1=\"0\" y1=\"{N(header)}\" x2=\"{N(box.Width)}\" y2=\"{N(header)}\" stroke=\"{border}\" />");
        }

        svg.AppendLine($"    <rect width=\"{N(box.Width)}\" height=\"{N(box.Height)}\" rx=\"{r}\" fill=\"none\" stroke=\"{border}\"{dash} />");

        var p = N(ErdLayout.Padding);
        svg.AppendLine($"    <text x=\"{p}\" y=\"19\" font-size=\"{N(ErdLayout.TitleSize)}\" font-weight=\"600\" fill=\"{(outside ? ErdPalette.Muted : ErdPalette.Text)}\">{X(box.Title)}</text>");
        if (box.Subtitle is { } subtitle)
        {
            svg.AppendLine($"    <text x=\"{p}\" y=\"35\" font-size=\"{N(ErdLayout.SubtitleSize)}\" fill=\"{ErdPalette.Muted}\">{X(subtitle)}</text>");
        }

        foreach (var row in box.Rows)
        {
            var baseline = N(row.Y + 14);
            if (row.Row.Marker is { Length: > 0 } marker)
            {
                var colour = row.Row.IsPrimaryKey ? ErdPalette.PrimaryKey : ErdPalette.ForeignKey;
                svg.AppendLine($"    <text x=\"{p}\" y=\"{baseline}\" font-size=\"10\" font-weight=\"600\" fill=\"{colour}\">{marker}</text>");
            }

            var weight = row.Row.IsRequired ? " font-weight=\"600\"" : string.Empty;
            svg.AppendLine($"    <text x=\"{N(ErdLayout.Padding + ErdLayout.MarkerWidth)}\" y=\"{baseline}\" font-size=\"{N(ErdLayout.RowSize)}\"{weight} fill=\"{ErdPalette.Text}\">{X(row.Name)}</text>");
            svg.AppendLine($"    <text x=\"{N(box.Width - ErdLayout.Padding)}\" y=\"{baseline}\" font-size=\"{N(ErdLayout.RowSize)}\" text-anchor=\"end\" fill=\"{(row.Row.IsForeignKey ? ErdPalette.ForeignKey : ErdPalette.Muted)}\">{X(row.Type)}</text>");
        }

        svg.AppendLine("  </g>");
    }

    private static string HeaderPath(double width, double height, bool roundBottom)
    {
        var r = ErdLayout.CornerRadius;
        var bottom = roundBottom
            ? $"V{N(height - r)} Q{N(width)},{N(height)} {N(width - r)},{N(height)} H{N(r)} Q0,{N(height)} 0,{N(height - r)}"
            : $"V{N(height)} H0";
        return $"M0,{N(r)} Q0,0 {N(r)},0 H{N(width - r)} Q{N(width)},0 {N(width)},{N(r)} {bottom} Z";
    }

    private static string PathData(IReadOnlyList<ErdSegment> path)
    {
        var d = new StringBuilder();
        ErdPoint? at = null;
        foreach (var segment in path)
        {
            if (at is not { } current || Math.Abs(current.X - segment.Start.X) > 0.01 || Math.Abs(current.Y - segment.Start.Y) > 0.01)
            {
                d.Append($"M{N(segment.Start.X)},{N(segment.Start.Y)} ");
            }

            d.Append(segment is { Control1: { } c1, Control2: { } c2 }
                ? $"C{N(c1.X)},{N(c1.Y)} {N(c2.X)},{N(c2.Y)} {N(segment.End.X)},{N(segment.End.Y)} "
                : $"L{N(segment.End.X)},{N(segment.End.Y)} ");
            at = segment.End;
        }

        return d.ToString().TrimEnd();
    }

    private static string N(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);

    private static string X(string text) => SecurityElement.Escape(text) ?? string.Empty;
}
