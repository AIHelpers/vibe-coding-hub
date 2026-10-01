using System.Collections.Generic;

namespace AiCodeAgent.Core.Models;

/// <summary>
/// The kind of annotation tool used to produce a stroke.
/// </summary>
public enum AnnotationToolType
{
    Pen,
    Highlighter,
    Arrow,
    Text,
    Eraser
}

/// <summary>
/// A single stroke captured by the freehand annotation overlay. Stores the
/// tool type, color, width, and the ordered list of points (in overlay pixel
/// coordinates) that make up the stroke. Arrows use exactly two points
/// (start → end); text strokes store the anchor point in <see cref="Points"/>
/// and the typed text in <see cref="Text"/>.
/// </summary>
public record AnnotationStroke
{
    public AnnotationToolType Tool { get; init; } = AnnotationToolType.Pen;
    public string Color { get; init; } = "#FF3333";
    public double Width { get; init; } = 2.0;
    public List<AnnotationPoint> Points { get; init; } = new();
    public string? Text { get; init; }
}

/// <summary>
/// A point in a stroke, in overlay pixel coordinates relative to the preview
/// pane's top-left corner.
/// </summary>
public record AnnotationPoint(double X, double Y);

/// <summary>
/// Payload produced when the user attaches a freehand annotation to the chat.
/// Contains the strokes (as vector data), an optional cropped preview image
/// (PNG, base-64 encoded), and the user's textual note.
/// </summary>
public record AnnotationMessage
{
    /// <summary>Vector strokes in overlay pixel coordinates.</summary>
    public List<AnnotationStroke> Strokes { get; init; } = new();

    /// <summary>
    /// Cropped preview image (PNG) encoded as a base-64 string, or null if no
    /// raster capture was available.
    /// </summary>
    public string? CroppedPreviewBase64 { get; init; }

    /// <summary>MIME type of <see cref="CroppedPreviewBase64"/> (e.g. "image/png").</summary>
    public string CroppedPreviewMimeType { get; init; } = "image/png";

    /// <summary>The user's optional text note accompanying the annotation.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Bounds of the annotated region in overlay pixel coordinates.</summary>
    public AnnotationRect Bounds { get; init; } = new(0, 0, 0, 0);

    /// <summary>
    /// Renders the strokes as a compact SVG string suitable for sending to the
    /// agent as inline context. Arrows are drawn as lines with a marker; text
    /// strokes emit a <text> element; the eraser produces no output.
    /// </summary>
    public string ToSvg()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\">");
        foreach (var stroke in Strokes)
        {
            switch (stroke.Tool)
            {
                case AnnotationToolType.Pen:
                case AnnotationToolType.Highlighter:
                    if (stroke.Points.Count > 0)
                    {
                        var opacity = stroke.Tool == AnnotationToolType.Highlighter ? "0.35" : "1";
                        var points = string.Join(" ", stroke.Points);
                        sb.Append($"<polyline points=\"{points}\" fill=\"none\" stroke=\"{stroke.Color}\" stroke-width=\"{stroke.Width}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" opacity=\"{opacity}\"/>");
                    }
                    break;
                case AnnotationToolType.Arrow:
                    if (stroke.Points.Count >= 2)
                    {
                        var a = stroke.Points[0];
                        var b = stroke.Points[1];
                        sb.Append($"<line x1=\"{a.X}\" y1=\"{a.Y}\" x2=\"{b.X}\" y2=\"{b.Y}\" stroke=\"{stroke.Color}\" stroke-width=\"{stroke.Width}\" marker-end=\"url(#arrow)\"/>");
                    }
                    break;
                case AnnotationToolType.Text:
                    if (stroke.Points.Count > 0 && !string.IsNullOrEmpty(stroke.Text))
                    {
                        var p = stroke.Points[0];
                        sb.Append($"<text x=\"{p.X}\" y=\"{p.Y}\" fill=\"{stroke.Color}\" font-size=\"{stroke.Width * 8}\">{System.Net.WebUtility.HtmlEncode(stroke.Text)}</text>");
                    }
                    break;
            }
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a human-readable description of the annotation for chat contexts
    /// that cannot embed images.
    /// </summary>
    public string ToDescription()
    {
        var parts = new List<string>();
        foreach (var stroke in Strokes)
        {
            switch (stroke.Tool)
            {
                case AnnotationToolType.Pen:
                    parts.Add($"freehand circle/curve around ({stroke.Points[0].X:F0},{stroke.Points[0].Y:F0})");
                    break;
                case AnnotationToolType.Highlighter:
                    parts.Add($"highlight over ({stroke.Bounds().X:F0},{stroke.Bounds().Y:F0})");
                    break;
                case AnnotationToolType.Arrow:
                    if (stroke.Points.Count >= 2)
                        parts.Add($"arrow from ({stroke.Points[0].X:F0},{stroke.Points[0].Y:F0}) to ({stroke.Points[1].X:F0},{stroke.Points[1].Y:F0})");
                    break;
                case AnnotationToolType.Text:
                    parts.Add($"text \"{stroke.Text}\" at ({(stroke.Points.Count > 0 ? stroke.Points[0].X : 0):F0},{(stroke.Points.Count > 0 ? stroke.Points[0].Y : 0):F0})");
                    break;
            }
        }
        return string.Join("; ", parts);
    }
}

/// <summary>A rectangle in overlay pixel coordinates.</summary>
public record AnnotationRect(double X, double Y, double Width, double Height);

internal static class AnnotationStrokeExtensions
{
    /// <summary>Computes the bounding box of a stroke's points.</summary>
    public static AnnotationRect Bounds(this AnnotationStroke stroke)
    {
        if (stroke.Points.Count == 0)
            return new AnnotationRect(0, 0, 0, 0);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in stroke.Points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new AnnotationRect(minX, minY, maxX - minX, maxY - minY);
    }
}