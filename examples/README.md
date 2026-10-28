# Example skills, characters and pipeline

Copy what you want into your global (`~/.aiagent/...`) or project (`<repo>/.aiagent/...`) folders:

```bash
# skills (the shared skill library)
cp -r examples/skills/* ~/.aiagent/skills/
# characters
mkdir -p ~/.aiagent/characters && cp examples/characters/*.md ~/.aiagent/characters/
# a pipeline and a flow that cast the characters into stages
mkdir -p ~/.aiagent/pipelines && cp examples/pipelines/*.json ~/.aiagent/pipelines/

aiagent skills doctor
aiagent pipeline run "Add pagination to the /users endpoint" --pipeline team-sdlc
```

**Share a flow with your team.** Flows load from three places: the built-in ones, `~/.aiagent/pipelines`
(global, only you) and `<repo>/.aiagent/pipelines` (project, committed next to the code it works on).
When a name exists in more than one place the project flow wins, then the global one (the same rule as
skills and characters).

```bash
cd my-repo
aiagent pipeline copy feature-team --to project   # writes .aiagent/pipelines/feature-team.json, then git add it
aiagent pipeline list                              # the SCOPE column shows project / global / builtin
```

Developers share the `software-developer` **template** (role, persona, common skills) and each one
adds its **own skills** from the library:

| Character | Extends | Own skills | Effective skills (pinned: team-conventions) |
|---|---|---|---|
| `software-developer` 💻 *(template)* | — (implementer) | release-notes | release-notes |
| `sam-developer` 🛠 | software-developer | — | release-notes |
| `dana-dotnet` 🟣 | software-developer | csharp, clean-architecture | release-notes, csharp, clean-architecture |
| `mia-ml` 🧪 | software-developer | python, machine-learning | python, machine-learning *(drops release-notes)* |
| `alex-architect` 🧭 | — (planner) | adr-writer | adr-writer |
| `quinn-qa` 🔍 | — (tester) | code-review-checklist | code-review-checklist |

Give a character another skill with **＋ Add skill** in its profile (Project Knowledge → Characters),
or from the command line — it picks the skill from the library, or creates it when it does not exist yet:

```bash
aiagent characters add-skill dana-dotnet ef-core --description "Use EF Core migrations and queries correctly"
aiagent characters show dana-dotnet     # skills grouped by source: template / own
aiagent chat --character mia-ml
```

`pipelines/feature-team.json` is a **flow**: Alex designs, then Dana (backend) and Mia (model) work in
parallel, Quinn reviews and sends the backend back on rejection (up to 2 times), and Sam writes the
release notes once Quinn approves. Open it in the desktop app's **🔀 Flows** panel, or:

```bash
aiagent pipeline show feature-team
aiagent pipeline run "Add churn prediction to the customer API" --pipeline feature-team
```

In the **Flows** panel the pipeline list shows each flow's scope, and **Save** asks whether the flow is
*Project (shared via git)* or *Global (only me)*; Project is the default when the working directory is a git repository.
