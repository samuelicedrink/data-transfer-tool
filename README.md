# Data Transfer Tool

A portable data-export tool for Windows 10 and Windows 11: a single `.exe` (C# WinForms, .NET Framework 4.8) that IT staff run from a USB stick to copy a user's selected folders to a local, USB or network destination using Robocopy.

> **Status:** M1 (Robocopy investigation) complete: [docs/M1_FINDINGS.md](docs/M1_FINDINGS.md). M2 (core library) complete. M3 (pre-flight engine) in progress. The specification is in [docs/SPEC.md](docs/SPEC.md).

## Target platforms

| OS         | Architecture | Status  |
|------------|--------------|---------|
| Windows 11 | x64          | Planned |
| Windows 10 | x64          | Planned |

## Repository layout

```
src/     application source code
docs/    design notes and user documentation
```

## Versioning and change log

This project follows [Semantic Versioning](https://semver.org/). Every user-visible change is recorded in [CHANGELOG.md](CHANGELOG.md) under `Unreleased`, and moved to a dated version section at release time. Releases are tagged `vMAJOR.MINOR.PATCH`.

## Build and test

Requires the .NET SDK and the .NET Framework 4.8 targeting pack (both come with Visual Studio 2022).

```
dotnet build src\DttCli -c Release
dotnet test tests\DataTransfer.Core.Tests -c Release
```

The integration tests generate about 1 GB of test data in the temp folder (set `DTT_TEST_ROOT` to use another drive) and remove it afterwards. Two tests need an elevated prompt and are skipped otherwise.
