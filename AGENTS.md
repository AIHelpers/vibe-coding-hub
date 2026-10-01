# AGENTS.md — Project Memory for vibe-coding-hub

> Persistent instructions the agent loads at the start of every session.
> Keep it concise; large files consume context.

## Instructions
- Describe the project in 1-2 lines.
- List key goals and constraints the agent must respect.

## Conventions
- Code style, naming, formatting.
- Preferred frameworks and libraries.
- Test command: `dotnet test`
- Build command: `dotnet build`

## Compact Instructions
- When compacting context, keep the most recent tool results.
- Preserve any error messages and the current task goal.
