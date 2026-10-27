---
name: csharp
description: Write idiomatic modern C# (.NET 8+) — nullable, async, records, LINQ — and its unit tests
tags: [dotnet, language]
version: 1
---
# C#

- Target the project's `TargetFramework`; use the newest language features it allows (file-scoped namespaces, records, pattern matching, collection expressions).
- Nullable reference types on: no `!` to silence warnings — fix the flow or make the type nullable.
- Async all the way: `Task`-returning methods end in `Async`, take a `CancellationToken` for I/O, and never block with `.Result`/`.Wait()`.
- Prefer immutable data (`record`, `init`), small methods, and LINQ for transformations (not for side effects).
- Dependency injection through constructors; no service locator, no statics with state.
- Tests: xUnit, one behavior per test, `Arrange/Act/Assert`, NSubstitute for collaborators. Run `dotnet build` and `dotnet test` before reporting done.
