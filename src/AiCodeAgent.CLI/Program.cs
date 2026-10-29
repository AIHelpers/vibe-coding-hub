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
var characterOption = new Option<string?>("--character", "Start the chat as this character (see 'characters list')");

chatCommand.AddOption(providerOption);
chatCommand.AddOption(modelOption);
chatCommand.AddOption(dirOption);
chatCommand.AddOption(autoOption);
chatCommand.AddOption(verboseOption);
chatCommand.AddOption(continueOption);
chatCommand.AddOption(resumeOption);
chatCommand.AddOption(forkSessionOption);
chatCommand.AddOption(characterOption);

chatCommand.SetHandler(async context =>
{
    var p = context.ParseResult;
    var provider = p.GetValueForOption(providerOption);
    var model = p.GetValueForOption(modelOption);
    var dir = p.GetValueForOption(dirOption);
    var auto = p.GetValueForOption(autoOption);
    var verbose = p.GetValueForOption(verboseOption);
    var cont = p.GetValueForOption(continueOption);
    var resume = p.GetValueForOption(resumeOption);
    var forkFrom = p.GetValueForOption(forkSessionOption);
    var characterId = p.GetValueForOption(characterOption);
    var services = await BuildServiceProvider(provider, model, dir, verbose);
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
    }, sessionId, characterId);
});

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
var pipelineTaskArg = new Argument<string>("task", () => string.Empty, "Description of the task to carry through the pipeline (or use --task-file)");
var taskFileOption = new Option<string?>("--task-file", "Read the task from this file instead of the argument (long task descriptions, CI)");
var nonInteractiveOption = new Option<bool>("--non-interactive", "CI mode for flows: never ask anything. Tool requests that need approval are denied, a step with requireConfirmation (or a human step) ends the flow with a reason. Exit codes: 0 success, 1 failed step, 2 budget exceeded, 3 validation error, 4 configuration error");
var permissionOption = new Option<string?>("--permission", "Flows: permission mode for every step, replacing the role defaults: Plan (read-only), Ask, AutoEdit or FullAuto. With --non-interactive, whatever the mode would still ask about is denied");
var outputOption = new Option<string?>("--output", "Non-interactive: report format, json or markdown (written to --output-file, else stdout)");
var outputFileOption = new Option<string?>("--output-file", "Non-interactive: write the report to this file");
var timeoutOption = new Option<int?>("--timeout", "Non-interactive: stop the run after this many seconds (exit code 1)");
var pipelineNameOption = new Option<string>("--pipeline", () => "full-sdlc", "Pipeline to run (see 'pipeline list')");
var castOption = new Option<string?>("--cast", "Cast characters into stages: role=character[,role=character...], e.g. planner=alex-architect,implementer=sam-dev");
pipelineRunCommand.AddArgument(pipelineTaskArg);
pipelineRunCommand.AddOption(pipelineNameOption);
pipelineRunCommand.AddOption(castOption);
var maxParallelOption = new Option<int?>("--max-parallel", "Flows: most characters working at the same time (default: the flow's maxParallel, else 3)");
pipelineRunCommand.AddOption(maxParallelOption);
var maxBudgetOption = new Option<decimal?>("--max-budget", "Flows: spend cap in USD for the run (default: the flow's maxBudgetUsd, else none). Running steps finish their current call, no new step starts, and the flow ends as budget_exceeded");
var jsonOption = new Option<bool>("--json", "Flows: print the result (status, totals, one entry per step run with tokens and cost) as JSON on stdout; progress goes to stderr");
pipelineRunCommand.AddOption(maxBudgetOption);
pipelineRunCommand.AddOption(jsonOption);
pipelineRunCommand.AddOption(taskFileOption);
pipelineRunCommand.AddOption(nonInteractiveOption);
pipelineRunCommand.AddOption(permissionOption);
pipelineRunCommand.AddOption(outputOption);
pipelineRunCommand.AddOption(outputFileOption);
pipelineRunCommand.AddOption(timeoutOption);
pipelineRunCommand.AddOption(providerOption);
pipelineRunCommand.AddOption(modelOption);
pipelineRunCommand.AddOption(dirOption);
pipelineRunCommand.SetHandler(async context =>
{
    var task = context.ParseResult.GetValueForArgument(pipelineTaskArg);
    var pipelineName = context.ParseResult.GetValueForOption(pipelineNameOption)!;
    var cast = context.ParseResult.GetValueForOption(castOption);
    var maxParallel = context.ParseResult.GetValueForOption(maxParallelOption);
    var maxBudget = context.ParseResult.GetValueForOption(maxBudgetOption);
    var asJson = context.ParseResult.GetValueForOption(jsonOption);
    var provider = context.ParseResult.GetValueForOption(providerOption);
    var model = context.ParseResult.GetValueForOption(modelOption);
    var dir = context.ParseResult.GetValueForOption(dirOption);
    var taskFile = context.ParseResult.GetValueForOption(taskFileOption);
    if (context.ParseResult.GetValueForOption(nonInteractiveOption))
    {
        // CI: no prompts, a report file and a meaningful exit code.
        Environment.ExitCode = await HeadlessPipelineMode.RunAsync(
            new HeadlessArgs(pipelineName, task, taskFile, cast, maxParallel, maxBudget,
                context.ParseResult.GetValueForOption(permissionOption),
                context.ParseResult.GetValueForOption(outputOption) ?? (asJson ? "json" : null),
                context.ParseResult.GetValueForOption(outputFileOption),
                context.ParseResult.GetValueForOption(timeoutOption), model, dir),
            async () => await BuildServiceProvider(provider, model, dir));
        return;
    }
    if (!HeadlessPipelineMode.TryResolveTask(task, taskFile, out task, out var taskError))
    {
        Console.Error.WriteLine(taskError);
        Environment.ExitCode = 1;
        return;
    }
    Dictionary<string, string> castMap;
    try { castMap = SdlcPipelineRunner.ParseCast(cast); }
    catch (FormatException ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; return; }
    var services = await BuildServiceProvider(provider, model, dir);
    var loader = services.GetRequiredService<SdlcPipelineLoader>();
    // The loader looks in <dir>/.aiagent/pipelines first, then ~/.aiagent/pipelines, then the built-ins.
    var entry = loader.GetEntry(pipelineName);
    var pipeline = entry?.Pipeline;
    if (entry == null || pipeline == null)
    {
        Console.Error.WriteLine($"Unknown pipeline '{pipelineName}'. Run 'pipeline list' to see available pipelines.");
        Environment.Exit(1);
        return;
    }
    Console.Error.WriteLine($"Using {entry.ScopeLabel} pipeline '{pipelineName}'" +
        (entry.FilePath != null ? $" ({entry.FilePath})" : "") +
        (entry.OverridesText != null ? $" - {entry.OverridesText}" : ""));

    var runner = new PipelineRunMode(services.GetRequiredService<SdlcPipelineRunner>());
    try
    {
        await runner.RunAsync(pipeline, task, dir ?? Directory.GetCurrentDirectory(), model, castMap, maxParallel, maxBudget, asJson);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("character", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }
});

var pipelineListCommand = new Command("list", "List available pipelines with their scope (project, global or builtin)");
pipelineListCommand.AddOption(dirOption);
pipelineListCommand.SetHandler(async context =>
{
    var dir = context.ParseResult.GetValueForOption(dirOption);
    var services = await BuildServiceProvider(null, null, dir);
    var loader = services.GetRequiredService<SdlcPipelineLoader>();
    Console.WriteLine("Available pipelines:");
    Console.WriteLine($"  {"NAME",-18} {"SCOPE",-8} DESCRIPTION");
    foreach (var entry in loader.GetEntries())
    {
        var p = entry.Pipeline;
        Console.WriteLine($"  {p.Name,-18} {entry.ScopeLabel,-8} {(p.IsFlow ? "[flow] " : "")}{p.Description}{(entry.OverridesText != null ? $" ({entry.OverridesText})" : "")}");
        if (p.IsFlow)
        {
            Console.WriteLine($"      {AiCodeAgent.Core.Flows.FlowGraph.Build(p).DescribeWaves()}");
            continue;
        }
        foreach (var stage in p.Stages)
            Console.WriteLine($"      - [{(stage.Enabled ? "x" : " ")}] {stage.Name} ({stage.Role}{(string.IsNullOrEmpty(stage.Character) ? "" : " as " + stage.Character)})");
    }
});

// pipeline validate / show: check a flow and see what runs in parallel, without a provider.
async Task<(SdlcPipelineDefinition? Pipeline, AiCodeAgent.Core.Flows.FlowGraph? Graph)> LoadGraphAsync(string name, string? cast, string? dir)
{
    var loader = new SdlcPipelineLoader(Microsoft.Extensions.Logging.Abstractions.NullLogger<SdlcPipelineLoader>.Instance,
        null, dir ?? Directory.GetCurrentDirectory());
    var pipeline = loader.GetPipeline(name);
    if (pipeline == null && AiCodeAgent.Core.Flows.ExampleLibrary.FromEmbedded().Templates()
            .FirstOrDefault(t => t.Id.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } bundled)
    {
        // A bundled flow template that was not copied yet: check it against the characters of this project.
        pipeline = bundled.Flow;
        Console.Error.WriteLine($"'{name}' is a bundled template (copy it with 'pipeline copy {name} --to project').");
    }
    if (pipeline == null)
    {
        Console.Error.WriteLine($"Unknown pipeline '{name}'. Run 'pipeline list' to see available pipelines.");
        Environment.ExitCode = 1;
        return (null, null);
    }
    Dictionary<string, string> castMap;
    try { castMap = SdlcPipelineRunner.ParseCast(cast); }
    catch (FormatException ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; return (null, null); }
    var registries = await SkillCharacterCommands.OpenAsync(dir);
    var graph = AiCodeAgent.Core.Flows.FlowGraph.Build(pipeline, id => registries.Characters.GetAsync(id).GetAwaiter().GetResult(), castMap);
    return (pipeline, graph);
}

var flowNameArg = new Argument<string>("name", "Pipeline (flow) name");
var pipelineValidateCommand = new Command("validate", "Check a pipeline/flow: unknown nodes and characters, cycles without a loop limit, bad conditions");
pipelineValidateCommand.AddArgument(flowNameArg);
pipelineValidateCommand.AddOption(castOption);
pipelineValidateCommand.AddOption(dirOption);
pipelineValidateCommand.SetHandler(async (name, cast, dir) =>
{
    var (_, graph) = await LoadGraphAsync(name, cast, dir);
    if (graph == null) return;
    foreach (var problem in graph.Problems)
        Console.WriteLine($"  {problem}");
    Console.WriteLine(graph.IsValid
        ? $"'{name}' is valid. {graph.DescribeWaves()}"
        : $"'{name}' has {graph.Problems.Count(p => p.IsError)} error(s).");
    if (!graph.IsValid) Environment.ExitCode = 1;
}, flowNameArg, castOption, dirOption);

var mermaidOption = new Option<bool>("--mermaid", "Print a Mermaid flowchart instead");
var withStatusOption = new Option<string?>("--with-status", "With --mermaid or --report: a saved run id (or 'latest'); nodes are coloured by that run's status and loops show their actual rounds");
var reportOption = new Option<bool>("--report", "Print the Markdown report of the run given by --with-status (task, diagram, steps, answers, totals, changed files)");
var pipelineShowCommand = new Command("show", "Show a pipeline/flow: which characters run in parallel, loops and conditions");
pipelineShowCommand.AddArgument(flowNameArg);
pipelineShowCommand.AddOption(mermaidOption);
pipelineShowCommand.AddOption(withStatusOption);
pipelineShowCommand.AddOption(reportOption);
pipelineShowCommand.AddOption(castOption);
pipelineShowCommand.AddOption(dirOption);
pipelineShowCommand.SetHandler(async (name, mermaid, cast, dir, withStatus, report) =>
{
    if (report && string.IsNullOrWhiteSpace(withStatus))
    {
        Console.Error.WriteLine("--report needs --with-status <runId|latest>.");
        Environment.ExitCode = 3;
        return;
    }
    if (!string.IsNullOrWhiteSpace(withStatus))
    {
        // Runs are saved per flow; read one back and draw it (or write its report) without a provider.
        var store = new AiCodeAgent.Core.Flows.FlowRunStore();
        var runId = withStatus.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? store.List(name).FirstOrDefault()?.RunId
            : withStatus;
        var run = runId == null ? null : store.Load(name, runId);
        if (run == null)
        {
            Console.Error.WriteLine(runId == null ? $"No saved runs of '{name}'." : $"No saved run '{runId}' of '{name}'.");
            Environment.ExitCode = 3;
            return;
        }
        Console.Write(report
            ? AiCodeAgent.Core.Flows.FlowReportBuilder.Markdown(run, s => store.LoadOutput(name, run.Id, s))
            : AiCodeAgent.Core.Flows.FlowReportBuilder.Mermaid(run));
        return;
    }
    var (pipeline, graph) = await LoadGraphAsync(name, cast, dir);
    if (pipeline == null || graph == null) return;
    if (mermaid)
    {
        Console.Write(graph.ToMermaid());
        return;
    }
    Console.WriteLine($"{pipeline.Name}{(pipeline.IsFlow ? " (flow)" : " (linear: one shared history)")}: {pipeline.Description}");
    Console.WriteLine();
    foreach (var (wave, index) in graph.Waves.Select((w, i) => (w, i)))
    {
        Console.WriteLine($"  {index + 1}. {string.Join("  ∥  ", wave.Select(id =>
        {
            var node = graph.GetNode(id)!;
            var who = AiCodeAgent.Core.Flows.FlowGraph.EffectiveCharacter(node.Stage, SdlcPipelineRunner.ParseCast(cast)) ?? node.Stage.Role;
            return $"{id} ({who}{(node.Outcomes.Count > 0 ? ": " + string.Join("/", node.Outcomes) : "")})";
        }))}");
    }
    var special = graph.Edges.Where(e => e.IsLoop || e.IsConditional).ToList();
    if (special.Count > 0)
    {
        Console.WriteLine();
        foreach (var e in special)
            Console.WriteLine(e.IsLoop
                ? $"  ↺ {e.From} → {e.To} when {e.When ?? "always"} (up to {e.MaxLoops}×)"
                : $"  ⤷ {e.From} → {e.To} when {e.When}");
    }
    foreach (var problem in graph.Problems)
        Console.WriteLine($"  {problem}");
    if (!graph.IsValid) Environment.ExitCode = 1;
}, flowNameArg, mermaidOption, castOption, dirOption, withStatusOption, reportOption);

pipelineCommand.AddCommand(pipelineRunCommand);
pipelineCommand.AddCommand(pipelineListCommand);
pipelineCommand.AddCommand(pipelineValidateCommand);
pipelineCommand.AddCommand(pipelineShowCommand);

// pipeline copy: start a project (or global) flow from a built-in, global or project one.
var copyNameArg = new Argument<string>("name", "Pipeline to copy");
var copyToOption = new Option<string>("--to", "Where to copy it: project (<dir>/.aiagent/pipelines, shared via git) or global (~/.aiagent/pipelines)") { IsRequired = true };
var copyForceOption = new Option<bool>("--force", "Overwrite a pipeline of the same name in the target scope");
var copyWithCharactersOption = new Option<bool>("--with-characters", "For a bundled template: also create the example characters (and skills) the project lacks");
var pipelineCopyCommand = new Command("copy", "Copy a pipeline into the project or global scope so you can edit it there");
pipelineCopyCommand.AddArgument(copyNameArg);
pipelineCopyCommand.AddOption(copyToOption);
pipelineCopyCommand.AddOption(copyForceOption);
pipelineCopyCommand.AddOption(copyWithCharactersOption);
pipelineCopyCommand.AddOption(dirOption);
pipelineCopyCommand.SetHandler(async (name, to, force, withCharacters, dir) =>
{
    if (!PipelineScopeExtensions.TryParse(to, out var target) || target == PipelineScope.BuiltIn)
    {
        Console.Error.WriteLine($"Unknown scope '{to}'. Use --to project or --to global.");
        Environment.ExitCode = 1;
        return;
    }
    var projectDir = dir ?? Directory.GetCurrentDirectory();
    var loader = new SdlcPipelineLoader(Microsoft.Extensions.Logging.Abstractions.NullLogger<SdlcPipelineLoader>.Instance, null, projectDir);
    if (target == PipelineScope.Project && loader.ProjectDirectory == null)
    {
        Console.Error.WriteLine($"'{projectDir}' cannot hold project pipelines (use --dir to pick the repository folder).");
        Environment.ExitCode = 1;
        return;
    }
    var library = AiCodeAgent.Core.Flows.ExampleLibrary.FromEmbedded();
    if (loader.GetEntry(name) == null && library.Templates().FirstOrDefault(t => t.Id.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } template)
    {
        // Not in any scope yet: copy it from the bundled templates.
        if (loader.GetEntry(name, target) != null && !force)
        {
            Console.Error.WriteLine($"A {target.Label()} pipeline '{name}' already exists. Use --force to overwrite it.");
            Environment.ExitCode = 1;
            return;
        }
        var registries = await SkillCharacterCommands.OpenAsync(projectDir);
        var plan = library.Plan(template,
            (await registries.Characters.ListAsync()).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
            (await registries.Skills.ListAllAsync()).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (withCharacters && !plan.NothingToCreate)
        {
            var inProject = target == PipelineScope.Project;
            var written = library.Install(plan,
                inProject ? registries.Characters.ProjectCharactersDirectory ?? registries.Characters.GlobalCharactersDirectory : registries.Characters.GlobalCharactersDirectory,
                inProject ? registries.Skills.ProjectSkillsDirectory ?? registries.Skills.GlobalSkillsDirectory : registries.Skills.GlobalSkillsDirectory);
            foreach (var file in written) Console.WriteLine($"Created {file}");
        }
        else if (plan.CharactersToCreate.Count > 0)
        {
            Console.WriteLine($"Missing characters: {string.Join(", ", plan.CharactersToCreate)}. Run again with --with-characters to create them.");
        }
        var saved = await loader.SavePipelineAsync(template.Flow, target);
        Console.WriteLine($"Copied template '{name}' to the {target.Label()} scope: {saved}");
        Console.WriteLine($"Sample task: {template.SampleTask}");
        return;
    }
    try
    {
        var copied = await loader.CopyPipelineAsync(name, target, force);
        Console.WriteLine($"Copied '{name}' to the {target.Label()} scope: {copied.FilePath}");
        if (target == PipelineScope.Project && !SdlcPipelineLoader.IsGitRepository(projectDir))
            Console.WriteLine("Note: this folder is not a git repository, so the flow is not shared until it is committed somewhere.");
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }
}, copyNameArg, copyToOption, copyForceOption, copyWithCharactersOption, dirOption);
pipelineCommand.AddCommand(pipelineCopyCommand);

// pipeline templates: the bundled flow templates and which of their characters this project has.
var pipelineTemplatesCommand = new Command("templates", "List the bundled flow templates (bug-fix, pr-review, release, ...) and the characters each needs");
pipelineTemplatesCommand.AddOption(dirOption);
pipelineTemplatesCommand.SetHandler(async (string? dir) =>
{
    var registries = await SkillCharacterCommands.OpenAsync(dir);
    var existing = (await registries.Characters.ListAsync()).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Console.WriteLine("Flow templates (copy one with: pipeline copy <name> --to project [--with-characters]):");
    foreach (var template in AiCodeAgent.Core.Flows.ExampleLibrary.FromEmbedded().Templates())
    {
        Console.WriteLine($"  {template.Id,-18} {template.Title}");
        Console.WriteLine($"      {template.Description}");
        Console.WriteLine($"      needs: {string.Join(", ", template.Characters.Select(c => (existing.Contains(c) ? "✓ " : "✗ ") + c))}");
        Console.WriteLine($"      sample task: {template.SampleTask}");
    }
}, dirOption);
pipelineCommand.AddCommand(pipelineTemplatesCommand);

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
            // Merged with models.json: known models first, retired ones last and marked.
            foreach (var m in AiCodeAgent.Core.Configuration.ModelCatalogLoader.Default.Merge(provider, models))
                Console.WriteLine($"  - {m.Label}");
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

SkillCharacterCommands.AddCommands(rootCommand);
rootCommand.AddCommand(chatCommand);
rootCommand.AddCommand(runCommand);
rootCommand.AddCommand(pipelineCommand);
rootCommand.AddCommand(configCommand);

var exitCode = await rootCommand.InvokeAsync(args);
// Handlers report failures through Environment.ExitCode (System.CommandLine returns 0 for a completed handler).
return exitCode != 0 ? exitCode : Environment.ExitCode;

static async Task<ServiceProvider> BuildServiceProvider(
    string? provider, string? model, string? dir, bool verbose = false)
{
    var services = new ServiceCollection();

    // Logging
    services.AddLogging(builder =>
    {
        builder.ClearProviders();
        CliLogging.Configure(builder, verbose);
    });

    // HTTP clients
    services.AddHttpClient("WebFetch", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", "AiCodeAgent/1.0");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
    {
        // See App.axaml.cs for why: WebFetchTool re-validates each redirect
        // hop itself, and this ConnectCallback is the second, connect-time
        // SSRF check that catches DNS rebinding.
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var entry = await System.Net.Dns.GetHostEntryAsync(context.DnsEndPoint.Host, cancellationToken);
            var address = entry.AddressList.FirstOrDefault(a => !SsrfGuard.IsBlockedAddress(a))
                ?? throw new InvalidOperationException(
                    $"Blocked address for host '{context.DnsEndPoint.Host}' (private/loopback/link-local range).");
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    });

    // Configuration
    var configSvc = new ConfigurationService();
    await configSvc.LoadAsync();
    services.AddSingleton(configSvc);
    services.AddSingleton(configSvc.Config);

    // Agent options - resolved by ISessionStore/SkillRegistry/HookRegistry factories
    services.AddSingleton(new AgentOptions
    {
        Model = model,
        WorkingDirectory = dir ?? Directory.GetCurrentDirectory()
    });

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
    // See App.axaml.cs for why this registration matters (issue 3).
    services.AddSingleton<IPermissionManager, PermissionManager>();
    services.AddSingleton<ICheckpointManager, CheckpointManager>();
    services.AddSingleton<AiCodeAgent.Core.Usage.SessionCostTracker>();
    services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
    services.AddSingleton<AgentConfiguration>(configSvc.Config.Agent);
    services.AddSingleton<RolePresetLoader>();
    services.AddSingleton<AiCodeAgent.Core.Agent.IWorkspaceIsolation, AiCodeAgent.Tools.Git.GitWorktreeManager>();
    services.AddSingleton<AgentSessionCoordinator>();
    // Built-in -> ~/.aiagent/pipelines -> <dir>/.aiagent/pipelines (the project wins on a name clash).
    services.AddSingleton(sp => new SdlcPipelineLoader(
        sp.GetRequiredService<ILogger<SdlcPipelineLoader>>(), null, dir ?? Directory.GetCurrentDirectory()));
    services.AddSingleton(new AiCodeAgent.Core.Flows.FlowRunStore(null, () => configSvc.Config.Agent.FlowRunHistoryLimit));
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

    // Characters: personas with their own skills that run workflows
    services.AddSingleton<AiCodeAgent.Core.Characters.ICharacterRegistry>(sp =>
        new AiCodeAgent.Core.Characters.CharacterRegistry(
            sp.GetRequiredService<RolePresetLoader>(),
            sp.GetService<ILogger<AiCodeAgent.Core.Characters.CharacterRegistry>>(),
            projectCharactersDir: AiCodeAgent.Core.Characters.CharacterRegistry.GetDefaultProjectCharactersDir(
                sp.GetRequiredService<AgentOptions>().WorkingDirectory)));
    services.AddSingleton<AiCodeAgent.Core.Characters.SkillMaintenance>();

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
    services.AddSingleton<ITool, GlobTool>();
    services.AddSingleton<ITool, AiCodeAgent.Tools.FileSystem.MultiEditTool>();
    services.AddSingleton<ITool, AiCodeAgent.Tools.FileSystem.ApplyPatchTool>();
    services.AddSingleton<ITool, AiCodeAgent.Tools.Planning.TodoWriteTool>();
    services.AddSingleton<ITool, AiCodeAgent.Tools.Planning.AskUserTool>();
    services.AddSingleton<ITool, AiCodeAgent.Tools.Verification.VerifyChangesTool>();
    services.AddSingleton<ITool, AiCodeAgent.Tools.Browser.BrowserCheckTool>();
    // The sandbox reads the saved settings (and the AIAGENT_SANDBOX* variables) on every command.
    services.AddSingleton<AiCodeAgent.Core.Agent.ICommandSandbox>(sp =>
    {
        var agentConfig = sp.GetRequiredService<AgentConfiguration>();
        return new AiCodeAgent.Tools.Shell.DockerCommandSandbox(
            () => AiCodeAgent.Tools.Shell.DockerSandboxOptions.FromConfiguration(agentConfig.Sandbox));
    });
    AiCodeAgent.Tools.Browser.BrowserCheckTool.ConfiguredBrowserPath = () => configSvc.Config.Agent?.BrowserPath;
    services.AddSingleton<AiCodeAgent.Core.Agent.IUserQuestionHandler, ConsoleUserQuestionHandler>();
    services.AddSingleton<ITool, ExecuteCommandTool>();
    services.AddSingleton<ITool, GitTool>();
    services.AddSingleton<ITool, WebFetchTool>();
    services.AddSingleton<ITool, DiagnosticsTool>();
    services.AddSingleton<ITool, FindReferencesTool>();
    services.AddSingleton<ITool, GoToDefinitionTool>();
    services.AddSingleton<ITool, GetDiagnosticsTool>();
    services.AddSingleton<ITool, ScaffoldBackendTool>();
    services.AddSingleton<ITool, SpawnSubagentTool>();
    services.AddSingleton<ITool, AiCodeAgent.Tools.Skills.UseSkillTool>();

    // Inter-agent communication: share the mailbox so agents can send messages.
    services.AddSingleton<ITool>(sp => new SendMessageTool(sp.GetRequiredService<AgentSessionCoordinator>().Mailbox, sp.GetService<IAgentEventBus>(), sp.GetRequiredService<ILogger<SendMessageTool>>()));

    // Subagent runner (Feature 07)
    services.AddSingleton<ISubagentRunner, SubagentRunner>();

    // Hooks (Feature 09)
    services.AddSingleton<IHookRegistry>(sp =>
    {
        var workdir = sp.GetRequiredService<AgentOptions>().WorkingDirectory;
        var registry = new HookRegistry(sp.GetService<ILogger<HookRegistry>>());
        try { registry.LoadAsync(workdir).GetAwaiter().GetResult(); } catch { /* best-effort */ }

        // Project hooks are arbitrary shell commands; require an explicit OK.
        if (registry.UntrustedProjectHooks.Count > 0)
        {
            Console.Error.WriteLine($"This project defines {registry.UntrustedProjectHooks.Count} hook(s) that run shell commands:");
            foreach (var h in registry.UntrustedProjectHooks)
                Console.Error.WriteLine($"  - {h.Name} [{h.Event}]: {h.Command}");
            if (!Console.IsInputRedirected)
            {
                Console.Error.Write("Trust and run them? [y/N] ");
                var answer = Console.ReadLine();
                if (string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                    registry.TrustProjectAsync(workdir).GetAwaiter().GetResult();
            }
            else
            {
                Console.Error.WriteLine("Skipping them (non-interactive). Run once interactively to trust this project.");
            }
        }
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
    services.AddSingleton<AfterEditDiagnosticsReporter>();

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
    services.AddSingleton<AiCodeAgent.Core.Sessions.ConversationRewinder>();
    services.AddSingleton<TerminalUI>();
    services.AddSingleton<SingleRunMode>();

    var sp = services.BuildServiceProvider();

    // Initialize tool registry
    var registry = sp.GetRequiredService<IToolRegistry>();
    foreach (var tool in sp.GetServices<ITool>())
        registry.Register(tool);

    // Persisted permission settings + the repo's restrict-only permission profile.
    try
    {
        var permissionManager = sp.GetRequiredService<IPermissionManager>();
        await permissionManager.LoadScopedSettingsAsync();
        await permissionManager.LoadProjectProfileAsync(dir ?? Directory.GetCurrentDirectory());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: could not load permission settings: {ex.Message}");
    }

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

    // The environment wins over the config file: API key, base URL (a self-hosted OpenAI-compatible server) and
    // default model, as AI_CODE_AGENT_<PROVIDER>_API_KEY / _BASE_URL / _MODEL (AIAGENT_<PROVIDER>_... also works).
    cfg = ProviderEnvironment.Apply(cfg, providerName);

    services.AddSingleton<IAiProvider>(sp =>
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        return ProviderFactory.Create(providerName, cfg, loggerFactory);
    });

    // Per-step providers for flows (E5-S3).
    services.AddSingleton<IAiProviderResolver>(sp => new ConfiguredProviderResolver(
        () => providerName,
        name => configSvc.GetProvider(name) is { } found ? ProviderEnvironment.Apply(found, name) : null,
        sp.GetRequiredService<ILoggerFactory>()));
}
