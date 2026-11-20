# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Change categories: `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed`, `Security`.

## [Unreleased]

### Added

- `LICENSE`: all rights reserved (owner, 2026-11-19). No use, copying, changes or sharing, and no business or for-profit use, without the owner's written permission; the public repository and its downloads do not grant any. README says so at the top.

## [1.0.3] - 2026-11-16

### Fixed

- Copied folders could be invisible in Explorer. Robocopy gives a destination folder the attributes of its source folder, so the copy of a whole drive (`C:\` to `<Dest>\<COMPUTERNAME>\C`) arrived Hidden + System like the drive root, which Explorer hides even with *Hidden items* switched on. Found by the owner in a real-life test. Now nothing at the destination is left Hidden or System: files are copied with `/A-:SH`, and after each folder job the tool clears both attributes on every folder and file under that job's destination (Robocopy has no flag that does this for folders; checked with `/DCOPY:DT` too). When a copy is cancelled, the top folders are still cleared. A destination left hidden by an earlier version is repaired by running the same job again (nothing is recopied). Only the destination is changed, never the source; Read-only is kept.

### Added

- **One log folder per job**, asked for by the owner after a real-life test that left about 95 log files in one folder. Each job writes to `Logs\<COMPUTERNAME> <yyyy-MM-dd HHmm> <job id>\` next to the tool, and every kind of Robocopy run appends to one file there: `copy.log` (main pass of every folder job), `retry.log` (locked-file retry and *Retry failed*), `scan.log` (pre-flight scans), next to `failures.csv` and, once the job is closed, `job.json`. The CSV's *Log file* and *Log line* columns point into these combined files. A resumed job keeps its folder.
- **Logs copied to the destination**: when a run ends, after *Retry failed* and when the tool is closed, the job's log folder is copied to `<Destination>\Transfer logs\<same name>\`, so each job's logs sit with its copy. The logs are written next to the tool while copying, so a destination that is lost or full cannot stop Robocopy or lose them; if the destination cannot be reached at the end, the report says where they are. *Open log folder* opens the job's own folder.
- Options: *Make copied files and folders visible* (on by default). Switching it off copies the Hidden and System attributes as v1.0.0 to v1.0.2 did.

### Changed

- Flag set: `/A-:SH` added by default (deviation from the M1 flag set, at the owner's request; see above).

### Test findings (v1.0.3, 2026-11-15, not elevated)

- Reproduced on a generated tree whose top folder is Hidden + System: the old flags leave the destination folder Hidden + System; `/A-:SH` fixes files only; a second Robocopy run puts a folder's attributes back, so the tool clears them after every run.
- Robocopy `/UNILOG+` writes a new log as ANSI (non-Latin file names garbled); the tool now creates each log with a UTF-16 byte order mark first, after which Robocopy appends in UTF-16. Checked on a generated tree with Japanese and accented names.
- 88 unit tests: 86 pass, 2 skipped (elevated-only). New: appending and line numbering across runs, one folder per job, copying to the destination (and an unreachable one reported), the locked-file log entry found at the line the CSV names inside `retry.log`.
- UI smoke A to F passed on the visible-attributes branch (apart from two false alarms from Windows' input indicator windows, since excluded). After merging the log change only the unit tests were run; the UI smoke was not run again at the owner's request (2026-11-16), as the owner is testing on a real PC.
- The UI smoke can now run on a desktop of its own (`DTT_SMOKE_QUIET=1`): UI Automation's Invoke and SetValue moved the focus with a simulated key press, which took the keyboard of the person using the PC. Test-only; the Release exe is unchanged.
- New engine test: hidden and system files and folders arrive visible, Read-only kept, source unchanged, a second run copies nothing and stays visible, an old hidden destination is repaired, and the option off keeps the attributes.

## [1.0.2] - 2026-11-15

### Added

- Every failure now records where its error is in the Robocopy logs: the failures CSV has two new columns at the end, **Log file** (for example `<job>_retry_s1_086.log`, in the `Logs` folder) and **Log line** (the line number of the `ERROR` entry, as a text editor shows it). When an item was tried more than once, they point at the latest attempt. The report's Details column shows the same. Asked for by the owner after a real-life test, to trace each CSV row back to its log entry. The job file keeps both, so they survive a resume.

### Test findings (v1.0.2, 2026-11-15, not elevated)

- 84 unit tests: 82 pass, 2 skipped (elevated-only). The locked-file engine test now opens the named log, reads the named line and finds the `ERROR 32` entry for that file there, and checks the two CSV columns. UI smoke A to F pass.

## [1.0.1] - 2026-11-15

### Fixed

- Folder tree: a collapsed folder with exactly one loaded subfolder kept a stale tick box on that subfolder after the folder was ticked or unticked (only folders with two or more children were refreshed). The selection itself was right; only the box was wrong. Found while building v2.
- `scripts\Build-Release.ps1` stopped at `git worktree add` under Windows PowerShell 5.1, which treats git's progress messages as errors; it now checks git's exit code instead.

## [1.0.0] - 2026-11-15

First release (milestones M1 to M6). The manual tests in `MANUAL_TESTS.md` have not been run yet; use this version only on test PCs until they pass.

### Added

- Initial repository structure: README, change log, `.gitignore`, `.gitattributes`.
- Build specification (`docs/SPEC.md`), decision log (`docs/DECISIONS.md`) and working instructions for Claude Code sessions (`CLAUDE.md`), including the rule that every change is committed, pushed to GitHub and recorded here.
- M1 investigation harness (`src/DttM1Harness`, console, .NET Framework 4.8) and captured Robocopy logs (`tests/fixtures/m1`) for the M2 parser.
- M1 findings report (`docs/M1_FINDINGS.md`).

- M2 core library `src/DataTransfer.Core` (no UI): job model, Robocopy command builder, log parser, live log reader, copy engine with cancel, pause on destination loss, stop on destination full, locked-file retry, sleep blocking, and job save/load.

- Unit and integration tests (`tests/DataTransfer.Core.Tests`, NUnit, test-only NuGet packages): parser tests on the captured M1 logs, command-builder tests, and engine tests that run the real Robocopy on generated trees (cancel and resume, locked-file retry, destination loss, progress).
- Console driver `src/DttCli` (scan, run, resume, retry, show) for exercising the library without a UI.

- M3 pre-flight engine in `src/DataTransfer.Core` with ten checks (administrator and backup privilege, log folder, destination incl. a path longer than 260 characters, power, open applications, other signed-in users, source scan, cloud-only files, EFS files, free space and FAT32), each pass/warn/fail with details, acknowledgement, and a fix where the tool can act (close applications, stop OneDrive, close other users' applications).
- The tool's own read-only folder walk (`SourceWalker`): names unreadable folders, counts cloud-only and EFS files, follows the same exclusions as the Robocopy command.
- Own network connection to a UNC destination with user name and password (password in memory only).
- `DttCli preflight` console command; stand-in application `tests/DttDummyApp` so tests never close a real program.

- M4 WinForms tool `src/DataTransferTool`: a single exe (library source compiled in, no extra DLL, only .NET Framework references), manifest with requireAdministrator and longPathAware. Screens: Select (lazy folder tree with tri-state check boxes, User data preset, tick by path, Options), Destination (local/USB folder or network share with user name and password, Test destination, layout preview), Pre-flight (all checks with pass/warn/fail, details, fix and acknowledge; Start copy enabled only when everything passed or was acknowledged), Run (progress, files, data, current folder, speed, time left, two-click Cancel, no dialogs).
- Options dialog: editable file exclusions, profile-folder exclusions, any-depth folder exclusions, known applications, close timeout, threads, retries, wait, backup mode, cloud-only exclusion.
- Folder selection model `FolderTree` and `UserDataPreset` in the library, with unit tests.
- Automated UI test `tests/DttUiSmoke`: drives the real window through UI Automation (SmokeTest build with an asInvoker manifest), takes screenshots, verifies the copy; it never presses a fix button, never brings the window to the front and never types into the desktop.

- M5 Report screen: outcome, copied / skipped / failed per folder job and in total, the list of files and folders not copied with a plain reason and the Windows message, Open CSV, Save CSV as, Open log folder, Retry failed, and Continue copy after a cancel or a full destination.
- Failures CSV (`failures_<COMPUTERNAME>_<job>.csv` in the log folder): source and destination path, type, reason, error code, Windows message, size, attempts, time. UTF-8 with BOM for Excel; cells that start like a formula are neutralised.
- Resume on launch: an unfinished job beside the exe is described and offered for resume; resuming restores the selection and destination and runs the pre-flight checks again. *Start a new job* moves the old job file to the log folder. A finished job is moved to the log folder on close.
- *Save credentials for resume* on the Destination screen (network share with a user name only): encrypted with DPAPI, CurrentUser scope, bound to the job, in `DataTransferJob.credentials` beside the job. Deleted when the job finishes, when a new job is started, and on close when the box is not ticked. Without saved credentials a resumed network job asks for the password.

- M6 manual test plan `MANUAL_TESTS.md`: 19 tests for what cannot be automated here (UAC prompt, clean Windows 10 and 11, domain PC, network share with credentials, saved credentials across accounts and PCs, USB pulled mid-run, network drop, real OneDrive profile, full destination, FAT32 and exFAT, second signed-in user, open applications, EFS, battery and sleep blocking, read-only stick, resume after a crash, a 200 GB job, high DPI), each with setup, steps and expected results, and a results table.
- `tests/manual/New-DttTestData.ps1`: generates safe test data on the PC under test (Office-like files, pictures, large files, a path over 400 characters, accented and Japanese names, files the default exclusions skip, optionally a 4.2 GB file and folders in the test user's profile); `-Remove` deletes it again.

### Changed

- `scripts\Build-Release.ps1`: builds a tag or branch into `release\v1\` or `release\v2\` (not committed) so both versions are available side by side.
- The window title shows the version (`Data Transfer Tool v1.0.0 - <PC>`) so v1 and v2 can be told apart. Exe version set to 1.0.0.
- Flag set after M1 testing (approved by the owner 2026-11-14): add `/BYTES` (exact byte counts; default output prints rounded sizes such as `1.7 m`), add `/FP` (same file-line shape with and without `/MT`) and add `/XA:O` (excludes cloud-only OneDrive files, verified on Windows 11 build 26200). No spec flag removed.

### Test findings (M1, 2026-11-14, not elevated and elevated)

- `/B` makes Robocopy exit 16 when the process is not elevated. Elevated run (same day): `/B` copies ACL-denied files and folders, does not get past locked files, and stamps killed partial files 1980-01-02.
- Long paths (source 637, destination 943 characters), skip-if-identical, `/FFT`, `/XJ`, `/COPY:DAT`, `/DCOPY:DAT` and resume after a killed run all verified by content hash.
- Cloud-only OneDrive files carry both OFFLINE and RECALL_ON_DATA_ACCESS as Robocopy sees them; `/XA:O` excludes them without downloading. A .NET Framework process sees only RECALL_ON_DATA_ACCESS, so our own scan must test that bit.
- `/XD AppData` excludes every folder named AppData at any depth.
- With `/MT` the log falls behind while a large file is copying, so byte progress needs a second source.

### Test findings (M2, 2026-11-14, not elevated)

- A `/L` scan does not name a folder it cannot read, with or without `/MT`; it only counts it in the summary. The scan result therefore carries a count of unreadable folders, not their paths.
- Destination loss was tested by removing a SUBST drive mapping mid-run: the engine stops Robocopy, waits, relaunches when the destination returns, and the tree verifies by hash with no failures left over.
- Elevated run (owner, same day) on a virtual disk: both tests pass. Destination full: Robocopy does not stop by itself (100 `ERROR 112` lines in under 2 s), the engine stops it and reports. Volume detached mid-run: errors 433, 21 and a negative driver code; the engine paused and finished after the volume returned, all files verified. Logs kept as fixtures in `tests/fixtures/m2`.

### Fixed

- Parser now reads negative Robocopy error codes and treats error 433 (device does not exist) as destination loss; both were found in the real volume-removal log.

### Test findings (M3, 2026-11-14, not elevated)

- The walk and Robocopy's scan agree file for file and byte for byte on generated trees (total and to-copy).
- Cloud-only check verified on generated files in a real OneDrive folder: 3 cloud-only files found by the walk, Robocopy skipped the same 3, nothing was downloaded.
- OneDrive's own `/shutdown` helper never exits when started without the shell; it is now started through the shell and any leftover OneDrive process is ended. Restarting OneDrive through the shell also stops it inheriting the tool's handles.
- Closing applications verified with the stand-in app: one closed on request, one that refused was force-closed after the timeout.
- Not testable here: UNC destination with real credentials, a second signed-in user, EFS files (Windows Home), battery power, FAT32 destination. These go to the M6 manual tests.

### Test findings (M4, 2026-11-14, not elevated)

- The Release exe is refused by Windows without elevation (error 740), confirming requireAdministrator.
- UI test passes all 97 checks: preset, tick and untick, options, destination test, pre-flight acknowledge flow, full copy verified by hash (unticked subfolder and default exclusions absent), two-click Cancel, and closing the window during a copy (cancels and exits in under a second, no dialog, job saved as Cancelled).
- Found and fixed: setting a split panel's divider while the panel is still small throws, and WinForms swallows exceptions from a Load event on 64-bit Windows, so the Pre-flight screen silently failed to open. The divider is now set once the panel has its real size.
- The tool window answers in under 1 ms during a 40,000-file copy.
- The "User data" preset also ticks `OneDrive*\Desktop`, `Documents` and `Pictures` inside a profile, where OneDrive folder backup moves them.

### Test findings (M5, 2026-11-15, not elevated)

- UI test extended to six scenarios, 182 checks, all passing: report after a clean copy; continue after Cancel (all 40,000 files); resume offered at the next launch after closing the window mid-copy (all 40,000 files); a file held open is reported with reason and error 32, appears in the CSV, and is copied by Retry failed once released (identical by hash); resume of a network job with saved credentials (used without prompting, password not shown in the window) and without them (asks for the password).
- The test password did not appear in any file the tool wrote (job file, logs, CSV).
- After an interruption the copied/skipped split is approximate (flagged on screen): a resumed 40,000-file job reported 39,998 copied and 2 skipped. The files at the destination were complete.
- Not testable here: saving credentials against a real share, and reading them back as a different Windows user or on another PC (DPAPI should refuse; the tool then asks for the password).
