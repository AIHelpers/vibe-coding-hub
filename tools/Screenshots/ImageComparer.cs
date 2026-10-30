using SkiaSharp;

namespace AiCodeAgent.Screenshots;

/// <summary>The verdict for one file name when two image folders are compared.</summary>
internal sealed record FileComparison(string Name, string Verdict, double Percent, bool Passed);

/// <summary>Compares PNG files and folders of PNG files (SkiaSharp is already part of the app's Avalonia stack).</summary>
internal static class ImageComparer
{
    public static PixelDiffResult CompareFiles(string pathA, string pathB)
    {
        using var a = SKBitmap.Decode(pathA) ?? throw new InvalidDataException($"Cannot read image '{pathA}'.");
        using var b = SKBitmap.Decode(pathB) ?? throw new InvalidDataException($"Cannot read image '{pathB}'.");
        if (a.Width != b.Width || a.Height != b.Height)
            throw new InvalidDataException($"Different sizes: {a.Width}x{a.Height} vs {b.Width}x{b.Height}.");

        return PixelDiff.Compare(ToRgba(a), ToRgba(b), a.Width, a.Height);
    }

    /// <summary>The pixels as straight (non-premultiplied) RGBA, whatever the PNG's own format was.</summary>
    private static byte[] ToRgba(SKBitmap bitmap) =>
        ReadPixels(bitmap, new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));

    private static byte[] ReadPixels(SKBitmap bitmap, SKImageInfo info)
    {
        var buffer = new byte[info.BytesSize];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var image = SKImage.FromBitmap(bitmap);
            if (!image.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes, 0, 0))
                throw new InvalidDataException("Cannot read the pixels of the image.");
        }
        finally
        {
            handle.Free();
        }
        return buffer;
    }

    /// <summary>Compares every PNG of <paramref name="folderA"/> with the same-named file of <paramref name="folderB"/>.</summary>
    public static IReadOnlyList<FileComparison> CompareFolders(string folderA, string folderB, double thresholdPercent)
    {
        var results = new List<FileComparison>();
        var namesA = ListPngs(folderA);
        var namesB = ListPngs(folderB);

        foreach (var name in namesA.Union(namesB, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.Ordinal))
        {
            var inA = namesA.Contains(name);
            var inB = namesB.Contains(name);
            if (!inA || !inB)
            {
                results.Add(new FileComparison(name, inA ? "missing in second run" : "missing in first run", 100, false));
                continue;
            }

            try
            {
                var diff = CompareFiles(Path.Combine(folderA, name), Path.Combine(folderB, name));
                var verdict = diff.IsIdentical ? "identical" : $"{diff.Percent:0.000} % of pixels differ";
                results.Add(new FileComparison(name, verdict, diff.Percent, diff.IsIdentical || diff.Percent < thresholdPercent));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                results.Add(new FileComparison(name, ex.Message, 100, false));
            }
        }

        return results;
    }

    private static HashSet<string> ListPngs(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.png", SearchOption.TopDirectoryOnly)
                .Select(p => Path.GetFileName(p)!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
