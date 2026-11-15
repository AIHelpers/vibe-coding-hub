# Evidence pack

An evidence pack answers one question for a reviewer: *what should I look at before I approve this?* It is built from the audit trail of a finished task or flow run, so it is as trustworthy as the trail, and it says what it does not know.

```
aiagent evidence latest                      # Markdown on the console
aiagent evidence <run id> --format json      # the stored structure
aiagent evidence latest -o evidence.md       # to a file
aiagent evidence latest | gh pr create --body-file - --title "..."
```

`<run>` is the id shown by `aiagent audit list`, part of it, a path to an `audit.jsonl`, or `latest`. Exit codes: `0` ok, `1` the audit trail failed verification (the pack is still printed and says so), `2` run not found, `3` bad arguments.

A pack is written automatically when a run ends: `<time>-<session>.evidence.json` beside a chat run's audit file, `evidence.json` in a flow run's folder under `~/.aiagent/runs`. The command rebuilds it from the trail every time, so the stored file is only a convenience.

## What is in it

| Part | Source in the audit trail | How it is decided |
|---|---|---|
| Tests before and after | `execute_command` results and the test stage of `verify_changes` | A command is a test command when it runs a known runner (`dotnet test`, `pytest`, `go test`, `npm test`, `jest`, `vitest`, `cargo test`, gradle/maven test, ...). The first and the last run of the same runner are compared. |
| Test counts and names | the console output | Parsers for dotnet, pytest, jest, vitest, cargo and go. Output that is not recognised gives passed or failed from the exit code and no counts. |
| Build and lint | commands and `verify_changes` stages | The last build command and the last lint command, and whether they exited with 0. |
| Changed files, ranked | `file.change` entries (diff, hashes) | A point system, shown next to each file: size (10, 50, 200 changed lines), public API touched (+3), security-sensitive path (+4), build, deploy or data config (+2), removal (+2). 5 or more is high, 3 or 4 medium. Test files stay low. |
| Acceptance criteria | `acceptance_criteria` tool calls that succeeded | The agent declares criteria before it edits and marks each one at the end. Every cited proof is checked against the rest of the trail; see below. |
| Not proven | all of the above | See below. |
| Commands, screenshots, steps, approvals | `tool.call`, `browser_check`, `step.end`, `approval.*` | Listed as recorded. Screenshots are local files: attach them to the pull request by hand. |
| Cost | `run.end` | Tokens, dollars and duration of the run. |

## Acceptance criteria

For a task that changes code the agent has the `acceptance_criteria` tool (it is in the planner and implementer presets, and any character can list it in `tools`). It states 2 to 8 checkable criteria before the first edit (`declare`, numbered AC1, AC2…) and, after the last test run, marks each one (`mark`) as `met` or `not_met` with its proof: `tests`, `files`, `commands`, and an optional `note`. A second `declare` replaces the list and drops earlier marks.

The marks are claims. The pack decides each criterion itself:

| Status | Meaning |
|---|---|
| proven | Marked met, and at least one proof is confirmed by the audit trail, and no cited test is failing. |
| claimed, not confirmed | Marked met, but nothing in the trail confirms the proof (none cited, a note only, a test that was not seen, a command that failed or never ran, or the code changed after the run). |
| contradicted | Marked met, but a cited test is failing in the last test run. |
| not met | The agent said so. |
| unproven | Declared, never marked. |

How a proof is confirmed:

- **test:** the test failed in the first run and does not fail in the last (needs a runner whose output names failing tests), or it was added in the diff of a test file and the last run passed. A passing run does not list test names, so an existing test that always passed cannot be confirmed by name: cite a command or a file instead. A test named among the failures of the last run contradicts the mark. A test run before the last file change proves nothing about the final code.
- **file:** the file was changed in the run (a path ending is enough: `Todo.Core/TodoService.cs`).
- **command:** a command containing the text was run, passed, and no file changed after it (`dotnet test`).

A note is never proof. Calls that returned an error are ignored, and a run with no criteria simply has no acceptance section. Criteria declared after the first edit are flagged, because they may have been written to fit the result.

## Not proven

The list is the honest part of the pack. It names:

- no test run was observed;
- the last test run failed, or the last build or lint run failed;
- files were changed after the last test run or build;
- the run did not finish (no end record) or ended with a status other than `completed`;
- the audit trail failed verification, so the pack may not match what happened;
- some acceptance criteria are not proven, or were declared after the first edit.

An empty list means no gap was detected in the recorded evidence. It is not a review.

## Limits

- Tool output over 8,000 characters is cut in the audit file. A long test log can lose the summary line at its end; the pack then marks the run as cut and falls back to the exit code.
- If the agent never ran the tests before it changed code, there is no "before" and the pack says so instead of inventing one.
- Edits made by an external ACP agent with its own tools are not in the trail, so they are not in the pack.
- The risk score is a transparent heuristic, not a security review. Paths and diffs decide it; it cannot know what the code does.
