# AI Code Agent

An intelligent coding assistant with a CLI interface, supporting multiple AI providers (OpenAI, Anthropic, Ollama, and any OpenAI-compatible API).

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     AI Code Agent                            │
├─────────────────────────────────────────────────────────────┤
│  CLI Interface  │  TUI (Terminal UI)  │  LSP Server         │
├─────────────────────────────────────────────────────────────┤
│              Agent Orchestration Layer                        │
├──────────────┬──────────────┬──────────────────────────────┤
│  Tool System │ Context Mgr  │  Memory & RAG                 │
├──────────────┴──────────────┴──────────────────────────────┤
│              AI Provider Abstraction Layer                    │
├─────────────┬──────────────┬──────────────┬────────────────┤
│  OpenAI     │  Anthropic   │  Ollama      │  Custom/Local  │
└─────────────┴──────────────┴──────────────┴────────────────┘
```

## Project Structure

```
AiCodeAgent/
├── src/
│   ├── AiCodeAgent.Core/           # Core models and interfaces
│   ├── AiCodeAgent.Providers/      # AI providers (OpenAI, Anthropic, Ollama, etc.)
│   ├── AiCodeAgent.Tools/          # Agent tools (file ops, shell, git, search, etc.)
│   ├── AiCodeAgent.Context/        # Context management
│   └── AiCodeAgent.CLI/            # CLI entry point
├── tests/
├── Makefile
├── install.sh
└── AiCodeAgent.slnx
```

## Build

```bash
dotnet build AiCodeAgent.slnx
# or
make build
```

## Configuration

```bash
# Set API keys
dotnet run --project src/AiCodeAgent.CLI -- config set-key openai sk-...
dotnet run --project src/AiCodeAgent.CLI -- config set-key anthropic sk-ant-...

# List providers
dotnet run --project src/AiCodeAgent.CLI -- config providers

# Or use environment variables
export AIAGENT_OPENAI_API_KEY=sk-...
export AIAGENT_ANTHROPIC_API_KEY=sk-ant-...
```

## First run and the sample project

On the first start (or from Command Palette → **Setup wizard**) a three-step wizard detects a local Ollama or LM Studio,
connects a cloud provider with an API key if you prefer one, and opens a project. **Try the sample project** (also in the
Command Palette) copies `samples/TodoApp` to `Documents/VibeCodingHub/samples/TodoApp`, makes it a git repository and
starts a guided first task: *send the task, approve the edit, see the tests pass, commit in Source Control*.
See [`samples/README.md`](samples/README.md).

## Updates

At most once a day the app asks GitHub for the latest release (`releases/latest`) and shows a banner when it is newer than
the running build, with the release notes and a **Download** button; **Dismiss** hides that version for good. It uses your
system proxy and stays silent when you are offline. Turn it off in Settings → *Check for updates automatically* (then no
request is made at all), or check right now with Command Palette → **Check for updates**. To try the banner without publishing
a release, set `AIAGENT_UPDATE_URL` to an address that returns the same JSON as GitHub's "latest release" call.

## Navigation: the activity bar

The bar on the left edge replaces the old row of emoji buttons. Press the menu icon at its top to show the labels.

| Group | Entries |
|-------|---------|
| **Work** | Chat · Explorer (`Ctrl+B`) · Changes (`Ctrl+Shift+G`, badge = changed files) · Flows · Tasks (badge = running tasks) |
| **Team** | Characters · Skills · AGENTS.md |
| **History** | Checkpoints · Sessions · Dashboard |
| bottom | Settings (`Ctrl+,`) · Theme |

Changes, Tasks, Checkpoints, Sessions and Dashboard (and the agent's plan) open in **one side panel** docked on the right, so they never cover the chat or each other. Drag its left edge to resize it. The open panel and its width are remembered per project folder. The top bar keeps the working folder, model, permission mode and Read/Edit/Exec; everything that no longer has a button (terminal, preview pane, diff of the active file, refresh, open file, Screens mode, quick open) is in the command palette (`Ctrl+Shift+P`).

## Terminal

Open it with Ctrl+backtick (or the command palette). It docks along the bottom and has **Shell** tabs and one **Agent output** tab.

- A shell tab is a real interactive shell (PowerShell 7 / Windows PowerShell / cmd on Windows, `$SHELL` elsewhere) started in the working directory, with colours, `Ctrl+C`, arrow keys, history, full-screen programs (vim, less) and reflow when you resize the panel. Several tabs can run at once; they keep running while the panel is hidden and end when you close the tab or the app.
- **Agent output** is the log of the commands the agent ran.
- Select text with the mouse (double click = word, triple click = line). `Ctrl+Shift+C` copies (plain `Ctrl+C` copies only while text is selected, otherwise it interrupts the program), `Ctrl+Shift+V` / `Shift+Insert` pastes, right click copies a selection or pastes. `Shift+PageUp/PageDown` and the mouse wheel scroll the history.
- **Send to chat** puts the selection into the chat input; **Ask the agent** sends the selection (or, with nothing selected, the last output) with the question "what went wrong, please fix it".
- Settings -> Terminal shell chooses another shell, e.g. `pwsh -NoLogo`. Your own shell is not sandboxed: the Docker sandbox setting only applies to commands the agent runs.

How it works: Windows uses ConPTY (`CreatePseudoConsole`, Windows 10 1809+), Linux/macOS use `forkpty`; both are small P/Invoke wrappers in `AiCodeAgent.Core/Terminal` and need no extra package (the candidates `Pty.Net` and `Quick.PtyNet` are unmaintained previews, and a ~400-line wrapper is easier to test on all three CI systems than a dependency). The screen is a VT100/xterm emulator in Core (`TerminalEmulator`, covered by unit tests) drawn by `TerminalControl`.

## Settings

Settings groups every option into sections: **Model · Agent · Safety · Cost · Integrations · Appearance · Advanced**. Each value is written to the file or key the engine already reads, so editing the file by hand and using the page are the same thing.

| Section | Option | Stored in |
|---|---|---|
| Agent | max iterations, reasoning-effort default, requirements questions | `config.json` (`Agent.MaxIterations`, `DefaultReasoningEffort`, `RequirementsClarifier`) |
| Safety | default permission mode, Docker sandbox (on/off, image, network, memory, CPUs) | `config.json` (`Agent.DefaultPermissionMode`, `Agent.Sandbox`) |
| Safety | hooks (list, trust status, trust button) | `settings.json` hooks, trust file in `~/.aiagent` |
| Cost | budget per task (USD) | `config.json` (`Agent.MaxBudgetUsd`) |
| Cost | price table | `~/.aiagent/pricing.json` |
| Integrations | MCP servers (manager: status, test, add, edit, tools), browser path | `mcp.json`, `config.json` (`Agent.BrowserPath`) |
| Advanced | log folder, config folder, reset settings | n/a |

Environment variables still override the page, and the page says so, for example "Overridden by AIAGENT_SANDBOX": `AIAGENT_SANDBOX`, `AIAGENT_SANDBOX_IMAGE`, `AIAGENT_SANDBOX_NETWORK`, `AIAGENT_SANDBOX_MEMORY`, `AIAGENT_SANDBOX_CPUS` and `AIAGENT_BROWSER`.

A budget cap is read before every model call, so a change applies to a task that is already running. When a task reaches the cap it stops with "budget exceeded" in the chat. Models without a price (for example local ones) count as $0.

## Usage

```bash
# Interactive chat
dotnet run --project src/AiCodeAgent.CLI -- chat
dotnet run --project src/AiCodeAgent.CLI -- chat --provider anthropic --model claude-3-5-sonnet-20241022
dotnet run --project src/AiCodeAgent.CLI -- chat --provider ollama --model codellama
dotnet run --project src/AiCodeAgent.CLI -- chat --provider lmstudio  # LM Studio locally

# Single prompt
dotnet run --project src/AiCodeAgent.CLI -- run "Add unit tests for the UserService class"
dotnet run --project src/AiCodeAgent.CLI -- run "Fix all compilation errors" --dir ./myproject
```

## SDLC Pipelines (multi-agent)

Run the full software development lifecycle as a chain of specialized agents — each stage runs
under its own role preset (system prompt, allowed tools, permission mode) and all stages share one
session, so later stages see what earlier stages did:

```bash
# List built-in, global and project pipelines (the SCOPE column says which)
dotnet run --project src/AiCodeAgent.CLI -- pipeline list

# Run the full pipeline: analyze -> implement -> review -> test -> deploy
dotnet run --project src/AiCodeAgent.CLI -- pipeline run "Add pagination to the /users endpoint"

# Use a lighter pipeline for small changes
dotnet run --project src/AiCodeAgent.CLI -- pipeline run "Fix off-by-one in date parsing" --pipeline quick-fix
```

Pipelines are plain JSON and fully configurable — drop a file in `~/.aiagent/pipelines/*.json` to add
your own stage sequence (custom roles, prompts, or a subset of the SDLC), or in `<repo>/.aiagent/pipelines/*.json`
to commit it next to the code it works on and share it with your team.

| Scope | Folder | Who sees it |
|---|---|---|
| `builtin` | ships with the app (`full-sdlc`, `quick-fix`) | everyone |
| `global` | `~/.aiagent/pipelines` | only you |
| `project` | `<repo>/.aiagent/pipelines` | everyone who clones the repo |

Load order is built-in, then global, then project: **on a name clash the project flow wins** (the same rule as
skills and characters), and the Flows panel shows "overrides global". The project folder is the working
directory (`--dir` on the CLI); it is re-read when you switch folders. `pipeline run` resolves the project flow
first, and `pipeline copy <name> --to project|global [--force]` copies a built-in, global or project pipeline
into the other scope so you can edit it there:

```bash
aiagent pipeline copy feature-team --to project   # → <repo>/.aiagent/pipelines/feature-team.json
``` Agent roles themselves are
equally configurable via `~/.aiagent/presets/*.json` (see `RolePresetLoader`); built-in roles are
`planner`, `implementer`, `reviewer`, `tester`, and `deployer`.

## Skills & Characters

**Skills** are reusable instruction packs written as Markdown. **Characters** are agent personas
that own a set of skills and run the workflow (chat, pipeline stages, subagents).

```
~/.aiagent/skills/<name>/SKILL.md        # global skill      (.aiagent/skills/... = project skill, wins on name clash)
~/.aiagent/characters/<id>.md            # global character  (.aiagent/characters/... = project character)
```

A skill:

```markdown
---
name: release-notes                  # must match the folder name
description: Draft release notes from the commits since the last tag
tags: [docs, git]
disable-model-invocation: false      # true = manual only (/skill), hidden from the model
allowed-tools: [git, read_file]      # advisory; never grants tools
---
1. Find the last tag ...
```

A character (the body is its persona):

```markdown
---
id: alex-architect
display-name: Alex — Architect
avatar: 🧭
description: Breaks tasks into designs and plans; never edits code
base-role: planner                   # inherits tools, permission mode and role instructions
model: claude-sonnet                 # optional
permission-mode: plan                # optional: ask | auto-edit | full-auto | plan
tools: { add: [web_fetch], remove: [] }   # or an explicit list: tools: [read_file, grep]
skills: [adr-writer]                 # listed for the agent, loaded on demand with use_skill ("*" = all skills)
pinned-skills: [team-conventions]    # full instructions always in context
---
You are Alex, a pragmatic software architect...
```

### Add skill: every character gets its own skills

All skills live in one shared library (`~/.aiagent/skills`, `.aiagent/skills`). Each character picks
its own, individual skills from it. In the desktop app open **Project Knowledge → Characters**, select a
character and click **＋ Add skill**:

- **From library** — pick an existing skill (only skills the character does not have yet are listed);
- **Create new** — enter a name, description and optional instructions; the skill is created in the
  library (project or global) and added to the character in one step.

Tick **📌 Pin** to keep the skill's full instructions in context; otherwise it is loaded on demand.
Each skill in the character's list has 🗑 to remove it. The same from the command line:

```bash
aiagent characters add-skill dana-dotnet csharp                  # pick an existing library skill
aiagent characters add-skill dana-dotnet ef-core \
  --description "Use EF Core migrations and queries correctly" \
  --instructions "Prefer AsNoTracking for read-only queries..." --project   # create it, then add it
aiagent characters add-skill dana-dotnet team-conventions --pinned
aiagent characters unassign dana-dotnet csharp
```

A character that could use every skill (a built-in role, or `skills: ["*"]`) switches to an explicit
list as soon as you add a skill to it.

### Templates: share what a profession has in common

Describe what a whole profession shares once, then give each character its specialty with its own skills:

```markdown
<!-- ~/.aiagent/characters/software-developer.md : the shared base -->
---
id: software-developer
template: true                       # a base for others; hidden from chat/subagent pickers
base-role: implementer
skills: [release-notes, code-review-checklist]
pinned-skills: [team-conventions]
---
You are a professional software developer...

<!-- ~/.aiagent/characters/dana-dotnet.md : the specialist -->
---
id: dana-dotnet
extends: software-developer          # inherits role, model, permissions, tools, persona and skills
skills: [csharp, clean-architecture] # her own skills (added with "＋ Add skill")
remove-skills: [release-notes]       # drop an inherited skill for her only
---
You are Dana, a .NET backend specialist...
```

Dana's effective skills: `code-review-checklist` (template), `csharp`, `clean-architecture` (own),
pinned `team-conventions` (template). An ML engineer `mia-ml` extends the same template and adds
`python` and `machine-learning`. Rules:

- Effective skills = template's skills + own skills − `remove-skills`; own skills always win.
  Personas stack (template text first, then the character's), and scalar settings (base role, model,
  permission mode) are inherited unless the character sets its own. Templates can extend templates.
- Changing a template changes every character built on it. Inheritance cycles are reported as errors;
  unknown templates as warnings.
- `characters show <id>` (and the character's skill list in the desktop app) shows where each skill comes
  from. Removing an inherited skill adds it to `remove-skills`; **↩ Restore** (or adding it again) un-removes it.

```bash
aiagent characters new software-developer --template --base-role implementer --skill release-notes --pin team-conventions
aiagent characters new dana-dotnet --extends software-developer --skill csharp --skill clean-architecture
aiagent characters extend sam-developer software-developer   # or: none
aiagent characters template software-developer on
```

In the desktop app, the character header has *Extends* and *Template*.

How it works:

- The agent sees only its character's skills in `<available_skills>` and loads one with the **`use_skill`** tool;
  `use_skill` refuses skills the character does not have. Pinned skills are injected in full on every turn.
- Built-in roles (`planner`, `implementer`, `reviewer`, `tester`, `deployer`) appear as built-in characters with
  access to every skill. Assigning a skill to one creates `~/.aiagent/characters/<role>.md`, which then applies
  wherever that role runs — including the built-in pipelines.
- Renaming a skill updates every character that uses it; deleting one removes it from characters (or keeps a
  reported dangling reference with `--keep-references`). Missing skills are skipped at run time, never fatal.

```bash
# Skills
aiagent skills list [--all] [--tag docs]
aiagent skills new release-notes --description "Draft release notes" [--project] [--tag git] [--edit]
aiagent skills show release-notes
aiagent skills edit release-notes          # opens $EDITOR, validates afterwards
aiagent skills rename release-notes changelog
aiagent skills rm changelog [--yes] [--keep-references]
aiagent skills doctor                      # invalid files, dangling references, large pinned skills

# Characters
aiagent characters list
aiagent characters new alex-architect --name "Alex — Architect" --base-role planner --skill adr-writer --pin team-conventions
aiagent characters assign alex-architect adr-writer [--pinned]
aiagent characters unassign alex-architect adr-writer
aiagent characters show alex-architect     # resolved tools, permission mode, skills
aiagent characters edit alex-architect
aiagent characters rm alex-architect

# Characters in the workflow
aiagent chat --character alex-architect
aiagent pipeline run "Add pagination" --cast planner=alex-architect,implementer=sam-developer
```

Pipeline stages can also name a character in JSON (`"character": "alex-architect"`), and `spawn_subagent`
takes a `character` argument (a subagent never gets a looser permission mode than its parent). In the
desktop app, use **Project Knowledge → Skills / Characters** (skill checklist per character), the character
selector next to the pipeline picker, or `/character`, `/characters`, `/skill`, `/skills` in the chat box.
Ready-made samples live in [`examples/`](examples/README.md).

## Packs: share skills, characters and flows from a git repo

A pack is a git repository with a `pack.json` and any of `skills/`, `characters/` and `pipelines/` folders:

```
dotnet-team-pack/
  pack.json
  skills/api-design/SKILL.md
  characters/pack-architect.md
  pipelines/pack-review.json
```

```json
{ "name": "dotnet-team-pack", "version": "1.2.0", "description": "Our .NET team's skills and roles",
  "author": "Acme", "license": "MIT", "requires": { "app": ">=1.3" } }
```

`name` is kebab-case. `requires.app` accepts `>=`, `>`, `<=`, `<` and `=` (combine with a space, e.g. `>=1.3 <2.0`).

**Install.**

```
aiagent pack add https://github.com/org/dotnet-team-pack --ref v1.2.0 [--save] [--yes]
aiagent pack list
aiagent pack update [name]      # packs that follow a branch move to its newest commit; pinned packs stay
aiagent pack remove <name> [--ref <ref>]
aiagent pack sync               # install exactly the refs in .aiagent/packs.json
```

In the app, use **Project Knowledge → ＋ Add pack** (or the **Packs** tab). Packs are cloned into `~/.aiagent/packs/<name>@<ref>` and registered as read-only sources with the scope `pack:<name>`. Their skills, characters and flows show a **Pack: <name>** badge and cannot be edited or deleted; copy one to your project or global folder to change it. Removing a pack removes everything it added.

**Pin a ref.** A tag or commit gives a reproducible install; a branch (or no `--ref`) follows the newest commit on `pack update`. `--save` records the pack in the project's `.aiagent/packs.json`, which you commit so teammates run `aiagent pack sync`:

```json
{ "packs": [ { "name": "dotnet-team-pack", "url": "https://github.com/org/dotnet-team-pack", "ref": "v1.2.0" } ] }
```

**Precedence on a name clash:** project > global > pack > built-in. If two packs define the same name, the pack whose name sorts later wins.

**Security.** Before anything is installed you see every file plus any `hooks`, MCP server entries (`mcp.json`) and scripts or binaries. Packs never enable hooks or MCP servers, and the app never runs files from a pack. Symlinks are rejected, and packs are limited to 5000 files / 100 MB. Pack commands need `git` on your PATH.

## Import skills and rules from other tools

Bring an existing setup over with one command, or with **Project Knowledge → Import…** (a preview with checkboxes comes first):

```
aiagent import [all|claude|claude-md|cursor|kiro|copilot] [--dry-run] [--yes] [--no-user]
```

| Source | Found at | Becomes |
|---|---|---|
| Claude Code / Agent Skills | `.claude/skills/*/SKILL.md`, `~/.claude/skills` | skills, copied with their front matter and bundled files (user skills go to your global skills) |
| `CLAUDE.md` | repo root | an `## Imported from CLAUDE.md` section in AGENTS.md |
| Cursor rules | `.cursor/rules/*.mdc`, `.cursorrules` | skills (`description` from the front matter, `globs` noted in the text); `alwaysApply: true` and `.cursorrules` are merged into the `team-conventions` skill |
| Kiro steering | `.kiro/steering/*.md` | skills; `inclusion: always` goes into `team-conventions`, `fileMatch` keeps its pattern, `manual` becomes a manual-only skill |
| Copilot instructions | `.github/copilot-instructions.md` | an `## Imported from copilot-instructions.md` section in AGENTS.md |

Names are normalised to kebab-case and a clash gets a suffix (`style-guide-2`). Pin `team-conventions` on characters to keep the always-on rules in context (`aiagent characters add-skill <id> team-conventions --pinned`). After importing, `skills doctor` runs and its result is shown. Source files are never changed or deleted. Imported skills carry an `imported-from` line and imported AGENTS.md sections an HTML comment marker, so running the import again reports "already imported" and changes nothing.

## Flows: characters working together

A **flow** is a pipeline whose stages are connected by arrows. Each character starts as soon as the
characters pointing into it are done, and characters whose work doesn't depend on each other **run in
parallel**, each in its own git worktree that is merged back when it finishes.

```json
{
  "name": "feature-team",
  "maxParallel": 3,
  "stages": [
    { "id": "design",  "character": "alex-architect", "promptTemplate": "Task: {task}\nPlan the backend and the model." },
    { "id": "backend", "character": "dana-dotnet",    "promptTemplate": "Build the backend part.\n{input:design}" },
    { "id": "model",   "character": "mia-ml",         "promptTemplate": "Build the model part.\n{input:design}" },
    { "id": "review",  "character": "quinn-qa",       "promptTemplate": "Review both parts.", "outcomes": ["approved", "rejected"] },
    { "id": "notes",   "character": "sam-developer",  "promptTemplate": "Write release notes for: {task}" }
  ],
  "edges": [
    { "from": "design",  "to": "backend" },
    { "from": "design",  "to": "model" },
    { "from": "backend", "to": "review" },
    { "from": "model",   "to": "review" },
    { "from": "review",  "to": "backend", "when": "rejected", "maxLoops": 2 },
    { "from": "review",  "to": "notes",   "when": "approved" }
  ]
}
```

- **What a character receives:** the task plus the final answers of the characters connected into it, as
  an `<inputs>` block. Parallel branches don't see each other. `{input:<id>}` places one input inline.
- **Conditions:** a step with `outcomes` must end its answer with `OUTCOME: <one of them>`, and arrows
  with `when` follow that outcome. A branch that isn't chosen is skipped and never blocks a later join.
- **Loops:** an arrow back to an earlier step re-runs it (and the steps between) with the feedback, up to
  `maxLoops` times. After that, a `"when": "loop-exhausted"` arrow can route elsewhere; otherwise the flow
  stops. A flow also stops after 25 step runs in total.
- **Models per step:** a step can set `"model"` and `"provider"`. `model` is a concrete id or a tier alias
  (`fast` | `balanced` | `frontier`) that resolves through the model catalog for the step's provider, e.g.
  `{ "id": "release-notes", "model": "fast" }` and `{ "id": "design", "model": "frontier" }`. Precedence:
  **step > character `model:` > session default** (`--model` / the chat picker). `provider` (e.g. `"anthropic"`)
  sends that step to another configured provider; parallel steps may use different providers. A step that names a
  provider but no model uses that provider's configured default model. Both fields are optional: flows without
  them run exactly as before. In the editor, the inspector has a **Model** combo (*Inherit (character/session)*,
  the tiers, then the catalog's models) and a **Provider** combo; the node shows it (`Sam — Developer · fast`).
- **Cost and budget:** every step run (loop rounds included) records its tokens, cost (the existing pricing
  tables, `~/.aiagent/pricing.json` overrides them) and duration. Each node shows a badge (`1.2k tok · $0.004`)
  and the header shows the run total. When a flow ends, the table of step runs (step, round, model, tokens in/out,
  cost, time) is listed under the canvas. `"maxBudgetUsd": 1.50` in the flow, or the **budget $** field next to
  **▶ Run**, caps one run: once the finished steps cost that much, no new step starts, a step that is running stops
  after the model call that crosses its share of the cap, and the flow ends as `budget_exceeded` with a message that
  says what was spent. Parallel steps each get what was left when they started, so a run can overshoot by one call
  per running step. A model without a known price counts as `$0` and is marked *unpriced* (a step without a `model`
  runs on its provider's configured default, whose price the flow cannot see, so set `model` on steps you want costed).
- **Run history and replay:** every flow run is saved to `~/.aiagent/runs/flows/<flow>/<runId>/`: `run.json` holds
  the flow as it was, the events and one record per step run (prompt, inputs, model, tokens, cost, tool calls,
  the file changes it produced), and each step's answer is a Markdown file next to it. The editor's **History**
  dropdown (next to the flow selector) lists the runs of the open flow with time, status, cost, duration and task;
  picking one opens it in read-only *replay mode*: the canvas shows the flow of that run coloured by how each step
  ended, and selecting a step shows its prompt, output, tool calls and changes. **↺ Re-run from here** starts a new
  run from the selected step: the steps before it keep their recorded output (shown as *reused*, no model call, no
  cost) and the step and everything after it run again. A step that failed in the original run cannot be reused, so
  re-run from it instead. Branches that were not taken stay skipped. The last 50 runs per flow are kept
  (`agent.flowRunHistoryLimit` in `~/.aiagent/config.json`). `aiagent pipeline run` prints `Run saved: <id>`.
  Linear pipelines (no `edges`) are not recorded.
- **Gates and human steps:** `"requireConfirmation": true` makes a step wait for you before it starts. At the gate
  the step offers **Approve**, **Approve with note…**, **Skip step** and **Stop flow**. A note is appended to the
  step's prompt as `<reviewer_note>…</reviewer_note>` (and is saved with the run, so replay shows it). *Skip step*
  leaves that step out and the rest of the flow carries on (steps that only depended on it are skipped too); *Stop
  flow* ends the flow there, steps already running finish. A gate nobody answers within 10 minutes counts as *Stop flow*.
  In the CLI the same choices are `a` / `n` / `s` / `t`. A step with `"kind": "human"` runs no agent: it shows its
  prompt (with the results of the steps before it) and asks you to choose one of its `outcomes`, optionally with a
  note; the arrows route on the choice exactly as they do for an agent's `OUTCOME:` line, so manual approval
  branches and review loops work (`approved` → continue, `rejected` → back to the author with your note as
  feedback). The note travels in the step's output to the steps after it. A human step has no character, model or
  provider and costs nothing; one without `outcomes` is simply a pause. Add one from the palette (**Human decision**).
- **Failures** skip only the steps after the failed one; other branches finish.
- Pipelines **without `edges`** keep the old behavior: stages in order, one shared history.

```bash
aiagent pipeline validate feature-team          # problems, exit code 1 on errors
aiagent pipeline show feature-team              # 1: design · 2: backend ∥ model · 3: review · 4: notes
aiagent pipeline show feature-team --mermaid    # diagram for docs and PRs
aiagent pipeline run "Add churn prediction" --pipeline feature-team --max-parallel 2
aiagent pipeline run "Add churn prediction" --pipeline feature-team --max-budget 1.50   # stop at $1.50
aiagent pipeline run "Add churn prediction" --pipeline feature-team --json              # result + usage as JSON on stdout
```

In the desktop app, open **🔀 Flows** (or "Flows" / "New Flow" in the command palette):

- **Start from a template:** **＋ New** opens a gallery of six ready-made flows (`bug-fix`, `feature-team`,
  `fullstack-feature`, `pr-review`, `release`, `migration`). A card shows a mini diagram, the characters the flow needs
  (✓ present in the project, ✗ missing) and a sample task. **Use template** copies the flow into the project and, after
  you confirm, creates the missing characters and the skills they use from the bundled `examples/`.
  **Blank flow** starts empty.
- **Draw:** drag characters from the left onto the canvas (or click **＋** to add one after the selected
  step), then drag from a step's right handle to another step. Select a step to set its character, prompt,
  outcomes and confirmation; select an arrow to set its condition and loop limit. Delete removes the
  selection; Ctrl+Z / Ctrl+Y undo and redo; the wheel zooms and dragging empty canvas pans.
- **Check:** problems are outlined in red as you edit, and the header shows which steps run in parallel.
  **Auto-layout** arranges steps left to right by execution order. **Save** writes `<name>.json` to the flow's own scope; a new flow asks
  *Project (shared via git)* or *Global (only me)* (Project is the default inside a git repository). The header shows a
  scope badge (`project`, `global`, `builtin`, `new`), and the pipeline list is grouped by scope.
- **Run:** type the task and press **▶ Run**. Each step shows ⏳ waiting, ▶ running, ✓ done (with its
  outcome), ✕ failed or – skipped, plus the round number in loops; arrows light up as they are taken. Click a
  step to watch its output, and approve steps that ask for confirmation right on the step.

### Run flows in CI (headless)

```bash
aiagent pipeline run --pipeline pr-review --task-file task.md --non-interactive \
  --permission Plan --output markdown --output-file report.md --timeout 900
```

`--non-interactive` never waits for input: a tool call that needs approval is **denied** (the agent is told and carries on), and a step with `requireConfirmation` or a `human` step **fails the run** with a clear reason. `--permission` (`Plan`, `Ask`, `AutoEdit`, `FullAuto`) replaces the permission mode of every step; the default for CI should be `Plan` (read-only). `--timeout` stops the run after that many seconds. `--output json|markdown` with `--output-file` writes the report (without a file it goes to stdout); progress goes to stderr. `--max-budget` caps spend.

| Exit code | Meaning |
|---|---|
| 0 | success |
| 1 | a step failed, the flow was stopped at a gate / human step, or it timed out |
| 2 | budget exceeded |
| 3 | validation error (bad arguments, unknown or invalid flow, empty task, a linear pipeline) |
| 4 | configuration error (unknown provider, missing key) |

Provider settings can come from the environment instead of a config file: `AI_CODE_AGENT_<PROVIDER>_API_KEY`, `_BASE_URL` (any OpenAI-compatible server, for example a self-hosted model) and `_MODEL`; the legacy prefix `AIAGENT_` works too. They win over the config file.

**GitHub Action.** `.github/actions/run-flow` installs the CLI from a release, writes the task from the PR title, description and diff, runs the flow and posts or updates **one** PR comment (found by a hidden `<!-- vibe-coding-hub:flow:<name> -->` marker). The sample `.github/workflows/flow-pr-review.yml` runs the project flow `.aiagent/pipelines/pr-review.json` by hand (`workflow_dispatch`, PR number as input); uncomment its `pull_request` trigger to run on every PR. Add `AIAGENT_OPENAI_API_KEY` (and optionally `AIAGENT_OPENAI_BASE_URL`) as repository secrets. The action needs `pull-requests: write`. The action folder can be mirrored to its own repository (`AIHelpers/vibe-coding-hub-action`) unchanged.

A pull request is untrusted input. Keep the flow read-only (`Plan`), and do not give secrets to workflows triggered by forks. The Markdown report is plain for now; E5-S8 enriches it.

### Export a flow run: report, Mermaid, PNG

- **Flows → Export ▾**: *Copy Markdown report* (the open run from History, otherwise the newest run), *Copy Mermaid* (the flow on the canvas; in replay it is coloured by that run) and *Save PNG…* (the whole flow, never clipped, at 2x).
- **CLI**: `aiagent pipeline show <flow> --mermaid --with-status <runId|latest>` colours nodes by status (done, failed, skipped, not run), shows the rounds of steps that ran more than once and labels loops with the rounds actually taken (`↺ 1/3`). `aiagent pipeline show <flow> --report --with-status <runId|latest>` prints the Markdown report.
- The report holds the task, the Mermaid diagram (GitHub renders it), a table of every step run (character, model, outcome, tokens, cost, duration), each step's final answer in a collapsible `<details>`, totals and the changed files.

### Watch a flow on another machine (prototype)

`aiagent serve` runs one flow and streams its events over a WebSocket; the desktop app attaches and shows the live canvas, node status, output, approvals and Stop. Design and limits: [docs/adr/0002-remote-runners.md](docs/adr/0002-remote-runners.md).

```
# on the build server (or in a container); prints the address and a token
aiagent serve "Add churn prediction to the customer API" --pipeline full-sdlc --permission AutoEdit
# from your machine, one safe way: tunnel the port over SSH, then attach to ws://127.0.0.1:5000/events
ssh -L 5000:127.0.0.1:5000 build-server
```

In the app: **Flows → 🔗 Attach ▾**, paste the address and token, **Connect**. The canvas switches to the served flow (your own flow comes back on **⏏ Detach**). Questions the flow asks (tool approvals, confirmation gates, human steps) appear in the inspector as usual; if nobody answers within `--approval-timeout` (default 600 s) the request is denied. Detaching does not stop the run; **■ Stop** does.

Options: `--host` (default `127.0.0.1`), `--port` (0 = free port), `--token` or `AIAGENT_SERVE_TOKEN`, `--wait-for-client <s>`, `--linger <s>`, `--approval-timeout <s>`, plus the usual `--cast`, `--max-budget`, `--max-parallel`, `--model`, `--provider`, `--dir`. The prototype speaks plain `ws://` with one shared token: keep it on loopback and tunnel it, or put TLS in front. Exit codes are the same as for `pipeline run --non-interactive`.

## Multi-Agent Coordination

Beyond sequential SDLC pipelines, the coordinator (AgentSessionCoordinator) supports parallel agent groups and inter-agent messaging:

- Parallel groups: consecutive plan steps sharing the same ParallelGroup label run concurrently; their events are merged into one tagged stream. A failing agent emits AgentErrorEvent without cancelling peers.
- Inter-agent mailbox: agents exchange messages via the send_message tool. Direct or broadcast (*) messages are injected into the recipient prompt at its next step, so agents communicate without sharing a context window.
- Per-agent attribution: events are wrapped in AgentTaggedEvent (agent id + role) and diff hunks record the producing agent.

## Interactive Commands

```
/help          - show help
/tools         - list available tools
/clear         - clear screen  
/reset         - start new session
/model gpt-4o  - change model
/skills        - list the active character's skills (--all for every skill)
/skill <name>  - send a skill's instructions with your next message
/characters    - list characters
/character <id>|none - switch the character answering in this chat
/cd ./src      - change directory
/exit          - exit
```

## Available Tools

- `read_file` - Read file contents (supports line ranges)
- `write_file` - Write/create files
- `edit_file` - Targeted text replacement in files
- `list_directory` - List directory tree with file sizes
- `grep` - Regex search in files with context
- `execute_command` - Run shell commands
- `git` - Git operations (status, diff, log, commit, etc.)
- `web_fetch` - Fetch content from URLs
- `run_diagnostics` - Build/test/lint projects (dotnet, npm, python)
- `spawn_subagent` - Delegate a scoped sub-task to an isolated subagent
- `send_message` - Send a message to another agent in the session (`*` = broadcast; agents in a cast pipeline are addressed by character id)
- `use_skill` - Load the full instructions of one of the agent's skills

## Key Features

- **Multi-agent sessions**: parallel agent groups, inter-agent mailbox (send_message), per-agent diff attribution
- **Multi-provider support**: OpenAI, Anthropic, Ollama, and any OpenAI-compatible API
- **Streaming responses**: Real-time streaming via `IAsyncEnumerable<StreamChunk>`
- **Tool system**: Extensible tool registry with automatic tool calling
- **Context management**: Automatic context trimming by token count
- **Security**: Read-only mode, allowed paths, dangerous command detection
- **Cross-platform**: .NET 10, native publishing for Win/Linux/macOS
- **Configuration**: JSON config file + environment variables

## Cost, isolation, verification and safety (new)

- **Prompt caching + cost tracking**: the Anthropic provider marks the system prompt and tool list as cacheable; the status bar shows tokens, session cost and cache-hit %. Set `AgentOptions.MaxBudgetUsd` to stop a run at a spend cap (`StopReason = "budget_exceeded"`). Built-in prices are estimates: override them in `~/.aiagent/pricing.json` (`{"claude-sonnet": {"InputPerMillion": 3, "OutputPerMillion": 15}}`). Unknown models (e.g. Ollama) are treated as free. The cost only appears when `AgentOptions.Model` is set explicitly.
- **Git worktrees for parallel agents**: agents in a parallel group each work in their own worktree/branch (`agent/<session>/<agent>`) and are merged back one at a time; conflicts keep the branch for manual resolution. Opt out per step with `SessionStep.IsolateInWorktree = false`. Agents start from `HEAD`, so uncommitted changes in your checkout are not visible to them.
- **New tools**: `glob`, `todo_write`, `ask_user`, `multi_edit`, `apply_patch` (single file, unified-diff hunks), `verify_changes` (build/test/lint from `.aiagent/verify.json`, `AGENTS.md` "Build/Test/Lint command:" lines, or auto-detection; stops after 5 failed attempts in a row), and `browser_check` (headless Chrome/Edge driven over the DevTools protocol: title, text, exact console levels, uncaught exceptions and failed requests, screenshot in `.aiagent/screenshots`; optional `actions` click, type, press keys, wait and `eval` before the report; set `AIAGENT_BROWSER` if the browser is not found, and `AIAGENT_BROWSER_NO_SANDBOX=1` in containers).
- **Desktop**: the header has a reasoning-effort selector (Default/Off/Low/Medium/High) and a rewind button; `/rewind [N [chat|code|both]]`, `/fork N` and `/effort ...` also work in the chat box. Rewind removes a message and everything after it (and can restore the files the agent changed since), fork continues from that point in a new session.
- **Sandboxed shell**: `AIAGENT_SANDBOX=docker` runs `execute_command` and `verify_changes` in a throw-away container (workspace mounted, network off, capabilities dropped). Options: `AIAGENT_SANDBOX_IMAGE` (default `ubuntu:24.04`, must contain your toolchain), `AIAGENT_SANDBOX_NETWORK=on`, `AIAGENT_SANDBOX_MEMORY`, `AIAGENT_SANDBOX_CPUS`. If Docker is unavailable the command is refused, never run on the host.

## Publish

```bash
make publish-all
# or
./install.sh
```

### MCP server manager

**Settings → Integrations → MCP servers** lists the global (`~/.aiagent/mcp.json`) and project (`.aiagent/mcp.json`) servers. Each row shows a status dot (connected / error / disabled), the tool count and buttons: **Test** (starts the server, lists its tools, stops it), **Enable/Disable**, **Tools**, **Edit** and **Remove**. Changes are applied right away: the app reconnects the servers and the agent sees the new tools.

- **＋ Add server** offers presets (GitHub, filesystem, Postgres, Playwright, Fetch) or a custom server (stdio command, or URL plus a token variable). The presets that use `npx` need Node.js; Fetch uses `uvx`.
- **Secrets are never written as plain text.** Write `${env:GITHUB_TOKEN}` in env values, arguments, the URL or the token; the value is read from the environment when the server starts. The app refuses to save a token, password or key in either file. If a variable is not set, the row shows which one, and Test does not start the server.
- **Tool browser.** *Tools* shows the server's tools with descriptions and the pattern for each (`mcp:github/create_issue`). Characters can be given the whole server with `tools: { add: [mcp:github/*] }`; the tool browser has a switch per character that edits this line. Built-in and pack characters are read-only. The same `mcp:server/*` patterns work in `disabled-tools` lists.

### GitHub issues as tasks

`/issue` lists the open issues of the repository behind the `origin` remote (pull requests are left out); `/issue label:bug assignee:me` filters them. `/issue 42` fills the message box with the issue's title, body and comments and links the task to the issue; `/issue clear` removes the link.

Sign-in: set `GITHUB_TOKEN` (or `GH_TOKEN`), or sign in with the GitHub CLI (`gh auth login`); the app uses `gh auth token` when no variable is set. Public repositories also work without a token, with GitHub's lower rate limit. Hosts are behind the `IGitHostProvider` interface, so GitLab and Azure DevOps can be added later.

### External agents as characters (ACP)

A character can be an external agent that speaks the [Agent Client Protocol](https://agentclientprotocol.com) (Gemini CLI, a Claude Code or Codex adapter, and others):

```markdown
---
id: claude-code-reviewer
display-name: Claude Code — Reviewer
description: Reviews the change with Claude Code
runtime: acp                 # builtin (default) | acp
command: claude-code-acp     # how to start the agent
args: [--stdio]              # optional
skills: [code-review-checklist]   # sent to the agent as instructions, with the persona below
permission-mode: ask
---
You review code carefully.
```

Use it like any character: `character: claude-code-reviewer` on a flow step, or `--cast review=claude-code-reviewer` for one run. The step's prompt (with `{input:<id>}` filled in) goes to the agent and its final message is the step's output, so conditional edges (`OUTCOME: approved`) work as usual. The agent signs in by itself (run its login once in a terminal); nothing secret belongs in the character file.

- **Approvals.** The agent's permission requests follow `permission-mode` (or the run's `--permission`): read-only requests are allowed; in `ask` mode everything else appears in the usual approval dialog; `auto-edit` also allows edits; `full-auto` allows everything; `plan` refuses everything but reads. Without a dialog (headless runs) requests that would need one are refused.
- **Checkpoints and diffs.** A file the agent writes through the editor gets a checkpoint first (Checkpoints panel) and its diff is shown and kept in the flow report. Changes the agent makes with its own tools are not seen by VCH.
- **Cost.** Tokens are shown when the agent reports them (`usage`); most agents do not, and those steps count as unpriced.
- **Limits.** The step runs in the flow's working directory, not in a worktree; the flow's budget cap cannot stop an external agent. See `docs/adr/0001-acp.md`.

A step can also name the agent directly without a character: `{ "id": "external-review", "kind": "acp", "acpCommand": "gemini", "acpArgs": ["--acp"], "promptTemplate": "Review this task: {task}" }`.

### Audit trail and exportable run reports

Every chat task and every saved flow run writes an append-only `audit.jsonl`: **who** (OS user, character, model, provider), **what** (each tool call with its arguments and result, each approval request and its answer with who decided and any note, each file change with SHA-256 hashes of the content before and after and the diff), **when** (UTC timestamps) and **how much** (tokens, cost, per step and per run). Flow runs keep it next to `run.json` (`~/.aiagent/runs/flows/<flow>/<run>/audit.jsonl`); chat tasks write `~/.aiagent/runs/<time>-<session>.audit.jsonl`.

- **Tamper evidence.** Each line holds the SHA-256 of the line before it, so editing, removing, inserting or reordering any line breaks the chain from that point. `aiagent audit verify <run>` names the line (exit code 1). A rewritten *last* line cannot be seen from the file alone: the run record (`run.json`) keeps the head hash, and `aiagent audit verify <run> --head <hash>` compares against it (or against a hash you noted earlier). This is tamper-*evident*, not tamper-proof: someone who can rewrite the whole file and every hash after the change can still do so; store the head hash somewhere else if that matters.
- **Export.** `aiagent audit export <run> --format md|json|zip [-o file]`; `aiagent audit list` shows what is there. The zip holds `audit.jsonl` exactly as written (so `audit verify` works on the export, also directly on the `.zip`), a Markdown report, `verification.json` and one `.diff` per file change. In the app: **Flows → Export ▾ → Save audit trail…** (latest or opened run) and **Session history → Audit** (all tasks of that session, one folder per task in the zip).
- **Secrets.** Before a value is written it is scrubbed: the values of environment variables whose name contains `KEY`, `TOKEN`, `SECRET`, `PASSWORD` or `CREDENTIAL` (8+ characters), well-known key shapes (`sk-…`, `sk-ant-…`, `ghp_…`, `github_pat_…`, `AKIA…`, `AIza…`, `xox…-`, JWTs, `Bearer …`, PEM private keys) and `name=value` / `--flag value` pairs whose name ends in key, token, secret or password. They become `[REDACTED]`. Keys that do not look like any of these and are not in the environment (for example one typed into a prompt in an unusual format) cannot be recognised.
- **Limits.** Tool output over 8,000 characters and diffs over 100,000 characters are cut in the file (the full SHA-256 and length are kept). Calls that the permission mode allowed without asking appear as tool calls with no approval record; the mode is in `run.start`. Edits an external ACP agent makes with its own tools are not seen. Audit files are not pruned automatically; a flow run's file goes when the run is deleted by the history retention.
