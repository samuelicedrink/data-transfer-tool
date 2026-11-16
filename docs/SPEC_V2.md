# Data Transfer Tool v2 — Select screen redesign

v2 keeps everything in v1 (`docs/SPEC.md`): the five steps, Robocopy as the copy engine,
pre-flight, report, resume and credentials. Only the **Select** step changes. v1 stays
available (tag `v1.0.0`, branch `v1`, `release\v1\`); v2 is built into `release\v2\`.

Principle (owner, 2026-11-15): **the tool never chooses for the technician.** It shows facts
and an estimate; the technician ticks what to copy. Nothing is ticked or skipped because of
its score.

## 1. User picker (top left of the Select step)

- Lists every profile that has signed in on this PC (the profile list the tool already reads,
  `HKLM\...\ProfileList`), excluding system profiles.
- For each user: account (`PC\anna`, `CORP\anna`), **access level**, last sign-in date, and
  "signed in now" when a Windows session is open. An "All users" entry comes first.
- Access level:
  - **Local admin**: the account, or a group it belongs to, is in the local Administrators
    group (SID `S-1-5-32-544`). Nested groups are followed; the group is named, e.g.
    "Local admin (via CORP\Domain Admins)".
  - **Standard user** otherwise.
  - **Unknown (domain not reachable)** when group membership of a domain account cannot be
    resolved. Never guessed.
- Changing the user recalculates only the Relevant and Advice columns. Picking a user never
  ticks or unticks anything, and *Tick user data* keeps covering every profile.
- A SID is never shown; account names are shown as Windows names them.

## 2. Folder and file tree with columns

| Column | Content |
|---|---|
| Folder / file | Tick box and name, as in v1. Opening a folder also lists its files. |
| Modified | Last modified date; for a folder, the newest file inside it. |
| Size | Bytes of the file; for a folder, the total of everything inside it. |
| Relevant | Estimated % that the item belongs to the selected user (section 3). |
| Advice | One short line naming the signals behind the score, e.g. "Owned by Anna, video projects". |

- Sizes, dates and scores come from a background scan and show "…" until ready. Folders the
  technician opens are scanned first; the screen never freezes.
- WinForms has no tree with columns, so the list is a tree-grid: the native tree view with the
  names and columns drawn by the tool (keyboard use, tick boxes and UI Automation as in v1).
- Files follow their folder (greyed box): they are copied or left out with it. Whether files
  get tick boxes of their own is an open question for the owner (DECISIONS.md, V2-M2).
- A folder lists its first 1,000 files by name; a line counts the rest. All are in its size.
- Cloud-only OneDrive files are counted but marked, as in v1 (they are not copied).

## 3. Relevance score

Each file gets a score from 0 to 100, starting at 50. The adjustments below are applied and
the result clamped to 0..100. The weights are a first guess, kept in one table in the core
library so they can be tuned after the manual tests.

| Signal | Adjustment |
|---|---|
| File owner is the selected user | +35 |
| File owner is another user's account | −40 |
| File owner is SYSTEM, TrustedInstaller or Administrators (installed programs) | −25 |
| Inside the selected user's profile | +15 |
| Inside another user's profile | −30 |
| Inside Windows, Program Files, ProgramData package caches | −20 |
| User-content type: pictures (all of them), video, audio, documents, archives, project files (`.prproj`, `.aep`, `.psd`, `.ai`, `.blend`, `.dwg`, `.als`, `.flp` …), mailboxes (`.pst`) | +10 |
| Program or cache file: `.exe`/`.dll`/`.sys` inside an install folder, browser caches, `node_modules`, temp folders | −30 |
| Known user-data location of an app (catalog below) | +20, and the app is named in Advice |
| Folder permissions name the selected user explicitly | +5 |

- **Folder score** = average of its files' scores weighted by size (by count when all are
  empty): roughly "what share of this folder's bytes looks like Anna's".
- **Advice**: 75 and above "Likely <user>'s"; 40 to 74 "Review"; below 40 "Probably not
  <user>'s" or "Program files", followed by the main signals.
- **Where the estimate is weak**, the Advice says so: files an admin copied in (owned by
  Administrators), FAT32/exFAT drives (no owner recorded), files with copied-in dates.
- Downloaded files are user data (owner, 2026-11-15): the "downloaded from the internet" mark
  does not lower the score.

### Known-app catalog (first version)

Adobe Photoshop and Illustrator presets, actions, brushes and workspaces; Premiere Pro and
After Effects profiles, auto-saves and presets; Office templates, `Normal.dotm`, signatures and
custom dictionaries; Outlook `.pst` files in AppData; browser bookmarks (not saved passwords);
Sticky Notes; saved games and `Documents\My Games`; OBS, VS Code and Notepad++ settings;
KakaoTalk, LINE and WeChat local data (KakaoTalk and LINE databases are encrypted to the PC:
Advice says "backup only, will not open on another PC"); Teams: "chats are stored in
Microsoft 365, nothing to copy". Every path is confirmed on a real install in the manual tests
before release.

## 4. Milestones

Same rules as v1: one branch per milestone, stop after each for the owner's go-ahead, then
merge to `main` and tag (`v2-m1`, …).

1. **V2-M1 User picker.** Profile list with access level, last sign-in and signed-in-now in
   the core library, with unit tests (simulated accounts for each case, plus read-only checks
   of the current user on the development PC; see DECISIONS.md). Picker on the Select step. v2 keeps its job and credential
   files apart from v1's so the two versions never resume each other's job.
2. **V2-M2 Tree-grid with Modified and Size.** Owner-drawn tree-grid, files under opened
   folders, background scan. UI smoke scenario updated.
3. **V2-M3 Relevance score and Advice.** Scoring engine and known-app catalog in the core
   library; tests with generated trees whose files are owned by different test accounts.
4. **V2-M4 Report, manual tests, release.** Report lists the user each source was scored
   for; manual tests T20 (two real users, standard and admin, plus a domain user) and T21
   (catalog paths on a PC with the apps installed); release `v2.0.0`.
