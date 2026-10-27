using System.Globalization;

namespace AiCodeAgent.Core.Theming;

/// <summary>WCAG 2.x colour contrast, used by the theme tests and to make unknown syntax colours readable (E4-S2).</summary>
public static class Contrast
{
    /// <summary>WCAG AA for normal-size text.</summary>
    public const double AaText = 4.5;

    /// <summary>WCAG AA for large text and UI components such as borders.</summary>
    public const double AaLarge = 3.0;

    /// <summary>Parses <c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c> (the alpha is ignored).</summary>
    public static (int R, int G, int B) ParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) throw new FormatException("Empty colour.");
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        if (h.Length == 8) h = h.Substring(2);
        if (h.Length != 6) throw new FormatException($"Not a colour: '{hex}'.");
        return (int.Parse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static string ToHex(int r, int g, int b) => $"#{r:X2}{g:X2}{b:X2}";

    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        static double Channel(int v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    /// <summary>Contrast ratio between two colours, from 1 (identical) to 21 (black on white).</summary>
    public static double Ratio(string a, string b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// Returns <paramref name="foreground"/> unchanged when it already reaches <paramref name="minimum"/> on <paramref name="background"/>;
    /// otherwise the nearest lighter or darker shade of it (same hue, mixed towards white or black) that does.
    /// </summary>
    public static string EnsureContrast(string foreground, string background, double minimum = AaText)
    {
        var (r, g, b) = ParseHex(foreground);
        var original = ToHex(r, g, b);
        if (Ratio(original, background) >= minimum) return original;

        string? best = null;
        var bestSteps = int.MaxValue;
        foreach (var target in new[] { 255, 0 })
        {
            for (var step = 1; step <= 50; step++)
            {
                var t = step / 50.0;
                var candidate = ToHex(Mix(r, target, t), Mix(g, target, t), Mix(b, target, t));
                if (Ratio(candidate, background) >= minimum)
                {
                    if (step < bestSteps) { best = candidate; bestSteps = step; }
                    break;
                }
            }
        }
        // Neither direction reaches it (mid-grey background): take whichever of black/white is better.
        return best ?? (Ratio("#000000", background) >= Ratio("#FFFFFF", background) ? "#000000" : "#FFFFFF");
    }

    private static int Mix(int from, int to, double t) => (int)Math.Round(from + (to - from) * t);
}
