# Decisions and deviations

Newest first. One entry per decision: date, what was decided, why, who decided.

## 2026-11-15 — Windows testing runs on the owner's Windows PC

M1 requires testing Robocopy "on this machine". The session that set up this
repository ran in a Linux cloud container with no Windows, Robocopy or .NET
Framework, so no test has been run yet. The owner chose to run builds and tests
on their own Windows PC, in a Claude Code session started locally in a clone of
this repository. Alternative considered and not chosen: GitHub Actions Windows
runners (Windows Server, no real OneDrive).

Status at this point: no application or harness code exists. M1 has not started.
