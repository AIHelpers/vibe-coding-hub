namespace AiCodeAgent.App.CommandPalette;

/// <summary>
/// Static fuzzy-match helpers used by the command palette search.
/// Uses a simple subsequence scoring algorithm.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Returns true if <paramref name="query"/> characters appear in order
    /// within <paramref name="text"/> (case-insensitive).
    /// </summary>
    public static bool IsMatch(string text, string query)
    {
        if (string.IsNullOrEmpty(query))
            return true;
        if (string.IsNullOrEmpty(text))
            return false;

        var t = text.AsSpan();
        var q = query.AsSpan();
        var ti = 0;
        var qi = 0;

        while (ti < t.Length && qi < q.Length)
        {
            if (char.ToUpperInvariant(t[ti]) == char.ToUpperInvariant(q[qi]))
            {
                qi++;
            }
            ti++;
        }

        return qi == q.Length;
    }

    /// <summary>
    /// Computes a simple subsequence score for ranking. Returns 0 when no match.
    /// Higher scores indicate "better" matches: consecutive characters, earlier
    /// positions, and word/camel-case boundaries score higher.
    /// </summary>
    public static int Score(string text, string query)
    {
        if (string.IsNullOrEmpty(query))
            return 1000;

        if (!IsMatch(text, query))
            return 0;

        int score = 0;
        var t = text.AsSpan();
        var q = query.AsSpan();
        var ti = 0;
        var qi = 0;
        var lastMatch = -1;

        while (ti < t.Length && qi < q.Length)
        {
            if (char.ToUpperInvariant(t[ti]) == char.ToUpperInvariant(q[qi]))
            {
                // Starting bonus: matches at the beginning of the text are best.
                if (ti == 0)
                    score += 40;

                // Consecutive-match bonus.
                if (lastMatch >= 0 && ti == lastMatch + 1)
                    score += 20;

                // Word-boundary bonus: after a space or at a camelCase boundary.
                if (ti > 0)
                {
                    var prev = t[ti - 1];
                    if (prev == ' ' || prev == '-' || prev == '_' || prev == '/' ||
                        (char.IsLower(prev) && char.IsUpper(t[ti])))
                    {
                        score += 15;
                    }
                }

                // Non-consecutive matches are slightly penalized.
                if (lastMatch >= 0 && ti > lastMatch + 1)
                    score -= 2;

                lastMatch = ti;
                qi++;
            }
            ti++;
        }

        // Prefer shorter texts when scores are otherwise equal.
        score += Math.Max(0, 100 - text.Length / 2);
        return score;
    }
}