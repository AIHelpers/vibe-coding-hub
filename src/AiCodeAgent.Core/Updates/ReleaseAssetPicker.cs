namespace AiCodeAgent.Core.Updates;

/// <summary>
/// Chooses the file of a release that suits this computer: an installer when there is one (.msi, .msix, .exe, .dmg, .pkg,
/// .AppImage), else the archive built for the OS and CPU (<c>vibe-coding-hub-1.3.0-win-x64.zip</c>). The command-line
/// tool's files (<c>vibe-coding-hub-cli-...</c>), checksums and signatures are never offered.
/// </summary>
public static class ReleaseAssetPicker
{
    private static readonly string[] Ignored = { ".sha256", ".sha512", ".sig", ".asc", ".txt", ".json", ".md", ".yml", ".yaml", ".blockmap", ".nupkg" };
    private static readonly string[] WindowsInstallers = { ".msi", ".msix", ".exe" };
    private static readonly string[] MacInstallers = { ".dmg", ".pkg" };
    private static readonly string[] LinuxInstallers = { ".appimage" };
    private static readonly string[] Archives = { ".zip", ".tar.gz", ".tgz" };

    public static ReleaseAsset? Pick(IEnumerable<ReleaseAsset> assets, UpdatePlatform platform)
    {
        ReleaseAsset? best = null;
        var bestScore = 0;
        foreach (var asset in assets)
        {
            var score = Score(asset.Name, platform);
            if (score > bestScore)
            {
                best = asset;
                bestScore = score;
            }
        }
        return best;
    }

    // 0 = not for this computer; higher is better.
    private static int Score(string fileName, UpdatePlatform platform)
    {
        var name = fileName.ToLowerInvariant();
        if (Ignored.Any(name.EndsWith)) return 0;
        if (name.Contains("-cli-") || name.StartsWith("cli-") || name.Contains("-cli.")) return 0;

        var (installers, osWords) = platform.Os switch
        {
            UpdateOs.Windows => (WindowsInstallers, new[] { "win", "windows" }),
            UpdateOs.MacOs => (MacInstallers, new[] { "osx", "macos", "darwin", "mac" }),
            UpdateOs.Linux => (LinuxInstallers, new[] { "linux" }),
            _ => (Array.Empty<string>(), Array.Empty<string>())
        };
        if (installers.Length == 0) return 0;

        // A file for another OS never matches, whatever its extension.
        if (OtherOsWords(platform.Os).Any(w => HasWord(name, w))) return 0;

        var isInstaller = installers.Any(name.EndsWith);
        var isArchive = Archives.Any(name.EndsWith) && osWords.Any(w => HasWord(name, w));
        if (!isInstaller && !isArchive) return 0;

        var wantsArm = platform.Arch == UpdateArch.Arm64;
        var mentionsArm = HasWord(name, "arm64") || HasWord(name, "aarch64");
        var mentionsX64 = HasWord(name, "x64") || HasWord(name, "amd64") || HasWord(name, "x86_64") || HasWord(name, "x86-64");
        if (wantsArm && mentionsX64 && !mentionsArm) return 0;
        if (!wantsArm && mentionsArm && !mentionsX64) return 0;

        var score = isInstaller ? 100 : 10;
        if (wantsArm ? mentionsArm : mentionsX64) score += 5;
        return score;
    }

    private static string[] OtherOsWords(UpdateOs os) => os switch
    {
        UpdateOs.Windows => new[] { "osx", "macos", "darwin", "linux" },
        UpdateOs.MacOs => new[] { "win", "windows", "linux" },
        UpdateOs.Linux => new[] { "win", "windows", "osx", "macos", "darwin" },
        _ => Array.Empty<string>()
    };

    // A whole word between separators: "win" matches "app-win-x64.zip" but not "darwin".
    private static bool HasWord(string name, string word)
    {
        var start = 0;
        while ((start = name.IndexOf(word, start, StringComparison.Ordinal)) >= 0)
        {
            var before = start == 0 ? '-' : name[start - 1];
            var afterIndex = start + word.Length;
            var after = afterIndex >= name.Length ? '-' : name[afterIndex];
            if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after)) return true;
            start += word.Length;
        }
        return false;
    }
}
