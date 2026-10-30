using System.Text.Json;
using AiCodeAgent.Core.Configuration;
using AiCodeAgent.Core.Flows;
using AiCodeAgent.Core.Models;
using AiCodeAgent.Providers.Scripted;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeAgent.Providers.Tests.Scripted;

/// <summary>E8-S1: the scripted provider matches conversations, plays turns, branches on tool results and streams like a real model.</summary>
public class ScriptedProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scripted-" + Guid.NewGuid().ToString("N"));

    public ScriptedProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ScriptedProvider Provider(string json) =>
        new(null, NullLogger.Instance, new[] { ScenarioLoader.Parse(json)! }, delayScale: 0);

    private static CompletionRequest Request(string firstUser, string? system = null, params Message[] after)
    {
        var messages = new List<Message> { new() { Role = MessageRole.User, Content = firstUser } };
        messages.AddRange(after);
        return new CompletionRequest { SystemPrompt = system, Messages = messages };
    }

    private static Message Assistant(string text = "ok") => new() { Role = MessageRole.Assistant, Content = text };
    private static Message ToolResult(string text) => new() { Role = MessageRole.Tool, Content = text };

    private const string Basic = """
        {
          "name": "basic",
          "match": { "first_user_contains": "overdue" },
          "turns": [
            { "text": "Reading.", "tool_call": { "name": "read_file", "args": { "path": "a.cs", "start_line": 3 } } },
            { "text": "Done." }
          ]
        }
        """;

    // ---- matching ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_scenario_is_chosen_by_the_first_user_message()
    {
        var provider = Provider(Basic);

        var hit = await provider.CompleteAsync(Request("Please fix the OVERDUE logic"));
        var miss = await provider.CompleteAsync(Request("Something else entirely"));

        Assert.Equal("Reading.", hit.Content);
        Assert.Contains("No scripted scenario matches", miss.Content);
        Assert.Contains("basic", miss.Content); // names what it does know
    }

    [Fact]
    public async Task A_scenario_can_match_the_system_prompt_and_prefixes_and_regexes()
    {
        var provider = new ScriptedProvider(null, NullLogger.Instance, new[]
        {
            ScenarioLoader.Parse("""{ "name": "a", "match": { "system_contains": "You are Quinn" }, "turns": [ { "text": "quinn" } ] }""")!,
            ScenarioLoader.Parse("""{ "name": "b", "match": { "first_user_starts_with": "Task:" }, "turns": [ { "text": "task" } ] }""")!,
            ScenarioLoader.Parse("""{ "name": "c", "match": { "first_user_regex": "explain (this|the) project" }, "turns": [ { "text": "explain" } ] }""")!
        }, delayScale: 0);

        Assert.Equal("quinn", (await provider.CompleteAsync(Request("x", "<role>You are Quinn, a skeptical QA</role>"))).Content);
        Assert.Equal("task", (await provider.CompleteAsync(Request("Task: build it"))).Content);
        Assert.Equal("explain", (await provider.CompleteAsync(Request("Could you Explain THIS project?"))).Content);
    }

    [Fact]
    public async Task A_scenario_without_any_condition_never_matches_by_accident()
    {
        var provider = Provider("""{ "name": "greedy", "turns": [ { "text": "I answer everything" } ] }""");

        var response = await provider.CompleteAsync(Request("hello"));

        Assert.Contains("No scripted scenario matches", response.Content);
    }

    // ---- turns and branching ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_turn_is_the_number_of_assistant_messages_so_far()
    {
        var provider = Provider(Basic);
        var first = Request("fix overdue");
        var second = Request("fix overdue", null, Assistant(), ToolResult("file text"));
        var third = Request("fix overdue", null, Assistant(), ToolResult("file text"), Assistant("Done."));

        Assert.Equal("Reading.", (await provider.CompleteAsync(first)).Content);
        Assert.Equal("Done.", (await provider.CompleteAsync(second)).Content);
        Assert.Contains("no more turns", (await provider.CompleteAsync(third)).Content);
    }

    [Fact]
    public async Task Tool_calls_carry_json_arguments_and_stable_ids()
    {
        var provider = Provider(Basic);

        var response = await provider.CompleteAsync(Request("fix overdue"));

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("a.cs", Assert.IsType<JsonElement>(call.Arguments["path"]).GetString());
        Assert.Equal(3, Assert.IsType<JsonElement>(call.Arguments["start_line"]).GetInt32());
        Assert.Equal("tool_calls", response.StopReason);
        Assert.StartsWith("scripted-", call.Id);
        var again = await Provider(Basic).CompleteAsync(Request("fix overdue"));
        Assert.Equal(call.Id, again.ToolCalls[0].Id); // deterministic
    }

    private const string Branching = """
        {
          "name": "branch",
          "match": { "first_user_contains": "go" },
          "turns": [
            { "tool_call": { "name": "verify_changes", "args": {} } },
            { "if_result_contains": "passed", "text": "green", "else": { "text": "red" } }
          ]
        }
        """;

    [Theory]
    [InlineData("All 2 stage(s) passed in 3s.", "green")]
    [InlineData("FAIL  Test: dotnet test (exit 1)", "red")]
    public async Task A_turn_branches_on_the_previous_tool_result(string toolResult, string expected)
    {
        var provider = Provider(Branching);

        var response = await provider.CompleteAsync(Request("go", null, Assistant(), ToolResult(toolResult)));

        Assert.Equal(expected, response.Content);
    }

    [Fact]
    public async Task A_condition_that_fails_without_an_else_says_where_the_script_drifted()
    {
        var provider = Provider("""
            { "name": "strict", "match": { "first_user_contains": "go" },
              "turns": [ { "tool_call": { "name": "x" } }, { "if_result_contains": "needle", "text": "found" } ] }
            """);

        var response = await provider.CompleteAsync(Request("go", null, Assistant(), ToolResult("haystack")));

        Assert.Contains("strict", response.Content);
        Assert.Contains("turn 2", response.Content);
        Assert.Contains("needle", response.Content);
        Assert.Contains("haystack", response.Content);
        Assert.Empty(response.ToolCalls);
    }

    // ---- rounds (a flow step that runs again) -------------------------------------------------------------------

    private const string Rounds = """
        {
          "name": "loop",
          "agents": [
            { "name": "review", "match": { "system_contains": "You are Quinn" },
              "rounds": [ [ { "text": "no\nOUTCOME: rejected" } ], [ { "text": "yes\nOUTCOME: approved" } ] ] }
          ]
        }
        """;

    [Fact]
    public async Task A_step_that_starts_again_plays_the_next_round_and_the_last_round_repeats()
    {
        var provider = Provider(Rounds);
        const string system = "You are Quinn.";
        var outcomes = new[] { "rejected", "approved" };

        var first = await provider.CompleteAsync(Request("review round 1", system));
        var second = await provider.CompleteAsync(Request("review round 2 with feedback", system));
        var third = await provider.CompleteAsync(Request("review round 3", system));

        Assert.Equal("rejected", FlowPrompt.ParseOutcome(first.Content, outcomes));
        Assert.Equal("approved", FlowPrompt.ParseOutcome(second.Content, outcomes));
        Assert.Equal("approved", FlowPrompt.ParseOutcome(third.Content, outcomes));
    }

    [Fact]
    public async Task Later_turns_of_a_conversation_stay_in_the_round_it_started_in()
    {
        var provider = Provider("""
            { "name": "r", "match": { "first_user_contains": "go" },
              "rounds": [
                [ { "tool_call": { "name": "a" } }, { "text": "round1-end" } ],
                [ { "tool_call": { "name": "b" } }, { "text": "round2-end" } ] ] }
            """);

        await provider.CompleteAsync(Request("go once"));                 // conversation 1 starts: round 1
        await provider.CompleteAsync(Request("go twice"));                // conversation 2 starts: round 2
        var end1 = await provider.CompleteAsync(Request("go once", null, Assistant(), ToolResult("r")));
        var end2 = await provider.CompleteAsync(Request("go twice", null, Assistant(), ToolResult("r")));

        Assert.Equal("round1-end", end1.Content);
        Assert.Equal("round2-end", end2.Content);
    }

    [Fact]
    public async Task Reset_starts_every_script_at_round_one_again()
    {
        var provider = Provider(Rounds);
        await provider.CompleteAsync(Request("a", "You are Quinn"));
        await provider.CompleteAsync(Request("b", "You are Quinn"));

        provider.Engine.Reset();
        var again = await provider.CompleteAsync(Request("c", "You are Quinn"));

        Assert.Contains("rejected", again.Content);
    }

    // ---- streaming -----------------------------------------------------------------------------------------------

    private static async Task<List<StreamChunk>> Collect(IAsyncEnumerable<StreamChunk> stream)
    {
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in stream) chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public async Task Text_streams_in_chunks_and_the_last_chunk_carries_tool_calls_and_usage()
    {
        var text = new string('a', 70);
        var provider = Provider($$"""
            { "name": "s", "match": { "first_user_contains": "go" },
              "turns": [ { "text": "{{text}}", "chunk": 30, "tool_call": { "name": "read_file", "args": { "path": "x" } },
                           "usage": { "prompt": 11, "completion": 7 } } ] }
            """);

        var chunks = await Collect(provider.StreamAsync(Request("go")));

        Assert.Equal(new[] { 30, 30, 10, 0 }, chunks.Select(c => c.Delta.Length).ToArray());
        Assert.All(chunks.Take(3), c => Assert.False(c.IsFinished));
        Assert.Equal(text, string.Concat(chunks.Select(c => c.Delta)));
        var last = chunks[^1];
        Assert.True(last.IsFinished);
        Assert.Equal("tool_calls", last.FinishReason);
        Assert.Equal("read_file", Assert.Single(last.ToolCalls!).Name);
        Assert.Equal(11, last.Usage!.PromptTokens);
        Assert.Equal(7, last.Usage.CompletionTokens);
    }

    [Fact]
    public async Task A_text_only_turn_finishes_with_stop_and_no_tool_calls()
    {
        var provider = Provider("""{ "name": "t", "match": { "first_user_contains": "go" }, "turns": [ { "text": "hello" } ] }""");

        var chunks = await Collect(provider.StreamAsync(Request("go")));

        var last = chunks[^1];
        Assert.True(last.IsFinished);
        Assert.Equal("stop", last.FinishReason);
        Assert.Null(last.ToolCalls);
        Assert.True(last.Usage!.CompletionTokens > 0); // estimated when the script gives none
    }

    [Fact]
    public async Task Streaming_and_completing_give_the_same_answer()
    {
        var provider = Provider(Basic);
        var request = Request("fix overdue");

        var streamed = await Collect(provider.StreamAsync(request));
        var completed = await provider.CompleteAsync(request);

        Assert.Equal(completed.Content, string.Concat(streamed.Select(c => c.Delta)));
        Assert.Equal(completed.ToolCalls[0].Name, streamed[^1].ToolCalls![0].Name);
    }

    [Fact]
    public async Task A_cancelled_stream_stops()
    {
        var provider = Provider("""{ "name": "t", "match": { "first_user_contains": "go" }, "turns": [ { "text": "0123456789", "chunk": 1 } ] }""");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(provider.StreamAsync(Request("go"), cts.Token)));
    }

    // ---- loading ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_built_in_scenarios_are_loaded()
    {
        var problems = new List<string>();

        var names = ScenarioLoader.LoadAll(null, null, problems).Select(s => s.Name).ToList();

        Assert.Empty(problems);
        Assert.Contains("fix-overdue", names);
        Assert.Contains("explain-project", names);
        Assert.Contains("feature-team", names);
    }

    [Fact]
    public void Files_allow_comments_and_trailing_commas_and_override_built_ins_by_name()
    {
        File.WriteAllText(Path.Combine(_dir, "mine.json"), """
            // my own version of a built-in
            { "name": "fix-overdue", "match": { "first_user_contains": "x", }, "turns": [ { "text": "custom", }, ], }
            """);

        var scenarios = ScenarioLoader.LoadAll(_dir);

        var fix = Assert.Single(scenarios, s => s.Name == "fix-overdue");
        Assert.EndsWith("mine.json", fix.Source);
        Assert.Equal("custom", fix.Turns![0].Text);
    }

    [Fact]
    public void A_broken_file_is_reported_and_skipped()
    {
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, "noname.json"), """{ "turns": [ { "text": "x" } ] }""");
        var problems = new List<string>();

        var scenarios = ScenarioLoader.LoadAll(_dir, null, problems);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("bad.json"));
        Assert.Contains(problems, p => p.Contains("no \"name\""));
        Assert.Contains(scenarios, s => s.Name == "fix-overdue"); // the rest still loads
    }

    // ---- registration ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("ON")]
    public void The_environment_variable_switches_the_provider_on(string value) =>
        Assert.True(ScriptedProviderSupport.IsEnabledFor(value));

    [Fact]
    public void The_factory_builds_the_scripted_provider_only_when_it_is_enabled()
    {
        var config = ScriptedProviderSupport.DefaultConfiguration;

        if (ScriptedProviderSupport.IsEnabled)
        {
            var provider = ProviderFactory.Create("scripted", config, NullLoggerFactory.Instance);
            Assert.IsType<ScriptedProvider>(provider);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => ProviderFactory.Create("scripted", config, NullLoggerFactory.Instance));
        }
    }

    // ---- the shipped scenarios against the real sample and flow ---------------------------------------------------

    private static ScriptedProvider BuiltIn() => new(null, NullLogger.Instance, ScenarioLoader.LoadAll().Where(s => s.Source == ScenarioLoader.BuiltInSource), delayScale: 0);

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AiCodeAgent.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    private const string SampleTask = "The Overdue_ExcludesCompletedItems test is failing. Fix the overdue logic in TodoService and verify with the tests.";

    [Fact]
    public async Task Fix_overdue_runs_read_edit_verify_summary_and_its_edit_fits_the_sample()
    {
        var provider = BuiltIn();
        var messages = new List<Message>();

        async Task<CompletionResponse> Step(string? toolResult)
        {
            if (toolResult != null) messages.Add(ToolResult(toolResult));
            var response = await provider.CompleteAsync(new CompletionRequest { Messages = new List<Message> { new() { Role = MessageRole.User, Content = SampleTask } }.Concat(messages).ToList() });
            messages.Add(Assistant(response.Content));
            return response;
        }

        var read = await Step(null);
        Assert.Equal("read_file", Assert.Single(read.ToolCalls).Name);

        var edit = await Step("file text");
        var editCall = Assert.Single(edit.ToolCalls);
        Assert.Equal("edit_file", editCall.Name);

        // The edit must apply to the sample exactly as shipped, and give the reference fix.
        var source = File.ReadAllText(RepoFile("samples", "TodoApp", "src", "TodoApp", "TodoService.cs"));
        var oldString = ((JsonElement)editCall.Arguments["old_string"]!).GetString()!;
        var newString = ((JsonElement)editCall.Arguments["new_string"]!).GetString()!;
        Assert.Equal(1, source.Split(oldString).Length - 1);
        Assert.Contains("!x.Done && x.Due < today", source.Replace(oldString, newString));

        var verify = await Step("edited");
        Assert.Equal("verify_changes", Assert.Single(verify.ToolCalls).Name);

        var summary = await Step("All 2 stage(s) passed in 4s.");
        Assert.Empty(summary.ToolCalls);
        Assert.Contains("all tests pass", summary.Content);
    }

    [Fact]
    public async Task Fix_overdue_reports_a_failing_verify_instead_of_claiming_success()
    {
        var provider = BuiltIn();
        var task = new Message { Role = MessageRole.User, Content = SampleTask };
        var history = new List<Message> { task, Assistant(), ToolResult("x"), Assistant(), ToolResult("y"), Assistant(), ToolResult("FAIL  Test: dotnet test (exit 1)") };

        var response = await provider.CompleteAsync(new CompletionRequest { Messages = history });

        Assert.Contains("still report a problem", response.Content);
    }

    [Fact]
    public async Task Feature_team_rejects_once_then_approves_and_the_backend_answers_the_feedback()
    {
        var provider = BuiltIn();
        var outcomes = new[] { "approved", "rejected" };
        string Quinn() => "<role>You are Quinn, a skeptical QA engineer.</role>";

        var design = await provider.CompleteAsync(Request("Task: add a feature", "You are Alex, a pragmatic software architect."));
        var backend1 = await provider.CompleteAsync(Request("Task: add a feature", "You are Dana, a .NET backend specialist."));
        var model = await provider.CompleteAsync(Request("Task: add a feature", "You are Mia, a machine-learning engineer."));
        var review1 = await provider.CompleteAsync(Request("Task: add a feature <inputs>", Quinn()));
        var backend2 = await provider.CompleteAsync(Request("Task: add a feature\n\nYour previous work was sent back", "You are Dana, a .NET backend specialist."));
        var review2 = await provider.CompleteAsync(Request("Task: add a feature <inputs> iteration 2", Quinn()));
        var notes = await provider.CompleteAsync(Request("Task: add a feature <inputs>", "You are Sam, a pragmatic generalist."));

        Assert.Contains("Design", design.Content);
        Assert.Equal("list_directory", Assert.Single(backend1.ToolCalls).Name);
        Assert.Contains("Model", model.Content);
        Assert.Equal("rejected", FlowPrompt.ParseOutcome(review1.Content, outcomes));
        Assert.Contains("round 2", backend2.Content);
        Assert.Equal("approved", FlowPrompt.ParseOutcome(review2.Content, outcomes));
        Assert.Contains("Release notes", notes.Content);
    }

    [Fact]
    public async Task Explain_project_lists_reads_and_summarizes()
    {
        var provider = BuiltIn();
        const string ask = "Please explain this project to me";

        var first = await provider.CompleteAsync(Request(ask));
        var second = await provider.CompleteAsync(Request(ask, null, Assistant(), ToolResult("src/\ntests/")));
        var third = await provider.CompleteAsync(Request(ask, null, Assistant(), ToolResult("x"), Assistant(), ToolResult("# AGENTS.md")));

        Assert.Equal("list_directory", Assert.Single(first.ToolCalls).Name);
        Assert.Equal("read_file", Assert.Single(second.ToolCalls).Name);
        Assert.Empty(third.ToolCalls);
        Assert.Contains("Layout", third.Content);
    }
}
