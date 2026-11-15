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

### Changed

- Proposed flag set after M1 testing (pending owner approval): add `/BYTES` (exact byte counts; default output prints rounded sizes such as `1.7 m`), add `/FP` (same file-line shape with and without `/MT`) and add `/XA:O` (excludes cloud-only OneDrive files, verified on Windows 11 build 26200). No spec flag removed.

### Test findings (M1, 2026-11-14, not elevated and elevated)

- `/B` makes Robocopy exit 16 when the process is not elevated. Elevated run (same day): `/B` copies ACL-denied files and folders, does not get past locked files, and stamps killed partial files 1980-01-02.
- Long paths (source 637, destination 943 characters), skip-if-identical, `/FFT`, `/XJ`, `/COPY:DAT`, `/DCOPY:DAT` and resume after a killed run all verified by content hash.
- Cloud-only OneDrive files carry both OFFLINE and RECALL_ON_DATA_ACCESS as Robocopy sees them; `/XA:O` excludes them without downloading. A .NET Framework process sees only RECALL_ON_DATA_ACCESS, so our own scan must test that bit.
- `/XD AppData` excludes every folder named AppData at any depth.
- With `/MT` the log falls behind while a large file is copying, so byte progress needs a second source.
