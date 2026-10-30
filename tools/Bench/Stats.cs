namespace AiCodeAgent.Bench;

/// <summary>A mean with its 95 % confidence interval and the number of samples it rests on.</summary>
internal readonly record struct Estimate(double Mean, double Lo, double Hi, int N)
{
    public static Estimate None => new(double.NaN, double.NaN, double.NaN, 0);
    public bool HasValue => N > 0;
}

/// <summary>The few statistics the report needs. Deterministic: the bootstrap uses a fixed seed.</summary>
internal static class Stats
{
    public const double Z95 = 1.959963984540054;
    public const int DefaultIterations = 10_000;
    public const int DefaultSeed = 20260101;

    /// <summary>Wilson score interval for a proportion (95 %).</summary>
    public static (double Lo, double Hi) Wilson(int successes, int trials)
    {
        if (trials <= 0) return (double.NaN, double.NaN);
        double n = trials;
        double p = successes / n;
        double z2 = Z95 * Z95;
        double denom = 1 + z2 / n;
        double centre = (p + z2 / (2 * n)) / denom;
        double half = Z95 * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n)) / denom;
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    /// <summary>
    /// Mean of <paramref name="values"/> with a 95 % percentile-bootstrap interval. The values are per-task figures,
    /// so the resampling is over tasks: the attempts of one task are not independent of each other.
    /// </summary>
    public static Estimate BootstrapMean(IReadOnlyList<double> values, int iterations = DefaultIterations, int seed = DefaultSeed)
    {
        var n = values.Count;
        if (n == 0) return Estimate.None;
        var mean = values.Average();
        if (n == 1) return new Estimate(mean, mean, mean, 1);

        var rng = new Random(seed);
        var means = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            double sum = 0;
            for (var j = 0; j < n; j++) sum += values[rng.Next(n)];
            means[i] = sum / n;
        }
        Array.Sort(means);
        var lo = means[(int)Math.Floor(0.025 * (iterations - 1))];
        var hi = means[(int)Math.Ceiling(0.975 * (iterations - 1))];
        return new Estimate(mean, lo, hi, n);
    }

    /// <summary>Paired differences <c>b - a</c> over the tasks both have, as a bootstrap estimate.</summary>
    public static Estimate PairedDifference(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        var diffs = new List<double>();
        foreach (var (task, av) in a.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (b.TryGetValue(task, out var bv)) diffs.Add(bv - av);
        return BootstrapMean(diffs);
    }
}
