using System.Collections.Generic;
using System.Linq;
using AiCodeAgent.Core.Models;

namespace AiCodeAgent.Tools.Acp;

/// <summary>One choice offered with a <c>session/request_permission</c> (<c>kind</c>: allow_once, allow_always, reject_once, reject_always).</summary>
internal sealed record AcpPermissionOption(string OptionId, string Name, string Kind);

/// <summary>
/// Maps an ACP permission request onto VCH's permission modes. The prototype has no dialog: the flow's mode decides.
/// Read-only kinds (read, search, think) are always allowed; edits need AutoEdit or FullAuto; everything else (execute,
/// delete, move, fetch, switch_mode, other) needs FullAuto. E6-S6 replaces the "otherwise reject" with the approval dialog.
/// </summary>
internal static class AcpPermissionPolicy
{
    private static readonly HashSet<string> ReadOnly = new() { "read", "search", "think" };

    public static bool Allows(PermissionMode mode, string toolKind) => mode switch
    {
        PermissionMode.FullAuto => true,
        PermissionMode.AutoEdit => ReadOnly.Contains(toolKind) || toolKind == "edit",
        _ => ReadOnly.Contains(toolKind)
    };

    /// <summary>The option to answer with: the matching allow/reject option, or null when the agent offered none (answer "cancelled").</summary>
    public static AcpPermissionOption? Choose(PermissionMode mode, string toolKind, IReadOnlyList<AcpPermissionOption> options)
    {
        var allow = Allows(mode, toolKind);
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
