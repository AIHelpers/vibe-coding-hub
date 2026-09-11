using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.IO;
using AiCodeAgent.App.CommandPalette;
using AiCodeAgent.App.EditHistory;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using AiCodeAgent.Context;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Context;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Core.Mcp;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Core.Sessions;
using AiCodeAgent.Indexing;
using AiCodeAgent.Indexing.Semantic;
using AiCodeAgent.LanguageServices;
using AiCodeAgent.LanguageServices.Models;
using AiCodeAgent.LanguageServices.Providers;
using AiCodeAgent.Providers;
using AiCodeAgent.Providers.Backend;
using AiCodeAgent.Tools;
using AiCodeAgent.Tools.Agent;
using AiCodeAgent.Tools.Backend;
using AiCodeAgent.Tools.Mcp;
using AiCodeAgent.Tools.Code;
using AiCodeAgent.Tools.FileSystem;
using AiCodeAgent.Tools.Git;
using AiCodeAgent.Tools.Search;
using AiCodeAgent.Tools.Shell;
using AiCodeAgent.Tools.Web;

namespace AiCodeAgent.App;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            // Load configuration on background thread to avoid async-over-sync deadlock
            var configSvc = Services.GetRequiredService<ConfigurationService>();
            try
            {
                Task.Run(() => configSvc.LoadAsync()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load config: {ex.Message}");
            }

            // Initialize tool registry
            var registry = Services.GetRequiredService<IToolRegistry>();
            foreach (var tool in Services.GetServices<ITool>())
                registry.Register(tool);

            // Initialize MCP connections and register their tools (Feature 08)
            try
            {
                var mcpRegistry = Services.GetRequiredService<IMcpRegistry>();
                Task.Run(() => mcpRegistry.InitializeAsync()).GetAwaiter().GetResult();
                var mcpLogger = Services.GetRequiredService<ILogger<McpToolAdapter>>();
                foreach (var client in mcpRegistry.Clients)
                {
                    var tools = Task.Run(() => client.ListToolsAsync()).GetAwaiter().GetResult();
                    foreach (var toolInfo in tools)
                        registry.Register(new McpToolAdapter(client, toolInfo, mcpLogger));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MCP initialization failed: {ex.Message}");
            }

            // Start background workspace indexing (fire-and-forget full scan)
            try
            {
                var indexer = Services.GetRequiredService<WorkspaceIndexer>();
                _ = indexer.StartAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to start workspace indexer: {ex.Message}");
            }

            // Wire the SharedContextStore's index query delegate so agents can
            // query the workspace index for relevant files by name/symbol match.
            try
            {
                var coordinator = Services.GetRequiredService<AgentSessionCoordinator>();
                var indexQuery = Services.GetRequiredService<WorkspaceIndexQueryService>();
                coordinator.Context.FileQueryDelegate = async (filter, limit) =>
                {
                    var matches = await indexQuery.SearchFilesAsync(filter, limit);
                    return matches.Select(m => m.Path).ToList();
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to wire index query delegate: {ex.Message}");
            }

            var mainViewModel = Services.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };
            // Attach the host window so view-models can access window-scoped
            // services (file/folder pickers, etc.)
            mainViewModel.AttachHostWindow(mainWindow);
            mainViewModel.SessionManager.AttachHostWindow(mainWindow);
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            mainWindow.Activate();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File("logs/aiagent-.log", rollingInterval: RollingInterval.Day)
                .CreateLogger();
            builder.AddSerilog(Log.Logger);
        });

        // HTTP clients
        services.AddHttpClient("WebFetch", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "AiCodeAgent/1.0");
        });

        // Core services
        services.AddSingleton<ConfigurationService>(sp => 
            new ConfigurationService());

        // Agent Configuration
        services.AddSingleton<AgentConfiguration>(sp =>
        {
            var configSvc = sp.GetRequiredService<ConfigurationService>();
            return configSvc.Config.Agent;
        });
        
        // Context Manager
        services.AddSingleton<IContextManager, InMemoryContextManager>();
        
        // Tool Registry
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        
        // Event Bus - decoupled event delivery for UI
        services.AddSingleton<IAgentEventBus, AgentEventBus>();
        
        // Permission Service
        services.AddSingleton<IPermissionService, PermissionService>();
        
        // Checkpoint Manager
        services.AddSingleton<ICheckpointManager, CheckpointManager>();
        
        // Agent Orchestrator
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();

        // Autonomous multi-file agent (Feature 4): planner + step-by-step runner.
        services.AddSingleton<IPlanGenerator, PlanGenerator>();
        services.AddSingleton<IAutonomousAgentRunner, AutonomousAgentRunner>();

        // Conversational requirements clarification (Feature 8)
        services.AddSingleton<IRequirementsAnalyzer, RequirementsAnalyzer>();
        services.AddSingleton<IRequirementsClarifier, RequirementsClarifier>();
        
        // Role Preset Loader
        services.AddSingleton<RolePresetLoader>();
        
        // Agent Session Coordinator (multi-agent orchestration)
        services.AddSingleton<AgentSessionCoordinator>();
        services.AddSingleton<SdlcPipelineLoader>();
        services.AddSingleton<SdlcPipelineRunner>();

        // Multi-agent session registry (Feature 5) - shared by SessionDashboardViewModel
        services.AddSingleton<SessionManager>();

        // Session export/import services (Priority 5)
        services.AddSingleton<SessionRecorder>();
        services.AddSingleton<SessionExportService>();
        services.AddSingleton<SessionImporter>();
        services.AddSingleton<PromptPackService>();

        // Project memory: AGENTS.md / AGENT.md / AIAGENT.md (Feature 04)
        services.AddSingleton<IProjectMemoryLoader, ProjectMemoryLoader>();

        // Skills (Feature 06)
        services.AddSingleton<ISkillRegistry>(sp =>
        {
            var agentCfg = sp.GetRequiredService<AgentConfiguration>();
            var workdir = Directory.GetCurrentDirectory();
            return new SkillRegistry(
                sp.GetService<ILogger<SkillRegistry>>(),
                projectSkillsDir: SkillRegistry.GetDefaultProjectSkillsDir(workdir),
                overrides: agentCfg.SkillOverrides);
        });

        // Register AI provider
        services.AddSingleton<IAiProvider>(sp =>
        {
            var configSvc = sp.GetRequiredService<ConfigurationService>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var providerName = configSvc.Config.DefaultProvider;
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

            if (string.IsNullOrEmpty(cfg.ApiKey))
            {
                var logger = loggerFactory.CreateLogger("App");
                logger.LogWarning("API key for provider '{Provider}' is not configured.", providerName);
            }

            return ProviderFactory.CreateOrFallback(providerName, cfg, loggerFactory);
        });

        // Backend primitives
        services.AddSingleton<BackendPrimitiveCatalog>();
        services.AddSingleton<BackendScaffolder>();

        // Register tools
        services.AddSingleton<ITool, ReadFileTool>();
        services.AddSingleton<ITool>(sp =>
            new WriteFileTool(
                sp.GetRequiredService<ILogger<WriteFileTool>>(),
                sp.GetRequiredService<AfterEditDiagnosticsReporter>()));
        services.AddSingleton<ITool>(sp =>
            new EditFileTool(
                sp.GetRequiredService<ILogger<EditFileTool>>(),
                sp.GetRequiredService<AfterEditDiagnosticsReporter>()));
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

        // Inter-agent communication: share the coordinator mailbox across agents.
        services.AddSingleton<ITool>(sp => new SendMessageTool(sp.GetRequiredService<AgentSessionCoordinator>().Mailbox, sp.GetService<IAgentEventBus>(), sp.GetRequiredService<ILogger<SendMessageTool>>()));

        // Subagent runner (Feature 07)
        services.AddSingleton<ISubagentRunner, SubagentRunner>();

        // Hooks (Feature 09)
        services.AddSingleton<IHookRegistry>(sp =>
        {
            var workdir = Directory.GetCurrentDirectory();
            var registry = new HookRegistry(sp.GetService<ILogger<HookRegistry>>());
            try { registry.LoadAsync(workdir).GetAwaiter().GetResult(); } catch { /* best-effort */ }
            return registry;
        });
        services.AddSingleton<IHookRunner>(sp =>
            new HookRunner(sp.GetRequiredService<IHookRegistry>(), sp.GetService<ILogger<HookRunner>>()));

        // MCP connections (Feature 08)
        var mcpProjectRoot = Directory.GetCurrentDirectory();
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

        // Shared changeset (canonical store for diff hunks)
        services.AddSingleton<SharedChangeset>();

        // LSP (Language Server Protocol) services
        services.AddSingleton<ILanguageProvider, CSharpLanguageProvider>();
        services.AddSingleton<ILanguageProvider, TypeScriptLanguageProvider>();
        services.AddSingleton<ILanguageProvider, PythonLanguageProvider>();
        services.AddSingleton<LanguageProviderRegistry>();
        services.AddSingleton<AfterEditDiagnosticsReporter>();
        services.AddSingleton<LspDocumentService>();

        // Workspace indexing (Priority 4)
        var workspaceRoot = DetectWorkspaceRootForIndexing();
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

        // Semantic codebase Q&A (Feature 2)
        services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            var configSvc = sp.GetRequiredService<ConfigurationService>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var providerName = configSvc.Config.DefaultProvider.ToLowerInvariant();
            var cfg = configSvc.GetProvider(configSvc.Config.DefaultProvider)
                ?? new Core.Configuration.ProviderConfiguration();

            // Resolve API key from env (same logic as chat provider registration)
            var envKeyNew = $"AI_CODE_AGENT_{providerName.ToUpper()}_API_KEY";
            var envKeyLegacy = $"AIAGENT_{providerName.ToUpper()}_API_KEY";
            var apiKey = Environment.GetEnvironmentVariable(envKeyNew)
                      ?? Environment.GetEnvironmentVariable(envKeyLegacy)
                      ?? cfg.ApiKey;
            cfg = cfg with { ApiKey = apiKey };

            if (providerName.Equals("openai", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(cfg.ApiKey))
            {
                return new Providers.OpenAI.OpenAiEmbeddingProvider(cfg, loggerFactory.CreateLogger<Providers.OpenAI.OpenAiEmbeddingProvider>());
            }

            // Fallback: deterministic null provider for dev/demo scenarios
            return new NullEmbeddingProvider();
        });

        services.AddSingleton<CodeChunker>();
        services.AddSingleton<EmbeddingService>(sp =>
            new EmbeddingService(
                sp.GetRequiredService<IEmbeddingProvider>(),
                batchSize: 32,
                sp.GetRequiredService<ILogger<EmbeddingService>>()));
        services.AddSingleton<VectorStore>();
        services.AddSingleton<SemanticIndex>(sp =>
        {
            var dbDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiCodeAgent", "semantic");
            return new SemanticIndex(
                dbDir,
                sp.GetRequiredService<CodeChunker>(),
                sp.GetRequiredService<EmbeddingService>(),
                sp.GetRequiredService<VectorStore>(),
                sp.GetRequiredService<ILogger<SemanticIndex>>());
        });
        services.AddSingleton<Retriever>(sp =>
            new Retriever(sp.GetRequiredService<SemanticIndex>(), defaultTopK: 8));

        // Command Palette
        services.AddSingleton<CommandPaletteRegistry>();
        services.AddSingleton<CommandPaletteViewModel>();

        // Predictive next-edit autocomplete services
        services.AddSingleton<EditHistoryTracker>();
        services.AddSingleton<NextEditPredictor>(sp =>
            new NextEditPredictor(sp.GetRequiredService<IAiProvider>()));

        // Click-to-edit visual layer services (Feature 3)
        services.AddSingleton<SourceIdResolver>();
        services.AddSingleton<PreviewPaneViewModel>();

        // New ViewModels - Workspace components
        services.AddSingleton<FileExplorerViewModel>();
        services.AddSingleton<TerminalViewModel>();
        services.AddSingleton<EditorPaneViewModel>();
        services.AddSingleton<CheckpointBrowserViewModel>();
        services.AddSingleton<DiffViewerViewModel>();
        services.AddSingleton<QuickOpenViewModel>();
        services.AddSingleton<ProjectKnowledgeViewModel>();
        services.AddSingleton<BackgroundTaskManagerViewModel>();
        services.AddSingleton<SessionManagerViewModel>();

        // ViewModels
        // Multi-agent session dashboard (Feature 5)
        services.AddSingleton<SessionDashboardViewModel>();

        services.AddSingleton<MainViewModel>();
        // Feature 6: In-Chat Branch / PR Workflow - GitService for slash commands
        services.AddTransient<GitService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<GitService>>();
            // GitService operates on the current working directory of the app.
            return new GitService(Directory.GetCurrentDirectory(), logger);
        });
        services.AddTransient<ChatViewModel>();
        services.AddTransient<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<ConfigurationService>(),
                null,
                sp.GetRequiredService<ILoggerFactory>()));

        // Services
        services.AddSingleton<AgentService>();
    }

    private static string DetectWorkspaceRootForIndexing()
    {
        var start = Directory.GetCurrentDirectory();
        try
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AiCodeAgent.slnx")) ||
                    Directory.GetFiles(dir.FullName, "*.sln").Length > 0 ||
                    Directory.GetFiles(dir.FullName, "*.slnx").Length > 0 ||
                    File.Exists(Path.Combine(dir.FullName, "package.json")) ||
                    File.Exists(Path.Combine(dir.FullName, "pyproject.toml")) ||
                    Directory.GetFiles(dir.FullName, "*.csproj").Length > 0)
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        catch { /* fall through */ }

        return start;
    }
}