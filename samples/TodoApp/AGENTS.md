# AGENTS.md — TodoApp

A tiny to-do list library with xUnit tests. It is the practice project of Vibe Coding Hub.

## Commands
- Build: `dotnet build TodoApp.slnx`
- Test (everything): `dotnet test TodoApp.slnx`
- Test (one test): `dotnet test TodoApp.slnx --filter "FullyQualifiedName~Overdue_ExcludesCompletedItems"`

Run them from the project root (the folder that holds `TodoApp.slnx`).

## Layout
- `src/TodoApp/` — the library (`TodoService`, `TodoItem`).
- `tests/TodoApp.Tests/` — xUnit tests, one behavior per test.

## Rules
- An item is **overdue** when it is not done and its due date is before today. An item due today is not overdue yet.
- Fix the code, never the test. Do not skip, delete or weaken a test to make it pass.
- Keep changes small. Match the existing style: file-scoped namespaces, nullable reference types on.
- A task is done only when `dotnet test TodoApp.slnx` reports 0 failures.
