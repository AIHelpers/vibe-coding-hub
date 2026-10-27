---
name: clean-architecture
description: Design and change code following Clean Architecture layers (Domain, Application, Infrastructure, Presentation)
tags: [architecture, design]
version: 1
---
# Clean Architecture

1. Find the layers in the solution (e.g. `*.Domain`, `*.Application`, `*.Infrastructure`, `*.Api`/`*.Web`). Keep to them; do not invent new ones.
2. Dependencies point inward only: Domain depends on nothing; Application on Domain; Infrastructure and Presentation on Application.
3. Business rules live in Domain entities/value objects and Application use cases (commands/queries + handlers) — never in controllers, repositories or EF configurations.
4. Application defines interfaces (repositories, clocks, gateways); Infrastructure implements them. Wire them up in the composition root.
5. Map at the boundaries: DTOs in/out of Presentation, persistence models in Infrastructure. Domain types do not leak to the API.
6. New feature checklist: use case + validator + handler test in Application, entity changes with tests in Domain, adapter in Infrastructure, endpoint in Presentation.
