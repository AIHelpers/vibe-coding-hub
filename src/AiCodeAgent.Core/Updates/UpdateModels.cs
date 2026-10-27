using System.Runtime.InteropServices;

namespace AiCodeAgent.Core.Updates;

/// <summary>A file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>A published release of the app.</summary>
/// <param name="Version">The parsed tag, e.g. 1.3.0.</param>
/// <param name="Tag">The tag as published, e.g. v1.3.0.</param>
/// <param name="Name">The release title.</param>
/// <param name="Notes">The release notes (markdown), possibly empty.</param>
/// <param name="PageUrl">The release page on GitHub.</param>
/// <param name="PublishedAt">When it was published, if known.</param>
/// <param name="Assets">Every attached file.</param>
/// <param name="Download">The file that suits this computer, or null when none does (then use <paramref name="PageUrl"/>).</param>
public sealed record ReleaseInfo(
    SemanticVersion Version,
    string Tag,
    string Name,
    string Notes,
    string PageUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<ReleaseAsset> Assets,
    ReleaseAsset? Download);

public enum UpdateStatus
{
    /// <summary>The newest release is not newer than this build (or nothing was published yet).</summary>
    UpToDate,

    /// <summary>A newer release exists.</summary>
    UpdateAvailable,

    /// <summary>The check did not finish: offline, rate-limited, unreadable answer. Never shown as an error to the user.</summary>
    Failed
}

public sealed record UpdateCheckResult(UpdateStatus Status, ReleaseInfo? Release, string? Error)
{
    public static UpdateCheckResult UpToDate(ReleaseInfo? latest = null) => new(UpdateStatus.UpToDate, latest, null);
    public static UpdateCheckResult Available(ReleaseInfo release) => new(UpdateStatus.UpdateAvailable, release, null);
    public static UpdateCheckResult Failed(string error) => new(UpdateStatus.Failed, null, error);
}

public enum UpdateOs { Windows, MacOs, Linux, Other }

public enum UpdateArch { X64, Arm64, Other }

/// <summary>The computer the update is for; decides which release file is offered.</summary>
public sealed record UpdatePlatform(UpdateOs Os, UpdateArch Arch)
{
    public static UpdatePlatform Current()
    {
        var os = OperatingSystem.IsWindows() ? UpdateOs.Windows
            : OperatingSystem.IsMacOS() ? UpdateOs.MacOs
            : OperatingSystem.IsLinux() ? UpdateOs.Linux
            : UpdateOs.Other;
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => UpdateArch.X64,
            Architecture.Arm64 => UpdateArch.Arm64,
            _ => UpdateArch.Other
        };
        return new UpdatePlatform(os, arch);
    }
}
