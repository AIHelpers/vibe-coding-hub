namespace AiCodeAgent.Screenshots;

/// <summary>How much two images of the same size differ.</summary>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="DifferentPixels">Pixels where any channel differs by more than the tolerance.</param>
internal readonly record struct PixelDiffResult(int Width, int Height, long DifferentPixels)
{
    public long TotalPixels => (long)Width * Height;

    public double Percent => TotalPixels == 0 ? 0 : DifferentPixels * 100.0 / TotalPixels;

    public bool IsIdentical => DifferentPixels == 0;
}

/// <summary>Pixel comparison of two RGBA buffers. Pure code, no imaging library, so it can be unit tested.</summary>
internal static class PixelDiff
{
    /// <param name="a">RGBA bytes (4 per pixel) of the first image.</param>
    /// <param name="b">RGBA bytes of the second image, same size.</param>
    /// <param name="channelTolerance">A channel counts as different only when it differs by MORE than this (0 = exact).</param>
    public static PixelDiffResult Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height, int channelTolerance = 0)
    {
        if (width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width), "The size cannot be negative.");
        var expected = checked((long)width * height * 4);
        if (a.Length != expected || b.Length != expected)
            throw new ArgumentException($"Both buffers must hold {width}x{height} RGBA pixels ({expected} bytes).");

        long different = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) > channelTolerance
                || Math.Abs(a[i + 1] - b[i + 1]) > channelTolerance
                || Math.Abs(a[i + 2] - b[i + 2]) > channelTolerance
                || Math.Abs(a[i + 3] - b[i + 3]) > channelTolerance)
                different++;
        }

        return new PixelDiffResult(width, height, different);
    }
}
