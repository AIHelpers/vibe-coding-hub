using AiCodeAgent.CLI;
using AiCodeAgent.Context;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers;
using AiCodeAgent.Providers.Anthropic;
using AiCodeAgent.Providers.Ollama;
using AiCodeAgent.Providers.OpenAI;
using AiCodeAgent.Providers.OpenAICompatible;
using AiCodeAgent.Tools.Code;
using AiCodeAgent.Tools.FileSystem;
using AiCodeAgent.Tools.Git;
using AiCodeAgent.Tools.Search;
using AiCodeAgent.Tools.Shell;
using AiCodeAgent.Tools.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;

var rootCommand = new RootCommand("AI Code Agent - An intelligent coding assistant");

// chat command
var chatCommand = new Command("chat", "Start an interactive chat session");
var providerOption = new Option<string>("--provider", "AI provider to use") { IsRequired = false };
var modelOption = new Option<string>("--model", "Model to use");
var dirOption = new Option<string>("--dir", "Working directory");
var autoOption = new Option<bool>("--auto", "Auto-approve tool executions");
var verboseOption = new Option<bool>("--verbose", "Verbose output");

chatCommand.AddOption(providerOption);
chatCommand.AddOption(modelOption);
chatCommand.AddOption(dirOption);
chatCommand.AddOption(autoOption);
chatCommand.AddOption(verboseOption);

chatCommand.SetHandler(async (provider, model, dir, auto, verbose) =>
{
    var services = await BuildServiceProvider(provider, model, dir);
    var ui = services.GetRequiredService<TerminalUI>();
    await ui.RunAsync(new AgentOptions
    {
        Model = model,
        WorkingDirectory = dir ?? Directory.GetCurrentDirectory(),
        AutoApprove = auto,
        Verbose = verbose
    });
}, providerOption, modelOption, dirOption, autoOption, verboseOption);

// run command - single prompt
var runCommand = new Command("run", "Execute a single prompt");
var promptArg = new Argument<string>("prompt", "The prompt to execute");
runCommand.AddArgument(promptArg);
runCommand.AddOption(providerOption);
runCommand.AddOption(modelOption);
runCommand.AddOption(dirOption);

runCommand.SetHandler(async (prompt, provider, model, dir) =>
{
    var services = await BuildServiceProvider(provider, model, dir);
    var runner = services.GetRequiredService<SingleRunMode>();
    await runner.RunAsync(prompt, new AgentOptions
    {
        Model = model,
        WorkingDirectory = dir ?? Directory.GetCurrentDirectory(),
        AutoApprove = true
    });
}, promptArg, providerOption, modelOption, dirOption);

// config command
var configCommand = new Command("config", "Configure the agent");
var setKeyCommand = new Command("set-key", "Set API key for a provider");
var setKeyProvider = new Argument<string>("provider");
var setKeyValue = new Argument<string>("key");
setKeyCommand.AddArgument(setKeyProvider);
setKeyCommand.AddArgument(setKeyValue);
setKeyCommand.SetHandler(async (provider, key) =>
{
    var configSvc = new ConfigurationService();
    await configSvc.LoadAsync();
    configSvc.SetApiKey(provider, key);
    await configSvc.SaveAsync();
    Console.WriteLine($"API key set for {provider}");
}, setKeyProvider, setKeyValue);

var listProvidersCommand = new Command("providers", "List available providers");
listProvidersCommand.SetHandler(async () =>
{
    var configSvc = new ConfigurationService();
    await configSvc.LoadAsync();
    Console.WriteLine("Available providers:");
    foreach (var (name, cfg) in configSvc.Config.Providers)
    {
        var hasKey = !string.IsNullOrEmpty(cfg.ApiKey) ? "key set" : "no key";
        Console.WriteLine($"  [{hasKey}] {name,-15} {cfg.BaseUrl} ({cfg.DefaultModel})");
    }
});

configCommand.AddCommand(setKeyCommand);
configCommand.AddCommand(listProvidersCommand);

rootCommand.AddCommand(chatCommand);
rootCommand.AddCommand(runCommand);
rootCommand.AddCommand(configCommand);

return await rootCommand.InvokeAsync(args);

static async Task<ServiceProvider> BuildServiceProvider(
    string? provider, string? model, string? dir)
{
    var services = new ServiceCollection();

    // Logging
    services.AddLogging(builder =>
    {
        builder.ClearProviders();
        builder.AddConsole();
    });

    // HTTP clients
    services.AddHttpClient("WebFetch", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", "AiCodeAgent/1.0");
    });

    // Configuration
    var configSvc = new ConfigurationService();
    await configSvc.LoadAsync();
    services.AddSingleton(configSvc);
    services.AddSingleton(configSvc.Config);

    // Register provider
    var providerName = provider ?? configSvc.Config.DefaultProvider;
    RegisterProvider(services, providerName, configSvc);

    // Core services
    services.AddSingleton<IContextManager, InMemoryContextManager>();
    services.AddSingleton<IToolRegistry, ToolRegistry>();
    services.AddSingleton<IAgentEventBus, AgentEventBus>();
    services.AddSingleton<IPermissionService, PermissionService>();
    services.AddSingleton<ICheckpointManager, CheckpointManager>();
    services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
    services.AddSingleton<AgentConfiguration>(configSvc.Config.Agent);

    // Register tools
    services.AddSingleton<ITool, ReadFileTool>();
    services.AddSingleton<ITool, WriteFileTool>();
    services.AddSingleton<ITool, EditFileTool>();
    services.AddSingleton<ITool, ListDirectoryTool>();
    services.AddSingleton<ITool, GrepTool>();
    services.AddSingleton<ITool, ExecuteCommandTool>();
    services.AddSingleton<ITool, GitTool>();
    services.AddSingleton<ITool, WebFetchTool>();
    services.AddSingleton<ITool, DiagnosticsTool>();

    // UI
    services.AddSingleton<TerminalUI>();
    services.AddSingleton<SingleRunMode>();

    var sp = services.BuildServiceProvider();

    // Initialize tool registry
    var registry = sp.GetRequiredService<IToolRegistry>();
    foreach (var tool in sp.GetServices<ITool>())
        registry.Register(tool);

    return sp;
}

static void RegisterProvider(
    IServiceCollection services,
    string providerName,
    ConfigurationService configSvc)
{
    var cfg = configSvc.GetProvider(providerName)
        ?? throw new InvalidOperationException($"Provider not found: {providerName}");

    // Try to get API key from environment (both legacy and new naming are supported)
    var envKeyNew = $"AI_CODE_AGENT_{providerName.ToUpper()}_API_KEY";
    var envKeyLegacy = $"AIAGENT_{providerName.ToUpper()}_API_KEY";
    var apiKey = Environment.GetEnvironmentVariable(envKeyNew)
              ?? Environment.GetEnvironmentVariable(envKeyLegacy)
              ?? cfg.ApiKey;
    cfg = cfg with { ApiKey = apiKey };

    services.AddSingleton<IAiProvider>(sp =>
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        return providerName.ToLower() switch
        {
            "openai" => new OpenAiProvider(cfg, loggerFactory.CreateLogger<OpenAiProvider>()),
            "anthropic" => new AnthropicProvider(cfg, loggerFactory.CreateLogger<AnthropicProvider>()),
            "ollama" => new OllamaProvider(cfg, loggerFactory.CreateLogger<OllamaProvider>()),
            _ => new OpenAiCompatibleProvider(providerName, cfg,
                loggerFactory.CreateLogger<OpenAiCompatibleProvider>())
        };
    });
}