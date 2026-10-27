---
name: code-review-checklist
description: Review a change against the team checklist (correctness, tests, security, readability)
tags: [review, quality]
version: 1
allowed-tools: [read_file, grep, glob, git, run_diagnostics]
---
# Code review checklist

Review the diff (`git diff` against the base branch) and report findings grouped by severity
(blocker / should fix / nit), each with `file:line` and a concrete suggestion.

- **Correctness** — edge cases (empty, null, very large, concurrent), error paths, off-by-one.
- **Tests** — new behavior has tests; tests assert outcomes, not implementation details; failing paths are covered.
- **Security** — input validation, injection (SQL, shell, path traversal), secrets in code or logs, authz checks.
- **Readability** — names say what things are; functions do one thing; no dead code or commented-out blocks.
- **Consistency** — follows existing patterns in the codebase rather than inventing new ones.
- **Operability** — useful log messages, no noisy logging in hot paths, config over hard-coded values.

Finish with a one-line verdict: approve, approve with nits, or request changes.
