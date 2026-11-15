# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Change categories: `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed`, `Security`.

## [Unreleased]

### Added

- Initial repository structure: README, change log, `.gitignore`, `.gitattributes`.
- Build specification (`docs/SPEC.md`), decision log (`docs/DECISIONS.md`) and working instructions for Claude Code sessions (`CLAUDE.md`), including the rule that every change is committed, pushed to GitHub and recorded here.
- M1 investigation harness (`src/DttM1Harness`, console, .NET Framework 4.8) and captured Robocopy logs (`tests/fixtures/m1`) for the M2 parser.
- M1 findings report (`docs/M1_FINDINGS.md`).

- M2 core library `src/DataTransfer.Core` (no UI): job model, Robocopy command builder, log parser, live log reader, copy engine with cancel, pause on destination loss, stop on destination full, locked-file retry, sleep blocking, and job save/load.

- Unit and integration tests (`tests/DataTransfer.Core.Tests`, NUnit, test-only NuGet packages): parser tests on the captured M1 logs, command-builder tests, and engine tests that run the real Robocopy on generated trees (cancel and resume, locked-file retry, destination loss, progress).
- Console driver `src/DttCli` (scan, run, resume, retry, show) for exercising the library without a UI.

### Changed

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
