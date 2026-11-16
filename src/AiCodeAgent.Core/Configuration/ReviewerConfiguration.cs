namespace AiCodeAgent.Core.Configuration;

/// <summary>
/// Fixes the reviewer of the independent review (E10-S4). Both empty means "choose for me": a configured provider other than the
/// one that wrote the change, else another model of the same provider. Set <see cref="Provider"/> (a key of the configured
/// providers) to always review with it, and <see cref="Model"/> to name its model.
/// </summary>
public record ReviewerConfiguration
{
    public string? Provider { get; init; }
    public string? Model { get; init; }
}
