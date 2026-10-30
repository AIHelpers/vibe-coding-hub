namespace AiCodeAgent.Core.Configuration;

/// <summary>
/// E8-S1: the scripted provider is a test double (a deterministic model that replays scenarios, see docs/testing.md).
/// It exists in the provider list only when <c>AIAGENT_ENABLE_SCRIPTED=1</c> is set or in a Debug build, so a released
/// app never offers it by accident.
/// </summary>
public static class ScriptedProviderSupport
{
    public const string ProviderName = "scripted";
    public const string EnableVariable = "AIAGENT_ENABLE_SCRIPTED";
    public const string DefaultModel = "scripted-1";

    /// <summary>True in Debug builds and whenever <see cref="EnableVariable"/> is 1, true, yes or on.</summary>
    public static bool IsEnabled => IsEnabledFor(Environment.GetEnvironmentVariable(EnableVariable));

    /// <summary>Same rule with the variable's value passed in (tests).</summary>
    public static bool IsEnabledFor(string? variableValue)
    {
        if (variableValue != null && variableValue.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on") return true;
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    /// <summary>The settings used when the user picks "scripted" without having configured it: nothing to enter.</summary>
    public static ProviderConfiguration DefaultConfiguration { get; } = new()
    {
        Name = ProviderName,
        BaseUrl = "http://localhost/scripted",
        DefaultModel = DefaultModel,
        TimeoutSeconds = 60
    };
}
