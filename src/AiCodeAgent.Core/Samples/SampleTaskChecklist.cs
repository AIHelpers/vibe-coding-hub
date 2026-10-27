using System.Text.RegularExpressions;

namespace AiCodeAgent.Core.Samples;

/// <summary>The four things the guided first task asks the user to do, in order.</summary>
public enum SampleStep
{
    SendTask = 0,
    ApproveEdit = 1,
    TestsPass = 2,
    Commit = 3
}

/// <summary>
/// Progress of the "Fix this test" tour, saved in <c>config.json</c> (<c>ui.sampleGuide</c>) so it survives a restart.
/// <see cref="Finished"/> is set when the last step is ticked or the user hides the checklist; the overlay never comes back after that.
/// </summary>
public sealed class SampleGuideState
{
    /// <summary>The sample folder the tour belongs to.</summary>
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>How many steps are ticked (they tick in order).</summary>
    public int Completed { get; set; }

    /// <summary>True once the checklist was completed or dismissed: it stays hidden for good.</summary>
    public bool Finished { get; set; }
}

/// <summary>
/// The checklist behind the overlay: <i>Send the task → Approve the edit → See tests pass → Commit in Source Control</i>.
/// A step ticks only when the one before it is ticked, so running the tests before the fix, or committing early,
/// does not skip ahead. Each report method returns true when it ticked a step.
/// </summary>
public sealed class SampleTaskChecklist
{
    public const int StepCount = 4;

    public static readonly IReadOnlyList<string> Labels = new[]
    {
        "Send the task",
        "Approve the edit",
        "See tests pass",
        "Commit in Source Control"
    };

    public SampleTaskChecklist(int completed = 0) => Completed = Math.Clamp(completed, 0, StepCount);

    /// <summary>How many steps are ticked.</summary>
    public int Completed { get; private set; }

    public bool IsComplete => Completed >= StepCount;

    public bool IsDone(SampleStep step) => (int)step < Completed;

    /// <summary>Raised after a step was ticked.</summary>
    public event Action? Changed;

    /// <summary>The user sent a message to the agent.</summary>
    public bool TaskSent() => Advance(SampleStep.SendTask);

    /// <summary>An edit went through (the user approved it, or a rule did).</summary>
    public bool EditApplied() => Advance(SampleStep.ApproveEdit);

    /// <summary>A <c>dotnet test</c> run finished with exit code 0.</summary>
    public bool TestsPassed() => Advance(SampleStep.TestsPass);

    /// <summary>A commit was made.</summary>
    public bool Committed() => Advance(SampleStep.Commit);

    private bool Advance(SampleStep step)
    {
        if ((int)step != Completed) return false;
        Completed++;
        Changed?.Invoke();
        return true;
    }

    // ----- recognizing the steps in the agent's tool calls -----

    private static readonly Regex DotnetTest = new(@"(^|[\s;&|(])dotnet\s+test(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GitCommit = new(@"(^|[\s;&|(])git\s+((?:-[cC]\s+\S+|-\S+)\s+)*commit(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for a shell command that runs the test suite (<c>dotnet test ...</c>, also inside <c>a &amp;&amp; b</c>).</summary>
    public static bool IsTestCommand(string? command) => !string.IsNullOrWhiteSpace(command) && DotnetTest.IsMatch(command);

    /// <summary>True for a shell command that makes a git commit.</summary>
    public static bool IsCommitCommand(string? command) => !string.IsNullOrWhiteSpace(command) && GitCommit.IsMatch(command);

    /// <summary>
    /// Feeds one finished tool call in: a successful edit ticks "Approve the edit"; a successful <c>dotnet test</c>
    /// ticks "See tests pass"; a successful <c>git commit</c> run by the agent ticks "Commit".
    /// A failed call never ticks anything (the tests of the sample fail until the fix is in).
    /// </summary>
    public bool ToolFinished(bool isEditTool, string toolName, string? command, bool isError)
    {
        if (isError) return false;
        if (isEditTool) return EditApplied();
        if (!string.Equals(toolName, "execute_command", StringComparison.Ordinal)) return false;
        if (IsTestCommand(command)) return TestsPassed();
        if (IsCommitCommand(command)) return Committed();
        return false;
    }
}
