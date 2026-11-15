# Review Queue

One list of everything that is waiting for you. Open it from the activity bar (inbox icon, badge = number waiting),
the command palette ("Review Queue"), or **Ctrl+Shift+R**.

## What appears in it

| Kind | Comes from | Approve | Request changes | Decline |
|---|---|---|---|---|
| Approval | A chat tool call that needs permission | Approve the call | Deny with a comment; the agent receives the comment as the tool result | Deny |
| Gate | A flow step that asks before it starts | Start the step | Start the step with your note added to its prompt | Stop the flow |
| Tool call in a flow | A flow step asking to run a tool | Approve | Deny with a comment | Deny |
| Decision | A flow "You decide" step | Picks the approving outcome (`approved`, `accepted`, `yes`, ...) | Picks the rejecting outcome (`rejected`, `needs_changes`, ...) with your note | Stops the flow |
| Finished task | A chat task that completed, with its evidence pack | Accept (clears it) | Sends your comment to the agent as a new message | Dismiss |

Items that block an agent are listed first, oldest first; finished tasks come last.

## Keys

Keys work while the panel has focus (it takes focus when it opens).

| Key | Action |
|---|---|
| J / Down, K / Up | Next / previous item |
| A, Enter | Approve |
| R | Request changes (opens the comment box) |
| X, Delete | Decline |
| Ctrl+Enter / Esc | Send / cancel the comment |

While you type a comment, only those two keys act, so letters are never taken as commands.

## Behaviour worth knowing

- While the panel is open, chat approvals are answered here instead of in the modal dialog. Closing the panel brings the dialog back.
- The queue reads its sources; it keeps no state. An item disappears however it was answered (panel, dialog, Flows inspector, stopping the run).
- Approvals can be answered in any order.
- A comment on a finished task is refused while the agent is busy or the chat input holds an unsent draft. Nothing you typed is lost.

## Limits

- The queue is in memory and starts empty after a restart.
- Only finished chat tasks are listed, not flow runs.
- Remote flow approvals are not listed.
- "Approve all edits" and "always allow" are only offered in the approval dialog.
- A decision step with custom outcomes cannot be answered here; open it in Flows.
