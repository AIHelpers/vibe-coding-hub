namespace AiCodeAgent.App.CommandPalette;

/// <summary>
/// Static match helpers used by the command palette search.
/// Ranking is tiered: exact title > title prefix > word-start match > contiguous substring
/// (title first, then keywords / category) > scattered fuzzy. Tiers are 1000+ apart, so the
/// small in-tier adjustments (shorter title, recently used) never promote a weaker tier.
/// </summary>
public static class FuzzyMatcher
{
    public const int ExactTitle = 10000;
    public const int TitlePrefix = 8000;
    public const int TitleWordStart = 6000;
    public const int TitleSubstring = 4000;
    public const int KeywordWordStart = 3000;
    public const int KeywordSubstring = 2000;

    /// <summary>Scores of this value or above are real (non-scattered) matches.</summary>
    public const int StrongMatchThreshold = KeywordSubstring;

    /// <summary>Scattered matches (all letters in order, anywhere) score below this.</summary>
    public const int FuzzyCeiling = 1000;

    /// <summary>True if <paramref name="query"/> characters appear in order within <paramref name="text"/>.</summary>
    public static bool IsMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(query))
            return true;
        if (string.IsNullOrEmpty(text))
            return false;

        var qi = 0;
        for (var ti = 0; ti < text.Length && qi < query.Length; ti++)
        {
            if (char.ToUpperInvariant(text[ti]) == char.ToUpperInvariant(query[qi]))
                qi++;
        }

        return qi == query.Length;
    }

    /// <summary>
    /// Scores one text against the query. Returns 0 when there is no match.
    /// The result is in one of the tiers above (plus a small shorter-text bonus).
    /// </summary>
    public static int Score(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 1000;
        if (string.IsNullOrEmpty(text))
            return 0;

        query = query.Trim();
        var tier = TierOf(text, query, allowWordStart: true);
        if (tier > 0)
            return tier + LengthBonus(text);

        return ScoreFuzzy(text, query);
    }

    /// <summary>
    /// Scores a palette entry: the title counts most, then keywords and the category.
    /// Returns 0 when there is no match.
    /// </summary>
    public static int ScoreEntry(ICommandPaletteEntry entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return 1000;

        query = query.Trim();
        var title = entry.Title ?? string.Empty;

        if (string.Equals(title, query, StringComparison.OrdinalIgnoreCase))
            return ExactTitle + LengthBonus(title);

        var titleTier = TierOf(title, query, allowWordStart: true);
        if (titleTier > 0)
            return titleTier + LengthBonus(title);

        // Keywords and category: word-start, then substring (never "prefix of everything").
        var best = 0;
        foreach (var other in OtherFields(entry))
        {
            var tier = TierOf(other, query, allowWordStart: true);
            if (tier <= 0)
                continue;
            // Prefix / word-start in a keyword is stronger than a plain substring of one.
            best = Math.Max(best, tier >= TitleWordStart ? KeywordWordStart : KeywordSubstring);
        }
        if (best > 0)
            return best + LengthBonus(title);

        return ScoreFuzzy(entry.SearchText ?? title, query);
    }

    private static IEnumerable<string> OtherFields(ICommandPaletteEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Category))
            yield return entry.Category;
        if (entry.Keywords != null)
        {
            foreach (var k in entry.Keywords)
            {
                if (!string.IsNullOrEmpty(k))
                    yield return k;
            }
        }
    }

    /// <summary>
    /// Returns the tier a single text reaches for the query (0 = none):
    /// prefix, word-start (every query word starts some word of the text) or substring.
    /// </summary>
    private static int TierOf(string text, string query, bool allowWordStart)
    {
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return TitlePrefix;

        if (allowWordStart && AllTokensStartAWord(text, query))
            return TitleWordStart;

        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            return TitleSubstring;

        return 0;
    }

    private static bool AllTokensStartAWord(string text, string query)
    {
        var words = SplitWords(text);
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        foreach (var token in tokens)
        {
            var found = false;
            foreach (var w in words)
            {
                if (w.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }

    /// <summary>Splits on anything that is not a letter or digit, and at camelCase boundaries.</summary>
    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                if (start >= 0) { words.Add(text[start..i]); start = -1; }
                continue;
            }

            if (start < 0)
            {
                start = i;
            }
            else if (char.IsUpper(c) && char.IsLower(text[i - 1]))
            {
                words.Add(text[start..i]);
                start = i;
            }
        }
        if (start >= 0)
            words.Add(text[start..]);
        return words;
    }

    /// <summary>Shorter titles first within a tier (0..99).</summary>
    private static int LengthBonus(string title) => Math.Max(0, 100 - Math.Min(title.Length, 100));

    /// <summary>
    /// Scattered subsequence match, 1..<see cref="FuzzyCeiling"/>-1. Rejected (0) when the letters are
    /// spread too thin: the first-to-last matched span may be at most 4x the query length + 4.
    /// </summary>
    private static int ScoreFuzzy(string text, string query)
    {
        var compact = query.Replace(" ", string.Empty);
        if (compact.Length == 0 || !IsMatch(text, compact))
            return 0;

        var score = 0;
        var qi = 0;
        var first = -1;
        var last = -1;
        for (var ti = 0; ti < text.Length && qi < compact.Length; ti++)
        {
            if (char.ToUpperInvariant(text[ti]) != char.ToUpperInvariant(compact[qi]))
                continue;

            if (first < 0) first = ti;
            if (ti == 0) score += 40;
            if (last >= 0 && ti == last + 1) score += 20;
            else if (last >= 0) score -= 2;
            if (ti > 0)
            {
                var prev = text[ti - 1];
                if (prev == ' ' || prev == '-' || prev == '_' || prev == '/' ||
                    (char.IsLower(prev) && char.IsUpper(text[ti])))
                    score += 15;
            }

            last = ti;
            qi++;
        }

        if (last - first + 1 > compact.Length * 4 + 4)
            return 0;

        score += LengthBonus(text);
        return Math.Clamp(score, 1, FuzzyCeiling - 1);
    }
}
