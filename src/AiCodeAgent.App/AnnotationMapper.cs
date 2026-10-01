using System;
using System.Collections.Generic;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.App;

/// <summary>
/// Maps coordinates between the annotation overlay and the underlying preview
/// pane, accounting for zoom and pan of the preview. Also computes the
/// bounding rect of a set of strokes for cropping.
/// </summary>
public class AnnotationMapper
{
    /// <summary>Horizontal pan offset of the preview, in overlay pixels.</summary>
    public double OffsetX { get; set; }

    /// <summary>Vertical pan offset of the preview, in overlay pixels.</summary>
    public double OffsetY { get; set; }

    /// <summary>Zoom factor of the preview (1.0 = 100%).</summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>
    /// Converts a point from overlay pixel coordinates to preview content
    /// coordinates (i.e. the coordinate space of the rendered app, before zoom
    /// and pan are applied).
    /// </summary>
    public AnnotationPoint OverlayToPreview(AnnotationPoint overlay)
    {
        return new AnnotationPoint(
            (overlay.X - OffsetX) / Math.Max(Zoom, 0.0001),
            (overlay.Y - OffsetY) / Math.Max(Zoom, 0.0001));
    }

    /// <summary>
    /// Converts a point from preview content coordinates back to overlay
    /// pixel coordinates.
    /// </summary>
    public AnnotationPoint PreviewToOverlay(AnnotationPoint preview)
    {
        return new AnnotationPoint(
            preview.X * Zoom + OffsetX,
            preview.Y * Zoom + OffsetY);
    }

    /// <summary>
    /// Computes the bounding rectangle (in overlay coordinates) of all strokes
    /// combined, with a small padding so the crop isn't tight against the ink.
    /// </summary>
    public static AnnotationRect ComputeBounds(IEnumerable<AnnotationStroke> strokes, double padding = 8)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var any = false;
        foreach (var s in strokes)
        {
            foreach (var p in s.Points)
            {
                any = true;
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        }
        if (!any) return new AnnotationRect(0, 0, 0, 0);
        return new AnnotationRect(
            minX - padding,
            minY - padding,
            (maxX - minX) + padding * 2,
            (maxY - minY) + padding * 2);
    }
}