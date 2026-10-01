using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Tools.Base;
using Microsoft.Extensions.Logging;

namespace AiCodeAgent.Tools.Agent;

/// <summary>
/// Tool that lets an agent send a message to another agent in the same
/// multi-agent session via the shared AgentMailbox. The message is delivered
/// when the recipient's next step starts (the coordinator injects pending
/// messages into the recipient's prompt). A broadcast (*) reaches every agent
/// in the session.
/// </summary>
public class SendMessageTool : BaseTool
{
    public const string ToolName = "send_message";

    private readonly AgentMailbox _mailbox;
    private readonly IAgentEventBus? _eventBus;

    public SendMessageTool(AgentMailbox mailbox, IAgentEventBus? eventBus, ILogger<SendMessageTool> logger)
        : base(logger)
    {
        _mailbox = mailbox;
        _eventBus = eventBus;
    }

    public override string Name => ToolName;

    public override string Description =>
        "Send a message to another agent in the current multi-agent session " +
        "(use '*' to broadcast to all agents). The message is delivered at the " +
        "start of the recipient's next step. Use this to share findings, ask " +
        "questions, or coordinate work between agents.";

    public override RiskLevel Risk => RiskLevel.Read;

    public override ToolDefinition Definition => new()
    {
        Name = Name,
        Description = Description,
        Parameters = new JsonSchema
        {
            Properties = new()
            {
                ["toAgentId"] = new()
                {
                    Type = "string",
                    Description = "Recipient agent id, or '*' to broadcast to all agents."
                },
                ["message"] = new()
                {
                    Type = "string",
                    Description = "Message body to deliver to the recipient."
                },
            },
            Required = ["toAgentId", "message"]
        }
    };

    public override Task<ToolResult> ExecuteAsync(ToolCall call, AgentExecutionContext context)
    {
        var toAgentId = GetArg<string>(call, "toAgentId") ?? string.Empty;
        var message = GetArg<string>(call, "message") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(toAgentId))
            return Task.FromResult(Error("A non-empty 'toAgentId' argument is required."));
        if (string.IsNullOrWhiteSpace(message))
            return Task.FromResult(Error("A non-empty 'message' argument is required."));

        var fromAgentId = context.AgentId ?? "main";
        var toId = toAgentId.Trim();

        // Sending a message to yourself is a no-op that would just echo back.
        if (string.Equals(toId, fromAgentId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toId, AgentMailbox.BroadcastId, StringComparison.Ordinal))
            return Task.FromResult(Error("Cannot send a message to yourself."));

        var posted = _mailbox.Post(fromAgentId, toId, message);

        _eventBus?.Publish(new AgentMessageEvent(posted));

        Logger.LogInformation("Agent {From} sent message {MessageId} to {To}",
            fromAgentId, posted.MessageId, toId);

        var payload = new
        {
            posted.MessageId,
            posted.ToAgentId,
            Delivery = "delivered at the start of the recipient's next step"
        };

        return Task.FromResult(Success(
            $"Message queued for agent '{posted.ToAgentId}'.",
            payload));
    }
}
