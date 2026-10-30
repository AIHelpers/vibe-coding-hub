using AiCodeAgent.Screenshots;
using SkiaSharp;

namespace AiCodeAgent.Screenshots.Tests;

/// <summary>The "second run is identical or differs by less than 0.5 % of the pixels" rule, on real PNG files.</summary>
public sealed class ImageComparerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "screenshots-tests-" + Guid.NewGuid().ToString("N"));

    public ImageComparerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A 100 x 100 white PNG with <paramref name="blackPixels"/> black pixels in the first row(s).</summary>
    private string Png(string folder, string name, int blackPixels)
    {
        using var bitmap = new SKBitmap(100, 100);
        bitmap.Erase(SKColors.White);
        for (var i = 0; i < blackPixels; i++)
            bitmap.SetPixel(i % 100, i / 100, SKColors.Black);

        var path = Path.Combine(_root, folder, name);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void SameImage_IsIdentical()
    {
        var a = Png("a", "x.png", 10);
        var b = Png("b", "x.png", 10);

        Assert.True(ImageComparer.CompareFiles(a, b).IsIdentical);
    }

    [Fact]
    public void FiftyPixelsOfTenThousand_AreHalfAPercent()
    {
        var a = Png("a", "x.png", 0);
        var b = Png("b", "x.png", 50);

        var diff = ImageComparer.CompareFiles(a, b);

        Assert.Equal(50, diff.DifferentPixels);
        Assert.Equal(0.5, diff.Percent, 6);
    }

    [Fact]
    public void Folders_PassBelowTheLimit_AndFailAtOrAboveIt()
    {
        Png("a", "small.png", 0); Png("b", "small.png", 49);   // 0.49 %
        Png("a", "edge.png", 0);  Png("b", "edge.png", 50);    // exactly 0.5 %: the rule says "less than"
        Png("a", "big.png", 0);   Png("b", "big.png", 300);    // 3 %
        Png("a", "same.png", 5);  Png("b", "same.png", 5);

        var results = ImageComparer.CompareFolders(Path.Combine(_root, "a"), Path.Combine(_root, "b"), 0.5)
            .ToDictionary(r => r.Name);

        Assert.True(results["same.png"].Passed);
        Assert.Equal("identical", results["same.png"].Verdict);
        Assert.True(results["small.png"].Passed);
        Assert.False(results["edge.png"].Passed);
        Assert.False(results["big.png"].Passed);
    }

    [Fact]
    public void FileInOnlyOneFolder_Fails()
    {
        Png("a", "only-first.png", 0);
        Png("b", "only-second.png", 0);

        var results = ImageComparer.CompareFolders(Path.Combine(_root, "a"), Path.Combine(_root, "b"), 0.5);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Passed));
    }

    [Fact]
    public void DifferentSizes_AreReportedAsAFailure()
    {
        Png("a", "x.png", 0);
        using (var small = new SKBitmap(10, 10))
        {
            small.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(small);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(_root, "b", "x.png"), data.ToArray());
        }

        var result = Assert.Single(ImageComparer.CompareFolders(Path.Combine(_root, "a"), Path.Combine(_root, "b"), 0.5));

        Assert.False(result.Passed);
        Assert.Contains("sizes", result.Verdict);
    }
}
