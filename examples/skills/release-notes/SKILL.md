---
name: release-notes
description: Draft release notes from the commits and merged PRs since the last tag
tags: [docs, git, release]
version: 1
allowed-tools: [git, read_file, write_file]
---
# Release notes

1. Find the last tag: `git describe --tags --abbrev=0`. If there is none, use the first commit.
2. List changes since then: `git log <tag>..HEAD --merges --pretty=format:"%s"` plus non-merge commits on the main branch.
3. Group entries under **Features**, **Fixes**, **Improvements**, **Breaking changes** (put breaking changes first when present).
4. Rewrite each entry for users: what changed and why it matters, not how it was implemented. Drop pure refactors and CI noise.
5. Write the notes to `CHANGELOG.md` under a new version heading (ask for the version number if it is not obvious) and show the result.
