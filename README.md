# Data Transfer Tool

> **All rights reserved.** Not for use, copying or any business use without the owner's written permission. See [LICENSE](LICENSE).

A portable data-export tool for Windows 10 and Windows 11: a single `.exe` (C# WinForms, .NET Framework 4.8) that IT staff run from a USB stick to copy a user's selected folders to a local, USB or network destination using Robocopy.

> **Status:** v1.0.0 released (milestones M1 to M6). **The manual tests in [MANUAL_TESTS.md](MANUAL_TESTS.md) have not been run yet; use v1 only on test PCs until they pass.** v2 (user picker and relevance columns on the Select screen) is in development. Findings: [docs/M1_FINDINGS.md](docs/M1_FINDINGS.md). Specification: [docs/SPEC.md](docs/SPEC.md). Decisions: [docs/DECISIONS.md](docs/DECISIONS.md).

## Target platforms

| OS         | Architecture | Status                                   |
|------------|--------------|------------------------------------------|
| Windows 11 | x64          | Developed and tested here                |
| Windows 10 | x64          | Not yet tested (M6 manual tests)         |

## Using the tool

The tool is the single file `DataTransferTool.exe` (built at `src\DataTransferTool\bin\Release\`). It needs nothing else: .NET Framework 4.8 is part of Windows 10 (1903 and later) and Windows 11. The `.exe.config` and `.pdb` built beside it are optional.

1. Copy `DataTransferTool.exe` to a USB stick.
2. On the PC to export, double-click it and accept the administrator prompt.
3. **Select:** tick folders, or press *Tick user data* for Desktop, Documents, Pictures and Downloads of every profile.
4. **Destination:** a folder on a USB or local drive, or a network share with user name and password. Press *Test destination*.
5. **Pre-flight:** fix or acknowledge every warning, then *Start copy*.
6. **Run:** wait for *Finished*. Files land in `<destination>\<COMPUTERNAME>\<drive>\<original path>`.
7. **Report:** totals and the files that were not copied, with reasons. *Retry failed* tries them again; nothing in the copy is left Hidden or System, so every copied folder shows in Explorer (Options can switch this off). The list is also saved as a CSV in the log folder. Each CSV row names the Robocopy log file and the line number of its error entry, so it can be looked up in the `Logs` folder.

If a copy is cancelled, the destination fills up, or the PC restarts, start the tool again from the same stick: it offers to resume the job. For a network share, tick *Save credentials for resume* so the password does not have to be typed again (it is stored encrypted, readable only by the same Windows account on the same PC).

The job file (`DataTransferJob.json`) and logs are written next to the exe, or under `%ProgramData%\DataTransferTool` when that folder is read-only. Each job has its own log folder, `Logs\<COMPUTERNAME> <date time> <id>\` (`copy.log`, `retry.log`, `scan.log`, `failures.csv`, `job.json`), which is also copied to `<Destination>\Transfer logs\` when a run ends.

## Repository layout

```
src/DataTransferTool    the tool: one WinForms exe (the library's source is compiled in)
src/DataTransfer.Core   copy and pre-flight logic, no UI
src/DttCli              console driver for the library (development aid)
src/DttM1Harness        M1 investigation harness
tests/                  unit, integration and UI tests; captured Robocopy logs in tests/fixtures
docs/                   specification, findings, decisions
```

## Versioning and change log

This project follows [Semantic Versioning](https://semver.org/). Every user-visible change is recorded in [CHANGELOG.md](CHANGELOG.md) under `Unreleased`, and moved to a dated version section at release time. Releases are tagged `vMAJOR.MINOR.PATCH`.

## Release folder (v1 and v2 side by side)

`scripts\Build-Release.ps1` builds a tag or branch in a temporary git worktree (your working copy is not touched) and puts the exe into `release\<name>\` with a `BUILD.txt` naming the commit. The `release` folder is not committed.

```
powershell -ExecutionPolicy Bypass -File scripts\Build-Release.ps1 -Ref v1.0.0 -Name v1
powershell -ExecutionPolicy Bypass -File scripts\Build-Release.ps1 -Ref HEAD -Name v2
```

| Folder | Built from | What it is |
|---|---|---|
| `release\v1\` | tag `v1.0.0` (fixes: branch `v1`) | The current tool: five steps, profile folder tree |
| `release\v2\` | the current v2 branch | In development: user picker, Modified / Size / Relevance columns |

Copy the whole folder (exe and `.config`) to the USB stick. The window title shows which version is running.

## Build and test

Requires the .NET SDK and the .NET Framework 4.8 targeting pack (both come with Visual Studio 2022).

```
dotnet build src\DataTransferTool -c Release
dotnet test tests\DataTransfer.Core.Tests -c Release
```

The integration tests generate about 1 GB of test data in the temp folder (set `DTT_TEST_ROOT` to use another drive) and remove it afterwards. Two tests need an elevated prompt and are skipped otherwise.

UI test (opens the tool's window for a few minutes; uses only the generated folders given to it; never brings the window to the front or types into the desktop):

```
dotnet build src\DataTransferTool -c SmokeTest
dotnet build tests\DttUiSmoke -c Release
tests\DttUiSmoke\bin\Release\DttUiSmoke.exe src\DataTransferTool\bin\SmokeTest\DataTransferTool.exe <screenshot folder> <work folder> <small tree> <large tree> <many-files tree>
```

## License

**All rights reserved.** This is proprietary software: no one may use, copy, change or share it, and no business may use it, without the owner's written permission. The code and the downloads being visible on GitHub do not give permission. See [LICENSE](LICENSE).
