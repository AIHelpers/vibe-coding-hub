using System.Collections.Generic;
using System.Linq;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Tools.Acp;

/// <summary>One choice offered with a <c>session/request_permission</c> (<c>kind</c>: allow_once, allow_always, reject_once, reject_always).</summary>
internal sealed record AcpPermissionOption(string OptionId, string Name, string Kind);

internal enum AcpDecision { Allow, Ask, Deny }

/// <summary>
/// Maps an ACP permission request onto VCH's permission modes.
/// Read-only kinds (read, search, think) are always allowed; edits need AutoEdit or FullAuto; everything else (execute,
/// delete, move, fetch, switch_mode, other) needs FullAuto. Where the mode says Ask, the question goes to the user (the
/// approval dialog) when the host provided one; without one the request is refused. Plan refuses everything but read-only kinds.
/// </summary>
internal static class AcpPermissionPolicy
{
    private static readonly HashSet<string> ReadOnly = new() { "read", "search", "think" };

    /// <summary>What the permission mode says about a tool kind: do it, ask the user, or refuse.</summary>
    public static AcpDecision Decide(PermissionMode mode, string toolKind) => mode switch
    {
        PermissionMode.FullAuto => AcpDecision.Allow,
        PermissionMode.AutoEdit => ReadOnly.Contains(toolKind) || toolKind == "edit" ? AcpDecision.Allow : AcpDecision.Ask,
        PermissionMode.Ask => ReadOnly.Contains(toolKind) ? AcpDecision.Allow : AcpDecision.Ask,
        _ => ReadOnly.Contains(toolKind) ? AcpDecision.Allow : AcpDecision.Deny
    };

    /// <summary>True when the mode allows the kind without asking.</summary>
    public static bool Allows(PermissionMode mode, string toolKind) => Decide(mode, toolKind) == AcpDecision.Allow;

    /// <summary>The option to answer with: the matching allow/reject option, or null when the agent offered none (answer "cancelled").</summary>
    public static AcpPermissionOption? Choose(PermissionMode mode, string toolKind, IReadOnlyList<AcpPermissionOption> options) =>
        Pick(Allows(mode, toolKind), mode, options);

    /// <summary>The option for an answer that is already decided (for example by the user).</summary>
    public static AcpPermissionOption? Pick(bool allow, PermissionMode mode, IReadOnlyList<AcpPermissionOption> options)
    {
        // "Always" outlives this decision inside the agent, so only FullAuto may fall back to it.
        var wanted = allow
            ? (mode == PermissionMode.FullAuto ? new[] { "allow_once", "allow_always" } : new[] { "allow_once" })
            : new[] { "reject_once", "reject_always" };
        foreach (var kind in wanted)
        {
            var option = options.FirstOrDefault(o => o.Kind == kind);
            if (option != null) return option;
        }
        return null;
    }
}
