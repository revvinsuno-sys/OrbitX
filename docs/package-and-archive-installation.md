# PKG and RAR installation

Installation success is independent of the browser's Kyty ELF/SELF structural checker.
Completed ELF downloads and complete extracted folders can move
to Installed after ownership and full-content verification. Images and packages still
normally require a readable game tree with `eboot.bin` and `sce_sys` resources. A library
folder's package batch also accepts nonempty DLC/data packages without a game executable.
The app does not
launch content to decide whether installation succeeded. Failed launch attempts preserve
the installation and its library entry; deletion remains an explicit user action.
Extracted folders without an unambiguous `eboot.bin`, including package-only folders,
are retained as one library folder for inspection rather than rejected as incomplete.

## Supported workflows

- Downloads: **Extract and install** handles ZIP and single-volume RAR4/RAR5,
  including compressed and solid RARs. **Retain source archives/images after installation**
  controls source retention. When unchecked, ZIP/RAR sources are deleted only after the
  installed file set and SHA-256 manifest are verified and their library registration is
  durable. Cleanup holds the exact source file exclusively and compares its size and
  SHA-256 against the original download record; changed, shared or active sources are
  preserved. Deleted archives leave Downloads; retained archives stay visible there. Cleanup failures remain
  visible for an explicit **Check extracted files** retry. Startup can hide an already
  absent source's completed history, but never resumes file deletion. Extracted game
  folders use the existing journalled, no-overwrite, same-volume relocation. Archives
  containing PKGs expose **Install** on their My Library folder. Its dialog lists only
  unextracted packages and permits one or multiple selections. Each chosen package uses
  the existing verified installer, extracting into its own subfolder named after the PKG;
  existing folders are preserved with an unused numbered destination. Retention applies
  to each original PKG. The parent stays one library entry with durable per-package
  registration linked to the installation journal. Successfully installed retained PKGs
  are recognised by their recorded SHA-256 and omitted from subsequent selections.
  Failed/unselected PKGs remain pending; Install disappears when none remain. exFAT
  images inside a folder can still be selected through **Install from file**. Separate
  download alternatives are never combined.
- **Install** / **Install from file**: PS4 CNT fake packages (automatic fake-package key
  extraction or an explicitly supplied 32-character passcode), PS5 FIH debug packages
  (standard all-zero or explicitly supplied passcode), and the existing exFAT images.
  PKG installation works without OSFMount. Standalone packages and folder batches
  automatically use the recovered fake-package key or standard debug passcode without
  prompting for credentials. Explicitly supplied passcodes remain supported by the
  installer API, in memory only and absent from settings/journals;
  unreadable packages remain pending with the failure in installation status.
- PKG is decoded into a private `.package-read-<random>` folder inside Installed, then
  passed through `ImageInstallationCoordinator`. It uses the configured Installed games
  directory and retention preference without a confirmation dialog. The entire extracted folder, including wrapper directories and companion files, is moved; the title determines
  the destination folder; a numbered suffix avoids existing folders without overwriting.
  Active extraction/installation progress appears within the matching Orbit Downloads/Library
  game card, with installation cancellation available there, and in Classic. Copy, complete SHA-256 verification, atomic finalisation and
  persisted library registration precede any original deletion. Temporary package output is removed
  before finalisation. Source identity and hash must still match before deletion.
- Post-extraction verification and archive cleanup report their current file, bytes,
  speed and elapsed time inside the matching game card. **Cancel verification** stops
  before a move/deletion when possible; an already-renamed folder finishes verification
  and durable registration. Closing requests the same cancellation and then closes
  automatically once ownership is released, preserving source files on cancellation.
- Library folders expose **Launch** when they contain launchable game roots. One root
  launches directly; multiple roots open a single-selection dialog. DLC-only folders do
  not appear as launch targets. A failed reinspection disables package launch targets.
  Folder reinspection verifies the original moved files, expected PKG deletions and each
  installed package manifest together. Installed package and launch state survive restart;
  launch eligibility still does not establish emulator gameplay compatibility.
- exFAT continues using read-only OSFMount through its authenticated helper. KytyPS5,
  A1–A5 download ownership/integrity and D1 HTTP resume are unchanged.

## Limits

Encrypted retail packages requiring console-specific image keys are not supported.
Neither this integration nor its dependencies obtain keys from a console, provider or
emulator. Unsupported/wrong-passcode/truncated packages fail with originals preserved.
Update/DLC-only packages without a complete game root are not merged into existing games.
FFPKG/UFS, FFPFS, FFPFSC and 7z are not new installation formats in this change.
Multipart RARs remain rejected rather than discovered or assembled. Encrypted ZIP
(AES/ZipCrypto) and RAR archives try the release-supplied password followed by the
ordered **Settings → Archive passwords** list. The defaults are Orbit's
[six documented source passwords](https://github.com/saawant12/orbit-store-ps5#automatic-archive-extraction-120).
Each candidate is tried once, with case-sensitive deduplication. A failed attempt
cleans its private staging directory before the next starts; only a completely read,
validated extraction is published. Exhaustion returns the persisted **Password protected**
badge without a warning popup. Cancellation and filesystem/path failures stop the operation.
Legacy ZIP weak password checks can fail later during decoding/CRC validation; those
failures retry while reading encrypted content. An encrypted corrupt archive may also
exhaust the candidates without being distinguishable from an unknown password.

The list allows 64 entries of at most 1024 characters, preserves spaces/case, and
saves when the field loses focus. Empty lines are ignored; an empty list disables
configured guesses. User-edited candidates are stored locally in app settings.
Release-supplied passwords stay transient and are resolved using recorded source,
entry and artifact IDs; they are never copied to job/library JSON or frontend DTOs.
Already downloaded archives can be retried without another download.

RAR entry decompression uses synchronous reads on the extraction worker. The pinned
SharpCompress 1.0.0 asynchronous RAR decoder can emit extra bytes and false CRC/size
failures for some valid compressed RAR5 entries; a synthetic 16 MiB solid archive
regresses this with exact-length and SHA-256 checks. Disk writes remain asynchronous,
and cancellation is checked between bounded reads. Failed reads cancel the reader
before stream disposal, avoiding an attempted drain of the remaining entry.
The extra SharpCompress input buffer is disabled for RAR extraction because its
encrypted block reads can fail across the buffer boundary, returning zero decoded
bytes from valid files. FileStream buffering remains enabled. A synthetic encrypted
RAR5 regression crosses that boundary and verifies all output lengths and SHA-256
hashes after a wrong-password retry.

Legacy encrypted RARs receive an explicit streaming plaintext CRC check because the
pinned reader suppresses its own CRC checks on encrypted entries. Wrong-password
CRC/size/decoding failures while reading encrypted entries try the next candidate;
unencrypted CRC failures remain integrity errors and cannot publish content.

RAR paths are validated before extraction, including Windows device names, alternate
data streams, traversal, duplicate case-insensitive destinations and links. Entry counts,
declared output bytes, disk space, actual lengths and the reader's CRC/BLAKE2 checks gate
publication. RAR/PKG expansion is capped at 2 TiB and one million entries. ZIP retains its
existing 8 GiB/20,000-entry policy. PKG preflight validates paths and sizes; PS5 extraction
must match the inspected file set. Sources remain held against writes/replacement while
being decoded. No extracted executable is run by the installer.

PKG reading needs temporary space plus the final installation (roughly twice the expanded
size before cleanup). PS5 data-first outer images use a 64 KiB decoded-block cache;
NAPS and PFSC v2/v3 inner images use a 256 KiB decoded-block cache and 64-bit offsets.
Extraction copies through a 1 MiB buffer. Layout metadata is limited to 64 MiB and
block indexes scale with block count. Cancellation is checked between streamed blocks;
an individual decode/write already in progress must finish. Output is never registered
while cancellation/cleanup runs. Unsupported NAPS geometry and Kraken blocks fail explicitly.
Finalized NAPS layouts use the declared file-offset count (including the mount-end
marker), a separately 16-byte-aligned block table, and the u2c map for absolute logical
block positions. Stored run bases retain the full 18-bit fractional offset. The reader
uses the packed per-chunk compression flags and 17-bit first-chunk size directly;
it does not guess decoder modes. Unindexed padding is not synthesized as game data.
Inner inode tables restart at each filesystem block, skipping each block's unused tail.
Streaming does not add support for missing compression formats.

## Dependency provenance and reproducibility

`ThirdParty/package-reader-sources.json` pins each copied source file's SHA-256, repository,
commit and the downloaded GitHub source archive's SHA-256. Three LibProsperoPkg files
carry a local streaming patch; their original hashes are retained as `UpstreamSha256`
and their patched hashes as `Sha256`. `ThirdParty/LibProsperoPkg/local-streaming.patch`
records these changes. Local streaming readers live outside the upstream snapshot.
Git preserves those source bytes, including upstream trailing spaces/EOF formatting;
those two whitespace checks are scoped out only for the pinned `upstream` snapshots.
The local reader projects target .NET 10; framework analyzers are disabled only for vendored
code, with legacy API/compiler warnings scoped to these dependency projects. PFS writers
are retained as dependencies/inputs for synthetic round-trip tests, not exposed as app
operations. No upstream native binaries, `right.sprx`, source archives or build outputs
are committed.

- [LibOrbisPkg](https://github.com/maxton/LibOrbisPkg/tree/643477263b2644e0803e0f58b8726ea4e3f3b7d4),
  commit `643477263b2644e0803e0f58b8726ea4e3f3b7d4`. The upstream LGPL-3.0 licence is
  preserved in `ThirdParty/LibOrbisPkg/LICENSE.txt`.
- [LibProsperoPkg 2.6.0](https://github.com/SvenGDK/LibProsperoPKG/tree/748eabf1b7d17819528cabf367d8e27109d8fce3),
  commit `748eabf1b7d17819528cabf367d8e27109d8fce3`. Its GPL-3.0-or-later licence and
  derived-code/Kraken notices are preserved in `ThirdParty/LibProsperoPkg`.
- [SharpCompress 1.0.0](https://github.com/adamhathcock/sharpcompress/tree/b6cc95af73950c914e0c6ce7ea3511528ede1121),
  MIT licence, pinned NuGet reference. Its stream reader handles decoding and payload
  checksums; this app owns all destination paths and writes. WinRAR is not a runtime
  dependency. The tiny RAR5 test fixture contains generated text only.

To reproduce the package dependencies, acquire the GitHub archive for each exact commit,
check `ArchiveSha256`, and copy only the manifest's files beneath its `SourcePrefix` into
the corresponding `upstream` directory. Preserve licence/notice files and local reader
project files. Apply `git apply --unidiff-zero ThirdParty/LibProsperoPkg/local-streaming.patch` from the
repository root after copying the original files. Run `pwsh -File scripts/verify-package-readers.ps1`, then normal `dotnet build`
or `dotnet run`; NuGet restores SharpCompress. Dependency upgrades are explicit changes:
review contracts, update pinned sources and hashes, and rerun package/archive tests.
Runtime never downloads replacement code. GPL obligations must be reviewed before any
distribution of the combined application.

## Validation

`dotnet run --project PS5CatalogBrowser.BrowserTests -- --package-folders` exercises the
Orbit Install/Launch controls and native selection dialogs, partial/multiple selection,
synthetic PS4/PS5 games and executable-free DLC, source retention/deletion, no-overwrite
destinations, grouped persistence, failed packages, tamper detection and restart.

`dotnet run --project PS5CatalogBrowser.Tests -- --packages` exercises real readers against
generated PFS v1/v2 packages, nested PFSC content, known/wrong passcodes, a complete
encrypted PS4 fake PKG, verified installation with source retain/delete, RAR4 stored and
RAR5 compressed payloads, unsafe paths, header/payload CRC failures and cancellation.
All game bytes are synthetic. This is not commercial-game or gameplay acceptance.

`dotnet run --project PS5CatalogBrowser.Tests -- --streaming-readers` checks virtual
5/12 GiB images without allocating or writing their full contents, reads past 4 GiB,
encrypted outer blocks, large file metadata, reference reconstruction/decompression,
bounded input reads, malformed geometry/digests, and cancellation.

`dotnet run --project PS5CatalogBrowser.Tests -c Release -- --validate-package-extraction "<package.pkg>" "<scratch-root>"`
uses the application's package mounter to extract every file, verify the inspected
file set and sizes, and remove its private temporary tree. The original package is
read-only; no library entry is registered. Ctrl+C requests cancellation and cleanup.
Full mounter extraction passed for Astro's Playroom PPSA01325 A01905 (34,364 files,
29,567,463,782 bytes) and Super Meat Boy 3D PPSA31162 A0200 (148 files,
4,727,725,299 bytes). This verifies package extraction, not emulator gameplay.

`dotnet run --project PS5CatalogBrowser.BrowserTests -- --runnable-content` exercises the
actual WPF view model/window, native ELF completion, ZIP/RAR actions, Kyty-incompatible
game movement, PKG installation without OSFMount, failed-process launch preservation and
restart persistence. Existing Core, installation and Browser regression checks remain
separate. The full Browser suite has a known baseline failure at
`LiveOrbitIntegrationTests.cs:67` (refresh preserves download history and library),
reproduced before this change.

The Orbit React UI integration remains a separate milestone by user direction.

## Download and installation lifecycle

While an image installation is active, other eligible PKG/exFAT controls show
**Queue Install**. Queued sources stay on their existing cards with **Cancel Install**
and a **Queued for installation** status badge. Cancellation removes that queued
request without opening or changing its source. The FIFO queue starts the next image
after success, failure or cancellation of the current attempt; each image rechecks
eligibility and uses the existing installation, verification and retention flow.
An active folder package batch finishes before queued individual images start.
The queue lasts for the current app session. Closing clears queued requests before
cancelling the active installation; restart never silently resumes them.

`dotnet run --project PS5CatalogBrowser.BrowserTests -- --installation-queue` verifies
the real Orbit controls and Classic bindings, FIFO order, cancelled/duplicate queued
items, automatic advance after failure/cancellation, retry cancellation, shutdown,
and unchanged synthetic sources. UI review: native buttons retain keyboard access,
the adjacent text badge uses `role="status"`, and Classic exposes polite live status
(UI-A11Y-001/008/009). The queued badge reuses the existing status colors and spacing.
The existing `--package-folders` test currently fails during its initial library
inventory assertion (`PackageFolderTests.cs:45`), before installation; the same
failure reproduces against the pre-queue Release app.

The first opening of Downloads and explicit Scan downloads inventory each top-level file and extracted folder without moving or
installing folders. Refresh reads filesystem metadata without traversing folders, opening archives or hashing content. Tracked sources are skipped. Unknown file types remain visible without gaining install support.
Nested extracted files belong to their folder card; private transfer staging stays
owned by its transfer. Scan restores cleared records whose source still exists.
Install moves the existing card to Installations. Failed/cancelled attempts stay
there with local retry and confirmed deletion; retry reuses the journal record.
Completed attempts leave Installations and appear in My Library. Retained original
archives/images remain in Downloads. Clear history cannot conceal existing sources.
Whole-folder relocation preserves extraction wrappers and companion files at the
installed destination; the original archive is removed only after full verification
and durable registration. No scan infers archive ownership from a folder name.

Downloads presentation can match a PPSA/CUSA ID in the filename/title against enabled
catalogue games. Multiple release options for one game do not make the game ambiguous;
conflicting games or multiple title IDs remain unresolved. This fallback never changes
the admitted release, checksum, or completion evidence. Find game opens the matching game
page, or editable Browse search if no unique page exists. Missing completed files offer
Retry to return to their game page for a fresh download selection. Completed local
sources offer the existing exact-path deletion confirmation. Empty extracted folders
are labelled and offer deletion rather than installation.
