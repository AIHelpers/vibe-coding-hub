using AiCodeAgent.Core.Configuration;

namespace AiCodeAgent.Core.Updates;

/// <summary>What <see cref="UpdateService.CheckAsync"/> found.</summary>
/// <param name="Requested">True when a request was sent (so the caller saves <c>LastUpdateCheck</c>).</param>
/// <param name="Result">The checker's answer, or null when no request was made.</param>
/// <param name="Offer">The release to show in the banner, or null (up to date, failed, switched off, not due, or dismissed).</param>
public sealed record UpdateOutcome(bool Requested, UpdateCheckResult? Result, ReleaseInfo? Offer);

/// <summary>
/// The rules around the check: at most one automatic request a day, none at all when the user turned it off, and a release the
/// user dismissed is not shown again (a newer one is). A check the user asks for ("Check for updates") skips all three.
/// </summary>
public sealed class UpdateService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IUpdateChecker _checker;
    private readonly Func<DateTimeOffset> _clock;

    public UpdateService(IUpdateChecker checker, Func<DateTimeOffset>? clock = null)
    {
        _checker = checker;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True when the last check is a day old or more (or never happened, or lies in the future).</summary>
    public static bool IsDue(UiConfiguration ui, DateTimeOffset now) =>
        ui.LastUpdateCheck is not { } last || now - last >= Interval || last > now + TimeSpan.FromHours(1);

    /// <summary>Updates <see cref="UiConfiguration.LastUpdateCheck"/> after an answer; the caller saves the configuration.</summary>
    public async Task<UpdateOutcome> CheckAsync(
        UiConfiguration ui, SemanticVersion current, UpdatePlatform platform, bool manual = false, CancellationToken cancellationToken = default)
    {
        var now = _clock();
        if (!manual && (!ui.CheckForUpdates || !IsDue(ui, now)))
            return new UpdateOutcome(false, null, null);

        var result = await _checker.CheckAsync(current, platform, cancellationToken).ConfigureAwait(false);

        // A failed check (offline) is not counted, so the next start tries again.
        if (result.Status != UpdateStatus.Failed) ui.LastUpdateCheck = now;
        if (result.Status != UpdateStatus.UpdateAvailable || result.Release == null)
            return new UpdateOutcome(true, result, null);

        if (!manual && IsDismissed(ui, result.Release))
            return new UpdateOutcome(true, result, null);
        return new UpdateOutcome(true, result, result.Release);
    }

    /// <summary>Remembers that the user does not want to hear about this release again.</summary>
    public static void Dismiss(UiConfiguration ui, ReleaseInfo release) => ui.DismissedUpdateVersion = release.Version.ToString();

    private static bool IsDismissed(UiConfiguration ui, ReleaseInfo release) =>
        SemanticVersion.TryParse(ui.DismissedUpdateVersion, out var dismissed) && dismissed == release.Version;
}
