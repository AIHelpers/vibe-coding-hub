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
# List built-in and user-defined pipelines
dotnet run --project src/AiCodeAgent.CLI -- pipeline list

# Run the full pipeline: analyze -> implement -> review -> test -> deploy
dotnet run --project src/AiCodeAgent.CLI -- pipeline run "Add pagination to the /users endpoint"

# Use a lighter pipeline for small changes
dotnet run --project src/AiCodeAgent.CLI -- pipeline run "Fix off-by-one in date parsing" --pipeline quick-fix
```

Pipelines are plain JSON and fully configurable — drop a file in `~/.aiagent/pipelines/*.json` to add
your own stage sequence (custom roles, prompts, or a subset of the SDLC). Agent roles themselves are
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
