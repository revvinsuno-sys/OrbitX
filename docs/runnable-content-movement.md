# Runnable content and Installed membership

The Installed directory is independent of Downloads. My Library displays registered
records whose existing content is strictly below the configured Installed directory.
Root-prefix siblings, missing content, filesystem links and incomplete move records
are excluded. Changing the directory changes the view; it does not delete records or
move existing installations. Browse's library caption uses this same view.

Installed membership is inventoried from the configured directory, rather than assumed
from saved paths. Startup reconciles this inventory with settings and completed
installation journals. A missing registration is restored as verified only after its
complete journal file set and hashes pass verification. Other discovered games are
registered as unverified local content, using installed metadata for their display
names. Missing old paths remain historical records; notes-only wrappers, links and
private staging folders do not appear as installed games. Reconciliation never moves
or deletes game files, source downloads or history.

While the app runs, a recursive filesystem watcher schedules a debounced inventory
refresh after additions, renames and deletions, including external Explorer changes.
Refresh defers during installation, extraction, relocation, deletion and download
maintenance. The watcher is rebound when the configured directory changes, is created
or disappears; buffer errors trigger a fresh inventory. Saves use current settings
after inspection so unrelated concurrent changes are preserved. Newly discovered
paths do not inherit verification from an unrelated or stale path. Existing records
retain historical evidence; ongoing file integrity is checked separately by Reinspect.

`--installed-inventory` selects Core reconciliation and Browser live-watcher tests.
With the app closed, `dotnet run --project scripts/InstalledLayoutRepair -- --reconcile-library`
performs the same reconciliation and prints its evidence report.

After durable direct/native/import completion, a standalone `.elf` may move to Installed.
Already-extracted folders are checked at startup and after explicit ZIP/RAR extraction.
The folder scan must find exactly one `eboot.bin`; installation is independent of Kyty's executable compatibility. All files are hashed, and the complete
file/directory set is verified. PKG, FFPKG, exFAT images, archives and unconfirmed ZAR
builds do not qualify. Nothing is automatically extracted, converted or launched.

Only content under Downloads is swept. Shared library content, multipart records,
active/incomplete transfers, deletion intents and links are rejected. No filename,
title or catalogue label is treated as proof of compatibility. Original external-import
files and source archives for explicitly extracted folders are retained.

`LocalLibraryEntry.ContentMove` records source, reserved destination, configured root,
file sizes/SHA-256 and directory set. The Pending intent is saved before a Windows
no-overwrite rename. No copy/delete fallback is used: source and destination must be
on the same volume. The native API's copy and overwrite flags are both disabled.
If the original basename is occupied, a numbered destination such as `game (1)` is
selected before saving the intent. Existing files, folders and retained library
destinations are skipped. A destination created during the subsequent verification
still causes the atomic rename to fail without overwriting; the source is preserved.

Before relocating a game folder, archive wrapper directories are removed so the
installed folder directly contains `eboot.bin`. A companion `decrypted/eboot.bin`
stays with that same game. Separate sibling game
roots are left unchanged. Wrapper-side notes and other files are preserved under a
collision-free `Archive extras` directory. New game folders use the metadata title
when available, otherwise the original game-directory name.

Layout changes save a `Normalizing` content-move intent before any rename. Startup
restores the original layout after an interrupted change; it does not guess that
normalization succeeded. Existing verified moved folders can be repaired with
`dotnet run --project scripts/InstalledLayoutRepair -- --apply` while the app is closed
(omit `--apply` to inspect the plan). Repair verifies the old complete manifest before
changing paths, then verifies all file hashes against the remapped manifest before
publishing it. File content, original downloads and installation history are retained.
See [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw).

The destination is verified before one atomic settings save publishes its library
path and Completed move. Directly moved file downloads become hidden `Installed`
history records. Their original download destination/completion reservation remains
immutable for A4/A5 cleanup; the move manifest and library artifact record carry the
current path. Extracted folders do not hide the retained source archive's download.
Deletion of a moved library item targets its installed content, not that archive.

Restart may finish registration of an already-renamed destination only after validating
the saved paths and complete manifest. Source-present, conflicting or modified states
remain preserved for attention; recovery does not delete or guess a replacement.
Moved content uses its own manifest for Reinspect. Existing exFAT installation journals
and their source-deletion policy are unchanged.

Validation includes real WebView2 ELF completion, external import/source preservation,
folder assets, malformed executables, collisions, shared/active ownership, save failures
before and after rename, restart recovery, hidden moved history and Installed-only WPF
membership. `--runnable-content` selects Core movement tests; the Browser
`--installation-history` checks also exercise runnable completion and library filtering.

Installed folder inspection reads `sce_sys/param.json` when exactly one eligible game
root exists. The library card uses its localized title and title ID, including for
previously installed extracted archives. The ID can match catalogue artwork and
Kyty compatibility; several releases of the same game are allowed, while conflicting
games remain unmatched. Missing or malformed metadata retains the original label.
These display fields are rebuilt during inspection and do not change installation
journals, hashes, saved download ownership or game files. No emulator launch is needed.
`--installed-identity` runs the focused identity regression checks.

Historical sweep before the PKG/RAR change (15 October 2026): no existing item qualified. The retained PKG/RAR/ZIP files are
unsupported as runnable content; the extracted `ccleste` `eboot.bin` failed the existing
KytyPS5 ELF/SELF structural validator. No game files were moved or removed in this sweep.

See [PKG and RAR installation](package-and-archive-installation.md) for the subsequent change: the structural compatibility gate is removed; PKG installation and RAR extraction are explicit actions. Failed launches preserve installed games.
