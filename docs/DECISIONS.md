# Decisions and deviations

Newest first. One entry per decision: date, what was decided, why, who decided.

## 2026-11-14 — M1 run on the owner's Windows PC; proposals awaiting approval

M1 was run in a local, non-elevated session on Windows 11. Results are in
`docs/M1_FINDINGS.md`. Proposed, not yet approved by the owner:

- Add `/BYTES`, `/FP` and `/XA:O` to the flag set.
- Pass the AppData exclusion as a full path under each profile instead of a
  bare name (a bare name also excludes unrelated folders called AppData).
- Take byte progress from destination free space, counts and errors from the log.
- Use `\\?\` paths in our own file code; do not rely on `longPathAware` alone.

Open: `/B` has not been tested elevated. Pushes to GitHub failed from this PC
(no stored sign-in); commits are local until the owner signs in.
## 2026-11-15 — Windows testing runs on the owner's Windows PC

M1 requires testing Robocopy "on this machine". The session that set up this
repository ran in a Linux cloud container with no Windows, Robocopy or .NET
Framework, so no test has been run yet. The owner chose to run builds and tests
on their own Windows PC, in a Claude Code session started locally in a clone of
this repository. Alternative considered and not chosen: GitHub Actions Windows
runners (Windows Server, no real OneDrive).

Status at this point: no application or harness code exists. M1 has not started.

