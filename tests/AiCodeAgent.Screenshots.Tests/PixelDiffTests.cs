using AiCodeAgent.Screenshots;

namespace AiCodeAgent.Screenshots.Tests;

public class PixelDiffTests
{
    private static byte[] Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = a;
        }
        return data;
    }

    [Fact]
    public void IdenticalImages_HaveNoDifference()
    {
        var a = Solid(10, 10, 1, 2, 3);

        var result = PixelDiff.Compare(a, (byte[])a.Clone(), 10, 10);

        Assert.True(result.IsIdentical);
        Assert.Equal(0, result.Percent);
        Assert.Equal(100, result.TotalPixels);
    }

    [Fact]
    public void OneDifferentPixel_IsOnePercentOfAHundred()
    {
        var a = Solid(10, 10, 0, 0, 0);
        var b = (byte[])a.Clone();
        b[4 * 7 + 1] = 200;

        var result = PixelDiff.Compare(a, b, 10, 10);

        Assert.Equal(1, result.DifferentPixels);
        Assert.Equal(1.0, result.Percent, 6);
    }

    [Fact]
    public void ADifferenceInAnyChannel_CountsOnce_PerPixel()
    {
        var a = Solid(2, 1, 0, 0, 0);
        var b = Solid(2, 1, 9, 9, 9, 0);

        Assert.Equal(2, PixelDiff.Compare(a, b, 2, 1).DifferentPixels);
    }

    [Fact]
    public void Tolerance_IgnoresTinyChannelDifferences()
    {
        var a = Solid(4, 4, 100, 100, 100);
        var b = Solid(4, 4, 102, 99, 100);

        Assert.Equal(16, PixelDiff.Compare(a, b, 4, 4).DifferentPixels);
        Assert.Equal(0, PixelDiff.Compare(a, b, 4, 4, channelTolerance: 2).DifferentPixels);
        Assert.Equal(16, PixelDiff.Compare(a, b, 4, 4, channelTolerance: 1).DifferentPixels);
    }

    [Fact]
    public void WrongBufferSize_Throws()
    {
        Assert.Throws<ArgumentException>(() => PixelDiff.Compare(new byte[16], new byte[12], 2, 2));
    }

    [Fact]
    public void EmptyImages_AreIdentical()
    {
        Assert.Equal(0, PixelDiff.Compare(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, 0, 0).Percent);
    }
}
