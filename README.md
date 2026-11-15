# Data Transfer Tool

A portable data-export tool for Windows 10 and Windows 11: a single `.exe` (C# WinForms, .NET Framework 4.8) that IT staff run from a USB stick to copy a user's selected folders to a local, USB or network destination using Robocopy.

> **Status:** M1 (Robocopy investigation), M2 (core library) and M3 (pre-flight engine) complete. M4 (WinForms UI) built and tested, awaiting owner go-ahead. Still to come: M5 (report screen, retry failed, resume on launch, saved credentials) and M6 (manual test plan). Findings: [docs/M1_FINDINGS.md](docs/M1_FINDINGS.md). Specification: [docs/SPEC.md](docs/SPEC.md). Decisions: [docs/DECISIONS.md](docs/DECISIONS.md).

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

The job file (`DataTransferJob.json`) and logs (`Logs\`) are written next to the exe, or under `%ProgramData%\DataTransferTool` when that folder is read-only.

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
