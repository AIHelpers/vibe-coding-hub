using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace AiCodeAgent.CLI;

/// <summary>
/// CLI commands for session export/import and prompt pack management
/// (docs/plan/05-priority-session-export-import.md).
/// </summary>
public static class SessionCommands
{
    /// <summary>Register session commands under the `config` command.</summary>
    public static void AddCommands(Command configCommand)
    {
        // config export-session <sessionId> --out <path>
        var exportSession = new Command("export-session", "Export a session bundle to a file");
        var sessionIdArg = new Argument<string>("sessionId", "The session ID to export");
        var outOption = new Option<string>("--out", "Output file path (.json or .agentsession)") { IsRequired = true };
        exportSession.AddArgument(sessionIdArg);
        exportSession.AddOption(outOption);
        exportSession.SetHandler(async (sessionId, outPath) =>
        {
            var sp = await BuildServicesAsync();
            var exportService = sp.GetRequiredService<SessionExportService>();
            await exportService.ExportAsync(sessionId, outPath);
            Console.WriteLine($"Session exported to {outPath}");
        }, sessionIdArg, outOption);

        // config import-session <path> [--new-session-id <id>] [--no-checkpoints]
        var importSession = new Command("import-session", "Import a session bundle");
        var bundlePathArg = new Argument<string>("path", "Path to the session bundle (.json or .agentsession)");
        var newSessionIdOption = new Option<string>("--new-session-id", "Assign a new session ID on import");
        var noCheckpointsOption = new Option<bool>("--no-checkpoints", "Do not apply checkpoints on import");
        importSession.AddArgument(bundlePathArg);
        importSession.AddOption(newSessionIdOption);
        importSession.AddOption(noCheckpointsOption);
        importSession.SetHandler(async (bundlePath, newSessionId, noCheckpoints) =>
        {
            var sp = await BuildServicesAsync();
            var importer = sp.GetRequiredService<SessionImporter>();
            var result = await importer.ImportAsync(bundlePath, newSessionId, !noCheckpoints);

            Console.WriteLine($"Imported session '{result.SessionId}':");
            Console.WriteLine($"  Messages:    {result.MessageCount}");
            Console.WriteLine($"  Tool calls:  {result.ToolCallCount}");
            Console.WriteLine($"  Hunks:       {result.HunkCount}");
            Console.WriteLine($"  Checkpoints: {result.CheckpointCount}");
            Console.WriteLine($"  Roles:       {string.Join(", ", result.RolesUsed)}");

            if (result.HasConflicts)
            {
                Console.WriteLine();
                Console.WriteLine("  ⚠ Conflicts detected:");
                foreach (var conflict in result.Conflicts)
                {
                    Console.WriteLine($"    - {conflict.Message}");
                }
            }
        }, bundlePathArg, newSessionIdOption, noCheckpointsOption);

        // config export-promptpack <path> --name <name> [--description <desc>]
        var exportPromptPack = new Command("export-promptpack", "Export role presets + templates to a prompt pack file");
        var packPathArg = new Argument<string>("path", "Output path (.promptpack.json)");
        var packNameOption = new Option<string>("--name", "Prompt pack name") { IsRequired = true };
        var packDescOption = new Option<string>("--description", "Prompt pack description");
        exportPromptPack.AddArgument(packPathArg);
        exportPromptPack.AddOption(packNameOption);
        exportPromptPack.AddOption(packDescOption);
        exportPromptPack.SetHandler(async (path, name, description) =>
        {
            var sp = await BuildServicesAsync();
            var loader = sp.GetRequiredService<RolePresetLoader>();
            var pack = PromptPackService.CreateFromPresets(loader, name, description);
            var service = sp.GetRequiredService<PromptPackService>();
            await service.SaveAsync(pack, path);
            Console.WriteLine($"Prompt pack '{name}' exported to {path} ({pack.Roles.Count} roles)");
        }, packPathArg, packNameOption, packDescOption);

        // config import-promptpack <path>
        var importPromptPack = new Command("import-promptpack", "Import a prompt pack file into the user presets");
        var packImportPathArg = new Argument<string>("path", "Path to the prompt pack (.promptpack.json)");
        importPromptPack.AddArgument(packImportPathArg);
        importPromptPack.SetHandler(async (path) =>
        {
            var sp = await BuildServicesAsync();
            var service = sp.GetRequiredService<PromptPackService>();
            var loader = sp.GetRequiredService<RolePresetLoader>();
            var pack = await service.LoadAsync(path);

            foreach (var role in pack.Roles)
            {
                await loader.SavePresetAsync(role);
            }

            Console.WriteLine($"Imported prompt pack '{pack.Name}' with {pack.Roles.Count} roles and " +
                              $"{pack.Templates.Count} templates");
        }, packImportPathArg);

        configCommand.AddCommand(exportSession);
        configCommand.AddCommand(importSession);
        configCommand.AddCommand(exportPromptPack);
        configCommand.AddCommand(importPromptPack);
    }

    private static async Task<ServiceProvider> BuildServicesAsync()
    {
        // Minimal DI container for session commands (mirrors Program.cs's BuildServiceProvider
        // but only registers what these commands need).
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());

        var promptPackService = new PromptPackService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PromptPackService>.Instance);
        services.AddSingleton(promptPackService);

        // RolePresetLoader requires ILogger<RolePresetLoader>
        services.AddSingleton<RolePresetLoader>();

        // Session services need IContextManager + ICheckpointManager
        services.AddSingleton<AiCodeAgent.Core.Interfaces.IContextManager, AiCodeAgent.Context.InMemoryContextManager>();
        services.AddSingleton<AiCodeAgent.Core.Interfaces.ICheckpointManager, CheckpointManager>();

        // Event bus + recorder (needed by SessionExportService)
        services.AddSingleton<AiCodeAgent.Core.Interfaces.IAgentEventBus, AgentEventBus>();
        services.AddSingleton<SessionRecorder>();
        services.AddSingleton<SessionExportService>();
        services.AddSingleton<SessionImporter>();

        return services.BuildServiceProvider();
    }
}