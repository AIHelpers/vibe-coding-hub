using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AiCodeAgent.Tools.GitHosting;

public sealed record GitHubToken(string Value, string Source);

/// <summary>Finds a GitHub token: <c>GITHUB_TOKEN</c> / <c>GH_TOKEN</c> first, then <c>gh auth token</c> when the GitHub CLI is signed in.</summary>
public sealed class GitHubTokenResolver
{
    private readonly Func<string, string?> _env;
    private readonly Func<CancellationToken, Task<string?>> _ghAuthToken;

    public GitHubTokenResolver(Func<string, string?>? environment = null, Func<CancellationToken, Task<string?>>? ghAuthToken = null)
    {
        _env = environment ?? Environment.GetEnvironmentVariable;
        _ghAuthToken = ghAuthToken ?? RunGhAuthTokenAsync;
    }

    public async Task<GitHubToken?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        foreach (var name in new[] { "GITHUB_TOKEN", "GH_TOKEN" })
        {
            var v = _env(name)?.Trim();
            if (!string.IsNullOrEmpty(v)) return new GitHubToken(v, name);
        }
        try
        {
            var t = (await _ghAuthToken(cancellationToken).ConfigureAwait(false))?.Trim();
            if (!string.IsNullOrEmpty(t)) return new GitHubToken(t, "gh auth token");
        }
        catch (OperationCanceledException) { throw; }
        catch { /* gh missing or not signed in */ }
        return null;
    }

    private static async Task<string?> RunGhAuthTokenAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh", "auth token")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var output = await p.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        return p.ExitCode == 0 ? output : null;
    }
}
