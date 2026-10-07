---
name: team-conventions
description: The team's coding conventions; pin it to characters that write code
tags: [conventions]
version: 1
---
# Team conventions

- C#: file-scoped namespaces, `var` when the type is obvious, nullable reference types on, no `#region`.
- Async all the way: methods returning `Task` end with `Async` and accept a `CancellationToken` when they do I/O.
- Every behavior change comes with unit tests (xUnit), and the full test suite must pass before the work is done.
- Keep pull requests small and focused; describe the "why" in the PR description.
- Never commit secrets, generated files, or local configuration.
