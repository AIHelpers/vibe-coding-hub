# Testing without a real model: the scripted provider

UI tests, smoke tests, documentation screenshots and demos need realistic agent behavior (streamed text, tool calls, approvals, flows with review loops) without API keys or cost. The **scripted provider** is a deterministic fake model that replays *scenarios*: JSON files that say what the model answers, turn by turn.

It is a test double. It is never offered in a released build unless you ask for it.

## Turn it on

| How | Effect |
|---|---|
| Debug build (`dotnet run`, `dotnet test` without `-c Release`) | The provider `scripted` exists. |
| `AIAGENT_ENABLE_SCRIPTED=1` (also `true`, `yes`, `on`) | Same, in any build. |

Then pick **scripted** as the provider in Settings → Model (no URL, key or model needed), or add it to `config.json`:

```json
{ "defaultProvider": "scripted", "providers": { "scripted": { "name": "scripted", "baseUrl": "", "defaultModel": "scripted-1" } } }
```

A flow step can use it too (`"provider": "scripted"` on the step).

### Run the sample end to end (with approvals)

1. `AIAGENT_ENABLE_SCRIPTED=1` and start the app. Settings → Model → provider **scripted**, Save.
2. Command Palette → **Try the sample project**. The chat box is filled with the first task ("The Overdue_ExcludesCompletedItems test is failing. Fix the overdue logic…").
3. Send it. The `fix-overdue` scenario runs: it reads `TodoService.cs`, asks to **edit** it (approve the diff), runs the checks, then summarizes. The sample's checklist ticks off exactly as with a real model.

Useful switches:

| Variable | Meaning |
|---|---|
| `AIAGENT_SCRIPTED_DELAY_SCALE` | Multiplies every `delay_ms`. `0` removes all waiting (tests, CI); `2` slows a demo down. Default `1`. |
| `AIAGENT_SCRIPTED_DIR` | A folder (or one file) with your own scenarios. |

## Where scenarios come from

Highest precedence first. When two files use the same `name`, the earlier source wins, so you can override a built-in.

1. The provider's `baseUrl`, when it is a folder or a file (handy in tests).
2. `AIAGENT_SCRIPTED_DIR`.
3. `~/.aiagent/scenarios/*.json`.
4. The built-in scenarios (inside the app, `src/AiCodeAgent.Providers/Scripted/Scenarios/`).

Files are JSON with `//` comments and trailing commas allowed. Property names are `snake_case`. (YAML is not supported: no extra dependency for a test tool.) A file that cannot be read is logged and skipped; the others still load.

## Built-in scenarios

| Name | Matches | What happens |
|---|---|---|
| `fix-overdue` | first message contains `Overdue_ExcludesCompletedItems` (the sample's task) | `read_file` → `edit_file` (needs approval) → `verify_changes` → summary. The summary branches on the verify result: success text if it contains "passed", otherwise it says the checks still fail. |
| `explain-project` | first message matches `explain (this\|the) (project\|repo\|codebase)` | `list_directory` → `read_file AGENTS.md` → summary. Read-only, no approvals. |
| `feature-team` | the flow `feature-team` (steps recognized by their character: "You are Alex / Dana / Mia / Quinn / Sam") | Alex designs; Dana and Mia build; Quinn **rejects** (round 1) so Dana runs again (round 2) and Quinn **approves** (round 2); Sam writes release notes. Dana's first round calls `list_directory`. |

## Scenario format

```jsonc
{
  "name": "my-scenario",                  // required, unique
  "description": "What it shows",
  "match": {                              // all set fields must hold; text matches ignore case
    "first_user_contains": "overdue",     // the first user message (the task, or a flow step's prompt)
    "first_user_starts_with": "Task:",
    "first_user_regex": "explain (this|the) project",
    "system_contains": "You are Quinn",   // the system prompt (for a flow step: the character's text)
    "system_starts_with": "...",
    "system_regex": "..."
  },
  "turns": [ /* one conversation */ ]
}
```

A scenario **with no `match` never matches** (so a half-written file cannot hijack every chat). A message that matches nothing gets a clear reply that lists the scenarios it knows.

### Turns

One turn is one model response. Every field is optional.

```jsonc
{
  "text": "I'll read the file first.",                       // streamed in chunks
  "tool_call": { "name": "read_file", "args": { "path": "src/TodoApp/TodoService.cs" } },
  "tool_calls": [ { "name": "glob", "args": { "pattern": "**/*.cs" } } ],   // several at once
  "delay_ms": 400,                // wait before the first chunk
  "chunk": 32,                    // characters per streamed chunk (default 32)
  "chunk_delay_ms": 20,           // wait between chunks
  "usage": { "prompt": 2400, "completion": 96 },   // omit to estimate (about 4 characters per token)
  "if_result_contains": "passed", // branch on the previous tool result (ignoring case)...
  "else": { "text": "It failed." } // ...else this turn (it can have its own condition)
}
```

- The turn played is the **number of assistant messages already in the conversation**, so a scenario needs no state while a conversation runs.
- A turn with `tool_call`/`tool_calls` ends with finish reason `tool_calls`; the app runs the tool (asking for approval when the permission mode requires it) and the next turn follows. Without tool calls the finish reason is `stop`.
- Tool-call ids are deterministic (`scripted-<name>-r<round>-t<turn>-<n>`), which keeps snapshots stable.
- `if_result_contains` without an `else` that does not hold produces an error reply naming the scenario, the turn and the actual result. A script that drifted from reality is easy to find.
- After the last turn the model answers "The scripted scenario has no more turns."

### Flows: `agents` and `rounds`

A flow has one conversation per step, so a scenario can list `agents`, each with its own `match` (the scenario's `match`, if any, applies to all of them):

```jsonc
{
  "name": "feature-team",
  "agents": [
    { "name": "review", "match": { "system_contains": "You are Quinn" },
      "rounds": [
        [ { "text": "...\nOUTCOME: rejected" } ],   // the first time the step runs
        [ { "text": "...\nOUTCOME: approved" } ]    // the second time (after the loop sent work back)
      ] }
  ]
}
```

`rounds` holds one list of turns per time that step's conversation *starts*. The last round repeats, so a step that loops more often keeps its final behavior. `turns` is shorthand for a single round. The counters live in the provider instance; `ScriptedProvider.Engine.Reset()` (or a new instance) starts every script at round 1 again. Matching is by character text, so give your scripted steps characters with distinct opening lines.

## Using it in tests

```csharp
var scenarios = ScenarioLoader.LoadAll();                       // or ScenarioLoader.Parse(json)
var provider  = new ScriptedProvider(scenarios: scenarios, delayScale: 0);   // 0 = no waiting
// use it wherever an IAiProvider is accepted (orchestrator, flow resolver, view-model tests)
```

`ScriptedProviderTests` (`tests/AiCodeAgent.Providers.Tests/Scripted`) shows matching, branching, rounds, streaming chunks and the built-in scenarios; it also checks that the `fix-overdue` edit applies to the sample's real `TodoService.cs`, so editing the sample without updating the scenario fails a test.

For screenshots and demos set `AIAGENT_SCRIPTED_DELAY_SCALE` so the run looks natural: the built-in scenarios use 300 to 500 ms delays.

The documentation screenshots are produced with the same provider: see [screenshots.md](screenshots.md).
