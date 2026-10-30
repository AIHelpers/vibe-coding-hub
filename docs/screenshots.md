# Regenerating the documentation screenshots

One command renders every documentation screenshot, in both themes, at 1600 x 900, with no API key and no cost:

```bash
dotnet run --project tools/Screenshots -- --out docs/content-kit/images
```

The tool starts the **real application** (its DI container, main window and view models) on Avalonia's headless platform with Skia drawing, points it at a private scratch home and a fresh copy of the TodoApp sample, and drives it with the [scripted provider](testing.md). Every image is a frame of the actual UI, not a mock-up.

## What you get

| Folder | Content |
|---|---|
| `docs/content-kit/images/NN-name.png` | the 22 screenshots in the **dark** theme |
| `docs/content-kit/images/light/NN-name.png` | the same 22 in the **light** theme |

`dotnet run --project tools/Screenshots -- --list` prints the file names. They are defined in one table, `tools/Screenshots/SceneCatalog.cs`; if the articles use another name, rename it there and nowhere else.

| # | File | Shows |
|---|---|---|
| 01 | `01-main-window.png` | main window, sample project open, guided checklist |
| 02 | `02-sample-task.png` | the first task typed into the chat box |
| 03 | `03-explorer.png` | file explorer next to the chat |
| 04 | `04-screens-mode.png` | Screens mode (one panel fills the window) |
| 05 | `05-approval.png` | approval dialog with the diff of the edit |
| 06 | `06-result.png` | finished run: tool cards, verification, summary |
| 07 | `07-diff.png` | side-by-side diff |
| 08 | `08-commit.png` | staged change and commit message |
| 09 | `09-project-knowledge.png` | AGENTS.md |
| 10 | `10-skills.png` | a skill |
| 11 | `11-character.png` | a character with inherited skills |
| 12 | `12-add-skill.png` | Add skill panel |
| 13 | `13-flow-canvas.png` | the feature-team flow on the canvas |
| 14 | `14-inspector.png` | a flow step with its inspector |
| 15 | `15-flow-running.png` | Backend and Model running in parallel |
| 16 | `16-flow-loop.png` | the review sends the work back |
| 17 | `17-flow-complete.png` | flow finished, release notes |
| 18 | `18-settings.png` | Settings |
| 19 | `19-command-palette.png` | command palette |
| 20 | `20-checkpoints.png` | checkpoints of the run |
| 21 | `21-background-tasks.png` | background tasks |
| 22 | `22-light-theme.png` | main window in the light theme |

## Options

| Option | Meaning |
|---|---|
| `--out <dir>` | output folder of the dark set (default `docs/content-kit/images`); the light set goes to `<dir>/light` |
| `--theme dark\|light\|both` | which set to render (default both; each theme runs in its own process) |
| `--only 5,approval,18` | only these scenes (number, name or file name) |
| `--list` | print the scenes and exit |
| `--check` | render everything **twice** and compare the two runs (see below) |
| `--compare <a> <b>` | compare two folders of PNG files |
| `--threshold <pct>` | share of differing pixels that still counts as equal (default `0.5`) |
| `--fast-checks` | use instant stand-in build/test commands in the sample (offline, no NuGet restore) |
| `--delay-scale <x>` | multiplier of the scripted provider's delays (default `3`; larger = easier to catch mid-run states) |
| `--timeout <s>` | time limit of one scene (default `120`) |
| `--repo <dir>` | repository root, when the tool runs from outside the repository |
| `--keep-workspace` | keep the scratch folder (always kept after a failure) |

Exit code: `0` when every image was produced, `1` otherwise. A failed scene does not stop the others; the tool prints which scenes failed and saves what the window looked like at the failure to `%TEMP%/vch-screenshots/<theme>/failures/`.

## Is it reproducible?

```bash
dotnet run --project tools/Screenshots -- --check
```

renders the whole set twice into temporary folders and compares each pair of images pixel by pixel. It passes when every image is identical or differs by **less than 0.5 %** of its pixels (`--threshold`). The same comparison is available for any two folders with `--compare`.

What the tool fixes so that two runs agree:

- a private `AIAGENT_HOME`, a fresh sample project at a fixed path (`%TEMP%/vch-screenshots/<theme>/projects/TodoApp`), a seeded config (no wizard, no update check, activity bar labels on);
- git without your own configuration (`GIT_CONFIG_GLOBAL`, `GIT_CONFIG_NOSYSTEM`), with a fixed identity and fixed author/committer dates, so commit ids and times do not move;
- the scripted provider (the same text and tool calls every time), English UI and culture, the embedded Inter font;
- one window size, one render scale.

What can still differ between two runs, by a few pixels each: clock times the app prints for the current moment (chat messages, tool durations such as `0.3 s`) and the position of a spinner. That is why the criterion is a pixel budget and not byte equality.

## Sample checks: real or stand-in

The `result` scene needs the agent's `verify_changes` to pass. By default it runs the sample's real commands (`dotnet build TodoApp.slnx`, `dotnet test TodoApp.slnx`), so the screenshots show exactly what users see. That needs the .NET SDK and, on the first run, access to NuGet (to restore xunit). Pass `--fast-checks` to use instant stand-in commands (`echo ...`) instead: the images then show those commands in the verification card, so use it for CI and drafts, not for published images.

## Not covered yet

- Moving images (GIF/MP4 of the flow run). The frame capture is in place (`SceneContext.CaptureAsync`); encoding frames with ffmpeg is the optional part of the story and is not implemented.
- Real-provider screenshots (for example Settings with a real provider). The scenes use the scripted provider; the Settings page therefore lists it. Retake `18-settings.png` by hand for publication (E8-S4).

## When a scene fails

The usual cause is a UI change: the scene waits for a control or state that no longer appears, and says so ("Timed out after 110 s waiting for ..."). Fix the driver in `tools/Screenshots/Scenes.cs`; drivers use only what a user can do (activity bar entries, buttons, typing a task).
