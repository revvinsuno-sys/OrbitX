# OrbitX Settings UI

Starting commit: `0bbd0d9b2de60942ffa95efa7e8efb472721a913`.

Graphical Settings is a React page inside the existing Orbit shell, alongside Discover, Browse, Library and Downloads in all four layouts. It keeps the same WebView, header, footer, page history and controller loop. Eleven borderless tiles cover the seven existing categories plus Appearance, Language, Metadata (including IGDB media) and About OrbitX. Escape, right-click and controller B/Circle go from a detail to the hub, then back to the page actually visited before Settings. Native pickers and consequential confirmations remain native dialogs. Classic retains its WPF settings view; both presentations share the same model and settings store.

The supplied racing background stays. Graphical category art uses aspect-preserving SVG viewports of artwork-only regions in the original component sheet; the additional tiles use the existing Orbit glyph library. Fonts, form controls, buttons and interface preferences follow the active graphical layout. Raster labels, mock controls and mock readiness states are not used. Native Classic artwork remains embedded under `Assets/Settings`; the frontend build bundles the same source images.

`UI/windows-adapter/SettingsPage.tsx` and `settings.css` own the graphical hub and detail presentation. `GET/POST windows/settings` exposes explicit fields, facts and allowlisted actions through the existing trusted in-process request boundary (16 KiB body limit for this route; other requests retain their existing limits). Text commits follow the existing immediate-versus-blur behavior; Kyty drafts still require Apply. Actions are acknowledged before the existing native handlers run, so native pickers cannot block the request loop. The native source-editor busy state and emulator eligibility disable competing controls and are rechecked at dispatch.

Saved archive passwords never enter the WebView: state contains only masked row indexes. Add/edit/remove uses the existing ordered-list setter, retaining case, spaces, deduplication, validation and local persistence. Metadata reuses the real IGDB form and protected credential writer; saved secrets remain blank, and blank-secret saves retain the stored secret. Appearance and Language reuse their existing upstream components and local preference persistence. Source drafts survive category navigation; source save invokes the existing native handler. There are no backend feature or settings-schema changes.

## Existing control mapping

The following native bindings/handlers remain the reference ownership map. Graphical controls use the same model setters and invoke these handlers for picker/operation actions; Classic retains these controls directly.

| Existing control | New category/panel | Existing handler/binding |
|---|---|---|
| SourceList | Catalogue sources | `ItemsSource={Binding Sources}; SelectedItem={Binding SelectedSource}; SelectionChanged=Sources_SelectionChanged` |
| SourceTypeBox | Catalogue sources | `SelectionChanged=SourceType_SelectionChanged` |
| Choose catalogue file | Catalogue sources | `Click=ChooseSourceFile_Click` |
| SourceManagedKeyBox | Catalogue sources | `ItemsSource={Binding ManagedOrbitKeys}; SelectionChanged=SourceManagedKey_SelectionChanged` |
| DownloadSourceKeyButton | Catalogue sources | `Click=DownloadSourceKey_Click` |
| Import key file | Catalogue sources | `Click=ChooseSourceKey_Click` |
| SourceFallbackBox | Catalogue sources | `ItemsSource={Binding LocalOrbitFallbackChoices}` |
| AddSourceButton | Catalogue sources | `Click=AddSource_Click` |
| SaveSourceButton | Catalogue sources | `Click=SaveSource_Click` |
| Remove selected | Catalogue sources | `Click=RemoveSource_Click` |
| Test Connection | Catalogue sources | `Click=TestSource_Click` |
| Refresh Now | Catalogue sources | `Click=RefreshSource_Click` |
| CancelSourceKeyButton | Catalogue sources | `Click=CancelSourceKey_Click; IsEnabled={Binding IsFetchingOrbitKey}` |
| DownloadRootBox | Downloads | `Text={Binding DownloadRoot, UpdateSourceTrigger=PropertyChanged}` |
| Choose folder | Downloads | `Click=ChooseDownloadRoot_Click` |
| DownloadBandwidthBox | Downloads | `Text={Binding DownloadBandwidthMBps, UpdateSourceTrigger=LostFocus, ValidatesOnExceptions=True}` |
| ArchivePasswordsBox | Archive passwords | `Text={Binding ArchivePasswordsText, UpdateSourceTrigger=LostFocus, ValidatesOnExceptions=True}` |
| Clear browser site data | Embedded browser | `Click=ClearBrowserData_Click` |
| OSFMountPathBox | Image Installation | `Text={Binding OSFMountPath, UpdateSourceTrigger=LostFocus}` |
| Choose executable | Image Installation | `Click=ChooseOSFMount_Click` |
| Detect installation | Image Installation | `Click=DetectOSFMount_Click` |
| Test availability | Image Installation | `Click=TestOSFMount_Click` |
| Get OSFMount | Image Installation | `Click=InstallOSFMount_Click` |
| InstalledRootBox | Image Installation | `Text={Binding InstalledGamesRoot, UpdateSourceTrigger=PropertyChanged}` |
| Choose installed games folder | Image Installation | `Click=ChooseInstalledRoot_Click` |
| Retain source archives/images after installation | Image Installation | `IsChecked={Binding RetainOriginalImage}` |
| SharpEmuPathBox | SharpEmu | `Text={Binding SharpEmuPath, UpdateSourceTrigger=LostFocus, ValidatesOnExceptions=True}; IsEnabled={Binding CanManageSharpEmu}` |
| Choose SharpEmu executable | SharpEmu | `Click=ChooseSharpEmu_Click; IsEnabled={Binding CanManageSharpEmu}` |
| Start full screen | SharpEmu | `IsChecked={Binding SharpEmuFullscreen}` |
| CheckSharpEmuButton | SharpEmu | `Click=CheckSharpEmu_Click; IsEnabled={Binding CanManageSharpEmu}` |
| UpdateSharpEmuButton | SharpEmu | `Click=UpdateSharpEmu_Click; IsEnabled={Binding CanBuildSharpEmu}` |
| Open build log | SharpEmu | `Click=SharpEmuLog_Click` |
| TextBox | SharpEmu | `Text={Binding SharpEmuUpdateOutput, Mode=OneWay}` |
| TextBox | KytyPS5 | `Text={Binding EmulatorPath, UpdateSourceTrigger=LostFocus}` |
| Choose executable | KytyPS5 | `Click=ChooseEmulator_Click` |
| Test availability | KytyPS5 | `Click=TestEmulator_Click` |
| CheckKytyVersionButton | KytyPS5 | `Click=CheckKytyVersion_Click; IsEnabled={Binding CanCheckKytyVersion}` |
| UpdateKytyButton | KytyPS5 | `Click=UpdateKyty_Click; IsEnabled={Binding CanUpdateKyty}` |
| TextBox | KytyPS5 | `Text={Binding KytyUpdateOutput, Mode=OneWay}` |
| Start full screen | KytyPS5 | `IsChecked={Binding ArgumentFullscreen}` |
| Use structured performance settings | KytyPS5 | `IsChecked={Binding PerformanceArgumentsEnabled}` |
| ArgumentWidthBox | KytyPS5 | `Text={Binding ArgumentWidth, UpdateSourceTrigger=PropertyChanged}` |
| ArgumentHeightBox | KytyPS5 | `Text={Binding ArgumentHeight, UpdateSourceTrigger=PropertyChanged}` |
| ArgumentVblankBox | KytyPS5 | `Text={Binding ArgumentVblank, UpdateSourceTrigger=PropertyChanged}` |
| ArgumentPresentBox | KytyPS5 | `ItemsSource={Binding PresentModeChoices}; SelectedItem={Binding ArgumentPresentMode}` |
| ArgumentShaderBox | KytyPS5 | `ItemsSource={Binding ShaderOptimizationChoices}; SelectedItem={Binding ArgumentShaderOptimization}` |
| Performance overlay | KytyPS5 | `IsChecked={Binding ArgumentPerformanceOverlay}` |
| AMD CPU instruction patches | KytyPS5 | `IsChecked={Binding ArgumentAmdCpuPatches}` |
| Red Zone protection (Windows) | KytyPS5 | `IsChecked={Binding ArgumentRedZoneProtection}` |
| ShaderCacheFolderBox | KytyPS5 | `Text={Binding ArgumentShaderCacheFolder, UpdateSourceTrigger=PropertyChanged}` |
| Choose cache folder | KytyPS5 | `Click=ChooseShaderCacheFolder_Click` |
| AdditionalArgumentsBox | KytyPS5 | `Text={Binding AdditionalArguments, UpdateSourceTrigger=PropertyChanged}` |
| ArgumentsPreviewBox | KytyPS5 | `Text={Binding ArgumentsPreview, Mode=OneWay}` |
| ApplyArgumentsButton | KytyPS5 | `Click=ApplyArguments_Click` |

## UI standards review

The installed skill references `templates/ui-review-report.md`, but that template is absent. This focused review records materially applicable requirements directly.

- UI-A11Y-001/002/003/008: native buttons, checkboxes, password fields and disclosures; Tab traversal, arrow/Home/End tile focus, Enter/Space button activation, Escape and F11 retained. Gold focus borders remain distinct from cyan category selection. Native window controls remain available.
- UI-A11Y-004/005: white and pale-blue content on dark navy; selection also exposes an automation item status and breadcrumb. Real availability/ownership text accompanies status, without mock success indicators.
- UI-A11Y-006/010: 42px action buttons, persistent labels, reachable fields. The compact strip scrolls horizontally at narrow widths while details scroll vertically. Home uses four desktop columns and three compact columns, based on the actual scroll viewport.
- UI-A11Y-009/011: existing polite/assertive status regions are retained; no autonomous movement or animation.
- UI-INT-002/007/010 and UI-FEED-009: handlers, confirmations, long-operation eligibility, LostFocus/PropertyChanged persistence and explicit argument Apply remain under existing native ownership. Navigation cannot invoke builds, cleanup or transfer operations.

## Original native visual polish (historical)

Panel headings and subtitles share the icon row, reducing redundant vertical space in SharpEmu and across the other categories. Navy panels use one subtle gradient/border system. Metadata uses aligned label/value rows sized to their content, with one column in narrow windows. Real availability and ownership text appears in information badges; no colour or mock readiness value replaces the existing state.

Primary/secondary actions share 42px sizing, spacing and artwork-only icons sampled from the supplied sheet. Truncated category titles/subtitles expose full category names and summaries through tooltips and automation name/help text. About joins the same navigation, using the supplied gear artwork and actual version binding. A viewport-based hub calculation accounts for the scrollbar, eliminating the unused compact column. The Orbit footer uses a single ellipsized status line with full tooltip/automation text; Classic uses its existing status footer without a duplicate.

The controlled SharpEmu updater fixture invokes the actual Check/Update handlers through the existing updater seam, holds a synthetic fetch, verifies disabled competing controls and live output, then releases it with a failure and checks recovery. It never updates the installed emulator.

## Original native acceptance (historical)

Original redesign `dotnet build PS5CatalogBrowser.slnx -c Release`: passed, zero warnings/errors. That iteration left the frontend unchanged; its existing MSBuild integrity/copy target remains in use. No backend or upstream frontend files changed. `git diff --check` passed. An XML comparison against the starting commit confirms that every original Settings click/selection handler and bound property path is retained.

Use the BrowserTests **apphost executable**, rather than `dotnet ...dll`, for tests that copy themselves to synthetic emulator names. Those fixtures depend on `Environment.ProcessPath` identifying the apphost.

| Focused check | Result |
|---|---|
| BrowserTests `--settings-navigation` | Passed: seven-category hub, selected/focus presentation, native Enter/arrow/Tab navigation, all category views, About, minimum/normal/fullscreen layouts, scroll reachability, shared workspace ownership, F11, Back/Escape, status-refresh stability, argument drafts/Apply, masked password add/edit/remove/validation and restart persistence |
| Settings restart in navigation test | Passed in a separate apphost process and a fresh model for independent download/installed roots, bandwidth, password list, archive retention, SharpEmu fullscreen preference and applied Kyty arguments |
| BrowserTests `--kyty-update` | Passed: actual Update button, eligibility, progress/output, concurrency, success/failure/retry and managed-path rules |
| BrowserTests `--arguments` | Passed: actual Classic/Orbit child argv, UI editing/validation, preview, persistence and next launch |
| BrowserTests `--source-visibility` | Passed: source editor selection, unsaved edits, enable/disable save, retained entries, refresh races and re-enable |
| BrowserTests `--orbit-keys` | Passed: source Add/Save, managed keys, cancellation, manual selection, reuse and restart |
| Core Tests `--sharpemu` | Passed: executable arguments, update SHA, patch/build failure, activation rollback, success/retry and leases |
| Core Tests `--archive-passwords` | Passed: encrypted ZIP/RAR ordered retries, case/exhaustion, extraction bytes, cancellation and staging cleanup |

All four broader failures are **confirmed baseline** by execution in a separate clean worktree at the starting commit and a matching redesign run. No failure is classified as introduced or inconclusive. [Baseline comparison](settings-baseline-regressions.md) records the matching exceptions, source locations, command routing and preserved evidence. These unrelated failures remain failing and are explicitly excluded from this task's remediation scope.

Rendered evidence is retained in ignored `UI/local-evidence/settings-acceptance/screenshots/` (54 PNGs): Settings home and all seven details in Orbit at 1280x800 and 920x620; fullscreen at 1920x1080; Classic minimum/normal/maximized checks; gold focus; compact output disclosures; and real fixture-driven updater progress, disabled controls and expanded output. Visual review compared every desktop/compact page with the approved references. The full SharpEmu executable, metadata, actions and collapsed output fit at fullscreen; smaller windows retain vertical scroll reachability. Source names/locations use explicit ellipsis with full tooltips; path inputs retain native caret scrolling, and commit/output text wraps.

The focused test also asserts compact hub column use, visible detail viewports, absence of horizontal detail overflow, reachability of each panel's bottom, and readable expanded SharpEmu/Kyty output. The separate-process restart probe confirms download/installation paths, bandwidth, ordered passwords, retention, SharpEmu fullscreen and applied Kyty arguments. All original Settings handlers and bound property paths were compared against the starting XAML and retained.

Fixture settings are isolated; existing read-only availability/build metadata can still reflect installed tools. No live emulator update, install/cleanup, artwork regeneration or browser-profile deletion was performed for visual review. Physical gamepad input, screen-reader use, physical pointer hover and every native picker remain manual acceptance limits; native keyboard/focus, enabled/disabled states, masking, persistence and layout reachability have focused automated evidence.

The original redesign was committed locally as `e60a1940e074dce55716f0e3c970b1ecb4a542ae` on `codex/orbit-settings-redesign`. No Git remote is configured. Unique screenshots, logs, runner source and the pre-polish redesign backup are retained under ignored local evidence.

## Intermediate native entrypoint correction (historical)

User review exposed a navigation gap: the original focused test entered the redesign through the secondary native Settings action, while the primary graphical Settings navigation still displayed the old web intermediary page. The Windows build adapter now routes the primary Settings action in Cinema, Panorama, Crossbar and Store directly to the redesigned workspace, retaining the Orbit interface and existing native handlers. Upstream frontend sources remain unchanged.

Appearance and language remain in the existing F2 preferences dialog, which also retains the existing IGDB form and catalogue-refresh action. F2 works from the redesigned workspace; About OrbitX provides a labelled shortcut to the same dialog. Saved secrets remain blank in the password input.

Release build and `--settings-navigation` pass after this correction. The focused suite now clicks the actual primary graphical Settings action in all four layouts, verifies that neither Classic nor the old intermediary page/modal is shown, checks preferences access and masking, and reruns category, compact-window, keyboard and restart-persistence checks. New screenshots are retained in ignored `UI/local-evidence/settings-entry-fix/`; the Store entry screenshot was visually reviewed. The four broader baseline failures documented separately were not rerun or repaired for this routing correction.

## Graphical page acceptance

The subsequent user review required a page in the existing graphical shell, rather than the native handoff. That handoff, copied native navigation and workspace reparenting have now been removed. The current implementation is described at the top of this document; the two prior acceptance sections above are historical.

- Release solution build: passed, zero warnings/errors; all 101 upstream frontend files remain unmodified.
- `--settings-navigation`: passed. Actual Settings navigation in Cinema, Panorama, Crossbar and Store preserves the same header and WebView. All eleven borderless tiles, keyboard focus restoration, Escape, right-click, simulated controller B, fullscreen, dropdown dismissal and compact scrolling are checked.
- The same suite exercises actual graphical download/bandwidth/installed-root fields, invalid bandwidth preservation, source draft retention and native Save, source-editor busy state, masked password add/remove, protected IGDB save/blank-secret retention, original Appearance confirmation, original Language persistence/reload, and Apply-only Kyty arguments. A separate-process restart verifies paths, bandwidth, source save, passwords, retention, SharpEmu fullscreen, arguments and protected IGDB settings.
- `--arguments`, `--source-visibility`, `--orbit-keys` and `--kyty-update`: passed. The arguments suite also confirms a game exit from graphical Settings returns to graphical My Library.
- The updater fixture invokes the real graphical Check/Update actions through the existing updater seam, holds synthetic process output, verifies disabled competing actions (including API rejection), then releases a controlled failure and verifies recovery. No installed emulator is updated.
- All four broader commands were rerun; their original failures remain confirmed baseline. An additional disposed-compatibility-refresh defect was independently reproduced by an external probe against the untouched starting assembly and current assembly. See [the comparison evidence](settings-baseline-regressions.md).

Ignored `UI/local-evidence/settings-web-page/graphical-*.png` contains 33 final WebView screenshots: the hub in all four layouts, hub plus eleven details at desktop/compact window sizes, both output disclosures, and live fixture update output. Every desktop/compact detail was visually reviewed; checks also cover panel overflow and bottom reachability. Artwork viewports preserve aspect ratio. `review-*.png` contains review contact sheets. Screenshots use bounded browser capture; earlier capture-tool timeouts are not counted as product passes. Physical controller input, screen-reader use and every native picker remain manual acceptance limits.

The baseline checkout was removed. Automatic approval review rejected cleanup of one stopped test profile (`C:\Users\revvi\AppData\Local\Temp\PS5SettingsTabs-733821aef3984dcfa062e6a1b25fea19`) and obsolete hashed static assets in the app/test `OrbitFrontend` output directories, reporting only `blocked by policy`. These generated leftovers remain a storage limitation; runtime assets are selected and verified by the current manifest. Unique screenshots, logs and probe source are retained. No remote is configured, so the result is local to `codex/orbit-settings-redesign`.

UI standards review: existing graphical shell typography and controls, borderless artwork tiles, persistent field labels, text alongside status icons, original accessible comboboxes, visible focus indicators, semantic disclosures, and actual prior-page Back behavior meet the materially applicable UI-A11Y-001/002/003/005/008/009/010 and UI-INT-003/004/007/010/011 requirements. Native confirmations and model validation remain authoritative. The skill's referenced review template is still absent, so this focused review records the applicable requirements directly.

## Consistent category tile rail

Home and detail pages now share the existing graphical `game-rail` scrolling surface. Artwork surfaces use the Store's 13rem square unit (Panorama uses its existing `--pn-unit`); SharpEmu and Kyty span two units plus the standard gap. Opening a panel preserves tile width, height and subtitles. Artwork is fitted at a smaller size, with explicit sprite clipping so adjacent images cannot bleed into the fitted area. Borders remain absent; keyboard focus remains visible.

The horizontal scrollbar is hidden consistently with other graphical rails. Mouse wheel and trackpad scrolling reach overflow categories, and Left/Right wrap through the eleven categories; Home/End and the existing controller input loop remain supported. At either wheel boundary, vertical page scrolling remains available. Settings handlers and persistence are unchanged.

Release build and `--settings-navigation` pass. Added acceptance checks cover every category's stable home/detail geometry, square/double-width artwork surfaces, hidden scrollbar, wheel movement and keyboard wrap in all four layouts and at 1280x800/920x620. The full suite also passes Back, masking, actual settings saves, restart persistence and expanded update/build output. The test now awaits WebView NavigationCompleted after reloads: earlier attempts could interrupt the language reload with the next layout reload and trigger ConnectionAborted; no production navigation changes were made. Screenshots of all eleven details and home, output views and review sheets are retained under ignored `UI/local-evidence/settings-tile-polish/`; build and acceptance logs are adjacent. Desktop/compact screenshot review found readable selected tiles, fitted artwork, wrapped metadata and reachable panels/output. Physical controller input remains unverified.

Focused UI standards review: UI-A11Y-001/002/003/006/008/011 apply to keyboard reachability, visible focus, semantic buttons, target sizes and reduced motion. Native horizontal scrolling is immediate and does not animate; full labels/subtitles remain available in text and tooltips. No artwork was regenerated and no upstream frontend files were edited.

## Home grid and detail strip revision

The latest requested layout supersedes the equal home/detail geometry above: Settings home now uses large 2:1 cards in a responsive vertical grid (four columns at wide desktop, three at desktop, two at compact width). Titles and current statuses sit inside the bottom left. Detail pages use one horizontally scrolling row of equal 1:1 cards with inset titles and no status or description. Existing borderless styling, artwork, keyboard focus, Back and controller input ownership are retained; Up/Down navigates the home grid and Left/Right wraps through categories.

Statuses use actual source/password counts, configured download location, device browser profile, OSFMount signature verification, emulator executable availability, active interface/language, saved IGDB credential configuration and build version. Configuration and executable detection do not imply a successful live service connection or emulator launch. The settings snapshot exposes only a credential configuration label, never the secret.

Release build, `--settings-navigation`, `--branding-startup`, restart persistence and `git diff --check` pass. Focused tests now assert the new aspect ratios, multi-row/single-row layouts, caption containment, absence of detail statuses, home grid keyboard movement, horizontal rail reachability and IGDB status after a real settings save. Screenshot review covers all eleven details and home at 1920x1080, 1280x800 and 920x620, plus all four graphical shells. Evidence is retained under ignored `UI/local-evidence/settings-tile-layout/` and adjacent `settings-tile-layout-*.log` files. Applicable UI standards remain visible focus, semantic buttons, readable text over artwork, text-based status and keyboard/scroll reachability; no upstream frontend files or artwork were changed.

## Supplied tile artwork

All eleven categories now use the six supplied October 23 artwork sheets, preserved byte-for-byte under `UI/windows-adapter/assets/settings/`. Runtime SVG viewports select the wide variant on home and the square variant on detail pages. Artwork fills each card without stretching; focal alignment preserves the principal icons when fitting the supplied wide panels into 2:1 cards. Captions, live statuses, grid/rail dimensions and borderless styling are retained. Detail buttons are 90% opaque unless selected, when they are 100%; focus remains separately visible and inactive hover does not change selection opacity.

Release build, Settings navigation/persistence and branding/startup suites pass. Focused assertions cover full-card wide artwork, square detail artwork and exact active/inactive opacity across all four layouts and three window sizes. Rendered home/detail artwork and caption readability were reviewed; screenshots and crossfade evidence are retained in ignored `UI/local-evidence/settings-artwork/`, with adjacent build/test logs. Original sheet hashes match all six source attachments. The applicable UI standards review covers visible keyboard focus, readable caption gradients, semantic selection, unchanged keyboard/Back reachability and reduced motion. No artwork was regenerated and no upstream frontend files were changed.
