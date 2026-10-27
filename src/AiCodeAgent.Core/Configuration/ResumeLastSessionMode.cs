namespace AiCodeAgent.Core.Configuration;

/// <summary>What the app does on start when the last session of the working directory has messages (Settings → Start-up).</summary>
public enum ResumeLastSessionMode
{
    /// <summary>Show a banner with Resume / New buttons (default).</summary>
    Ask,

    /// <summary>Continue the last session without asking.</summary>
    Always,

    /// <summary>Always start with an empty chat.</summary>
    Never
}
