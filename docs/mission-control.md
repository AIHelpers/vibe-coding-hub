# Mission Control

A board for running several tasks side by side. Each card is one task; an agent works on it in its own git worktree on its own
branch, so cards started together cannot overwrite each other's files.

## How to use it

Open **Board** in the activity bar (or run *Mission Control* from the command palette).

1. Type a task in the box at the top and press **Enter**. The card lands in **Backlog**.
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

## What a run does

- The card gets a worktree `…/.aiagent-worktrees/<repo>/…` and a branch `agent/<card>/<title>`. The agent's file tools are limited to
  that worktree.
- Edits are approved automatically; anything that would ask for approval (running a command, for example) is **denied at once**,
  because nobody sits at a card to answer. The agent then works without it.
- When the agent stops, its work is **committed on the card's branch and not merged**. Merging is your decision. **Copy branch**
  copies the name; the Changes panel and `git` work on it as on any branch.
- If the agent changed nothing (an answer, an analysis), no branch is kept and the card says *Answered, no changes*.
- A card cannot run without a git repository that has at least one commit. It fails with that message instead of running in the
  shared folder.
- Cost and tokens add up over all runs of a card. A retry starts a new run on a **new** branch; the earlier branch stays.

## Projects and storage

Each project has its own board. Switching project does not stop cards that are running in another one; the badge on **Board**
counts running cards in all projects.

The board is saved in your user folder, `~/.aiagent/boards/<project>-<hash>/board.json`, not in the repository: it holds paths
and branch names that belong to this machine. If the app closes while a card runs, the card shows as **Interrupted** next time;
work that was not committed is still in its worktree folder.

## Limits

- Starting a card does not choose a character, flow or external agent yet; cards use the session's model and settings.
- Approvals cannot be answered from a card yet (see above).
- Merging and conflict help, the card's diff and terminal, issue intake and the cost forecast come in the next stories.
- Branches and worktrees of removed cards are not deleted. Remove them with `git worktree remove` and `git branch -D`.
- All cards of a project are started at once; there is no limit on parallel cards yet, so mind your provider's rate limits.
