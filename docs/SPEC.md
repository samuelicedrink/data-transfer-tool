# Build specification

This is the owner's specification, recorded verbatim. Do not edit it to match the
implementation; if the implementation has to deviate, record the deviation and the
reason in `docs/DECISIONS.md` and in `CHANGELOG.md`.

---

Build a portable Windows data-export tool. Work in milestones; stop after each
one, show me what you built and how you verified it, and wait for my go-ahead.

## What it is
A single .exe that IT staff run from a USB stick on a PC a user has finished
with. They tick folders, pick a destination, pass a pre-flight check, and press
Run. It copies the selected data using Robocopy and never stops to ask a
question once the run has started. Typical job: ~200 GB of documents, pictures
and Office files, hundreds of thousands of files.

## Hard constraints
- C# WinForms targeting .NET Framework 4.8 (preinstalled on Windows 10 1903+
  and Windows 11). One exe, no installer, no runtime to deploy. Avoid NuGet
  dependencies that add extra DLLs unless they are merged into the exe.
- Must run on Windows 10 and Windows 11.
- App manifest: requireAdministrator and longPathAware.
- Robocopy (built into Windows) is the copy engine. Do not write a custom one.
- Assume English Windows for parsing Robocopy output.
- Selection is folder-level only; no ticking individual files.

## Screens
1. Select: lazy-loaded folder tree with tri-state checkboxes, plus a "User
   data" preset that ticks Desktop, Documents, Pictures and Downloads for every
   profile under C:\Users.
2. Destination: a local/USB folder, or a UNC path with username and password.
   Because the app is elevated, it must open its own network connection with
   those credentials (mapped drives of the logged-in user are not visible).
3. Pre-flight: runs every check below, shows pass/warn/fail with a fix button
   where one exists. Run stays disabled until all pass or are acknowledged.
4. Run: progress (files, bytes, current folder, ETA) and Cancel. No dialogs.
5. Report: copied / skipped / failed totals, CSV of failures with reasons,
   "Retry failed" button.

## Pre-flight checks
- Process is elevated and backup privilege is available.
- Destination: connect, write and delete a test file, including one with a
  path longer than 260 characters.
- Free space vs. scan total; warn if destination is FAT32.
- Source scan with Robocopy /L using the real flag set: file count, total
  bytes, unreadable folders.
- Cloud-only OneDrive files: excluded from the copy; show count and size.
- Open applications from an editable known list (Office apps, Outlook,
  browsers, Teams, OneDrive, PDF readers): ask them to close, force-close after
  a timeout. Stop the OneDrive client for the duration.
- Other signed-in user sessions: flag, with an option to close their apps.
- EFS-encrypted files: flag.
- Laptop on battery: warn.
- Log folder writable.

## Copy behaviour
- Starting flags (verify each by testing, adjust and tell me why):
  /E /B /R:1 /W:2 /XJ /COPY:DAT /DCOPY:DAT /FFT /MT:8 /NP /UNILOG
- One Robocopy job per ticked top-level folder; unticked subfolders become /XD.
- Default exclusions, editable: AppData, desktop.ini, thumbs.db, ~$*, *.tmp.
- Destination layout: <Dest>\<COMPUTERNAME>\<drive letter>\<original path>.
- Files already at the destination with same size and timestamp are skipped.
- Per-file errors: skip and log, never prompt.
- Destination unreachable mid-run: pause, poll every few seconds, continue
  when it returns. Destination full: stop cleanly and report.
- Block system sleep during the run.
- After the main pass, retry files that failed because they were locked.

## Resume and credentials
- Save the job definition (selections, destination, flags, progress) beside
  the exe, falling back to %ProgramData% if that location is read-only.
- On launch, if an unfinished job exists, offer to resume it.
- Credentials live in memory only by default. Provide a checkbox "Save
  credentials for resume"; when ticked, store them encrypted with DPAPI
  (ProtectedData, CurrentUser scope). Never write them in plain text, and never
  write them to logs.

## Milestones
M1. Investigation, no UI. Write a small console harness and test on this
    machine: the flag set, long paths, skip-if-identical, resume after a killed
    run, and how to exclude cloud-only OneDrive files. For the last one, check
    whether /XA:O is enough or whether files carry only
    FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS; if Robocopy cannot exclude them,
    propose an alternative before continuing. Report findings.
M2. Core library: job model, Robocopy runner, log parser, progress events,
    pause/resume on destination loss, locked-file retry. Unit tests for the
    parser using real captured logs.
M3. Pre-flight engine with all checks, still console-driven.
M4. WinForms UI wired to the library.
M5. Report, retry, resume, DPAPI credential option.
M6. Write MANUAL_TESTS.md covering what you cannot test yourself: UAC prompt,
    domain PC, UNC share with credentials, USB pulled mid-run, network drop,
    full destination, second signed-in user, real OneDrive-synced profile,
    clean Windows 10 and Windows 11 machines.

## Rules
- Keep copy logic in a library separate from the UI.
- Test with generated folder trees, never against real user data on this PC.
- If something in this spec turns out not to work as described, stop and tell
  me instead of working around it silently.
