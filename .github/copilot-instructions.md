# Annotator

Follow `AGENTS.md`, `CONTRIBUTING.md` and `docs/parity.md`.

For local checks, use `.github/skills/annotator-local-validation/SKILL.md`.
For release questions, versioning or test packages, use
`.github/skills/annotator-release/SKILL.md`. For product demo recordings, use
`.github/skills/annotator-demo-recording/SKILL.md`.

Keep verified user-visible changes in `releases/notes.json` under
`unreleased`, then render `CHANGELOG.md` with
`python3 tools/release/release.py changelog --write`. Don't call Unreleased
changes shipped.

Both platforms share the version in `apps/macos/project.yml`; the Windows
package version is `(Major + 1).Minor.Build.0`. Ask a maintainer before
changing the version. A version change is not approval to publish, deploy,
notarize, submit to the Microsoft Store, open a winget pull request, change
update feeds or turn off `partOfReleases` in `releases/windows.json`. Those run
through the Release workflow or the maintainer tools in `docs/maintainers/`.
