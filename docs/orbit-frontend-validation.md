# Orbit frontend validation

## My Library cover grid and controller focus (2026-10-17)

- Debug and Release builds pass with zero warnings/errors; TypeScript and all 101 upstream-file hashes pass.
- Full Core runner passes. Browser `--cover-grid`, `--library-cards`, `--compatibility` and `--game-return` pass. Controlled gamepads cover A/X/B, held buttons/sticks across focus loss, and focus restoration; this is not physical controller acceptance.
- The popup presentation test now expects zero confirmation prompts for the fixture's exact selected-release filename, matching the existing native admission policy. Separate assertions retain the blocked-popup status and absence of any ad-page request; provider behavior is unchanged. Debug build and `--presentation` pass. The full Debug Browser runner passes the grid, frontend/download-to-library, provider, source, popup and key checks, then fails at the existing `LiveOrbitIntegrationTests.cs:67` assertion (`refresh preserves download history and library`). The full suite is not green; later cases were not reached. The captured run is in ignored `UI/local-evidence/provider-popup/full-browser-debug.log`.
- Actual WebView2 screenshots with labelled synthetic games/artwork are in ignored `UI/local-evidence/cover-grid`: collapsed 1920×1080, 1280×720, 720×800 and 390×760; pointer website preview, keyboard/local preview, More, installation details, and the bottom/right unavailable preview. The grid remains stable and chip text contrast is measured at ≥4.5:1.
- Review covers keyboard operability/focus, distinct Play/More routing, reduced motion, viewport bounds, status/source labels and retained deletion safeguards. Screen-reader and physical controller acceptance remain manual.


Validated on Windows on 2026-10-15, starting from `bb68de81ab60c15741bb73ef6f6c845a6ae1191a` on `codex/exfat-game-installer`. All changes are local; no remote is configured or pushed. Source/build/security contracts and upgrade instructions are in [orbit-frontend.md](orbit-frontend.md).

The navigation/management follow-up starts at `001c62069e77d18312d9b9e6d00b0698fc82759d` on the same branch. It adds arrow/WASD and F2 coverage in all four actual layouts, faster catalogue projection/serialization and independent frontend startup. Orbit management calls the pre-existing native functions through bounded identity-only actions; it no longer hands operations to Classic. Targeted checks cover real synthetic direct transfer, native HTTP pause/resume and verified completion, file-preserving history clear, RAR extraction into My Library, PS4 and PS5 PKG installation through native passcode/destination dialogs, installation-history clearing with content retained, reinspection, provider-window isolation, native cancellation ownership, Settings content reuse/restoration, invalid actions, offline/restart and fallback. The targeted Core projection/package checks and Browser frontend/ownership/runnable-content/installation-history checks pass for this follow-up; the final Release build has zero warnings/errors. Previous full-suite results/known failures below remain the baseline, not a claim that the full suites were rerun.

## Continuous download-to-library acceptance

Starting from `0a1829d`, BrowserTests `--orbit-install-e2e` loads a temporary HTTP catalogue through the normal source reader and operates the real Store layout: Browse → game card → View releases → Download → native queue → extract/install → Library. It does not dispatch shortcut release events or insert/import completed downloads. Cases cover direct HTTP RAR, actual WebView2 provider-page/native-download RAR (including its real confirmation), and direct HTTP PS4/PS5 PKGs with the existing passcode/destination dialogs. Transfers use only local synthetic fixtures; live-host compatibility and game launch are not established.

The test reproduced an installation failure when two independent archives both contain a `game` folder. Destination selection now skips occupied/registered names and persists a separate numbered path. Atomic no-overwrite rename, source ownership, verification and same-volume requirements remain unchanged. Core tests cover existing file/folder collisions, retained missing library paths, restart, and a destination created after intent persistence.

The Release build passes with zero warnings/errors; focused Release `--orbit-install-e2e`, Core `--runnable-content`/`--completion`, and Debug `--orbit-frontend` regressions pass. All four flows pass with actual received bytes/SHA-256, completed download journals, verified installed-file manifests, retained original files, rendered Orbit My Library entries and persisted membership after restart. The test monitors Classic visibility and the selected interface throughout. Temporary settings/content are cleaned in `finally`; optional `PS5_ORBIT_MANAGEMENT_EVIDENCE` captures each completed download and installed card. This case also runs with `--orbit-frontend`.

```powershell
dotnet run --project PS5CatalogBrowser.BrowserTests -c Debug -- --orbit-install-e2e
dotnet run --project PS5CatalogBrowser.Tests -c Debug -- --runnable-content
```

## Navigation and measured delay

**Appearance (F2)** returns to the original interface chooser. Game release actions invoke the existing Windows download functions from Orbit; Downloads/My Library manage real native records without changing interface. Arrow keys and WASD share original spatial navigation, preserve editable input and scroll immediately. Keep-interface confirmation owns focus until answered.

The previous startup awaited all catalogue requests before initializing Orbit. Initialization now starts independently, and the pending-feed WebView2 test proves the interface becomes available before a stalled request completes. Catalogue work also repeatedly validated artwork and built provider hashes per release, exported duplicate native identity records and re-encoded catalogue responses on reload.

An opt-in live run measured the same 22,053 options before/after (single local observations, not a controlled benchmark):

| Work | Before | After |
|---|---:|---:|
| Native catalogue projection | 852 ms | 370 ms |
| Initial JSON encoding | 201 ms | 158 ms |
| JSON payload | 35,151,865 bytes | 23,993,725 bytes |
| Repeated unchanged encoding | rebuilt | cached, 0.001 ms |

The final live run still loaded all four configured sources and all four genuine layouts, and left the original user settings byte-identical. First catalogue projection/JSON parsing, image-host latency and upstream full-motion/blur treatments still cost time; this change does not claim zero-latency rendering or a measured overall frame-rate improvement. F2 → Motion → Reduced is available for less animation. No provider transfer or emulator launch was performed.

## Automated results

| Check | Result |
|---|---|
| `powershell -NoProfile -File UI/verify-upstream.ps1` | PASS: 101 original files match the verified source bundle hashes. |
| `powershell -NoProfile -File UI/build-frontend.ps1 -Restore` | PASS: locked npm restore with scripts disabled, original/adapter TypeScript checks, Vite production assets and packaging manifest. |
| `dotnet build PS5CatalogBrowser.slnx -c Debug` / `-c Release` | PASS: zero warnings/errors. |
| `dotnet run --project PS5CatalogBrowser.Tests -c Debug` | PASS: full Core suite, including identity/redaction projection, durable HTTP resume, A1-A5, installation, package/archive safety, launcher, cleanup and recovery. No elevation/driver mount was performed for this UI change. |
| BrowserTests `--orbit-frontend` | PASS: genuine React/WebView2 with existing encrypted local catalogue loader; exact clicked PS4 details and release controls, separate PS5 release selectors, search by title ID/no-match, platform/source filters, favourites through native persistence, original Appearance selection/confirmation, all four layouts, resize, modal/input Escape handling, F11/fullscreen restore, owned native Settings over Orbit, restart preferences, native rendering-failure fallback, offline snapshot retention and invalid bridge/API/navigation rejection. An active controlled native writer stays subscribed/owned and receives zero cancellation requests through UI/theme changes. Only its terminal callback releases ownership. |
| BrowserTests full runner | Reaches the existing failure in `LiveOrbitIntegrationTests.cs:67`, `refresh preserves download history and library`. It was reproduced on the starting work before this milestone. The new frontend tests, provider flow, source visibility/editor, artwork/popup and Orbit key tests pass before the failure. No all-green Browser-suite claim is made. |
| Remaining Browser checks | PASS separately: `--library-deletion-ui`, `--installation-history`, `--orbit-downloads`, `--orbit`, `--ownership`, `--runnable-content`, `--completion`, `--cleanup`, `--admission`, `--installation-ui`, `--direct-downloads`, `--browser-downloads`. The last flag exposes the existing native-download fixture runner without repeating the failed earlier suite. |
| BrowserTests `--http-resume` | FAIL: old test searches for **Pause selected** / **Resume selected**, but starting commit `bb68de81` already labels them **Pause** / **Resume** inside **More actions**. The test and these controls are unchanged by this milestone. Full Core HTTP-resume tests pass. |
| `git diff --check` | PASS. |

Run the new WebView2 tests and opt-in live verification with:

```powershell
$env:PS5_ORBIT_EVIDENCE = "$PWD/UI/evidence"
dotnet run --project PS5CatalogBrowser.BrowserTests -c Debug -- --orbit-frontend
$env:PS5_ORBIT_LIVE_EVIDENCE = "$PWD/UI/local-evidence"
dotnet run --project PS5CatalogBrowser.BrowserTests -c Debug -- --orbit-frontend-live
```

The opt-in live check reads configured catalogue URLs and selected keys through the existing Core services. It uses a separate temporary model/profile, never acquires the user's application-settings ownership, never starts a game transfer, and verifies the original settings file remains byte-identical. Its images contain runtime commercial artwork and stay local/ignored; no feed data, credentials, game files or artwork are committed.

Observed configured live sources: Homebrew 82 entries/82 releases; evoX 122/115; OptiStore 1,115/17,992; Orbit 1,598/3,825. The presentation contains 2,917 source-scoped game entries and 22,053 options, including explicitly identified metadata-only options for entries without artifacts. Store/Crossbar/Cinema/Panorama loaded real remote artwork (13/8/18/19 images respectively in the final captured views; asynchronous image counts vary with the host response). This is actual catalogue-to-React evidence, not a static mock page. It does not establish provider transfer or game launch compatibility.

## Visual review

Actual Windows management previews in ignored `UI/local-evidence/management` show the native queue and installed library. Their controls, progress, wrapping and contrast were visually inspected; queue/library viewport checks pass in all four layouts. Set `PS5_ORBIT_MANAGEMENT_EVIDENCE` to capture these during `--orbit-frontend`. Original layout `CapturePreviewAsync` images are available in `UI/evidence` using synthetic catalogue metadata and the original logo fallback. Local, ignored `UI/local-evidence/live-*.png` captures show the configured real catalogues and artwork.

| Layout | Captured web viewport | Observed result |
|---|---|---|
| Store | 1920×1080 | Original top navigation, featured hero, game rows, card ratios, selected tile/focus outlines and fallback artwork. |
| Crossbar | 1280×720 | Original category bar, source/platform controls, vertical selected list, ribbon background, Inter Tight typography and preview panel. |
| Cinema | 1260×800, a normal resizable desktop window | Original Sora headings, blurred artwork, filmstrip collections and full mouse-accessible navigation. |
| Panorama | 1920×1080 | Original large light headings, wide sections, grouped tile geometry, accent/background preferences and focus treatment. Horizontal sections intentionally extend beyond the viewport; this is upstream navigation, not page overflow. |

The [official appearance guide](https://github.com/saawant12/orbit-store-ps5/blob/e54f1ae1e8234da856e9c9dd39d2c9082b7a9b38/guides/appearance.md) and its Store, Cinema, Panorama and native Crossbar reference images were visually inspected against the actual captures. Browser reference access initially failed through browser-client's trust boundary; the available computer-use browser surface successfully displayed the official images. Store/Cinema/Panorama preserve their browser reference treatments. Crossbar's guide images show the native PS5 version; its browser controls differ, so native and browser geometry are not claimed pixel-identical. Different catalogue sources, titles, viewport/DPI and Windows action labels also intentionally differ. No layout recreation or original CSS edits were used.

Accessibility review followed the relevant keyboard/focus, dialog and reduced-motion rules in the UI standards skill. Native controls have explicit text/automation labels; fallback remains outside the WebView. Original web focus outlines, labeled filters, dialog/confirmation focus handling and reduced-motion choices are retained. Automated input/click events and rendered inspection establish functional coverage; human keyboard-only and screen-reader acceptance across every layout/device DPI remains a manual follow-up, not a claimed pass. Fullscreen Escape was checked against a real original Settings modal and editable input.

## Boundaries and cleanup

Downloads, Library, installation, extraction and launch controls in Orbit dispatch to the same native functions and confirmations as Classic Windows. Native pickers/provider windows/Settings remain Windows UI. No provider restrictions were bypassed, no live game download or emulator launch was tested, and KytyPS5 was not modified. Native state ownership and persistence remain canonical.

Original user settings/download/library records remained byte-identical during live acceptance. Reversible test data is isolated. WebView2 may retain some temporary profiles until its subprocess exits; existing Browser runners reported a provider-flow and a native-browser profile requiring post-process cleanup. Six earlier failed frontend test profiles also remain under the `PS5OrbitFrontend-*` temporary prefix; subsequent frontend runs clean their own fixtures in `finally`.

Automatic approval review rejected the scoped recursive removal of `UI/.source-acquire` as **blocked by policy**, without a more specific reason. Its source acquisition archive and extracted inner tarball (about 370 MB total) remain ignored and uncommitted. No alternate deletion mechanism was used to bypass the rejection. These acquisition files and leftover temporary test profiles are the outstanding cleanup limitation.
