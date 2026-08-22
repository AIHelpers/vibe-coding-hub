using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AiCodeAgent.Core.Models;

/// <summary>
/// Structured requirements captured through the conversational clarification
/// loop (Feature 8). Each dimension corresponds to a facet of a software
/// project that a vague user prompt may omit. The orchestrator incorporates
/// confirmed <see cref="AppRequirements"/> into its system prompt so generated
/// code matches the user's intent.
/// </summary>
public sealed record AppRequirements
{
    /// <summary>The target platform/runtime (e.g. "web", "mobile", "desktop", "cli").</summary>
    public string? Platform { get; set; }

    /// <summary>Authentication strategy (e.g. "none", "JWT", "OAuth2", "session").</summary>
    public string? Auth { get; set; }

    /// <summary>Core data model description (entities, relationships, storage).</summary>
    public string? DataModel { get; set; }

    /// <summary>Styling/UX guidance (e.g. "tailwind", "material", "minimal").</summary>
    public string? Styling { get; set; }

    /// <summary>External integrations (e.g. "Stripe", "SendGrid", "none").</summary>
    public string? Integrations { get; set; }

    /// <summary>Deployment target (e.g. "docker", "vercel", "on-prem").</summary>
    public string? Deployment { get; set; }

    /// <summary>Free-form notes captured during clarification.</summary>
    public List<string> Notes { get; init; } = new();

    /// <summary>True when the user explicitly chose "just build it".</summary>
    [JsonIgnore]
    public bool Skipped { get; set; }

    /// <summary>Returns a human-readable summary suitable for a chat card.</summary>
    public string ToSummaryCard() =>
        $"📋 **Requirements Summary**\n" +
        $"- **Platform:** {FormatValue(Platform)}\n" +
        $"- **Auth:** {FormatValue(Auth)}\n" +
        $"- **Data Model:** {FormatValue(DataModel)}\n" +
        $"- **Styling:** {FormatValue(Styling)}\n" +
        $"- **Integrations:** {FormatValue(Integrations)}\n" +
        $"- **Deployment:** {FormatValue(Deployment)}" +
        (Notes.Count > 0 ? $"\n- **Notes:** {string.Join("; ", Notes)}" : "");

    /// <summary>Returns a compact key/value block for the system prompt.</summary>
    public string ToPromptBlock()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(Platform)) lines.Add($"- Platform: {Platform}");
        if (!string.IsNullOrWhiteSpace(Auth)) lines.Add($"- Auth: {Auth}");
        if (!string.IsNullOrWhiteSpace(DataModel)) lines.Add($"- Data Model: {DataModel}");
        if (!string.IsNullOrWhiteSpace(Styling)) lines.Add($"- Styling: {Styling}");
        if (!string.IsNullOrWhiteSpace(Integrations)) lines.Add($"- Integrations: {Integrations}");
        if (!string.IsNullOrWhiteSpace(Deployment)) lines.Add($"- Deployment: {Deployment}");
        if (Notes.Count > 0) lines.Add($"- Notes: {string.Join("; ", Notes)}");
        return lines.Count > 0
            ? "Confirmed application requirements:\n" + string.Join("\n", lines)
            : string.Empty;
    }

    private static string FormatValue(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "_(unspecified)_" : v!;
}