# Example skills, characters and pipeline

Copy what you want into your global (`~/.aiagent/...`) or project (`<repo>/.aiagent/...`) folders:

```bash
# skills
cp -r examples/skills/* ~/.aiagent/skills/
# characters
mkdir -p ~/.aiagent/characters && cp examples/characters/*.md ~/.aiagent/characters/
# pipeline that casts the characters into stages
mkdir -p ~/.aiagent/pipelines && cp examples/pipelines/team-sdlc.json ~/.aiagent/pipelines/

aiagent skills doctor
aiagent pipeline run "Add pagination to the /users endpoint" --pipeline team-sdlc
```

| Character | Base role | Skills | Pinned |
|---|---|---|---|
| `alex-architect` 🧭 | planner | adr-writer | team-conventions |
| `sam-developer` 🛠 | implementer | release-notes | team-conventions |
| `quinn-qa` 🔍 | tester | code-review-checklist | team-conventions |
