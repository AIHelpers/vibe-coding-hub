# ADR 0002 · Remote runners: run a flow on a build server, watch it from the desktop

- **Status:** Proposed. Option (a) is prototyped (E7-S3); the product version is a separate story.
- **Date:** 2026-10-30
- **Story:** E7-S3 (spike, time-boxed 3 days). Builds on E5-S7 (headless CLI), E5-S5 (run records) and E7-S2 (audit trail).
- **Decision drivers:** a flow can run for a long time and needs a machine with the repository, the toolchain and the provider keys; the person who must approve its risky steps sits at a different machine. The desktop app should show the same live canvas it shows for a local run.

## Recommendation

**Go with (a): `aiagent serve`, a WebSocket event stream, as the one remote protocol.** Treat (b) and (c) as ways to *start* the server, not as alternatives to it:

| Option | Verdict |
|---|---|
| **(a) CLI with `serve`, WebSocket events** | **GO.** The prototype attaches the existing canvas to a run in another process with no change to the canvas code: the stream is the `FlowEvents` we already have. Approvals, human steps and cancel work over the same socket. |
| **(b) GitHub Actions runner + artifact upload** | **Keep for CI, not for watching.** Artifacts appear when a step ends, so there is no live status and no way to answer an approval. It stays the right place for non-interactive, read-only flows (E5-S7). A workflow *can* run `aiagent serve` and expose it through a tunnel for the rare long job; that is an add-on, not the design. |
| **(c) SSH + remote CLI** | **Use as the transport, not as a protocol.** `ssh -L 5000:127.0.0.1:5000 host aiagent serve ...` gives authentication, encryption and key management for free, and the desktop attaches to `ws://127.0.0.1:5000`. A bare "run the CLI and parse its stderr" would need its own event format, and approvals could not go back. |

**Estimate for the product version: about 4 weeks of one developer** (breakdown at the end).

## What the prototype does

`aiagent serve <task> --pipeline <flow>` runs one flow and listens (loopback by default) on `ws://127.0.0.1:<port>/events`. The desktop app (Flows → **Attach**) connects, receives the flow definition, draws it, and then applies each event with the same `ApplyRunEvent` a local run uses.

```
 desktop app                                   build server / container
 ┌──────────────────────┐   ws://…/events     ┌────────────────────────────────┐
 │ FlowEditorViewModel  │◄── hello, events ───│ RemoteRunServer                │
 │  ApplyRunEvent       │                     │   ▲ Publish(evt)               │
 │  Approve / Stop      │─── answer, cancel ─►│   │                            │
 └──────────────────────┘                     │ SdlcPipelineRunner (the flow)  │
                                              │   DockerCommandSandbox, worktrees, provider keys │
                                              └────────────────────────────────┘
```

- **Protocol (v1).** JSON text messages. Server → client: `hello` (protocol, run id, flow, task, full definition), `event` (`kind`, optional `agent`/`role`, `data`), `end`. Client → server: `answer` (approval), `human` (human-step choice), `cancel`. Event kinds: `flowStarted`, `flowNodeStatus`, `flowEdgeTaken`, `flowUsage`, `flowFinished`, `textDelta`, `toolCallStart`, `agentFinished`, `status`, `error`, `approvalRequest`, `humanRequest`, `resolved`. Unknown kinds are ignored, so the server can add kinds without breaking older clients; `hello.protocol` changes only for breaking changes.
- **Questions.** An approval or a human step is sent with a `requestId`. The client rebuilds a real `ApprovalRequestEvent` / `FlowHumanRequestEvent`; when the UI completes its task, the answer goes back. The first answer wins; every other client gets `resolved` so its "waiting" badge clears.
- **Late joiners.** The server keeps the messages of the run and replays them (minus questions that are already closed), so attaching halfway shows the whole canvas and the output so far.
- **Detach is not stop.** Closing the window or pressing Detach leaves the run going. Only an explicit **Stop** sends `cancel`.

Code: `Core/Remote/*` (protocol, codec and decoder, server, client), `CLI/ServeMode.cs`, `App/ViewModels/FlowEditorViewModel.Remote.cs`, tests in `Core.Tests/Remote`.

## Questions the story asked

### Authentication

Prototype: one bearer token (`Authorization: Bearer …` or `?token=` for clients that cannot set headers), compared in constant time, printed once on start or taken from `--token` / `AIAGENT_SERVE_TOKEN`. Default bind is loopback, so by default only local processes and SSH tunnels reach it. Anyone holding the token can watch **and** answer approvals and stop the run.

Product version: a token per person with a role (`viewer`: events only; `approver`: may answer; `owner`: may cancel), tokens stored hashed, expiry, and every answer recorded in the audit trail with the token's owner as `DecidedBy` (the prototype records the client address). For teams, put OIDC in front (the reverse proxy validates the user; `serve` trusts a signed header from it). The transport is TLS: Kestrel with a certificate, or a TLS proxy, or an SSH tunnel / Tailscale. The prototype speaks plain `ws://` and warns when bound beyond loopback.

### Approvals across the network

Same prompts, new failure modes, so three rules:

1. **Nobody is watching is not "yes".** A question with no answer is denied after `--approval-timeout` (default 10 min) and the audit trail says why. Detaching never approves anything.
2. **One question, one answer.** The first answer wins and the server tells everyone else it is closed. Stale answers are ignored.
3. **The preview travels with the question.** The prototype sends tool name, arguments and risk. The product version must also send the diff/command preview (`ToolPreview`) so a remote approver sees what the local one would.

A server restart loses open questions (they are denied with the run). Reconnects are safe: the client re-attaches and gets the backlog.

### Worktree and merge-back location

The flow runs where the code is: on the server, in a **worktree on the server** (E1 isolation), against a fresh clone or a mounted checkout. Nothing is merged back automatically. The result leaves the server in one of three ways, in order of preference:

1. **Push a branch + open a PR** (the E6-S4 pieces: PR creation and review comments). The desktop then reviews it like any other PR. This keeps the server's checkout disposable.
2. **Patch download:** the server exposes the final diff over the socket/HTTP (`GET /runs/<id>/patch`) and the desktop applies it to a local worktree.
3. **Leave it on the server** for runs that only produce a report.

The desktop never needs write access to the server's filesystem; the server needs push access to the remote only.

### Secrets

Provider keys and git credentials live **on the runner** (environment or secret store) and never travel to the desktop; the desktop only needs the serve token. The audit trail and the event stream pass through `SecretRedactor` (E7-S2) before leaving the server, so output that quotes a key does not leak it to a viewer. In a container (`DockerCommandSandbox`) the tool commands see only the variables the flow allows. The serve token is a secret too: it is printed to stderr once, not logged, and the app does not save it.

## Alternatives in more detail

**(b) GitHub Actions.** Good: no machine to run, secrets handled by GitHub, runs on PR events. Bad for this goal: a run is a batch (job log and artifacts), the runner has no inbound address, so the desktop cannot connect; live status would need the job to push events to a relay we would have to operate. Approvals would be a second channel again (PR comment reactions at best). Keep using it for the E5-S7 PR-review flow.

**(c) SSH + remote CLI.** Good: zero new server code, existing keys and audit. Bad as a protocol: stderr lines are not an event stream, and without one the desktop cannot draw the canvas or return an approval. As a tunnel for (a) it is the simplest secure setup and is what we recommend for a first release (`ssh -L`). No extra product code needed beyond documenting it.

**gRPC / SignalR.** More tooling, binary framing, code generation; our events are already a closed set of small JSON objects and the first client may be a browser. WebSocket + JSON is the smallest thing that works and is easy to proxy.

## Known limits of the prototype

- One run per process; `serve` exits (after `--linger`) when the flow finishes. A multi-run daemon needs a run registry and `GET /runs`.
- The backlog is a list capped at 20 000 messages. The product version should keep a **snapshot** (node states, outputs, usage) plus a ring buffer of recent deltas, so a late joiner is fast and memory is bounded.
- `HttpListener` is enough for localhost; Kestrel is the production host (TLS, limits, HTTP/2, graceful shutdown).
- Single shared token, no roles, no TLS (see Authentication).
- Only streamed output, status, usage and questions travel. Diffs, checkpoints and file previews do not yet.
- The tests drive the server and client with scripted events over a real socket. A real model run through `serve` was **not** exercised here.

## Estimate for the product version

| Piece | Days |
|---|---|
| Kestrel host, TLS options, health/readiness, graceful shutdown | 2 |
| Token store with roles and expiry, audit attribution, rate limiting | 3 |
| Snapshot + ring buffer, reconnect with `lastEventId`, run registry (`GET /runs`, several runs per server) | 4 |
| Approval previews (diff/command) in the protocol, UI for them | 2 |
| Result delivery: branch push + PR (reuses E6-S4), patch endpoint | 3 |
| Container image + `docker run` recipe, SSH-tunnel and Tailscale docs | 2 |
| Desktop: saved servers, reconnect, several attached runs, error states | 3 |
| Tests (protocol compatibility, reconnect, auth, soak with a long run) and hardening | 3 |
| **Total** | **≈ 22 working days (≈ 4 weeks)** |

## Open questions

1. Should an attached desktop be able to **start** a run (`POST /runs`), or only watch runs started on the server? Starting needs a task, a flow and a branch policy; it widens what a token can do.
2. Is a viewer-only link (read-only token) wanted for sharing a live run with a reviewer?
3. How long should the server keep a finished run's state for late attaches (the prototype: until the process exits)?
