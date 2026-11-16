# Independent review

After a task, a second model can read the diff and report problems. Its findings appear in the Evidence Pack next to the facts.

## How to use it

On the evidence card at the end of a task, press **Review with a second model**. The button appears when the task changed files and
a provider is configured. When the review is done, the card shows a one-line result and the full pack gets an
**Independent review** section. **Copy for PR body** includes it. The Review Queue entry for the task shows it too.

## Who reviews

The reviewer is picked in this order:

1. The model fixed in settings (`reviewer.provider` and `reviewer.model` in the configuration file), if any.
2. A configured provider other than the one that wrote the change. A provider counts as configured when it has an API key or runs on this machine.
3. Another model of the same provider.
4. The same model. The pack then says plainly that the review is **not independent**.

Each review records which model gave it and how independent it was.

## What the reviewer sees

- The task, the acceptance criteria (if the agent declared any), a few facts the pack already proved (test, build and lint results, gaps).
- The diff of every changed file, riskiest files first, up to about 60,000 characters. Files that do not fit are listed in the pack as
  **not shown to the reviewer**.
- No tools. It cannot read other files, run commands or change anything. It cannot be steered by files it was not shown.

The diff, the task and the criteria are passed as data, and the prompt tells the reviewer to ignore instructions inside them. This
reduces prompt injection through code comments; it does not remove the risk. Treat the review as an opinion.

## What it is, and what it is not

- It is a second opinion: a verdict (approve, concerns, reject), findings with severity, file and line, and a note per criterion.
- It is **not proof.** It never changes a failing test, an unproven criterion or the "Not proven" list. A review that says "approve"
  does not make an unproven task proven.
- Findings that name a file the diff did not contain lose the file and line. At most 12 findings are kept.
- Text written by the model is shown with Markdown links, images and HTML disabled, so a review cannot load anything from the web.

## Cost and privacy

The diff is sent to the reviewer's provider. With a cloud provider this means your code leaves the machine, as with any other model
call. Use a local provider as the reviewer to keep it local. The review's cost is recorded in the review, not added to the task's cost.

## Storage

The review is stored next to the run: `<run>.review.json` beside a chat run's audit file. It is a separate file because the audit
trail is sealed when the run ends. A rebuilt pack picks it up again. Deleting the file removes the review.

## Limits

- It runs on demand, from the card. There is no automatic review and no flow step yet.
- Only chat tasks have the button. Flow runs have no button yet.
- The reviewer sees only the recorded diffs, not the surrounding code.
- A review costs one model request (two when the first answer was not valid JSON).
