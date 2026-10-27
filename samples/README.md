# Samples

## TodoApp

A tiny .NET 10 class library with xUnit tests. Vibe Coding Hub ships it inside the app: **Try the sample project**
(first-run wizard, or Command Palette → "Try the sample project") copies it to
`Documents/VibeCodingHub/samples/TodoApp`, turns it into a git repository with one commit, opens it and puts the first
task into the chat box. A checklist then ticks off as you do each step: *send the task → approve the edit → see the
tests pass → commit in Source Control*.

`TodoService.Overdue` has a deliberate bug (`x.Due <= today` instead of `!x.Done && x.Due < today`), so exactly one test,
`Overdue_ExcludesCompletedItems`, fails until it is fixed.

| Path | What |
|---|---|
| `TodoApp/` | The sample itself (`TodoApp.slnx`, `src/`, `tests/`, `AGENTS.md`, `.gitignore`, `.aiagent/` with example characters and skills). |
| `reference/TodoApp-overdue-fix.patch` | The reference fix. Not part of the sample the user gets. |

The app embeds `TodoApp/` as resources (see `AiCodeAgent.App.csproj`), so a change to the sample needs no extra step.

### Keeping the sample honest

`SampleProjectReferencePatchTests` (in `AiCodeAgent.Core.Tests`) copies the sample, runs its tests, and requires exactly
one failure; it then applies the reference patch and requires zero. If you change the sample, update the patch so it still
applies: `git diff --no-index` between the buggy and the fixed `TodoService.cs` gives the file (keep the `a/` and `b/`
prefixes, drop the `index` line).

Run only that test: `dotnet test AiCodeAgent.slnx --filter "FullyQualifiedName~SampleProjectReferencePatchTests"`
(needs `git` and NuGet access: it restores xunit for the sample).
