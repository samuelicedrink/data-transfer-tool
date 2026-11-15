# Manual tests

These are the tests that could not be automated on the development PC: they need
real hardware, other Windows versions, a domain, a network share, other user
accounts or a real OneDrive. Everything that *could* be automated is covered by
`tests/DataTransfer.Core.Tests` (unit and integration) and `tests/DttUiSmoke`
(UI); see the README.

Run them before calling the tool ready for production use. Record each result in
the table at the end and commit the filled-in copy as
`docs/test-results/YYYY-MM-DD-<tester>.md` (no real user, computer or share names:
write "domain PC 1", "NAS share" and so on).

## Ground rules

- **Never test against a real user's data.** Use a test account, a spare or
  freshly installed PC, and data generated with `tests/manual/New-DttTestData.ps1`.
- Test the **Release** exe (`src\DataTransferTool\bin\Release\DataTransferTool.exe`),
  copied to a USB stick on its own. Not the SmokeTest build.
- Note the tool version (right-click the exe > Properties > Details) and the
  Windows version (`winver`) with every result.
- When something fails, keep the `Logs` folder next to the exe (Robocopy logs,
  `crash_*.txt`, `failures_*.csv`) and attach it to the result, after checking it
  contains no real user data.

## Preparation

### P1. Build the stick

1. On the development PC: `dotnet build src\DataTransferTool -c Release`.
2. Copy `DataTransferTool.exe` (only that file) to an empty folder on a USB stick
   formatted NTFS or exFAT, for example `E:\DTT\`.
3. Copy `tests\manual\New-DttTestData.ps1` to the same stick.

### P2. Generate test data on the PC under test

Open PowerShell **as the test user** and run, adjusting the size to the test:

```
powershell -ExecutionPolicy Bypass -File E:\DTT\New-DttTestData.ps1 -Root C:\DttTest -TotalMB 2000
```

This creates `C:\DttTest` with Office-like files, pictures, large files, a path
longer than 400 characters, accented and Japanese names, the files the default
exclusions skip, an `Unticked` folder and an empty folder. Options:

| Option | Use |
|---|---|
| `-TotalMB 20000` | a realistic-size job (about 20 GB) |
| `-LargeFileMB 2000` | fewer, larger files (useful for "pull the USB" tests) |
| `-FourGbFile` | adds a 4.2 GB file for the FAT32 test |
| `-ProfileFolders` | also puts a `DttTest` folder into Desktop, Documents, Pictures and Downloads of the **current** profile, for the *Tick user data* tests. **Test accounts only.** |
| `-Remove` | deletes what the script created (add `-ProfileFolders` if used) |

### P3. How to check a copy

The destination layout is `<destination>\<COMPUTERNAME>\<drive letter>\<original path>`.
To compare a source folder with its copy, in PowerShell:

```
robocopy C:\DttTest E:\Export\PC01\C\DttTest /L /E /XJ /FFT /NJH /NFL /NDL /XD Unticked /XF desktop.ini thumbs.db ~$* *.tmp
```

Robocopy lists nothing to copy (`Copied` column 0 in the summary) when the copy
is complete. Adjust `/XD` to what you unticked.

## Tests

Each test lists what it proves, the setup, the steps and the expected result.
"Report" means the Report screen at the end of a run.

### T01. UAC prompt and elevation

**Proves:** the exe asks for administrator rights and works only when elevated.

1. Signed in as a **standard user** (not an administrator), double-click the exe.
   - Expected: the UAC prompt asks for an administrator **user name and password**.
     Cancel it: the tool does not start, nothing is written next to the exe.
2. Enter administrator credentials.
   - Expected: the tool opens on *Select folders*. The window title shows the computer name.
3. Signed in as an **administrator**, double-click the exe.
   - Expected: a UAC *Yes/No* prompt (no password); *Yes* opens the tool.
4. On the Pre-flight screen, *Administrator rights and backup privilege* shows **PASS**.
5. Right-click the exe > *Run as administrator* also works; double-clicking it from
   a network share works or gives a clear Windows message (record which).

Also note if Windows SmartScreen shows "Windows protected your PC". The exe is not
code-signed; SmartScreen normally appears only for files downloaded from the
internet, not copied from a USB stick. Record what happens.

### T02. Clean Windows 11 PC

**Proves:** the single exe runs on a PC with nothing installed.

Setup: a freshly installed Windows 11 (any edition, latest updates), no Visual
Studio, no .NET SDK, no Office, a local administrator account.

1. Check .NET Framework 4.8 is present: `reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release` shows 528040 or higher.
2. Generate 2 GB of test data (P2). Run the tool from the stick.
3. Tick `C:\DttTest`, untick `C:\DttTest\Unticked`. Destination: a folder on a second USB stick or disk.
4. Pass pre-flight (acknowledge warnings), start the copy.
   - Expected: no error about missing .NET or DLLs. Progress shows files, data,
     current folder, speed and time left.
   - Expected: Report says *Everything was copied.* P3 check finds nothing missing.
     `Unticked` and the excluded files are absent. The long-path and Japanese-named
     files are present.
5. Look next to the exe: `Logs\` exists; after *Close*, `DataTransferJob.json` is
   gone and `Logs\job_*.json` exists.

### T03. Clean Windows 10 PC

Same as T02 on a freshly installed **Windows 10 22H2** (and, if available,
Windows 10 1903, the oldest supported). Additionally:

1. At step 4, pre-flight row *Cloud-only OneDrive files* must not FAIL. If you can
   sign in to OneDrive on this PC, run T09 here too: the M1 investigation proved
   `/XA:O` skips cloud-only files on Windows 11 only; **this is the open Windows 10
   question.** If the check fails with "Robocopy on this Windows version does not
   exclude them", **do not acknowledge it** (acknowledging lets Robocopy download
   every cloud-only file). Record it and stop: the fallback exclusion method
   proposed in `docs/M1_FINDINGS.md` then has to be built.
2. Long paths: on Windows 10 `LongPathsEnabled` is off by default (`reg query
   HKLM\SYSTEM\CurrentControlSet\Control\FileSystem /v LongPathsEnabled`). Record
   the value; the long-path file must still be copied, and the Destination test
   must still pass "including paths longer than 260 characters".

### T04. Domain-joined PC

**Proves:** domain profiles, domain administrator elevation and the User data preset.

Setup: a domain-joined test PC with two test domain users who have each signed
in at least once, and data generated in both profiles with `-ProfileFolders`
(run P2 as each user).

1. Start the tool; at UAC enter a **domain administrator** account (not one of the test users).
2. Press *Tick user data*.
   - Expected: message "Ticked N folder(s) in 2 profile(s)" (more if other
     profiles exist); the list shows Desktop, Documents, Pictures and Downloads
     of each profile, and `OneDrive - <org>\...` folders if Known Folder Move is on.
   - Expected: `Public`, `Default` and service profiles are not ticked.
3. Copy to a local destination.
   - Expected: both users' `DttTest` folders are copied, although the
     administrator has no permission on the other profiles (backup mode).
     `AppData` of each profile is not copied.
4. Record whether the folders of a user whose profile is **roaming or redirected
   to a server** show up (they are copied only if they are on the local disk).

### T05. Network share with credentials (UNC)

**Proves:** the elevated tool opens its own connection; mapped drives are not needed.

Setup: a test share (`\\server\share`) writable only by a test account that is
**not** the account running the tool. On the PC, map the share to a drive letter
as the signed-in user, so you can confirm the tool does not depend on it.

1. Destination: *Network share*, path `\\server\share\Exports`, user `DOMAIN\testuser`
   (or `server\localuser`), password. Press *Test destination*.
   - Expected: green *OK*, "Connected to \\server\share as ...".
2. Repeat with a wrong password.
   - Expected: red message "Could not connect ... The specified network password
     is not correct", pre-flight *Destination* is **FAIL (must fix)**, Start copy disabled.
     The password appears nowhere on screen.
3. Repeat with an empty user name (the tool's own account).
   - Expected: works if that account can write to the share, otherwise a clear
     access-denied failure.
4. Run a 2 GB copy to the share.
   - Expected: Report *Everything was copied.*; files on the share at
     `Exports\<COMPUTERNAME>\C\DttTest\...`.
5. Server message `1219` (connected to the same server with other credentials):
   connect the share in Explorer as yet another user first, then run step 1.
   Record the result; the tool tries to replace the connection.
6. Search the stick for the password: `findstr /s /i /m "<password>" E:\DTT\*`
   finds nothing.

### T06. Saved credentials for resume (DPAPI)

**Proves:** credentials are saved only when asked, encrypted, and only usable by the same account on the same PC.

1. As T05, tick *Save credentials for resume*, start a 5 GB copy, Cancel it (two clicks), close the tool.
   - Expected: `DataTransferJob.credentials` next to the exe; opening it in
     Notepad shows no readable user name or password; `findstr` (T05 step 6) finds nothing.
2. Start the tool again (same administrator account).
   - Expected: *Unfinished job* dialog with "Credentials: saved for resume".
     *Resume this job* goes straight to Pre-flight without asking for the password,
     and the copy completes.
   - Expected: after *Close*, the `.credentials` file is gone.
3. Repeat step 1, then start the tool as a **different administrator account**.
   - Expected: the other account cannot decrypt them, so the dialog says
     "Credentials: not saved - you will be asked for the password", and resume
     stops on the Destination screen with "Enter the password for ... to resume".
4. Repeat step 1, move the stick to **another PC** and start the tool there.
   - Expected: the dialog shows "(NOT this PC)" next to the computer name and
     "not saved" for the credentials; resume asks for the password. Choose *Start a new job*: the old job goes to `Logs\`
     and the `.credentials` file is deleted.
5. Without ticking the box: Cancel, close, restart, resume.
   - Expected: no `.credentials` file at any point; resume asks for the password.

### T07. USB destination pulled out mid-run

**Proves:** the tool pauses when the destination disappears and continues when it returns.

Setup: 10 GB of test data with `-LargeFileMB 2000`; destination on a USB stick or disk.

1. Start the copy. While a large file is copying, pull the USB destination out.
   - Expected: within a few seconds the status says "Destination unreachable -
     waiting for it to return (checking every 3 s)". No dialog. Cancel still works.
2. Wait one minute, plug it back into the **same port**.
   - Expected: Windows gives it the same drive letter; the status returns to
     *Copying* by itself and the job finishes. Report *Everything was copied.*;
     P3 check finds nothing missing; the file that was being copied when the stick
     was pulled is complete (compare its size and, for one file, `certutil -hashfile`).
3. Repeat, but plug the stick into another port so it gets a **different letter**.
   - Expected: the tool keeps waiting (it only knows the old letter). Cancel, close,
     start the tool again: the resume dialog appears; resume, change the destination
     letter on the Destination screen, continue. Record whether this is acceptable.
4. Also run step 1 with **write caching** ("Better performance" in the disk's policy)
   if the stick supports it, and note any corrupted file at the destination.

### T08. Network drop mid-run

**Proves:** the same pause and continue for a network share.

Setup: T05 share, 10 GB of test data.

1. Start the copy; after a minute unplug the network cable or turn off Wi-Fi.
   - Expected: "Destination unreachable - waiting..." within about 10 seconds (a dead
     network path can take longer to fail than a pulled USB stick; record the time).
2. After 2 minutes reconnect.
   - Expected: the copy continues by itself (Windows restores the share connection
     with the stored credentials), and finishes. P3 check passes.
3. Repeat with an outage of 15 minutes, and with a laptop moved to a different Wi-Fi.
   Record whether it continues.
4. Repeat with the **server** rebooted instead of the client disconnected.

### T09. Real OneDrive-synced profile

**Proves:** cloud-only files are skipped without downloading, OneDrive is stopped and restarted.

Setup: a test account signed in to OneDrive (Files On-Demand on), with Desktop and
Documents backed up by OneDrive (Known Folder Move), containing generated files.
Make some files cloud-only (right-click > *Free up space*) and some *Always keep
on this device*. Note the free space on C:.

1. Start the tool, *Tick user data*.
   - Expected: `OneDrive - <org>\Desktop` and `\Documents` (or `OneDrive\...`) are ticked.
2. Pre-flight.
   - Expected: *Cloud-only OneDrive files* shows **WARN** with the number and size of
     the cloud-only files, listing some of them. *Open applications* lists OneDrive.
3. Press *Close applications* on the *Open applications* row.
   - Expected: OneDrive disappears from the tray within ~20 s; the row turns PASS
     with "OneDrive is stopped and will be started again after the job".
4. Acknowledge, copy, finish, *Close*.
   - Expected: the cloud-only files are **not** at the destination; the local and
     *Always keep* files are. Free space on C: did not drop (nothing was downloaded);
     the cloud-only files still show the cloud icon.
   - Expected: OneDrive is running again (tray icon), **not** as administrator
     (Task Manager > Details > right-click columns > *Elevated*: No), and syncing.
5. Repeat with a second OneDrive account (personal + business) signed in at the same time.

### T10. Full destination

**Proves:** the tool stops cleanly when the destination fills up and can continue.

Setup: an empty 8 GB or 16 GB USB stick (NTFS or exFAT), 20 GB of test data.

1. Pre-flight.
   - Expected: *Free space at destination* **FAIL** "Not enough space ... Short by N GB".
     It can be acknowledged (not "must fix").
2. Acknowledge and start the copy.
   - Expected: when the stick is full, the copy stops within seconds with
     "Stopped: the destination is full." No dialog, no long run of errors.
   - Expected: Report says "The destination is full..." and offers *Continue copy*.
3. Delete some files from the stick (or swap in a bigger one with the same letter),
   press *Continue copy*.
   - Expected: the copy continues and skips what is already there.
4. Close the tool while it is stopped on full, restart it.
   - Expected: the resume dialog says "it stopped because the destination was full".

### T11. FAT32 and exFAT destinations

1. FAT32 stick, data generated with `-FourGbFile`.
   - Expected: pre-flight *Free space* **FAIL** mentioning FAT32 and the 4.2 GB file
     (acknowledgeable). If acknowledged, that file fails in the Report; everything else copies.
2. FAT32 stick, no file over 4 GB.
   - Expected: **WARN** about FAT32. Copy, then copy again: the second run copies
     **0 files** (the 2-second timestamp tolerance works).
3. exFAT stick with the 4.2 GB file.
   - Expected: no FAT32 warning; the large file copies.

### T12. Second signed-in user

**Proves:** other sessions are detected and their applications can be closed.

Setup: two test accounts. Sign in as user B, open a generated `.docx` from B's
Documents in Word (or any app on the known list) and leave it open. Switch user
(Start > user icon > Switch user) and sign in as user A, an administrator.

1. Start the tool as A; tick both users' Documents.
   - Expected: pre-flight *Other signed-in users* **WARN** listing user B with the
     number of known applications open; a *Close their applications* button.
2. Without pressing it, acknowledge and copy.
   - Expected: B's open document is reported as **File was open in another program**
     (if Word holds it locked; record if it is copied instead).
3. Press *Close their applications* (back on Pre-flight in a new run).
   - Expected: B's Word is closed (unsaved changes in B's session are lost, which is
     why the check warns first). The copy then includes the document.
4. With user B **signed out** but a disconnected session present (lock screen),
   record what the check shows.

### T13. Open applications on the PC under test

**Proves:** the list, the polite close and the force-close after the timeout.

1. As the signed-in user, open Word with an unsaved new document, Excel with a
   saved workbook from `C:\DttTest`, Edge, and a PDF in Acrobat Reader.
2. Pre-flight: *Open applications* **WARN** lists each one (process name and window title).
3. Press *Close applications*.
   - Expected: Excel and Edge close at once; Word shows "Save changes?"; after 20 s
     (Options > *Seconds an application gets to close*) Word is ended anyway.
     The row turns PASS. Record what the user would lose (the unsaved document).
4. Options: add a process name (for example `notepad`) to the list, open Notepad,
   run the checks again: Notepad is listed.

### T14. EFS-encrypted files

Setup: Windows **Pro or Enterprise** (Home has no EFS). As a test user, encrypt a
folder of generated files (Properties > Advanced > *Encrypt contents*).

1. Run the tool as a different administrator, tick that folder.
   - Expected: pre-flight *EFS-encrypted files* **WARN** with count and examples.
2. Copy. Record what happens to the encrypted files: copied (still encrypted, so
   unreadable without the user's certificate) or failed with a reason in the Report.

### T15. Laptop on battery; sleep blocked during the run

1. Unplug the charger. Pre-flight *Power* shows **WARN** "Running on battery (N%)".
2. Plug in, run the checks again: **PASS**.
3. Start a long copy (20 GB). In an administrator command prompt run `powercfg /requests`.
   - Expected: under **SYSTEM**, an entry `[PROCESS] ...\DataTransferTool.exe` (the
     tool, not Robocopy, holds the request). It disappears when the copy ends.
4. Set the sleep timeout to 1 minute (Settings > Power) and leave the PC alone
   during the copy.
   - Expected: the PC does not sleep while copying; after the copy has finished it
     sleeps normally. (The screen may still turn off; that is fine.)
5. Close the lid of a laptop during a copy: record the behaviour (Windows usually
   sleeps on lid close regardless; the job can then be resumed).

### T16. Read-only stick: job file in ProgramData

1. Put the exe on a stick with the hardware write-protect switch on (or a read-only
   share or CD).
2. Start the tool and run a small copy.
   - Expected: pre-flight *Log folder writable* PASS with a path under
     `C:\ProgramData\DataTransferTool\Logs`; the job file is there too, and resume
     works from there after a cancel.

### T17. Resume after a crash or restart

1. Start a 20 GB copy, then restart Windows from the Start menu (or pull the power
   on a spare PC).
   - Expected: no Robocopy is left running after the restart.
2. Start the tool again.
   - Expected: the resume dialog says "the tool was closed or the PC restarted during
     the copy". Resume completes the job; P3 check passes; no file at the destination
     is truncated (pick the largest few and compare with `certutil -hashfile`).
3. Kill `DataTransferTool.exe` in Task Manager during a copy.
   - Expected: Robocopy stops too within a second (Task Manager), the job can be resumed.

### T18. A realistic job

**Proves:** the tool copes with the typical size from the specification.

Setup: 200 GB of generated data (`-TotalMB 200000`, several hours to generate) or
a test account filled with a representative copy of non-personal files, a USB 3
disk as destination.

1. Run the whole flow. Record: scan time, copy time, average speed, peak memory of
   `DataTransferTool.exe` (Task Manager), whether the window stays responsive,
   and whether the time-left estimate was sensible.
2. Copy again: the second run should copy 0 files and take a fraction of the time.

### T19. High DPI and small screens

1. 1366 x 768 at 100 %, 1920 x 1080 at 150 %, a 4K screen at 200 %.
   - Expected: every screen is readable, no button or text is cut off (the Options
     dialog and the Pre-flight details in particular). Screenshots welcome.

## Results

Copy this table into the results file. Result: PASS, FAIL, PARTIAL, or N/T (not tested, with the reason).

| Test | Windows (winver) | Tool version | Result | Notes / log reference |
|---|---|---|---|---|
| T01 UAC prompt and elevation | | | | |
| T02 Clean Windows 11 | | | | |
| T03 Clean Windows 10 | | | | |
| T04 Domain-joined PC | | | | |
| T05 Network share with credentials | | | | |
| T06 Saved credentials (DPAPI) | | | | |
| T07 USB pulled mid-run | | | | |
| T08 Network drop mid-run | | | | |
| T09 Real OneDrive-synced profile | | | | |
| T10 Full destination | | | | |
| T11 FAT32 and exFAT | | | | |
| T12 Second signed-in user | | | | |
| T13 Open applications | | | | |
| T14 EFS-encrypted files | | | | |
| T15 Battery and sleep | | | | |
| T16 Read-only stick | | | | |
| T17 Resume after crash or restart | | | | |
| T18 Realistic job | | | | |
| T19 High DPI and small screens | | | | |

## Already covered by automated tests

For reference, so these need no manual repetition (all on Windows 11, development PC):

| Area | Where |
|---|---|
| Flag set, long paths, skip-if-identical, `/FFT`, kill and resume, cloud-only exclusion on Win 11 | `src/DttM1Harness`, `docs/M1_FINDINGS.md` |
| Log parser on real Robocopy logs, incl. disk full and volume removed | `tests/DataTransfer.Core.Tests/ParserTests.cs` |
| Engine: cancel, resume, locked-file retry, destination lost (SUBST drive and detached virtual disk), destination full (virtual disk) | `EngineTests.cs` (two tests need an elevated prompt) |
| Pre-flight checks, folder walk, closing a stand-in application | `PreflightTests.cs` |
| Selection model, User data preset, credential store, failures CSV | `FolderTreeTests.cs`, `ReportingTests.cs` |
| Whole UI: select, options, destination, pre-flight, copy, cancel, close during copy, report, CSV, retry, resume, credentials on resume | `tests/DttUiSmoke` (182 checks) |
