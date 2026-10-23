# Managed SharpEmu integration

Official source: `https://github.com/sharpemu/sharpemu.git`.
The initial supported upstream revision is
`b6347f9f9f1c3e92ee3909e427cfba99397432c7`
(`v0.0.6-mono-23-gb6347f9`, product version `0.0.6-mono`).
`global.json` requests SDK 10.0.103 with `latestFeature` roll-forward; SDK
10.0.402 was used for Windows x64 validation. The entry project is
`src/SharpEmu.CLI/SharpEmu.CLI.csproj`, whose assembly name is `SharpEmu`.
Its upstream publish produces a self-contained `SharpEmu.exe`, FFmpeg and
LibAtrac9 plugins, licences and the upstream demo. No third-party binaries or
upstream source tree are committed here.

## Installation and update ownership

The managed root is `D:\Games\PS5\Tools\SharpEmu`:

- `source`: clean official upstream `main` Git checkout; updates fetch without
  committing, patching or checking out another upstream branch.
- `installed`: active complete Windows x64 publish.
- `staging/<transaction>`: a source archive, frozen patch and publish for one update.
- `previous/<transaction>`: preserved complete previous installations.
- `logs/<transaction>.log`: persistent build, validation and failure output.

Settings has an independent editable SharpEmu executable path and the managed
build's version, full commit (abbreviated on screen), latest checked official main
commit, check/update timestamps, F10 status, build output and log access. Startup
uses the same read-only check policy as Kyty. Check first, then use **Update/build
managed SharpEmu**. An external executable selection remains selected; updating
always targets the managed directory. Select the managed executable explicitly
to switch back from an external build.

`SharpEmuUpdater` verifies the official origin and clean `main`, compares full
SHAs, fetches the selected revision, and rejects a revision that changed after
the check. It archives source into a unique staging directory, freezes the patch
bytes/hash, runs `git apply --check`, applies once, publishes and validates the
executable, required plugins, hashes and embedded installation metadata. The
focused SDL/Vulkan/input tests must pass before activation. A patch conflict or
new upstream F10 handling requires review; the updater never drops or repairs a
patch speculatively.

Publishing uses the source's `global.json`:

```powershell
dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r win-x64 --self-contained true -o <staged-publish>
```

Managed launches hold a shared filesystem lease until child completion. Updates
hold an exclusive lease and also reject a running managed SharpEmu process;
uncertain process inspection fails closed. Browser launches and path changes
are blocked while the update is active. A flushed activation journal precedes
directory renames. The old installation is moved to `previous`, then the complete
staged directory becomes `installed`; successful validation removes the journal.
Failures restore the old directory and metadata. A fresh updater recovers an
interrupted transaction before retrying. Launch refuses an outstanding journal.
Recovered builds and previous installations are never merged into another tree.

Generated transaction files are removed after success/failure, except when
recovery needs them or Windows file locks prevent deletion; the log identifies
retained paths. Prior known-good installations and logs remain for recovery.
Installation metadata lives inside the active tree in
`catalog-browser-installation.json`, so executable and metadata activate together.

## Launch and persistence

Both backends use the existing launch log, duplicate-launch guard and process
completion/return-to-library path. `LocalLibraryEntry.LastEmulator` is additive
JSON state, keyed by the existing durable library/release record. Missing values
default to KytyPS5. Picking an emulator or cancelling does not write preference;
only a returned, successfully started child process changes it. Failed executable
validation, cancelled folder selection or failed process creation preserve the
last successful choice. Invalid saved backend values fail explicitly.

SharpEmu resolves `eboot.bin` at the chosen game root and supplies its full path
through `ProcessStartInfo.ArgumentList`, with the emulator directory as working
directory. ELF/SELF compatibility is delegated to SharpEmu because the browser's
Kyty-specific structural validator can reject layouts SharpEmu supports. At this
revision the README's `--log-to-file` option is rejected by the parser. The
verified option is `--log-file`, so the actual argument vector is
`<full eboot.bin> --log-file`.
No game conversion or mutation is performed.

Orbit covers and Classic thumbnails use a 650 ms hold for the native emulator
selector. A short click retains normal launch. Movement beyond 10 pixels, leaving,
capture loss, cancellation and deactivation cancel the hold and suppress its
click. Orbit uses pointer events; Classic handles mouse and touch. More/Choose
emulator provides a keyboard-accessible alternative. Enter/Action and existing
Back navigation remain unchanged.

## F10 patch and Vulkan ownership

`f10-menu.patch` is GPL-2.0-or-later, matching the patched SharpEmu sources. It adds
an SDL-owned F10/Guide menu with only Continue and Exit to library. F10/Escape/B
resumes; all four cursor keys, D-pad and left stick select; Enter/A confirms.
Both rows accept pointer input.
Repeat keydown does not toggle repeatedly. Menu input is captured, held keys must
be released, and controller input must return to neutral before gameplay resumes.
Focus loss closes the menu. Exit uses the existing SDL window-close path.
The menu states that guest execution continues and unsaved progress is lost on exit.

The Vulkan presenter retains a host-owned image of the composed presentation
before drawing overlays. Menu state revisions can restore this image and present
again without another guest flip. Host redraws do not consume guest image versions,
acknowledge guest flips or advance guest frame statistics. Transfers use the
existing ordered graphics queue and synchronization barriers; the retained image
is retired with drained swapchain resources. Before a first frame, or after a
resize invalidates retained pixels, the menu draws on black until guest output
resumes. A lost/hung Vulkan device is outside this menu's recovery contract.
This Windows integration does not add a Metal menu implementation.

To check reapplication without modifying the upstream checkout:

```powershell
git -C D:\Games\PS5\Tools\SharpEmu\source apply --check <browser-root>\integrations\sharpemu\f10-menu.patch
```

For manual patch development, use `git archive` from the recorded upstream SHA
into an owned staging directory and apply the patch there. Keep `source` clean.

## Validation and limits

```powershell
dotnet run --project PS5CatalogBrowser.Tests -c Release -- --sharpemu
dotnet run --project PS5CatalogBrowser.BrowserTests -c Release -- --sharpemu
# Explicit real managed check/build/test/activation using the same updater as Settings:
dotnet run --project PS5CatalogBrowser.Tests -c Release -- --sharpemu-managed-update
```

Core tests cover legacy defaults, per-release persistence/restart, exact arguments,
missing/invalid inputs, patch/build failures, success/current revisions, concurrency,
activation rollback and restart recovery. WPF/WebView tests launch real synthetic
children through both backends, check successful preference writes, actual process
creation failure, native selector choice/cancel, Settings and cover hold/cancellation.
The patch's 17 focused tests include a real SDL/Vulkan presenter with injected
keyboard events and no subsequent guest flip, plus input capture and normal
window-close request. These are synthetic/device tests, not game compatibility.
Physical keyboard/controller delivery, native screen-reader support for the bitmap
menu and representative gameplay remain manual acceptance checks.

The pre-integration core suite has an existing
`OrbitFrontendCatalogTests.cs:14` compatibility-version assertion failure. It is
outside this feature and is kept separate from the focused tests. The existing
`--cover-grid` check also expects mixed local/website chips at startup, while the
starting commit already selects the explicit KytyPS5 source and renders colour
dots. Its first provenance assertion fails against that unchanged compatibility
implementation. The new pointer tests run against the current grid contract.

UI standards review: native labelled Settings controls, text availability/status,
polite progress announcements, 44px dialog actions, modal cancellation/focus,
and the explicit keyboard selector alternative address UI-INT-003/004/005 and
UI-A11Y-001/003/005/006/008/009/010. Physical touch and screen-reader validation
are not implied by the synthetic pointer and WPF checks.
