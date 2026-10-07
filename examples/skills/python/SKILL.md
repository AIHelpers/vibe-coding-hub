---
name: python
description: Write clean, typed Python 3.11+ with pytest tests, following the project's tooling (uv/poetry/pip, ruff, mypy)
tags: [python, language]
version: 1
---
# Python

- Detect the toolchain first (`pyproject.toml`, `uv.lock`, `poetry.lock`, `requirements*.txt`) and use it; never mix package managers.
- Type hints on public functions; run `mypy`/`pyright` if configured. Prefer `dataclasses`/`pydantic` models over loose dicts.
- Small pure functions, explicit imports, no wildcard imports, no mutable default arguments.
- Errors: raise specific exceptions; never `except Exception: pass`.
- Tests with `pytest` (fixtures, `parametrize`), deterministic (seed randomness, no network). Run `ruff check`, the type checker and `pytest` before reporting done.
