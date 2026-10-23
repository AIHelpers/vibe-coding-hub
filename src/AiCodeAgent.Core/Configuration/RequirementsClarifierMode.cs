namespace AiCodeAgent.Core.Configuration;

/// <summary>When the requirements clarifier asks questions before a task (Settings → Requirements questions).</summary>
public enum RequirementsClarifierMode
{
    /// <summary>Never ask. <c>/requirements</c> still runs it on demand.</summary>
    Off,

    /// <summary>Ask only before building a new app or feature from scratch (default).</summary>
    Auto,

    /// <summary>Ask before every build task.</summary>
    Always
}
