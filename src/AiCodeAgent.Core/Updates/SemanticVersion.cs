using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Updates;

/// <summary>
/// A version such as <c>1.2.3</c>, <c>v1.2.3-rc.1</c> or <c>0.0.0-dev.57</c>, compared by the Semantic Versioning 2.0 rules:
/// a pre-release is older than its release (<c>1.2.0-rc.1 &lt; 1.2.0</c>), numeric pre-release parts compare as numbers
/// (<c>beta.2 &lt; beta.11</c>), and build metadata after <c>+</c> is ignored. A fourth number (<c>1.2.3.4</c>, as in
/// assembly versions) is accepted and compared after the patch.
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"^[vV]?(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?(?:\.(?<rev>\d+))?(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private SemanticVersion(int major, int minor, int patch, int revision, string[] preRelease, string? build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Revision = revision;
        PreRelease = preRelease;
        Build = build;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The optional fourth number; 0 when absent.</summary>
    public int Revision { get; }

    /// <summary>The dot-separated parts after <c>-</c>; empty for a release.</summary>
    public IReadOnlyList<string> PreRelease { get; }

    /// <summary>The text after <c>+</c>, or null. Ignored when comparing.</summary>
    public string? Build { get; }

    public bool IsPreRelease => PreRelease.Count > 0;

    public static bool TryParse(string? text, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Pattern.Match(text.Trim());
        if (!match.Success) return false;

        if (!TryNumber(match.Groups["major"], out var major)
            || !TryNumber(match.Groups["minor"], out var minor)
            || !TryNumber(match.Groups["patch"], out var patch)
            || !TryNumber(match.Groups["rev"], out var rev))
            return false;

        var pre = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : Array.Empty<string>();
        version = new SemanticVersion(major, minor, patch, rev, pre, match.Groups["build"].Success ? match.Groups["build"].Value : null);
        return true;
    }

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version) ? version! : throw new FormatException($"'{text}' is not a version number.");

    private static bool TryNumber(Group group, out int value)
    {
        value = 0;
        return !group.Success || int.TryParse(group.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        var byNumbers = Major.CompareTo(other.Major);
        if (byNumbers != 0) return byNumbers;
        byNumbers = Minor.CompareTo(other.Minor);
        if (byNumbers != 0) return byNumbers;
        byNumbers = Patch.CompareTo(other.Patch);
        if (byNumbers != 0) return byNumbers;
        byNumbers = Revision.CompareTo(other.Revision);
        if (byNumbers != 0) return byNumbers;

        // A release is newer than any pre-release of the same numbers.
        if (!IsPreRelease && !other.IsPreRelease) return 0;
        if (!IsPreRelease) return 1;
        if (!other.IsPreRelease) return -1;

        for (var i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
        {
            var result = ComparePart(PreRelease[i], other.PreRelease[i]);
            if (result != 0) return result;
        }
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    private static int ComparePart(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        if (aNumeric && bNumeric)
        {
            // Compare as numbers without overflow: longer (after trimming zeros) is bigger.
            var x = a.TrimStart('0');
            var y = b.TrimStart('0');
            return x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
        }
        if (aNumeric) return -1; // numbers sort before words
        if (bNumeric) return 1;
        return string.CompareOrdinal(a, b);
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        hash.Add(Revision);
        foreach (var part in PreRelease) hash.Add(part.TrimStart('0'), StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>The version as written without a leading <c>v</c>, e.g. <c>1.2.3-rc.1</c> (build metadata is kept).</summary>
    public override string ToString()
    {
        var text = $"{Major}.{Minor}.{Patch}";
        if (Revision != 0) text += $".{Revision}";
        if (IsPreRelease) text += "-" + string.Join('.', PreRelease);
        if (Build != null) text += "+" + Build;
        return text;
    }

    public static bool operator ==(SemanticVersion? a, SemanticVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemanticVersion? a, SemanticVersion? b) => !(a == b);
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
}
