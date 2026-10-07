# Example skills, characters and pipeline

Copy what you want into your global (`~/.aiagent/...`) or project (`<repo>/.aiagent/...`) folders:

```bash
# skills (the shared skill library)
cp -r examples/skills/* ~/.aiagent/skills/
# characters
mkdir -p ~/.aiagent/characters && cp examples/characters/*.md ~/.aiagent/characters/
# pipeline that casts the characters into stages
mkdir -p ~/.aiagent/pipelines && cp examples/pipelines/team-sdlc.json ~/.aiagent/pipelines/

aiagent skills doctor
aiagent pipeline run "Add pagination to the /users endpoint" --pipeline team-sdlc
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
