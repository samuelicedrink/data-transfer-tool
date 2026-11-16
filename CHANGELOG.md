# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Change categories: `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed`, `Security`.

## [Unreleased]

### Added

- v2 started: `docs/SPEC_V2.md` describes the Select-screen redesign (user picker with access level; Modified, Size, Relevant and Advice columns) and its milestones V2-M1 to V2-M4. Exe version 2.0.0-alpha.
- Core: `UserDirectory` lists the people who have signed in on this PC (profile list; system profiles and profiles whose folder is gone left out) with account name, last sign-in (profile load time, else `NTUSER.DAT` time), signed in now (Remote Desktop session list) and access level: Local admin (direct, or via a named group such as a domain group), Standard user, or Access unknown with the reason (domain not reachable, Microsoft Entra groups offline, Administrators group unreadable). Membership is read with the AuthZ API; nothing is guessed (V2-M1).
- Select step: **User** picker at the top left ("All users" first, then signed-in users first and most recent sign-in next), each entry with its access level; the line beside it shows access and sign-in, the tooltip the profile folder. Picking a user ticks nothing; it only sets whose data the relevance estimate is for (owner, 2026-11-15). *Tick user data* still ticks every profile, as in v1. A SID is never shown: a group without a name reads "a Microsoft Entra group" or "a group whose name cannot be looked up". The list loads in the background. The picked user is saved in the job as a SID only (V2-M1).
- UI smoke scenario G: user list loads, current user listed with the access level Windows reports, picking a user ticks nothing, no SID on screen, *Tick user data* still covers every profile, back to All users. Combo items are read with `CB_GETLBTEXT` so no dropdown opens on the desktop.
- Core: `FolderScanner` works out each folder's size, file count and newest modified date in the background, for the v2 Modified and Size columns. The newest request is served first and interrupts a long scan (which carries on afterwards); results are kept so subfolders are instant. Junctions are not followed (as `/XJ`), cloud-only files are counted and marked, folders that cannot be listed are counted so the size reads as a minimum. `ListFiles` lists a folder's own files (sorted, capped). Read-only; no file is opened (V2-M2).
- Select step: the folder tree is a tree-grid with **Modified** (newest file inside a folder) and **Size** (everything inside) columns, filled by the background scan; rows show `…` until their numbers arrive and the screen never waits for them. Opening a folder also lists its files (first 1,000 by name; a line counts the rest) with their own date and size. Files have a greyed box that follows their folder: they are copied or left out with it (Space on a file says so). A line under the tree gives the selected item's numbers, cloud-only files and unreadable folders (size then shown with `+`). It is the native tree view with the names and columns drawn by the tool, so keyboard use, tick boxes and UI Automation work as in v1 (V2-M2).
- Files have tick boxes of their own (owner, 2026-11-15). An unticked file in a ticked folder is left out with `/XF` and its full path (checked first: Robocopy then skips only that file, not others of the same name). A file ticked in an unticked folder becomes a job of its own that copies only the ticked files of that folder, without subfolders. A folder with files ticked differently shows the mixed box; ticking or unticking a folder resets its files. The job file keeps `ExcludedFiles` and `Files` per job, so resume restores the ticks. The selection list and the report say `(without 1 subfolder and 2 files)` or `(3 files only)`. A file that matches a default exclusion (such as `~$*`) says so in the line under the tree, because it is not copied even when ticked (V2-M3).
- UI smoke scenario H: folder line matches a walk of the generated tree (file count, newest date), files listed under their folder with size and date, Space unticks and ticks a file, a single file ticked in an unticked folder becomes its own job, subfolder numbers instant, a generated folder of 1,200 files lists 1,000 and counts all.

### Test findings (V2-M1, 2026-11-15, not elevated)

- 65 unit tests pass (10 new: access level via fakes for direct admin, domain group, unreachable domain, Microsoft Entra offline, Everyone in Administrators, unreadable Administrators, sessions and order, deleted account; plus two read-only checks on this PC against `whoami /groups`). UI smoke A to G pass.

### Test findings (V2-M2, 2026-11-15, not elevated)

- 99 unit tests: 97 pass, 2 skipped (the existing elevated-only tests). 5 new for the folder scan on generated trees: size and newest date with a junction left out, an unreadable folder counted (denied with `icacls`, removed afterwards), a parent requested while its child is being scanned, a newer request overtaking a long scan, and the file listing (sorted, capped, no subfolders). UI smoke A to H pass.
- Screenshot review: a name containing `&` was cut short because the tree view measures it narrower than it is drawn; names are now measured by the tool.

### Changed

- Select step: ticking a profile (or a folder above it) leaves that profile's `AppData` unticked in the tree, because the copy leaves it out (Options, profile-level exclusions). Before, the tree showed it ticked, so an app-settings folder ticked inside it looked copied but was not. Now a folder such as `AppData\Roaming\Adobe` can be ticked on its own and becomes a job of its own that is copied. A folder left out by this rule does not make the profile show as mixed (V2-M3).
- v2 keeps its job and credential files under `%ProgramData%\DataTransferTool\v2` when the exe's folder is read-only, so v1 and v2 never resume each other's job (V2-M1).

### Fixed

- Folder tree: a collapsed folder with exactly one loaded subfolder kept a stale tick box on that subfolder after ticking or unticking (only folders with two or more children were refreshed). Found while building V2-M2; also fixed for v1 in 1.0.1.

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
