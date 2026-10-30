# Privacy and network transparency

Vibe Coding Hub runs on your computer. Your code, prompts and files go **only** to the services you configured. This page lists every place the product can send data, what is stored locally, and how to check both yourself.

**Short version**

- The product has **no telemetry**: it does not send usage statistics, crash reports, prompts or code to Anthropic, to the authors of this project or to anyone else.
- Code and prompts leave your computer only to **the model provider you configured** (and to MCP servers / tools you added yourself, see below).
- **Settings → Advanced → Network activity** lists every host this session contacted. **Offline mode** blocks everything except `localhost` and the configured provider.

## What is sent where

| Destination | What is sent | When | How to stop it |
|---|---|---|---|
| **Model provider** (the one in Settings → Model: OpenAI, Anthropic, Ollama, LM Studio, any OpenAI-compatible server) | The conversation: your prompts, the system prompt, the contents of files the agent reads, tool results, and (for semantic search) code chunks sent to the **embedding** model if the index uses a remote embedding provider | Every model call | Use a local provider (Ollama, LM Studio, vLLM on `localhost`). Nothing leaves the machine then. |
| **Remote MCP servers** (`mcp.json`, entries with a `url`) | Whatever the agent passes to that server's tools, plus the bearer token you configured | Only when the agent uses a tool of that server | Remove the entry, or turn on Offline mode (remote MCP servers are blocked; local `stdio` servers are separate processes, see "Not covered"). |
| **`web_fetch` targets** | A GET request to the page the agent asks for. Requests to private and loopback addresses are refused (SSRF guard). The request carries a fixed `User-Agent` and none of your credentials. | Only when the agent calls `web_fetch` | Deny the tool, use Plan mode, or turn on Offline mode. |
| **GitHub** (`api.github.com`) for **update checks** | One `GET` of the latest release of `AIHelpers/vibe-coding-hub` with a `User-Agent` of the form `VibeCodingHub/<version>`. Nothing about you or your project. | At most once a day after start, and when you pick "Check for updates" | Settings → Advanced → "Check for updates automatically" off, or Offline mode. `AIAGENT_UPDATE_URL` points the check at your own server. |
| **GitHub** (`api.github.com`) for **issues** | Reads issues for the repository you open from the issue picker, using your `gh` login or token. | Only when you use the issue picker | Do not use the picker, or Offline mode. |
| **Local provider detection** | `GET` to `localhost:11434` and `localhost:1234` | First-run setup wizard | n/a (never leaves the computer) |
| **Git remotes** | `git` itself talks to your remote when you ask the agent (or the Source Control panel) to push, pull or fetch. | Only on those actions | n/a (see "Not covered") |
| **External agents** (ACP characters such as Claude Code, Codex, Gemini CLI) | They are separate programs with their own network behavior and privacy terms. We hand them the task and the files they ask for. | Only when a flow step uses one | Do not use `runtime: acp` characters. |

Nothing else. In particular there is no analytics endpoint, no license server and no "phone home" of any kind.

### What is **never** logged by the network list

The **Network activity** list and the in-memory log behind it keep only: time, **host name**, port, scheme, HTTP method and why the request was made. They never keep the path, query string, headers, request or response bodies, API keys, prompts or code. The list lives in memory and disappears when the app closes (the *Clear list* button empties it earlier).

## Verify it yourself

1. **Settings → Advanced → Network activity.** Run a task, press *Refresh*. Every row is a host this session talked to, with the purpose (`model provider`, `MCP server`, `web_fetch`, `update check`, `GitHub (issues)`, `local provider detection`) and a count. A request that was refused shows up as *blocked*.
2. **Offline mode.** Turn it on and save. From then on every outgoing request passes a single gate (a delegating `HttpMessageHandler` that all the product's HTTP clients use) that allows only:
   - `localhost`, `*.localhost`, `127.0.0.0/8` and `::1`, and
   - the host of each model provider that is set up (so a remote provider you chose still works).

   Everything else is refused **before a connection is opened**, including redirects that lead to another host. With a local Ollama provider a whole agent task makes zero non-localhost requests; this is covered by a test (`OfflineModeTests`).
   Set the environment variable `AIAGENT_OFFLINE=1` to force offline mode on (CLI and desktop), whatever the saved setting says: useful in locked-down images.
3. **Your own tools.** Anything that watches traffic at the operating-system level (a firewall, `Wireshark`, `lsof -i`) should show the same picture: connections to your provider, and nothing else.

### Not covered by the gate (be aware)

The gate controls the product's own HTTP clients. It cannot see traffic made by **other programs** the product starts on your behalf:

- shell commands the agent runs (`git push`, `npm install`, `dotnet restore`, `curl`, ...). Use the Docker sandbox with `AIAGENT_SANDBOX_NETWORK` off (the default) to cut their network access;
- local MCP servers started over `stdio` (they are your programs) and ACP external agents;
- the Docker daemon, language servers and the browser check, which use their own connections.

For a strict guarantee, combine Offline mode with the Docker sandbox and an OS firewall.

## Telemetry

There is none. If telemetry is ever added, it will be **off by default**, behind a clear toggle in Settings, with its payload documented here, and it will never include code, prompts, file names or file contents. Until this page lists a payload, assume nothing is sent.

## What is stored locally

Everything lives under your user folder `~/.aiagent` (Windows: `%USERPROFILE%\.aiagent`; set `AIAGENT_HOME` to move it) and in the project you open. Settings → Advanced shows the config and log folders and has *Open* buttons.

| Where | What | Notes |
|---|---|---|
| `~/.aiagent/config.json` | Settings, providers and **API keys** | Keys are encrypted at rest (Windows DPAPI for the current user; AES-256-GCM with a machine-derived key elsewhere). Treat the file as sensitive anyway. |
| `~/.aiagent/sessions/` | Chat sessions: messages, tool calls, diffs | Your prompts and the model's answers, in readable form. |
| `~/.aiagent/checkpoints/` | Snapshots of files before the agent changed them (undo) | Copies of your source files. |
| `~/.aiagent/runs/` | Flow run records, outputs and the hash-chained **audit trail** (`audit.jsonl`) | The audit trail redacts known secret patterns (API keys, tokens, `*KEY*`/`*TOKEN*`/`*SECRET*` environment values). Export with `aiagent audit export`. |
| `~/.aiagent/index/` | Code index and, if enabled, embeddings of code chunks | Derived from your code. |
| `~/.aiagent/logs/` (see *Log folder*) | Diagnostic logs, rolled daily, a limited number of days kept | Technical messages, file paths and errors. Safe to delete. |
| `~/.aiagent/skills`, `characters`, `pipelines`, `packs`, `presets`, `pricing.json`, `models.json`, `mcp.json` | Your configuration and installed packs | MCP tokens in `mcp.json` are stored as you typed them. |
| `<project>/.aiagent/`, `.aiagent-worktrees/` | Project skills, characters, flows; git worktrees for isolated flow runs | Shared through git if you commit `.aiagent/`. |

To remove everything: close the app and delete `~/.aiagent` (and `.aiagent` / `.aiagent-worktrees` in your projects).

## For security reviewers

- The set of outgoing destinations is closed and listed above; each one has an owner-visible purpose label in the Network activity list.
- All product HTTP clients are created behind `NetworkPolicyHandler` (`AiCodeAgent.Core/Network`). A new client that skips it is a bug; `NetworkPolicyHandlerTests` and `OfflineModeTests` pin the behavior.
- `AIAGENT_OFFLINE=1` makes the guarantee a deployment setting instead of a user choice.
