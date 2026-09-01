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
- `send_message` - Send a message to another agent in the session (`*` = broadcast)

## Key Features

- **Multi-agent sessions**: parallel agent groups, inter-agent mailbox (send_message), per-agent diff attribution
- **Multi-provider support**: OpenAI, Anthropic, Ollama, and any OpenAI-compatible API
- **Streaming responses**: Real-time streaming via `IAsyncEnumerable<StreamChunk>`
- **Tool system**: Extensible tool registry with automatic tool calling
- **Context management**: Automatic context trimming by token count
- **Security**: Read-only mode, allowed paths, dangerous command detection
- **Cross-platform**: .NET 10, native publishing for Win/Linux/macOS
- **Configuration**: JSON config file + environment variables

## Publish

```bash
make publish-all
# or
./install.sh
```
