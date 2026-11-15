# M1 findings — Robocopy investigation

Tested 2026-11-14 on Windows 11 (build 26200, Home N), Robocopy 10.0.26100.1,
NTFS to NTFS on a local fixed disk, with `src/DttM1Harness` against generated
trees only. Captured logs: `tests/fixtures/m1/`.

The harness was run twice: not elevated (without `/B`) and elevated (full spec
flag set including `/B`). Both runs finished with 0 failed checks. The OneDrive
test was run not elevated only.

## Elevated run (`/B` active)

- The spec flag set verbatim copies and verifies cleanly (exit 1, 228 files identical).
- `/B` reads past permissions: the ACL-denied file and the ACL-denied folder
  were both copied, and the `/L` scan reported no unreadable folder.
- `/B` does not help with locked files: the file held open by another process
  still failed with error 32, and was picked up by the second pass once released.
- Partial files left by a kill are stamped 1980-01-02 instead of the kill time,
  which removes the 2-second `/FFT` edge case noted below for most of them.
- Not checked elevated: `/B` together with `/XA:O` on cloud-only files.

## Result per question

| Question | Result |
|---|---|
| Flag set | Works, with two additions and one caveat (below). |
| Long paths | Works. Source path of 637 chars and destination paths up to 943 chars copied and verified by hash. |
| Skip-if-identical | Works. Second run: 0 copied, 232 skipped, exit 0. |
| Resume after kill | Works. Rerun skipped the finished files, recopied the partial ones, tree verified identical by hash. |
| Cloud-only OneDrive | `/XA:O` is enough on this Windows 11 build. Nothing was downloaded. Windows 10 not tested. |

## Flag set

Tested: `/E /B /R:1 /W:2 /XJ /COPY:DAT /DCOPY:DAT /FFT /MT:8 /NP /UNILOG`

| Flag | Verified | Finding |
|---|---|---|
| `/E` | yes | Empty folders are created. |
| `/B` | yes | Not elevated, Robocopy refuses to start (exit 16), so the app must confirm elevation first. Elevated, it copies files and folders the account has no permission to read. It does not get past locked files. |
| `/R:1 /W:2` | yes | A locked file and an ACL-denied file each cost one 2-second wait, then are logged and skipped. No prompt. Exit code 9 (8 = some failures, 1 = some copied). |
| `/XJ` | yes | A junction inside the tree was not followed and not created at the destination. |
| `/COPY:DAT` | yes | Content, attributes (read-only, hidden), modified and created times all match. |
| `/DCOPY:DAT` | yes | The `T` is what preserves a folder's created-time; without it only the modified-time survived. |
| `/FFT` | yes | Destination time 1 s off: skipped with `/FFT`, recopied without. 3 s off: recopied. |
| `/MT:8` | yes | About 3x faster than single-threaded on 6,000 small files (1.4 s vs 3.8 s). Changes the log format (below). |
| `/NP` | yes | No percentage lines in the log. |
| `/UNILOG` | yes | UTF-16 log; accented, Japanese and symbol file names are intact. Stdout stays silent. |

### Proposed changes to the flag set

1. **Add `/BYTES`.** Without it sizes are printed as `1.7 m`, `44.44 m`, which
   cannot be summed accurately. With it every size is an exact byte count.
2. **Add `/XA:O`** to exclude cloud-only files (see OneDrive section).
3. **Caveat on `AppData`.** `/XD AppData` excludes every folder named `AppData`
   at any depth, not only the one under a user profile. A generated
   `Docs\Sub\AppData` was skipped too. Proposal: keep `AppData` as the editable
   default but pass it as a full path (`C:\Users\<name>\AppData`) when the job
   root is a profile. Owner to decide.

No flag from the spec needs removing.

## Log format (input for the M2 parser)

- With `/MT`, each file line carries the full source path whether or not `/FP`
  is given, and there are no per-directory lines. Without `/MT` the log has
  `New Dir` lines and, without `/FP`, bare file names. The parser should target
  the `/MT` format and always pass `/FP` so both shapes are the same.
- File line: `<tab>    New File  <tab><tab> <bytes><tab><full path>`.
- Error block: `<date> <time> ERROR <n> (0x<hex>) Copying File <path>` then the
  Windows message on the next line, then `ERROR: RETRY LIMIT EXCEEDED.` after
  the last retry. Folders use `Copying Directory`.
- Summary `Skipped` counts excluded files and folders together with identical
  ones. To report "skipped because identical" separately from "excluded", the
  parser has to subtract, or the listing needs `/V` (which labels them `named`).
- Exit code is a bit mask: 1 copied, 2 extras, 4 mismatches, 8 failures,
  16 fatal. Killed process: -1 and no summary block.

## Live progress

- The `/UNILOG` file can be opened for reading while Robocopy writes it, and it
  grows during the run; on 6,000 small files it tracked the destination closely.
- A file's line is written when the copy of that file **starts**, not when it
  ends.
- With `/MT`, output is held back while a large file is in flight: in the kill
  test the log listed 8 files while 157 were already at the destination.
- So: the log is good for "current folder" and file counts, but byte progress
  during large files needs a second source. Proposal for M2: poll the
  destination volume's free space (cheap, works for local and UNC) and use the
  log for counts, names and errors.
- Reading stdout through a pipe (no `/UNILOG`) is equally live. `/UNILOG` plus
  `/TEE` gives both. Recommendation: `/UNILOG` only, tail the file; it also
  survives a crash of our own process.

## Skip-if-identical

- Identical rerun: exit 0, 0 copied.
- Changed 2 files and added 1: exactly 3 copied.
- A file whose content changed but whose size and modified-time were kept is
  treated as identical. That is the rule the spec asks for; noted so nobody is
  surprised.

## Resume after a killed run

Robocopy was killed with 157 of 300 files (2.3 GB) at the destination.

- Files in flight at the kill were left at **full size with incomplete
  content**, stamped with the time of the kill rather than the source time.
- Because the timestamp differs, the rerun recopied all of them. No partial file
  had both the final size and the final timestamp. After the rerun all 300
  files matched by hash; a third run had nothing to do.
- Edge case: a source file modified within 2 seconds of the moment of the kill
  could look identical under `/FFT`. Not a realistic risk on a PC the user has
  finished with; recorded for completeness.
- Cosmetic: one file that was complete at the kill kept a created-time of the
  copy moment. Content, size and modified-time were right.
- Resume therefore needs no bookkeeping of our own for correctness: rerun the
  same job and Robocopy sorts it out. The saved job state is only needed to
  know which jobs to rerun.

## Long paths

- Robocopy handles them with no extra flag.
- This PC has `LongPathsEnabled=1`, so plain .NET file calls on a >260 path
  worked. That registry value is off by default on Windows 10 and 11, and the
  `longPathAware` manifest entry does nothing without it. The app must
  therefore use `\\?\` paths for its own file work (test file, scan) and not
  rely on the manifest alone. The manifest entry stays, as the spec requires.

## Cloud-only OneDrive files

Tested with six generated files in a temporary folder inside the real OneDrive
sync root (four made cloud-only with `attrib +U -P`, one "always keep", one
ordinary). The folder was deleted afterwards.

- A cloud-only file as Robocopy, `attrib` and PowerShell see it:
  `0x00501620` = ARCHIVE, SPARSE, REPARSE_POINT, **OFFLINE**, UNPINNED,
  **RECALL_ON_DATA_ACCESS**. So it carries both attributes, not only the recall
  one, and `/XA:O` matches.
- `/L` with `/XA:O`: 2 would copy, 4 skipped. Real copy with `/XA:O`: the two
  local files copied, none of the four cloud-only files copied, and none was
  downloaded (size on disk stayed 0).
- Without the exclusion, Robocopy copied a cloud-only file by downloading it
  (the source became a local file). On a 200 GB profile that would silently
  pull everything down, so the exclusion matters.
- **Trap for our own code:** the harness (a .NET Framework exe) saw the same
  file as `0x00500020` — Windows hid OFFLINE, SPARSE and REPARSE_POINT from it.
  Only UNPINNED and RECALL_ON_DATA_ACCESS were visible. The pre-flight count of
  cloud-only files must therefore test `RECALL_ON_DATA_ACCESS` (0x400000), not
  OFFLINE.
- Fallback if `/XA:O` turns out not to work on some Windows 10 build: the
  pre-flight scan already has to list cloud-only files to show count and size;
  it can write them as full-path `/XF` entries into a Robocopy job file (`/JOB`),
  which has no command-line length limit. Pre-flight can detect the need by
  comparing its own count with the `/L /XA:O` result. Not built unless needed.

## Not yet tested

| Item | Why | When |
|---|---|---|
| `/B` with `/XA:O` on cloud-only files | OneDrive test was run not elevated only | M3, when the pre-flight cloud check is built |
| `/FFT` against a real FAT32/exFAT stick | no such volume here | M6 manual test |
| `/XA:O` on Windows 10 | only Windows 11 available | M6 manual test |
| EFS files | Windows Home has no EFS | M6 manual test |
| Destination full / unplugged | needs a small removable or virtual disk (elevation) | M2 |

## How to reproduce

```
dotnet build src\DttM1Harness -c Release
src\DttM1Harness\bin\Release\DttM1Harness.exe all D:\DttM1
src\DttM1Harness\bin\Release\DttM1Harness.exe onedrive "%OneDrive%" D:\DttM1
```

The work folder (about 3 GB) can be deleted afterwards. Latest non-elevated
output: `tests/fixtures/m1/harness_report_not_elevated.txt`.
