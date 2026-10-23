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

## Definition of Done (mandatory)
- A task, story or PR is **not done** while CI has any error. `dotnet build AiCodeAgent.slnx` must succeed with 0 errors, and `dotnet test AiCodeAgent.slnx` must pass with 0 failures on every CI OS (Ubuntu, Windows, macOS).
- Never mark a story `done`, call it "ready", or open a PR as ready while the build or any test fails. Fix it first, or report the failure as blocking.
- Do not skip, delete or weaken a test to get green. A platform-specific skip needs a comment with the reason and the tracking story.
- Report only results you actually observed. If you could not run the build or tests, say so and leave the task open.

## Compact Instructions
- When compacting context, keep the most recent tool results.
- Preserve any error messages and the current task goal.
