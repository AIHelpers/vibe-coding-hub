using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Planning;

/// <summary>Ask the user a clarifying question during a run instead of guessing.</summary>
public class AskUserTool : BaseTool
{
    private readonly IUserQuestionHandler? _handler;

    public AskUserTool(ILogger<AskUserTool> logger, IUserQuestionHandler? handler = null) : base(logger) => _handler = handler;

    public override string Name => "ask_user";
    public override string Description =>
        "Ask the user a question when a decision is genuinely theirs to make (ambiguous requirement, destructive choice, " +
        "missing credentials). Provide 2-4 short 'options' when possible. Do not use it for things you can find out yourself.";
    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["question"] = new() { Type = "string", Description = "The question, self-contained and specific" },
                ["options"] = new()
                {
                    Type = "array",
                    Description = "Suggested answers (optional; the user may still type their own)",
                    Items = new PropertySchema { Type = "string", Description = "One suggested answer" }
                }
            },
            Required = ["question"]
        }
    };

    public override async Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var question = GetArg<string>(call, "question");
        if (string.IsNullOrWhiteSpace(question))
            return Error("'question' is required.");

        if (_handler == null)
            return Error("No interactive user is available to answer. State your assumption explicitly and continue.");

        var options = GetArg<string[]>(call, "options", Array.Empty<string>()) ?? Array.Empty<string>();
        try
        {
            var answer = await _handler.AskAsync(question, options, context.CancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(answer)
                ? Success("The user did not answer. Proceed with your best assumption and say what you assumed.")
                : Success($"User answered: {answer}");
        }
        catch (OperationCanceledException)
        {
            return Error("Question cancelled.");
        }
    }
}
