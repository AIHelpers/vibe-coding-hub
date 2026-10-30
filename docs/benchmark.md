# Benchmark: single agent vs flows

`tools/Bench` answers one question with data: **for the same tasks and the same model, do the flows (`bug-fix`, `feature-team`) solve more tasks than one agent in chat mode, and what do they cost in tokens, dollars and time?**

```bash
dotnet build AiCodeAgent.slnx
dotnet run --project tools/Bench -- --tasks bench/tasks.json --configs single,bug-fix-flow --model <id>
```

It writes three files to `bench/results/` (change with `--out`):

| File | Content |
|---|---|
| `report.md` | methodology, model versions, dates, results with 95 % confidence intervals, paired comparison, **every** task |
| `attempts.csv` | one row per attempt: status, tokens, cost, times, models, run id, note |
| `attempts.jsonl` | the same rows as they finished (used by `--resume`) |

## What you must supply: the tasks

The repository ships a **smoke suite with one task** (`bench/tasks.json`, the TodoApp sample). It proves that the harness works end to end. One task says nothing about single agents versus flows, so **its numbers must not be quoted as a result**. The report of a suite like this says in its first lines how small it is.

A publishable benchmark needs real tasks, SWE-bench-lite style: 30 to 50 genuine bug fixes from open-source .NET and Python repositories. Collecting them is manual work this repository cannot do for you. Each task needs:

1. a repository state **before** the fix (a commit),
2. a task text, written like an issue and **without** the solution,
3. a hidden test that fails before the fix and passes after it (usually the tests the real fix added).

### The task file

```json
{
  "name": "bugfix-dotnet-python-v1",
  "description": "Optional text shown in the report.",
  "tasks": [
    {
      "id": "polly-1234",
      "language": "csharp",
      "source": "https://github.com/OWNER/REPO/issues/1234",
      "prompt": "The text of the issue, as the agent should see it.",
      "repo": { "git": "https://github.com/OWNER/REPO.git", "commit": "<sha before the fix>" },
      "setupCommand": "dotnet restore",
      "testPatch": "patches/polly-1234.tests.patch",
      "testCommand": "dotnet test --nologo --filter FullyQualifiedName~Issue1234"
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | unique; letters, digits, `-`, `_`, `.` |
| `repo` | `{ "path": "<folder>" }` (relative to the task file) **or** `{ "git": "<url>", "commit": "<sha>" }`. A git snapshot is cloned once into the scratch folder and copied for every attempt. |
| `prompt` | the task text. A single agent and a flow get exactly this text. |
| `setupCommand` | optional; runs once in each fresh workspace before the agent (restore packages). Not timed. |
| `testCommand` | the hidden test: exit code 0 means solved. Runs in the workspace after the agent. |
| `testPatch` | optional; a patch with the hidden tests, applied with `git apply` after the agent finished and before `testCommand`, so the agent never sees the tests. |

Commands run through `cmd /c` on Windows and `bash -c` elsewhere. Comments and trailing commas are allowed in the file.

**Check the tasks before you spend money:**

```bash
dotnet run --project tools/Bench -- --tasks bench/tasks.json --validate
```

`--validate` runs the setup, applies the test patch and runs the test on the **untouched** snapshot. The test has to fail there; a test that already passes cannot tell a fix from no fix. No agent runs and nothing is spent. `--list` prints the tasks.

## Configurations

| Name | What runs |
|---|---|
| `single` | one agent in chat mode: `aiagent run "<task>" --model <id> --dir <workspace>` (tools auto-approved) |
| `<pipeline>-flow`, e.g. `bug-fix-flow`, `feature-team-flow` (also `flow:<pipeline>`) | `aiagent pipeline run "<task>" --pipeline <pipeline> --non-interactive --output json --permission FullAuto --model <id> --dir <workspace>` |

Both may edit files and run commands without asking, so the permission level is the same. The model comes from `--model` for every configuration.

Before the first flow run make the flow available to the agent, once:

```bash
aiagent pipeline copy bug-fix --to global --with-characters
```

A flow that cannot start (unknown pipeline, no API key) exits with code 3 or 4. The harness records that attempt as `error`, lists it in the report and counts it as a failure; it does not pretend the fix failed.

### Keep the comparison fair

- **Same model.** The harness passes `--model` to everything and lists the models it **saw** in the run reports. If a flow step pins another model, the report warns that the comparison is not like for like. Check the example flows' characters for pinned models before you run.
- **Same starting point.** Every attempt gets a fresh copy of the snapshot as a git repository with the snapshot committed.
- **Same text.** One prompt per task, used for every configuration.
- **One attempt at a time.** The order is seed, then task, then configuration, so a slow hour of the API spreads over all configurations. Do not run two benchmarks against the same agent home at the same time: the harness finds a single agent's usage by the run report that appeared during its run.

## Verdict, cost and time

- **Pass** = the hidden test exits with 0 on the workspace the agent left behind, whatever the agent said about itself. A flow that reports a failed step is still judged by the test.
- **Timeout** = the agent ran longer than `--timeout` (default 1800 s). Counts as a failure; the test is not run.
- **Cost and tokens** are what the agent reports: the flow report's totals, or the `run.end` entry of the chat run's audit file in `~/.aiagent/runs/`. Prices come from the agent's own price table; a model without a price is flagged "understated" in the report.
- **Time** is the wall-clock time of the agent run. Setup and the hidden test are not included.
- **Cap the spend.** `--max-budget <usd>` caps each flow attempt. A single agent has no cap, only the timeout. A full suite is `tasks x configurations x seeds` model runs: 40 tasks, 2 configurations and 3 seeds are 240 runs, and a flow run uses several characters. Run `--only` with two or three tasks and `--seeds 1` first to see what an attempt costs.

## Seeds

`--seeds 3` (default) repeats every task three times per configuration. The seed numbers the repetition and is passed to the agent as `AIAGENT_BENCH_SEED`, but the agent does not promise reproducible sampling, so the repetitions are **independent samples**, not replays. That is the point: they show how much a result moves from run to run.

## Statistics in the report

- **Pass rate (attempts)**: passed / all attempts with the Wilson 95 % interval.
- **Every other interval** is a percentile bootstrap (10 000 resamples, fixed seed, so the same data give the same report) **over tasks**. The three attempts of one task are not independent, so the unit of resampling is the task, and each task weighs the same.
- **Paired comparison**: for each task, the difference between a configuration and the first one in `--configs`, averaged over tasks. An interval that contains 0 means the data do not separate the two.

With 30 tasks the intervals are wide. That is the honest width; do not read a 5-point difference as a win when its interval spans 0.

## Never cherry-pick

- Every task and every attempt is in the report and in the CSV, including errors and timeouts.
- Results are never overwritten: starting into a folder that already has `attempts.jsonl` stops with an error. A stopped or crashed run continues with `--resume` (finished attempts are kept, the rest are run); another folder starts a new run.
- A run that did not finish, a subset chosen with `--only` and a configuration that recorded no tokens are each stated at the top of the report.
- Fix a broken task by editing the task file and starting a new run. Do not delete rows.

## All options

Run `dotnet run --project tools/Bench -- --help`.

| Option | Meaning |
|---|---|
| `--tasks <file>` | task list (default `bench/tasks.json`) |
| `--configs <a,b>` | `single`, `<pipeline>-flow`, `flow:<pipeline>` (default `single,bug-fix-flow`) |
| `--model <id>` | model for every configuration (required for a run) |
| `--provider <name>` | provider for every configuration |
| `--seeds <n>` | attempts per task and configuration (default 3) |
| `--out <dir>` | output folder (default `bench/results`) |
| `--resume` | continue a stopped run |
| `--only <ids>` | only these tasks (stated in the report) |
| `--max-budget <usd>` | cap per flow attempt |
| `--timeout <s>` / `--test-timeout <s>` | limits for the agent and for the hidden test (default 1800 / 900) |
| `--agent <path>` | the `aiagent` executable or `aiagent.dll`; default: the newest build under `src/AiCodeAgent.CLI/bin` |
| `--agent-home <dir>` | `AIAGENT_HOME` for the agent (settings, keys, run reports) |
| `--work-dir <dir>` | scratch folder (default `%TEMP%/vch-bench`) |
| `--keep-workspaces` | keep each attempt's workspace to look at what the agent did |
| `--validate` / `--list` | check the tasks / print them |

Exit codes: `0` finished, `1` stopped early (a partial report is still written), `2` bad input.

## Trying the harness without a model

The [scripted provider](testing.md) replays scenarios with no API key and no cost. Use it only to check that the plumbing works (workspace, agent start, usage, report); its numbers mean nothing. Set `AIAGENT_ENABLE_SCRIPTED=1`, add `--provider scripted --model scripted-1` and use `--seeds 1 --out bench/results/smoke`. Whether a built-in scenario applies to your task depends on its matching rules (see testing.md); with no match the scripted model only answers that it has nothing to say, and the attempt is a `fail`.

The unit tests (`dotnet test AiCodeAgent.slnx --filter "FullyQualifiedName~Bench"`) run the whole pipeline against a fake agent: workspaces, verdicts, resume, the CSV and the report.
