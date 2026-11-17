# Mission Control

A board for running several tasks side by side. Each card is one task; an agent works on it in its own git worktree on its own
branch, so cards started together cannot overwrite each other's files.

## How to use it

Open **Board** in the activity bar (or run *Mission Control* from the command palette).

1. Type a task in the box at the top and press **Enter**. The card lands in **Backlog**. Pick who works on it first if you do not
   want the default agent (see [Who works on a card](#who-works-on-a-card)).
2. Press **▶ Start** on a card, or **▶ Start all** to start every card in the Backlog at once.
3. A running card shows its status, the last thing the agent did, tool calls, tokens and cost. Press **■ Stop** to stop it.
4. When the agent stops, the card moves to **Needs review** with its answer (or the error) and the branch that holds its work.
5. Look at the result, then press **✓ Mark done** (the card moves to **Done**), **↻ Retry**, or **Back to backlog**.

| Column | Meaning |
|---|---|
| Backlog | Waiting to be started. |
| Running | An agent is working on it. |
| Needs review | The agent stopped: it finished, failed, was stopped, or the app closed while it ran. A badge says which. |
| Done | You accepted the result. |

## Who works on a card

The drop-down next to the task box chooses the agent for the next card; it stays selected, so a batch of cards for one agent is
quick to add. A card waiting in the Backlog has its own drop-down to change its agent. After a run the agent is shown as text,
and a retry uses the same agent. Send the card **Back to backlog** to change it.

| Choice | What runs | Good for |
|---|---|---|
| **Default agent** | The app's own agent with the model and settings you use in chat. | Everyday tasks. |
| **A character** | The app's own agent shaped by the character file: persona, tools, skills, model, permission mode. | A reviewer, a test writer, a refactoring specialist. |
| **Flow: …** | A flow (a pipeline with connections) run to the end on the card's worktree. The card's task is the flow's task. | Plan → build → review in one card. |
| **Claude Code / Codex / Gemini CLI (ACP)** | The coding agent installed on your computer, driven over the Agent Client Protocol. It spends the subscription or key you already set up in that tool. | Using a plan you already pay for. |

**External agents.** The list shows Claude Code, Codex and Gemini CLI with a note when one is not installed. Choosing one that is
missing explains what to install and leaves the card as it was. An agent is *installed* when its program is on your `PATH`:
`claude`, `codex` or `gemini`. Claude Code and Codex also need Node.js (`npx` starts a small ACP adapter on first use); sign in
once in a terminal before using them on the board. The list is read again each time you open the board, so a CLI you installed a
minute ago shows up. Any other ACP program can be used through a character file with `runtime: acp` and a `command` (see the
characters guide); such characters appear in the list marked *(external)*.

**Flows.** Only flows are offered (pipelines with connections between steps), not linear pipelines. The flow runs unattended, like
a CI run: a tool that needs approval is denied, and a step that waits for a person (a gate or a human step) stops the flow with a
clear reason on the card. Progress shows as the step that is running; cost and tokens appear when the flow ends.

**When an agent cannot start.** A character that was deleted, a flow that was renamed or a program that was removed does not fail
silently: the card stays in the list with *(not found)*, and starting it ends in *Needs review* with a message, before any
worktree is made.

## What a run does

- The card gets a worktree `…/.aiagent-worktrees/<repo>/…` and a branch `agent/<card>/<title>`. The agent's file tools are limited to
  that worktree.
- Edits are approved automatically; anything that would ask for approval (running a command, for example) is **denied at once**,
  because nobody sits at a card to answer. The agent then works without it. For an external agent the card's summary lists what
  it asked for and was refused.
- When the agent stops, its work is **committed on the card's branch and not merged**. Merging is your decision. **Copy branch**
  copies the name; the Changes panel and `git` work on it as on any branch.
- If the agent changed nothing (an answer, an analysis), no branch is kept and the card says *Answered, no changes*.
- A card cannot run without a git repository that has at least one commit. It fails with that message instead of running in the
  shared folder.
- Cost and tokens add up over all runs of a card. A retry starts a new run on a **new** branch; the earlier branch stays.

## Control a running card

A running card has a text box and three buttons. Everything you do keeps the agent's work: the card continues the same
conversation in the same worktree, so the agent remembers what it did and learned.

| Action | What happens |
|---|---|
| **Pause** | The agent stops after the step it is on and waits. The card stays in *Running* with a *Paused* badge. |
| **Resume** | The agent continues where it left off. |
| **Send** | Your message breaks off the current step. The agent reads it and carries on with the task. A message sent to a paused card also resumes it. |
| **Redirect** | Like Send, but tells the agent to drop its current approach and do what you wrote instead. Files it already changed are kept unless they conflict. |
| **Stop** | Ends the run. Works while paused too. The work so far is committed on the branch. |

The step that is running when you pause, send or redirect is cancelled, as when you press Stop in chat; tool calls that had not
started are answered with "Cancelled before execution" and the agent sees that.

**What can be steered.** The default agent and characters that run on the built-in agent. Flows and external agents (Claude
Code, Codex, Gemini CLI) start from scratch on every call, so there is no conversation to continue; their cards show
*This agent can only be stopped while it runs* and offer **Stop** only.

**Terminal.** **Terminal** on a card opens a shell in the card's worktree and closes the board so you can see it; the cards keep
running. If the worktree folder was removed, the card says so and names its branch.

**Changes.** **Changes** lists the files the card changed (added, modified, deleted, renamed) against the commit its branch left
the project at, so commits that reached the project meanwhile do not show up as the card's work. While the worktree exists the list
includes work the agent has not committed yet, so you can look over a running card's shoulder. Click a file for a side-by-side
diff, which opens over the board.

## Projects and storage

Each project has its own board. Switching project does not stop cards that are running in another one; the badge on **Board**
counts running cards in all projects.

The board is saved in your user folder, `~/.aiagent/boards/<project>-<hash>/board.json`, not in the repository: it holds paths
and branch names that belong to this machine. If the app closes while a card runs, the card shows as **Interrupted** next time;
work that was not committed is still in its worktree folder.

## Limits

- Characters set their own permission mode, but a character that *asks* before every edit is run with auto-approved edits on a
  card; one that is read-only (plan) stays read-only.
- External agents report cost and tokens only if the agent sends them (ACP does not require it), so their cards may show no cost.
- A flow's own parallel steps make worktrees of their own inside the card's worktree; the flow merges them as it does anywhere
  else. Very large flows on many cards at once can be slow.
- Approvals cannot be answered from a card yet (see above).
- Merging and conflict help, issue intake and the cost forecast come in the next stories.
- A message interrupts the step the agent is on, so a long command it was running is cancelled and must be started again.
- Branches and worktrees of removed cards are not deleted. Remove them with `git worktree remove` and `git branch -D`.
- All cards of a project are started at once; there is no limit on parallel cards yet, so mind your provider's rate limits.
