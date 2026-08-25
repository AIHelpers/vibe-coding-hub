using AiCodeAgent.CLI;
using AiCodeAgent.Context;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using AiCodeAgent.Indexing;
using AiCodeAgent.LanguageServices;
using AiCodeAgent.LanguageServices.Models;
using AiCodeAgent.LanguageServices.Providers;
using AiCodeAgent.Providers;
using AiCodeAgent.Providers.Backend;
using AiCodeAgent.Core.Mcp;
using AiCodeAgent.Tools.Agent;
using AiCodeAgent.Tools.Backend;
using AiCodeAgent.Tools.Code;
using AiCodeAgent.Tools.FileSystem;
using AiCodeAgent.Tools.Git;
using AiCodeAgent.Tools.Mcp;
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
var continueOption = new Option<bool>("--continue", "Resume the most recent session for this worktree");
var resumeOption = new Option<bool>("--resume", "Open the interactive session picker");
var forkSessionOption = new Option<string?>("--fork-session", "Fork an existing session id into a new one");

chatCommand.AddOption(providerOption);
chatCommand.AddOption(modelOption);
chatCommand.AddOption(dirOption);
chatCommand.AddOption(autoOption);
chatCommand.AddOption(verboseOption);
chatCommand.AddOption(continueOption);
chatCommand.AddOption(resumeOption);
chatCommand.AddOption(forkSessionOption);

chatCommand.SetHandler(async (provider, model, dir, auto, verbose, cont, resume, forkFrom) =>
{
    var services = await BuildServiceProvider(provider, model, dir);
    var sessionManager = services.GetRequiredService<SessionPersistenceManager>();
    var store = services.GetRequiredService<ISessionStore>();
    var worktree = dir ?? Directory.GetCurrentDirectory();

    string sessionId;
    if (!string.IsNullOrEmpty(forkFrom))
    {
        sessionId = await sessionManager.ForkAsync(forkFrom);
        Console.WriteLine($"Forked session '{forkFrom}' -> '{sessionId}'");
    }
    else if (cont)
    {
        var sessions = await store.ListAsync(worktree);
        var latest = sessions.FirstOrDefault();
        if (latest == null)
        {
            sessionId = sessionManager.CreateNew();
            Console.WriteLine("No previous session found; starting a new one.");
        }
        else
        {
            var (id, history) = await sessionManager.ResumeAsync(latest.SessionId);
            sessionId = id;
            Console.WriteLine($"Resumed session '{sessionId}' ({history.Count} entries).");
        }
    }
    else if (resume)
    {
        var sessions = await store.ListAsync(worktree);
        if (sessions.Count == 0)
        {
            sessionId = sessionManager.CreateNew();
            Console.WriteLine("No sessions found; starting a new one.");
        }
        else
        {
            Console.WriteLine("Available sessions:");
            for (int i = 0; i < sessions.Count; i++)
            {
                Console.WriteLine($"  [{i}] {sessions[i].SessionId}  {sessions[i].UpdatedAt:yyyy-MM-dd HH:mm}  ({sessions[i].EntryCount} entries)");
            }
            Console.Write("Select index (blank to start new): ");
            var sel = Console.ReadLine();
            if (int.TryParse(sel, out var idx) && idx >= 0 && idx < sessions.Count)
            {
                var (id, history) = await sessionManager.ResumeAsync(sessions[idx].SessionId);
                sessionId = id;
                Console.WriteLine($"Resumed session '{sessionId}' ({history.Count} entries).");
            }
            else
            {
                sessionId = sessionManager.CreateNew();
                Console.WriteLine("Starting a new session.");
            }
        }
    }
    else
    {
        sessionId = sessionManager.CreateNew();
    }

    var ui = services.GetRequiredService<TerminalUI>();
    await ui.RunAsync(new AgentOptions
    {
        Model = model,
        WorkingDirectory = worktree,
        AutoApprove = auto,
        Verbose = verbose
    }, sessionId);
}, providerOption, modelOption, dirOption, autoOption, verboseOption, continueOption, resumeOption, forkSessionOption);

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

// pipeline command - run the full (or a custom) SDLC as a multi-agent session
var pipelineCommand = new Command("pipeline", "Run a configurable multi-agent SDLC pipeline (analyze/implement/review/test/deploy)");

var pipelineRunCommand = new Command("run", "Run a pipeline against a task");
var pipelineTaskArg = new Argument<string>("task", "Description of the task to carry through the pipeline");
var pipelineNameOption = new Option<string>("--pipeline", () => "full-sdlc", "Pipeline to run (see 'pipeline list')");
pipelineRunCommand.AddArgument(pipelineTaskArg);
pipelineRunCommand.AddOption(pipelineNameOption);
pipelineRunCommand.AddOption(providerOption);
pipelineRunCommand.AddOption(modelOption);
pipelineRunCommand.AddOption(dirOption);
pipelineRunCommand.SetHandler(async (task, pipelineName, provider, model, dir) =>
{
    var services = await BuildServiceProvider(provider, model, dir);
    var loader = services.GetRequiredService<SdlcPipelineLoader>();
    var pipeline = loader.GetPipeline(pipelineName);
    if (pipeline == null)
    {
        Console.Error.WriteLine($"Unknown pipeline '{pipelineName}'. Run 'pipeline list' to see available pipelines.");
        Environment.Exit(1);
        return;
    }

    var runner = new PipelineRunMode(services.GetRequiredService<SdlcPipelineRunner>());
    await runner.RunAsync(pipeline, task, dir ?? Directory.GetCurrentDirectory(), model);
}, pipelineTaskArg, pipelineNameOption, providerOption, modelOption, dirOption);

var pipelineListCommand = new Command("list", "List available pipelines");
pipelineListCommand.SetHandler(async () =>
{
    var services = await BuildServiceProvider(null, null, null);
    var loader = services.GetRequiredService<SdlcPipelineLoader>();
    Console.WriteLine("Available pipelines:");
    foreach (var p in loader.GetAllPipelines())
    {
        Console.WriteLine($"  {p.Name,-12} {p.Description}");
        foreach (var stage in p.Stages)
            Console.WriteLine($"      - [{(stage.Enabled ? "x" : " ")}] {stage.Name} ({stage.Role})");
    }
});

pipelineCommand.AddCommand(pipelineRunCommand);
pipelineCommand.AddCommand(pipelineListCommand);

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

// config add-provider command - add a custom local provider with a custom URL
var addProviderCommand = new Command("add-provider", "Add a custom local provider with a custom URL");
var addProviderName = new Argument<string>("name", "Provider name (e.g. lmstudio, vllm, localai)");
var addProviderUrl = new Option<string>("--url", "Base URL of the provider (e.g. http://localhost:1234)") { IsRequired = true };
var addProviderModel = new Option<string>("--model", "Default model to use");
var addProviderApiKey = new Option<string>("--api-key", "API key (if required by the provider)");
var addProviderTimeout = new Option<int>("--timeout", "Request timeout in seconds");
var addProviderNoVerifySsl = new Option<bool>("--no-verify-ssl", "Disable SSL certificate verification (for local dev)");

addProviderCommand.AddArgument(addProviderName);
addProviderCommand.AddOption(addProviderUrl);
addProviderCommand.AddOption(addProviderModel);
addProviderCommand.AddOption(addProviderApiKey);
addProviderCommand.AddOption(addProviderTimeout);
addProviderCommand.AddOption(addProviderNoVerifySsl);

addProviderCommand.SetHandler(async (name, url, model, apiKey, timeout, noVerifySsl) =>
{
    var configSvc = new ConfigurationService();
    await configSvc.LoadAsync();

    var existing = configSvc.GetProvider(name);
    var providerCfg = new ProviderConfiguration
    {
        Name = name,
        BaseUrl = url,
        DefaultModel = string.IsNullOrEmpty(model)
            ? existing?.DefaultModel ?? "local-model"
            : model,
        ApiKey = !string.IsNullOrEmpty(apiKey) ? apiKey : existing?.ApiKey,
        TimeoutSeconds = timeout > 0 ? timeout : existing?.TimeoutSeconds ?? 300,
        VerifySsl = !noVerifySsl
    };

    configSvc.Config.Providers[name.ToLowerInvariant()] = providerCfg;
    await configSvc.SaveAsync();
    Console.WriteLine($"Provider '{name}' added:");
    Console.WriteLine($"  URL:    {url}");
    Console.WriteLine($"  Model:  {providerCfg.DefaultModel}");
    Console.WriteLine($"  Timeout: {providerCfg.TimeoutSeconds}s");
    Console.WriteLine($"  SSL:    {(providerCfg.VerifySsl ? "verify" : "skip")}");
    Console.WriteLine();
    Console.WriteLine("Use it with: aicodeagent chat --provider " + name.ToLowerInvariant());
}, addProviderName, addProviderUrl, addProviderModel, addProviderApiKey, addProviderTimeout, addProviderNoVerifySsl);

// config models command - list available models for a provider
var modelsCommand = new Command("models", "List available models for a provider");
var modelsProviderArg = new Argument<string>("provider", "Provider name (e.g. openai, anthropic, ollama, or a custom provider)");
modelsCommand.AddArgument(modelsProviderArg);
modelsCommand.SetHandler(async (provider) =>
{
    var configSvc = new ConfigurationService();
    await configSvc.LoadAsync();
    var cfg = configSvc.GetProvider(provider);
    if (cfg == null)
    {
        Console.Error.WriteLine($"Provider not found: {provider}");
        Console.Error.WriteLine("Run 'config providers' to see available providers.");
        return;
    }

    // Resolve API key from environment (same logic as RegisterProvider)
    var providerKey = provider.ToLowerInvariant();
    var envKeyNew = $"AI_CODE_AGENT_{providerKey.ToUpper()}_API_KEY";
    var envKeyLegacy = $"AIAGENT_{providerKey.ToUpper()}_API_KEY";
    var apiKey = Environment.GetEnvironmentVariable(envKeyNew)
              ?? Environment.GetEnvironmentVariable(envKeyLegacy)
              ?? cfg.ApiKey;
    cfg = cfg with { ApiKey = apiKey };

    using var loggerFactory = LoggerFactory.Create(b => { b.ClearProviders(); });
    IAiProvider aiProvider;
    try
    {
        aiProvider = ProviderFactory.Create(providerKey, cfg, loggerFactory);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to create provider '{provider}': {ex.Message}");
        return;
    }

    Console.WriteLine($"Available models for provider '{provider}' ({aiProvider.Name}):");
    try
    {
        var models = await aiProvider.GetAvailableModelsAsync();
        if (models.Length == 0)
        {
            Console.WriteLine("  (no models returned)");
        }
        else
        {
            foreach (var m in models)
                Console.WriteLine($"  - {m}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to fetch models: {ex.Message}");
        Console.Error.WriteLine("Falling back to statically known models:");
        foreach (var m in aiProvider.SupportedModels)
            Console.Error.WriteLine($"  - {m}");
    }
}, modelsProviderArg);

configCommand.AddCommand(setKeyCommand);
configCommand.AddCommand(listProvidersCommand);
configCommand.AddCommand(addProviderCommand);
configCommand.AddCommand(modelsCommand);

// Session export/import + prompt pack commands (Priority 5)
SessionCommands.AddCommands(configCommand);

// Task history commands (list/show/delete saved tasks)
TaskHistoryCommands.AddCommands(rootCommand);

// File viewing & diff commands
FileViewCommands.AddCommands(rootCommand);

rootCommand.AddCommand(chatCommand);
rootCommand.AddCommand(runCommand);
rootCommand.AddCommand(pipelineCommand);
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
    services.AddSingleton<ISessionStore>(sp =>
    {
        var worktree = sp.GetRequiredService<AgentOptions>().WorkingDirectory;
        return new JsonlSessionStore(worktree);
    });
    services.AddSingleton<SessionPersistenceManager>();
    services.AddSingleton<IToolRegistry, ToolRegistry>();
    services.AddSingleton<IAgentEventBus, AgentEventBus>();
    services.AddSingleton<IPermissionService, PermissionService>();
    services.AddSingleton<ICheckpointManager, CheckpointManager>();
    services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
    services.AddSingleton<AgentConfiguration>(configSvc.Config.Agent);
    services.AddSingleton<RolePresetLoader>();
    services.AddSingleton<AgentSessionCoordinator>();
    services.AddSingleton<SdlcPipelineLoader>();
    services.AddSingleton<SdlcPipelineRunner>();

    // Session export/import services (Priority 5)
    services.AddSingleton<SessionRecorder>();
    services.AddSingleton<SessionExportService>();
    services.AddSingleton<SessionImporter>();
    services.AddSingleton<PromptPackService>();

    // Task history service
    services.AddSingleton<TaskHistoryStore>();

    // Project memory (Feature 04)
    services.AddSingleton<IProjectMemoryLoader, ProjectMemoryLoader>();

    // Auto memory (Feature 05)
    services.AddSingleton<IAutoMemory>(sp =>
        new AutoMemoryStore(sp.GetService<ILogger<AutoMemoryStore>>()));
    services.AddSingleton<LearningExtractor>();

    // Skills (Feature 06)
    services.AddSingleton<ISkillRegistry>(sp =>
    {
        var agentCfg = sp.GetRequiredService<AgentConfiguration>();
        var workdir = sp.GetRequiredService<AgentOptions>().WorkingDirectory;
        return new SkillRegistry(
            sp.GetService<ILogger<SkillRegistry>>(),
            projectSkillsDir: SkillRegistry.GetDefaultProjectSkillsDir(workdir),
            overrides: agentCfg.SkillOverrides);
    });

    // Backend primitives
    services.AddSingleton<BackendPrimitiveCatalog>();
    services.AddSingleton<BackendScaffolder>();

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
    services.AddSingleton<ITool, FindReferencesTool>();
    services.AddSingleton<ITool, GoToDefinitionTool>();
    services.AddSingleton<ITool, GetDiagnosticsTool>();
    services.AddSingleton<ITool, ScaffoldBackendTool>();
    services.AddSingleton<ITool, SpawnSubagentTool>();

    // Subagent runner (Feature 07)
    services.AddSingleton<ISubagentRunner, SubagentRunner>();

    // Hooks (Feature 09)
    services.AddSingleton<IHookRegistry>(sp =>
    {
        var workdir = sp.GetRequiredService<AgentOptions>().WorkingDirectory;
        var registry = new HookRegistry(sp.GetService<ILogger<HookRegistry>>());
        try { registry.LoadAsync(workdir).GetAwaiter().GetResult(); } catch { /* best-effort */ }
        return registry;
    });
    services.AddSingleton<IHookRunner>(sp =>
        new HookRunner(sp.GetRequiredService<IHookRegistry>(), sp.GetService<ILogger<HookRunner>>()));

    // MCP connections (Feature 08)
    var mcpProjectRoot = string.IsNullOrEmpty(dir) ? Directory.GetCurrentDirectory() : Path.GetFullPath(dir);
    var mcpConfigs = McpConfigLoader.Load(mcpProjectRoot);
    services.AddSingleton(mcpConfigs);
    services.AddSingleton<IMcpRegistry>(sp =>
    {
        var loggerFactory = sp.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger<McpRegistry>();
        var configs = sp.GetRequiredService<List<McpServerConfig>>();
        return new McpRegistry(
            configs,
            cfg => cfg.IsRemote
                ? (IMcpClient)new HttpMcpClient(cfg, new HttpClient(), loggerFactory?.CreateLogger<HttpMcpClient>())
                : new StdioMcpClient(cfg, loggerFactory?.CreateLogger<StdioMcpClient>()),
            logger);
    });

    // LSP services
    services.AddSingleton<ILanguageProvider, CSharpLanguageProvider>();
    services.AddSingleton<ILanguageProvider, TypeScriptLanguageProvider>();
    services.AddSingleton<ILanguageProvider, PythonLanguageProvider>();
    services.AddSingleton<LanguageProviderRegistry>();

    // Workspace indexing (Priority 4)
    var workspaceRoot = DetectWorkspaceRootForIndexing(dir);
    services.AddSingleton<WorkspaceIndexStore>(sp =>
        new WorkspaceIndexStore(workspaceRoot, sp.GetRequiredService<ILogger<WorkspaceIndexStore>>()));
    services.AddSingleton<WorkspaceIndexer>(sp =>
        new WorkspaceIndexer(
            sp.GetRequiredService<WorkspaceIndexStore>(),
            sp.GetRequiredService<ILogger<WorkspaceIndexer>>(),
            workspaceRoot));
    services.AddSingleton<WorkspaceIndexQueryService>();
    services.AddSingleton<SymbolIndexer>(sp =>
        new SymbolIndexer(
            sp.GetRequiredService<WorkspaceIndexStore>(),
            sp.GetRequiredService<LanguageProviderRegistry>(),
            workspaceRoot,
            sp.GetRequiredService<ILogger<SymbolIndexer>>()));

    // Model registry (Feature 11)
    services.AddSingleton<ModelRegistry>();

    // UI
    services.AddSingleton<TerminalUI>();
    services.AddSingleton<SingleRunMode>();

    var sp = services.BuildServiceProvider();

    // Initialize tool registry
    var registry = sp.GetRequiredService<IToolRegistry>();
    foreach (var tool in sp.GetServices<ITool>())
        registry.Register(tool);

    // Initialize MCP connections and register their tools (Feature 08)
    try
    {
        var mcpRegistry = sp.GetRequiredService<IMcpRegistry>();
        await mcpRegistry.InitializeAsync();
        var mcpLogger = sp.GetRequiredService<ILogger<McpToolAdapter>>();
        foreach (var client in mcpRegistry.Clients)
        {
            var tools = await client.ListToolsAsync();
            foreach (var toolInfo in tools)
            {
                registry.Register(new McpToolAdapter(client, toolInfo, mcpLogger));
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: MCP initialization failed: {ex.Message}");
    }

    // Wire the SharedContextStore's index query delegate so agents can
    // query the workspace index for relevant files by name/symbol match.
    try
    {
        var coordinator = sp.GetRequiredService<AgentSessionCoordinator>();
        var indexQuery = sp.GetRequiredService<WorkspaceIndexQueryService>();
        coordinator.Context.FileQueryDelegate = async (filter, limit) =>
        {
            var matches = await indexQuery.SearchFilesAsync(filter, limit);
            return matches.Select(m => m.Path).ToList();
        };
    }
    catch { /* best-effort */ }

    return sp;
}

static string DetectWorkspaceRootForIndexing(string? dir)
{
    var start = string.IsNullOrEmpty(dir)
        ? Directory.GetCurrentDirectory()
        : Path.GetFullPath(dir);
    try
    {
        var d = new DirectoryInfo(start);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "AiCodeAgent.slnx")) ||
                Directory.GetFiles(d.FullName, "*.sln").Length > 0 ||
                Directory.GetFiles(d.FullName, "*.slnx").Length > 0 ||
                File.Exists(Path.Combine(d.FullName, "package.json")) ||
                File.Exists(Path.Combine(d.FullName, "pyproject.toml")) ||
                Directory.GetFiles(d.FullName, "*.csproj").Length > 0)
            {
                return d.FullName;
            }
            d = d.Parent;
        }
    }
    catch { /* fall through */ }

    return start;
}

static void RegisterProvider(
    IServiceCollection services,
    string providerName,
    ConfigurationService configSvc)
{
    var cfg = configSvc.GetProvider(providerName)
        ?? throw new InvalidOperationException($"Provider not found: {providerName}");
    // Normalize the provider name to lowercase so it matches the provider key casing
    providerName = providerName.ToLowerInvariant();

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
        return ProviderFactory.Create(providerName, cfg, loggerFactory);
    });
}
