using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using System;
using System.IO;
using AiCodeAgent.App.Services;
using AiCodeAgent.App.ViewModels;
using AiCodeAgent.App.Views;
using AiCodeAgent.Context;
using AiCodeAgent.Core.Agent;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Interfaces;
using AiCodeAgent.Providers;
using AiCodeAgent.Providers.OpenAI;
using AiCodeAgent.Tools;
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

            var mainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };
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

        // Register AI provider
        services.AddSingleton<IAiProvider>(sp =>
        {
            var configSvc = sp.GetRequiredService<ConfigurationService>();
            var logger = sp.GetRequiredService<ILogger<OpenAiProvider>>();
            var cfg = configSvc.GetProvider(configSvc.Config.DefaultProvider)
                ?? throw new InvalidOperationException($"Provider not found: {configSvc.Config.DefaultProvider}");
            
            if (string.IsNullOrEmpty(cfg.ApiKey))
            {
                logger.LogWarning("API key for provider '{Provider}' is not configured.", 
                    configSvc.Config.DefaultProvider);
            }
            
            try
            {
                return new OpenAiProvider(cfg, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create AI provider, using fallback");
                return new NoOpAiProvider(logger, $"Provider '{configSvc.Config.DefaultProvider}' is not properly configured.");
            }
        });

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

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Services
        services.AddSingleton<AgentService>();
    }
}