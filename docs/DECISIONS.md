# Decisions and deviations

Newest first. One entry per decision: date, what was decided, why, who decided.

> Note (2026-11-14): the entries from "Owner approved the M1 proposals" to
> "Owner confirmed two M4 choices" were written to this file on the dates shown
> but did not reach it at the time, because the scripted edits silently matched
> nothing. They were reconstructed from the session record and the change log
> and added in one commit.

## 2026-11-15 — M6: what remains before release (for owner review)

- The manual tests in `MANUAL_TESTS.md` have not been run. Until they pass, the
  tool should be used only on test PCs.
- Proposal: when the manual tests pass, release the tool as `v1.0.0`: move
  `[Unreleased]` in the change log to a dated `1.0.0` section, set the exe
  version to 1.0.0 and tag `v1.0.0`. Any failing test is fixed first on its own
  branch.
- The exe is not code-signed. Signing it would avoid SmartScreen warnings when
  the exe is downloaded rather than copied from a stick; it needs a code-signing
  certificate, which is the owner's decision.
- Test results go into `docs/test-results/` without real user, computer or
  share names.

## 2026-11-15 — Owner approved M5

The owner gave the go-ahead to merge M5 and start M6, keeping the M5 choices
below (including running the pre-flight checks again on resume). M5 merged to
`main` and tagged `m5`.

## 2026-11-15 — M5 design choices

- **Report** is a fifth screen after Run. It shows a Continue copy button when
  the job did not finish (cancelled, destination full), and Retry failed when
  it finished with failures. Retry failed runs immediately, without the
  locked-file wait.
- **Failures CSV** is written to the log folder automatically after every run
  and retry (so it exists even if nobody presses a button), and removed when
  nothing is failing any more. *Save CSV as* writes a copy elsewhere.
- **Resume runs the pre-flight checks again**, because open applications, free
  space or the destination may have changed since the job was interrupted.
  Folders already finished are skipped; the folder that was interrupted is run
  again and Robocopy skips its identical files.
- **Start a new job** keeps the old job as a record: its file is moved to the
  log folder and its saved credentials are deleted.
- **Saved credentials** are bound to the Windows account and PC (DPAPI
  CurrentUser) and to the job ID. A job resumed by another technician account
  or on another PC simply asks for the password again.
- **When credentials are deleted:** at the start of a copy if the box is not
  ticked, when the job finishes, when a new job is started, and when the tool
  closes unless the job can still be resumed and the box was ticked.

## 2026-11-15 — Owner approved M4

The owner gave the go-ahead to merge M4 and start M5. M4 merged to `main` and
tagged `m4`.

## 2026-11-14 — Owner confirmed two M4 choices

- A folder whose subfolders are all ticked by hand, but which was not ticked
  itself, shows mixed and its loose files are not copied. Clicking the folder
  ticks everything.
- The User data preset also ticks `OneDrive*\Desktop`, `Documents` and
  `Pictures` inside each profile.

Decided by the owner.

## 2026-11-14 — M4 design choices

- **Selection model.** Every folder has its own tick. Ticking a folder ticks
  everything below it; a folder shows the mixed box when part of it is ticked.
  A ticked folder whose parent is not ticked is one Robocopy job, and unticked
  folders inside it become `/XD`. A ticked folder inside an unticked one is its
  own job.
- **User data preset** ticks Desktop, Documents, Pictures and Downloads of every
  profile except Public and Default, plus the OneDrive backup folders above,
  because OneDrive folder backup moves the real folders there. Junctions (such
  as `My Documents`) are not shown in the tree.
- **Cancel without a dialog**: Cancel needs two clicks within 5 seconds.
  Closing the window during a copy cancels it and closes the tool, without
  asking.
- **Tick by path** box next to the tree, so a deep folder can be ticked without
  expanding the tree by hand.
- **Report screen** is M5. Until then the Run screen shows the totals and lists
  failed files in its activity list.
- **SmokeTest build configuration** produces the same exe with an asInvoker
  manifest, used only by the automated UI test. The Release build always
  requires administrator.

## 2026-11-14 — Owner approved M3

The owner accepted the M3 choices below: blocking failures, cloud-only files as
an acknowledged warning, and no special handling when the elevated account is a
different user from the signed-in one (OneDrive restart). M3 merged to `main`
and tagged `m3`.

## 2026-11-14 — M3 design choices

- **Blocking failures cannot be acknowledged.** The spec says Run stays disabled
  until all checks pass or are acknowledged. Four failures make the job
  impossible, so acknowledging them is not offered: not elevated while backup
  mode is on, log folder not writable, destination not reachable or writable,
  and a source scan that cannot run. Everything else (warnings, not enough free
  space, FAT32 with a file of 4 GB or more, long paths refused by the
  destination) can be acknowledged.
- **Cloud-only files are a warning that must be acknowledged**, because the
  export will not contain them.
- **Safety net for `/XA:O`.** If Robocopy's scan wants to copy more files than
  the tool's walk found to be local, the cloud check fails instead of letting
  Robocopy download everything (the open Windows 10 question from M1).
- **Checks only look.** Applications are closed only through the fix button:
  windows are asked to close, then whatever is left after the timeout (default
  20 s, editable) is ended. Other users' applications cannot be asked and are
  ended directly.
- **OneDrive** is stopped with its own shutdown command and started again after
  the job, through Explorer when the tool is elevated so it does not run as
  administrator.
- **Known applications list and timeout** are saved with the job and editable.

## 2026-11-14 — Owner approved M2; unreadable folders are named by the tool

The owner accepted the M2 choices below. For the pre-flight scan the tool does
its own folder walk to name unreadable folders (Robocopy's scan only counts
them); the same walk counts cloud-only and EFS files. M2 merged to `main` and
tagged `m2`.

## 2026-11-14 — M2 design choices

- **Pause on destination loss = stop and relaunch.** Robocopy cannot be paused
  safely when its destination vanishes, so the engine stops it, polls the
  destination every few seconds and starts the same job again when it is back.
  M1 showed a relaunch skips finished files and recopies partial ones.
- **Locked-file retry is targeted.** After the main pass only the locked files
  are retried (grouped by folder), not the whole tree.
- **False failures are filtered.** A file that fails once and succeeds on
  Robocopy's own retry still leaves an error line; the engine checks the
  destination before reporting a failure.
- **Counts after an interruption are approximate.** A stopped Robocopy writes
  no summary, so copied counts for that pass are estimated and flagged.
- **Robocopy cannot outlive the tool.** Child processes are placed in a job
  object that Windows closes when the tool exits or crashes.
- **Single exe.** The library is a separate DLL during development so it can be
  unit-tested; the tool compiles the same source files into its one exe (done
  in M4).
- **Test packages.** NUnit is restored from nuget.org for the test project only
  (`tests/NuGet.config`); nothing from NuGet ships in the tool.

## 2026-11-14 — Owner approved the M1 proposals

After reviewing `docs/M1_FINDINGS.md` the owner approved:

- Flag set gains `/BYTES`, `/FP` and `/XA:O`. Working set:
  `/E /B /R:1 /W:2 /XJ /COPY:DAT /DCOPY:DAT /FFT /MT:8 /NP /BYTES /FP /XA:O /UNILOG`.
- AppData is excluded by full path under each user profile, not by bare name.
- Byte progress and ETA come from destination free space; file counts, current
  folder and errors come from the Robocopy log.

The elevated harness run closed the open `/B` question. M1 merged to `main` and
tagged `m1`.

## 2026-11-14 — M1 run on the owner's Windows PC

M1 was run on Windows 11, not elevated and then elevated. Results are in
`docs/M1_FINDINGS.md`. Proposed at the time (approved above):

- Add `/BYTES`, `/FP` and `/XA:O` to the flag set.
- Pass the AppData exclusion as a full path under each profile instead of a
  bare name (a bare name also excludes unrelated folders called AppData).
- Take byte progress from destination free space, counts and errors from the log.
- Use `\\?\` paths in our own file code; do not rely on `longPathAware` alone.

## 2026-11-15 — Windows testing runs on the owner's Windows PC

M1 requires testing Robocopy "on this machine". The session that set up this
repository ran in a Linux cloud container with no Windows, Robocopy or .NET
Framework, so no test has been run yet. The owner chose to run builds and tests
on their own Windows PC, in a Claude Code session started locally in a clone of
this repository. Alternative considered and not chosen: GitHub Actions Windows
runners (Windows Server, no real OneDrive).

Status at this point: no application or harness code exists. M1 has not started.
