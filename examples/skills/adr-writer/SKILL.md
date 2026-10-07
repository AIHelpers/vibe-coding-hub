---
name: adr-writer
description: Write an Architecture Decision Record when a task involves a significant design choice
tags: [architecture, docs]
version: 1
allowed-tools: [read_file, glob, grep, write_file]
---
# ADR writer

Use this when the task introduces or changes an architectural decision (new dependency,
new module boundary, data model change, protocol choice).

1. Look for an existing ADR folder (`docs/adr`, `docs/decisions`, `adr/`). Follow its numbering and template if one exists.
2. Otherwise create `docs/adr/NNNN-<short-title>.md` with the next free number.
3. Sections, each short and concrete:
   - **Status** — Proposed / Accepted / Superseded by …
   - **Context** — the forces at play, constraints, what triggered the decision.
   - **Decision** — what we will do, in one or two sentences, then details.
   - **Alternatives considered** — at least two, with why they lost.
   - **Consequences** — positive, negative, and follow-up work.
4. Link the ADR from the plan or PR description.
