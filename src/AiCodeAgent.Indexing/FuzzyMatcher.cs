namespace AiCodeAgent.Indexing;

/// <summary>
/// Lightweight fuzzy subsequence matching used to score file and symbol
/// queries against a filter string — same scoring style as the command-palette
/// matcher so results feel consistent across the app.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>Whether all query characters appear in order in text (case-insensitive).</summary>
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
    /// Computes a subsequence score; returns 0 when no match. Consecutive
    /// characters and word/camel boundaries score higher; longer texts are
    /// slightly penalized so shorter results rank first.
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
                if (ti == 0)
                    score += 40;
                if (lastMatch >= 0 && ti == lastMatch + 1)
                    score += 20;
                if (ti > 0)
                {
                    var prev = t[ti - 1];
                    if (prev == ' ' || prev == '-' || prev == '_' || prev == '/' ||
                        prev == '.' || prev == '\\' ||
                        (char.IsLower(prev) && char.IsUpper(t[ti])))
                    {
                        score += 15;
                    }
                }
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