using AiCodeAgent.Core.Agent;

namespace AiCodeAgent.CLI;

/// <summary>Answers the agent's ask_user questions on the terminal. Numbers pick a suggested option; any other text is the answer.</summary>
public sealed class ConsoleUserQuestionHandler : IUserQuestionHandler
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<string?> AskAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        // Redirected stdin means nobody can answer (CI, piped runs): let the tool tell the model to assume.
        if (Console.IsInputRedirected) return null;

        await Gate.WaitAsync(ct);
        try
        {
            Console.WriteLine();
            Console.WriteLine($"? {question}");
            for (var i = 0; i < options.Count; i++)
                Console.WriteLine($"  {i + 1}. {options[i]}");
            Console.Write(options.Count > 0 ? "Answer (number or text, empty to skip): " : "Answer (empty to skip): ");

            var line = await Task.Run(Console.ReadLine, ct);
            if (string.IsNullOrWhiteSpace(line)) return null;
            line = line.Trim();
            return int.TryParse(line, out var n) && n >= 1 && n <= options.Count ? options[n - 1] : line;
        }
        finally
        {
            Gate.Release();
        }
    }
}
