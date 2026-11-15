# Data Transfer Tool — working instructions

Portable Windows data-export tool (C# WinForms, .NET Framework 4.8, Robocopy as
the copy engine). The full specification is `docs/SPEC.md`; read it before any
work. Decisions and deviations from it are in `docs/DECISIONS.md`.

## How to work

- Work in the milestones defined in `docs/SPEC.md`. After each milestone, stop,
  show what was built and how it was verified, and wait for the owner's go-ahead.
- Development and testing happen on a Windows PC. If the current session is not
  running on Windows, say so before doing anything else.
- Test only with generated folder trees, never against real user data.
- If something in the spec does not work as described, stop and tell the owner
  instead of working around it.
- Copy logic lives in a library separate from the UI.

## Everything is recorded on GitHub

The owner wants the full history on GitHub (`origin`), not only on the local PC.

- Do each milestone on its own branch (`m1-investigation`, `m2-core-library`, ...).
- Commit in small, working steps and push the branch after every commit. Never
  end a working session with unpushed commits; check `git status` before stopping.
- When the owner gives the go-ahead for a milestone, merge its branch into
  `main`, push, and tag it (`m1`, `m2`, ...).
- If a push fails (no GitHub sign-in on this PC, no network), tell the owner
  right away; do not carry on silently with local-only commits.

## Change log

- Update `CHANGELOG.md` in the same commit as the change it describes, under
  `[Unreleased]`, using the Keep a Changelog categories.
- Record test findings and flag changes there too, with the reason.
- At a release, move `[Unreleased]` to a dated version section and tag
  `vMAJOR.MINOR.PATCH`.
- Captured Robocopy logs used as test fixtures are committed under
  `tests/fixtures/`. Built binaries are not committed (see `.gitignore`).

## Never commit

Credentials, real user data, real computer or user names from tested PCs, or
logs that contain them.
