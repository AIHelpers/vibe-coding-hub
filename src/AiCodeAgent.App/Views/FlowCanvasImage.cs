using System;
using System.IO;
using AiCodeAgent.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AiCodeAgent.App.Views;

/// <summary>E5-S8: renders the flow canvas to a PNG.</summary>
public static class FlowCanvasImage
{
    /// <summary>Pixels per canvas unit; 2 keeps text sharp when the image is pasted into a document.</summary>
    public const double Scale = 2;

    /// <summary>The size of the image for <paramref name="bounds"/>.</summary>
    public static PixelSize PixelSizeOf(FlowExportBounds bounds) =>
        new(Math.Max(1, (int)Math.Ceiling(bounds.Width * Scale)), Math.Max(1, (int)Math.Ceiling(bounds.Height * Scale)));

    /// <summary>
    /// Renders the part of <paramref name="surface"/> inside <paramref name="bounds"/>, whatever the current zoom and pan
    /// are. The surface's transform and background are replaced for the duration of the (synchronous) render and then restored.
    /// </summary>
    public static void WritePng(Canvas surface, FlowExportBounds bounds, IBrush background, Stream destination)
    {
        var transform = surface.RenderTransform;
        var origin = surface.RenderTransformOrigin;
        var previousBackground = surface.Background;
        try
        {
            surface.RenderTransformOrigin = RelativePoint.TopLeft;
            surface.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new ScaleTransform(Scale, Scale),
                    new TranslateTransform(-bounds.X * Scale, -bounds.Y * Scale)
                }
            };
            surface.Background = background;
            using var bitmap = new RenderTargetBitmap(PixelSizeOf(bounds), new Vector(96, 96));
            bitmap.Render(surface);
            bitmap.Save(destination);
        }
        finally
        {
            surface.RenderTransform = transform;
            surface.RenderTransformOrigin = origin;
            surface.Background = previousBackground;
        }
    }
}
