namespace AiCodeAgent.Core.Agent;

/// <summary>
/// Lets a running agent ask the human a question mid-run (used by the ask_user tool). The UI or CLI supplies the
/// implementation; when none is registered the tool tells the model to proceed on its own assumptions.
/// </summary>
public interface IUserQuestionHandler
{
    /// <summary>Asks <paramref name="question"/>; <paramref name="options"/> are suggested answers (may be empty). Returns null if the user declined to answer.</summary>
    Task<string?> AskAsync(string question, IReadOnlyList<string> options, CancellationToken ct);
}
