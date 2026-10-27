using System;
using System.Diagnostics;
using AiCodeAgent.Core.Updates;

namespace AiCodeAgent.App.Services;

/// <summary>Opens a web address in the user's browser.</summary>
public interface IUrlLauncher
{
    /// <summary>Returns false when the address was refused or could not be opened.</summary>
    bool Open(string url);
}

/// <summary>
/// Opens GitHub links (release pages and release files) with the system's default handler. The addresses come from a web
/// answer, so anything but a plain https link to GitHub is refused (see <see cref="UpdateUrlGuard"/>).
/// </summary>
public sealed class SystemUrlLauncher : IUrlLauncher
{
    public bool Open(string url)
    {
        if (!UpdateUrlGuard.IsSafe(url)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open {url}: {ex.Message}");
            return false;
        }
    }
}
