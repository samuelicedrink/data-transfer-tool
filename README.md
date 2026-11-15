# Data Transfer Tool

A portable data-export tool for Windows 10 and Windows 11: a single `.exe` (C# WinForms, .NET Framework 4.8) that IT staff run from a USB stick to copy a user's selected folders to a local, USB or network destination using Robocopy.

> **Status:** pre-development. No application code yet. The specification is in [docs/SPEC.md](docs/SPEC.md); milestone M1 (Robocopy investigation) has not started.

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
