using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PPObjectSearch.Services;

namespace PPObjectSearch.Views;

/// <summary>
/// Draws a placed entity relationship diagram with WPF - the preview on screen, and the PNG and
/// clipboard image - in the same colours and at the same sizes as the SVG.
/// </summary>
public static class ErdDrawing
{
    private static readonly Typeface Regular = new(new FontFamily(ErdPalette.FontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBold = new(new FontFamily(ErdPalette.FontFamily), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>The largest side a saved image may have, and the most pixels; a larger diagram is saved at a smaller scale.</summary>
    private const double MaxSide = 16000;
    private const double MaxPixels = 64_000_000;

    private static readonly object MeasureLock = new();
    private static GlyphTypeface? _regularGlyphs;
    private static GlyphTypeface? _boldGlyphs;
    private static bool _glyphsRead;

    /// <summary>
    /// Text widths from the font's own advance widths, so the layout can be worked out away from
    /// the UI thread. Where the font is missing a character, or the font itself, a fair guess.
    /// </summary>
    public static double Measure(string text, double fontSize, bool bold)
    {
        lock (MeasureLock)
        {
            if (!_glyphsRead)
            {
                _glyphsRead = true;
                if (Regular.TryGetGlyphTypeface(out var regular)) _regularGlyphs = regular;
                if (SemiBold.TryGetGlyphTypeface(out var semiBold)) _boldGlyphs = semiBold;
            }

            var glyphs = bold ? _boldGlyphs : _regularGlyphs;
            var width = 0.0;
            foreach (var c in text)
            {
                width += glyphs is not null && glyphs.CharacterToGlyphMap.TryGetValue(c, out var glyph)
                    ? glyphs.AdvanceWidths[glyph] * fontSize
                    : fontSize * (bold ? 0.6 : 0.55);
            }

            return width;
        }
    }

    public static DrawingGroup Draw(ErdPlacedDiagram diagram)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brush(ErdPalette.Paper), null, new Rect(0, 0, diagram.Width, diagram.Height));

            if (!string.IsNullOrWhiteSpace(diagram.Title))
            {
                Text(dc, diagram.Title, ErdLayout.Margin, ErdLayout.Margin + 18, ErdLayout.HeadingSize, true, ErdPalette.Text);
                if (!string.IsNullOrWhiteSpace(diagram.Caption))
                {
                    Text(dc, diagram.Caption, ErdLayout.Margin, ErdLayout.Margin + 38, ErdLayout.CaptionSize, false, ErdPalette.Muted);
                }
            }

            var line = Pen(ErdPalette.Line, 1.2);
            var dashed = Pen(ErdPalette.Line, 1.2, new DashStyle([5 / 1.2, 4 / 1.2], 0));
            var paper = Brush(ErdPalette.Paper);

            foreach (var link in diagram.Links)
            {
                dc.DrawGeometry(null, link.Link.IsManyToMany ? dashed : line, PathGeometry(link.Path));
                foreach (var (from, to) in link.Strokes) dc.DrawLine(line, Point(from), Point(to));
                foreach (var (centre, radius) in link.Circles) dc.DrawEllipse(paper, line, Point(centre), radius, radius);
            }

            foreach (var box in diagram.Boxes) Box(dc, box);
        }

        group.Freeze();
        return group;
    }

    private static void Box(DrawingContext dc, ErdPlacedBox box)
    {
        var outside = box.Box.IsOutside;
        var border = outside
            ? Pen(ErdPalette.OutsideBorder, 1, new DashStyle([4, 3], 0))
            : Pen(ErdPalette.Border, 1);
        var bounds = new Rect(box.X, box.Y, box.Width, box.Height);
        var r = ErdLayout.CornerRadius;

        dc.DrawRoundedRectangle(Brush(ErdPalette.Paper), null, bounds, r, r);

        // The header's fill, clipped to the box so its top corners round with it.
        var header = box.Rows.Count == 0 ? box.Height : box.HeaderHeight;
        dc.PushClip(new RectangleGeometry(bounds, r, r));
        dc.DrawRectangle(Brush(outside ? ErdPalette.OutsideHeader : ErdPalette.Header), null, new Rect(box.X, box.Y, box.Width, header));
        dc.Pop();

        if (box.Rows.Count > 0) dc.DrawLine(Pen(ErdPalette.Border, 1), new Point(box.X, box.Y + header), new Point(box.X + box.Width, box.Y + header));
        dc.DrawRoundedRectangle(null, border, bounds, r, r);

        var x = box.X + ErdLayout.Padding;
        Text(dc, box.Title, x, box.Y + 19, ErdLayout.TitleSize, true, outside ? ErdPalette.Muted : ErdPalette.Text);
        if (box.Subtitle is { } subtitle) Text(dc, subtitle, x, box.Y + 35, ErdLayout.SubtitleSize, false, ErdPalette.Muted);

        foreach (var row in box.Rows)
        {
            var baseline = box.Y + row.Y + 14;
            if (row.Row.Marker is { Length: > 0 } marker)
            {
                Text(dc, marker, x, baseline, 10, true, row.Row.IsPrimaryKey ? ErdPalette.PrimaryKey : ErdPalette.ForeignKey);
            }

            Text(dc, row.Name, x + ErdLayout.MarkerWidth, baseline, ErdLayout.RowSize, row.Row.IsRequired, ErdPalette.Text);
            Text(dc, row.Type, box.X + box.Width - ErdLayout.Padding, baseline, ErdLayout.RowSize, false,
                row.Row.IsForeignKey ? ErdPalette.ForeignKey : ErdPalette.Muted, alignRight: true);
        }
    }

    /// <summary>The diagram as an image, at a scale - cut down for a diagram too big to hold at it.</summary>
    public static BitmapSource Render(ErdPlacedDiagram diagram, double scale = 2)
    {
        scale = Math.Min(scale, Math.Min(MaxSide / diagram.Width, MaxSide / diagram.Height));
        scale = Math.Min(scale, Math.Sqrt(MaxPixels / (diagram.Width * diagram.Height)));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawDrawing(Draw(diagram));
        }

        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(diagram.Width * scale)), Math.Max(1, (int)Math.Ceiling(diagram.Height * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static void SavePng(BitmapSource image, Stream stream)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }

    private static void Text(DrawingContext dc, string text, double x, double baseline, double size, bool bold, string colour, bool alignRight = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            bold ? SemiBold : Regular, size, Brush(colour), 1.0);
        dc.DrawText(formatted, new Point(alignRight ? x - formatted.WidthIncludingTrailingWhitespace : x, baseline - formatted.Baseline));
    }

    private static Geometry PathGeometry(IReadOnlyList<ErdSegment> path)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            ErdPoint? at = null;
            foreach (var segment in path)
            {
                if (at is not { } current || Math.Abs(current.X - segment.Start.X) > 0.01 || Math.Abs(current.Y - segment.Start.Y) > 0.01)
                {
                    context.BeginFigure(Point(segment.Start), false, false);
                }

                if (segment is { Control1: { } c1, Control2: { } c2 }) context.BezierTo(Point(c1), Point(c2), Point(segment.End), true, true);
                else context.LineTo(Point(segment.End), true, true);
                at = segment.End;
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point Point(ErdPoint p) => new(p.X, p.Y);

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    private static SolidColorBrush Brush(string colour)
    {
        lock (Brushes)
        {
            if (Brushes.TryGetValue(colour, out var brush)) return brush;

            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
            brush.Freeze();
            return Brushes[colour] = brush;
        }
    }

    private static Pen Pen(string colour, double thickness, DashStyle? dash = null)
    {
        var pen = new Pen(Brush(colour), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dash is not null) pen.DashStyle = dash;
        pen.Freeze();
        return pen;
    }
}
