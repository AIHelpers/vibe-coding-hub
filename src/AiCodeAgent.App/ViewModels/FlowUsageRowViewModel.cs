using System;
using System.Linq;
using AiCodeAgent.Core.Flows;

namespace AiCodeAgent.App.ViewModels;

/// <summary>One line of the usage table under the canvas: a step run, or the run total.</summary>
public sealed class FlowUsageRowViewModel
{
    public string Step { get; init; } = string.Empty;
    public string Round { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string TokensIn { get; init; } = string.Empty;
    public string TokensOut { get; init; } = string.Empty;
    public string Cost { get; init; } = string.Empty;
    public string Time { get; init; } = string.Empty;
    public bool IsTotal { get; init; }

    public static FlowUsageRowViewModel From(FlowStepRunUsage u) => new()
    {
        Step = u.NodeId,
        Round = u.Round.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Model = string.IsNullOrEmpty(u.Model) ? "(default)" : string.IsNullOrEmpty(u.Provider) ? u.Model : $"{u.Provider}/{u.Model}",
        TokensIn = FlowUsageFormat.Tokens(u.TokensIn),
        TokensOut = FlowUsageFormat.Tokens(u.TokensOut),
        Cost = u.Unpriced ? "unpriced" : FlowUsageFormat.Cost(u.CostUsd),
        Time = Seconds(u.Duration)
    };

    public static FlowUsageRowViewModel Total(FlowRunResult result)
    {
        var t = result.Usage;
        return new FlowUsageRowViewModel
        {
            Step = "total",
            TokensIn = FlowUsageFormat.Tokens(t.TokensIn),
            TokensOut = FlowUsageFormat.Tokens(t.TokensOut),
            Cost = t.HasUnpriced && t.CostUsd <= 0 ? "unpriced" : FlowUsageFormat.Cost(t.CostUsd),
            Time = Seconds(TimeSpan.FromTicks(result.StepRuns.Sum(r => r.Duration.Ticks))),
            IsTotal = true
        };
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s";
}
