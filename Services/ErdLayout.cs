using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using MsaglPoint = Microsoft.Msagl.Core.Geometry.Point;

namespace PPObjectSearch.Services;

/// <summary>Measures a line of text: its width at a font size, bold or not.</summary>
public delegate double ErdTextMeasure(string text, double fontSize, bool bold);

public readonly record struct ErdPoint(double X, double Y);

/// <summary>A piece of a line: straight when it has no control points, else a cubic Bézier.</summary>
public sealed record ErdSegment(ErdPoint Start, ErdPoint End, ErdPoint? Control1 = null, ErdPoint? Control2 = null)
{
    public bool IsCurve => Control1 is not null && Control2 is not null;
}

/// <summary>A row of a placed box, its text cut to fit.</summary>
public sealed record ErdPlacedRow(ErdRow Row, string Name, string Type, double Y);

/// <summary>A box where it is drawn: its top-left corner and size, its header and its rows.</summary>
public sealed record ErdPlacedBox(
    ErdBox Box, double X, double Y, double Width, double Height, double HeaderHeight,
    string Title, string? Subtitle, IReadOnlyList<ErdPlacedRow> Rows)
{
    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>
/// A line where it is drawn, and its ends' crow's-foot marks as short strokes and circles - so
/// every renderer draws the same marks without working them out again.
/// </summary>
public sealed record ErdPlacedLink(
    ErdLink Link, IReadOnlyList<ErdSegment> Path,
    IReadOnlyList<(ErdPoint From, ErdPoint To)> Strokes,
    IReadOnlyList<(ErdPoint Centre, double Radius)> Circles);

/// <summary>The placed diagram, in pixels from its top-left corner, with an optional heading.</summary>
public sealed record ErdPlacedDiagram(
    double Width, double Height, string? Title, string? Caption,
    IReadOnlyList<ErdPlacedBox> Boxes, IReadOnlyList<ErdPlacedLink> Links)
{
    public ErdPlacedBox? BoxAt(double x, double y) => Boxes.FirstOrDefault(b => b.Contains(x, y));
}

/// <summary>
/// Places an entity relationship diagram: sizes each box to its text, then lays the boxes out in
/// layers with MSAGL - tables looked up above the tables that look them up - and routes the lines
/// around them as splines.
/// </summary>
public static class ErdLayout
{
    // The sizes every renderer draws at; the boxes are measured with them.
    public const double TitleSize = 13;
    public const double SubtitleSize = 11;
    public const double RowSize = 12;
    public const double HeadingSize = 18;
    public const double CaptionSize = 12;

    public const double Padding = 10;
    public const double RowHeight = 19;
    public const double MarkerWidth = 26;
    public const double ColumnGap = 18;
    public const double CornerRadius = 6;
    public const double Margin = 24;

    private const double MinWidth = 150;
    private const double MaxWidth = 440;
    private const double MaxTypeWidth = 170;
    private const double HeadingHeight = 50;

    public static ErdPlacedDiagram Arrange(
        ErdDiagram diagram,
        ErdTextMeasure measure,
        string? title = null,
        string? caption = null,
        CancellationToken ct = default)
    {
        var graph = new GeometryGraph();
        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        var sized = new Dictionary<Node, ErdPlacedBox>();

        foreach (var box in diagram.Boxes)
        {
            var placed = Size(box, measure);
            var node = new Node(CurveFactory.CreateRectangle(placed.Width, placed.Height, new MsaglPoint()), box);
            graph.Nodes.Add(node);
            nodes[box.LogicalName] = node;
            sized[node] = placed;
        }

        var edges = new List<(Edge Edge, ErdLink Link)>();
        var loops = new List<ErdLink>();
        var depths = Depths(diagram.Links);
        foreach (var link in diagram.Links)
        {
            if (!nodes.TryGetValue(link.One, out var one) || !nodes.TryGetValue(link.Many, out var many)) continue;

            // A table looking itself up is drawn as a loop at its corner, once it is placed.
            if (one == many)
            {
                loops.Add(link);
                continue;
            }

            // The one end is the edge's source, so the layers put it above the many end. A
            // many-to-many has no such order: it runs down from whichever end the lookups put
            // higher, so it never turns their order over.
            if (link.IsManyToMany && depths.GetValueOrDefault(link.Many) < depths.GetValueOrDefault(link.One)) (one, many) = (many, one);

            var edge = new Edge(one, many) { UserData = link };
            graph.Edges.Add(edge);
            edges.Add((edge, link));
        }

        var boxes = new List<ErdPlacedBox>();
        var links = new List<ErdPlacedLink>();
        var top = string.IsNullOrWhiteSpace(title) ? Margin : Margin + HeadingHeight;

        if (graph.Nodes.Count == 0)
        {
            return new ErdPlacedDiagram(Math.Max(MinWidth, Heading(title, caption, measure)) + Margin * 2, top + Margin, title, caption, boxes, links);
        }

        var settings = new SugiyamaLayoutSettings
        {
            NodeSeparation = 36,
            LayerSeparation = 64,
            EdgeRoutingSettings = { EdgeRoutingMode = Microsoft.Msagl.Core.Routing.EdgeRoutingMode.Spline }
        };

        var cancel = new Microsoft.Msagl.Core.CancelToken();
        using (ct.Register(() => cancel.Canceled = true))
        {
            LayoutHelpers.CalculateLayout(graph, settings, cancel);
        }

        ct.ThrowIfCancellationRequested();

        // MSAGL's y runs upwards; the diagram's runs down from the top-left corner.
        var bounds = graph.BoundingBox;
        ErdPoint P(MsaglPoint p) => new(p.X - bounds.Left + Margin, bounds.Top - p.Y + top);

        foreach (var node in graph.Nodes)
        {
            var corner = P(new MsaglPoint(node.BoundingBox.Left, node.BoundingBox.Top));
            boxes.Add(sized[node] with { X = corner.X, Y = corner.Y });
        }

        foreach (var (edge, link) in edges)
        {
            if (edge.Curve is not { } curve) continue;

            var path = new List<ErdSegment>();
            Flatten(curve, path, P);
            if (path.Count > 0) links.Add(Marked(link, path));
        }

        foreach (var table in loops.GroupBy(l => l.One, StringComparer.OrdinalIgnoreCase))
        {
            var box = boxes.First(b => string.Equals(b.Box.LogicalName, table.Key, StringComparison.OrdinalIgnoreCase));
            var k = 0;
            foreach (var link in table)
            {
                // Out of the right side and back into the bottom, each loop outside the last.
                var offset = 40 + k * 18;
                var reach = 76 + k * 18;
                var right = new ErdPoint(box.X + box.Width, box.Y + box.Height - Math.Min(offset, Math.Max(8, box.Height - 8)));
                var bottom = new ErdPoint(box.X + box.Width - offset, box.Y + box.Height);
                links.Add(Marked(link, [new ErdSegment(right, bottom, right with { X = right.X + reach }, bottom with { Y = bottom.Y + reach })]));
                k++;
            }
        }

        // A loop can reach past the boxes, so the size is taken from everything drawn.
        var width = Math.Max(Heading(title, caption, measure) + Margin, boxes.Max(b => b.X + b.Width));
        var height = boxes.Max(b => b.Y + b.Height);
        foreach (var segment in links.SelectMany(l => l.Path))
        {
            foreach (var point in new[] { segment.Start, segment.End, segment.Control1 ?? segment.Start, segment.Control2 ?? segment.End })
            {
                width = Math.Max(width, point.X);
                height = Math.Max(height, point.Y);
            }
        }

        return new ErdPlacedDiagram(width + Margin, height + Margin, title, caption, boxes, links);
    }

    /// <summary>A line with its ends' marks, each pointing away from its box along the line.</summary>
    private static ErdPlacedLink Marked(ErdLink link, IReadOnlyList<ErdSegment> path)
    {
        var strokes = new List<(ErdPoint, ErdPoint)>();
        var circles = new List<(ErdPoint, double)>();

        var start = path[0];
        var end = path[^1];
        Mark(start.Start, Away(start.Start, start.Control1 ?? start.End), link.OneEnd, strokes, circles);
        Mark(end.End, Away(end.End, end.Control2 ?? end.Start), link.ManyEnd, strokes, circles);

        return new ErdPlacedLink(link, path, strokes, circles);
    }

    /// <summary>
    /// How far down the lookups put each table: one below the deepest table it looks up. A cycle
    /// of lookups stops deepening once every table has had its turn.
    /// </summary>
    private static Dictionary<string, int> Depths(IReadOnlyList<ErdLink> links)
    {
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lookups = links.Where(l => !l.IsManyToMany && !string.Equals(l.One, l.Many, StringComparison.OrdinalIgnoreCase)).ToList();
        var limit = links.Count + 1;

        for (var pass = 0; pass < limit; pass++)
        {
            var changed = false;
            foreach (var link in lookups)
            {
                var depth = Math.Min(depths.GetValueOrDefault(link.One) + 1, limit);
                if (depth <= depths.GetValueOrDefault(link.Many)) continue;

                depths[link.Many] = depth;
                changed = true;
            }

            if (!changed) break;
        }

        return depths;
    }

    /// <summary>A box's size from its text; the text cut, with an ellipsis, where the box would be too wide.</summary>
    private static ErdPlacedBox Size(ErdBox box, ErdTextMeasure measure)
    {
        var typeWidth = 0.0;
        var nameWidth = 0.0;
        foreach (var row in box.Rows)
        {
            typeWidth = Math.Max(typeWidth, measure(TypeText(row), RowSize, false));
            nameWidth = Math.Max(nameWidth, measure(row.Name, RowSize, false));
        }

        typeWidth = Math.Min(typeWidth, MaxTypeWidth);

        var rowsWidth = box.Rows.Count == 0 ? 0 : MarkerWidth + nameWidth + ColumnGap + typeWidth;
        var headerWidth = Math.Max(measure(box.Title, TitleSize, true), box.Subtitle is null ? 0 : measure(box.Subtitle, SubtitleSize, false));
        var width = Math.Clamp(Math.Max(rowsWidth, headerWidth) + Padding * 2, MinWidth, MaxWidth);

        var inner = width - Padding * 2;
        var nameRoom = inner - MarkerWidth - ColumnGap - typeWidth;
        var header = box.Subtitle is null ? 30 : 44;

        var rows = new List<ErdPlacedRow>();
        for (var i = 0; i < box.Rows.Count; i++)
        {
            var row = box.Rows[i];
            rows.Add(new ErdPlacedRow(row, Fit(row.Name, nameRoom, RowSize, false, measure),
                Fit(TypeText(row), typeWidth, RowSize, false, measure), header + 5 + i * RowHeight));
        }

        var height = header + (rows.Count == 0 ? 0 : rows.Count * RowHeight + 10);

        return new ErdPlacedBox(box, 0, 0, width, height, header,
            Fit(box.Title, inner, TitleSize, true, measure),
            box.Subtitle is null ? null : Fit(box.Subtitle, inner, SubtitleSize, false, measure), rows);
    }

    /// <summary>A lookup drawn names what it points at; anything else, its type.</summary>
    internal static string TypeText(ErdRow row) => row.References is { } references ? "→ " + references : row.Type;

    private static double Heading(string? title, string? caption, ErdTextMeasure measure) =>
        Math.Max(string.IsNullOrWhiteSpace(title) ? 0 : measure(title, HeadingSize, true),
                 string.IsNullOrWhiteSpace(caption) ? 0 : measure(caption, CaptionSize, false));

    internal static string Fit(string text, double room, double size, bool bold, ErdTextMeasure measure)
    {
        if (measure(text, size, bold) <= room) return text;

        // The longest start that fits with its ellipsis; a binary search, as names can be long.
        int low = 0, high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (measure(text[..mid] + "…", size, bold) <= room) low = mid;
            else high = mid - 1;
        }

        return text[..low] + "…";
    }

    /// <summary>A curve as lines and Béziers. Arcs and anything else are followed in short straight steps.</summary>
    private static void Flatten(ICurve curve, List<ErdSegment> path, Func<MsaglPoint, ErdPoint> p)
    {
        switch (curve)
        {
            case Curve composite:
                foreach (var segment in composite.Segments) Flatten(segment, path, p);
                break;
            case LineSegment line:
                path.Add(new ErdSegment(p(line.Start), p(line.End)));
                break;
            case CubicBezierSegment bezier:
                path.Add(new ErdSegment(p(bezier.B(0)), p(bezier.B(3)), p(bezier.B(1)), p(bezier.B(2))));
                break;
            default:
                const int steps = 8;
                var from = curve.ParStart;
                var span = curve.ParEnd - curve.ParStart;
                for (var i = 0; i < steps; i++)
                {
                    path.Add(new ErdSegment(p(curve[from + span * i / steps]), p(curve[from + span * (i + 1) / steps])));
                }

                break;
        }
    }

    private static ErdPoint Away(ErdPoint end, ErdPoint toward)
    {
        var dx = toward.X - end.X;
        var dy = toward.Y - end.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return length < 0.001 ? new ErdPoint(0, 1) : new ErdPoint(dx / length, dy / length);
    }

    /// <summary>
    /// An end's marks, read outwards from the box: the most rows nearest it (a bar for one, a
    /// crow's foot for many), then the fewest (a bar for exactly one, a circle for zero).
    /// </summary>
    private static void Mark(ErdPoint at, ErdPoint d, ErdCardinality end,
        List<(ErdPoint, ErdPoint)> strokes, List<(ErdPoint, double)> circles)
    {
        var n = new ErdPoint(-d.Y, d.X);
        ErdPoint Along(double distance, double across = 0) =>
            new(at.X + d.X * distance + n.X * across, at.Y + d.Y * distance + n.Y * across);

        if (end == ErdCardinality.ZeroOrMany)
        {
            var heel = Along(12);
            strokes.Add((heel, Along(0, 7)));
            strokes.Add((heel, Along(0)));
            strokes.Add((heel, Along(0, -7)));
        }
        else
        {
            strokes.Add((Along(7, 6), Along(7, -6)));
        }

        if (end == ErdCardinality.ExactlyOne) strokes.Add((Along(12, 6), Along(12, -6)));
        else circles.Add((Along(19), 4));
    }
}
