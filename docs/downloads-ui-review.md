# Downloads interaction review

The native menu's **Close** button sits at the far right, remains keyboard accessible,
and follows the fullscreen toolbar's existing hide/reveal behaviour. It calls normal
window shutdown, retaining active-download pause/confirmation/termination guards.
The existing menu wraps within the remaining width. WebView2 checks cover fullscreen
visibility and the actual Close button's normal shutdown path.

Failed transfer, extraction and associated installation rows expose a text **Retry**
button. In Classic the corresponding control is disabled unless the selected failed
job is eligible. Retry uses the existing transfer owner and busy guards, respects
Pause/Cancel during a resume attempt, and returns to the exact current catalogue game
when a new selection is needed. No provider capability URL crosses the frontend bridge.
Store navigation compares platforms without case sensitivity and uses the recorded
Orbit game ID/platform if a source was re-added with new local IDs. If an exact item
is unavailable or ambiguous, Orbit opens Browse with the saved download title as its
search query and resets other filters. Titles are search text, never download identity.
A fresh admission replaces failed history for the explicitly retried game, including
an alternative provider choice, while active versions and completed source files are
preserved. Synthetic checks cover HTTP resume/restart, native resume success/failure,
confirmed cancellation before cleanup, failed extraction/installation associations,
real Orbit button-to-game navigation, and active-version isolation. HTTP resume,
native ownership and Orbit installation/navigation regressions pass
(UI-INT-003, UI-A11Y-001/005/008).

Archive passwords use the existing shared Settings surface in Orbit and Classic:
a labelled multiline native text field, explicit one-per-line/case/space/order limits,
and exception validation on save when leaving the field. The six defaults are editable;
blank lines remove candidates, and exact duplicates are ignored. The operation keeps
its existing progress and busy guards, returns only a text password badge after all
candidates fail, and never displays a password value in status/errors. Synthetic
ZIP AES/ZipCrypto and RAR5 header/data tests cover retries and cleanup; native checks
cover changing the list, retrying an existing archive to Installed, source-supplied
password selection without serialization, and Orbit's actual Settings binding
(UI-FORM-001/003/005, UI-A11Y-001/005/008).

Adding a download preserves the current Orbit page and displays a nonmodal five-second
text toast with `role=status` and a polite live region. Classic uses its native polite
status toast. Neither toast captures input. An accepted native offer minimises its
provider window after completing admission and activates the owning app while keeping
the native writer alive. Settings exposes a labelled, validated shared bandwidth
field, defaulting to 100 decimal MB/s, with the queue and next-transfer semantics
beside it (UI-A11Y-001/005/009, UI-INT-003).

Synthetic WebView2/HTTP checks measure the aggregate cap across two provider transfers
and a direct transfer, frozen active limits, queued cancellation, persistence,
unlimited mode, and minimisation. Orbit checks cover the toast, unchanged current
screen, bound Settings field, and RAR/PS4/PS5 download-to-library flows. The fixed
provider limit uses [the Chromium Network emulation API](https://chromedevtools.github.io/devtools-protocol/tot/Network/#method-emulateNetworkConditions).
Only one managed network slot is used: runtime tests showed that reapplying the
throttle while native downloads are active can remove their limit.

Fullscreen now uses only the Orbit surface, removing native padding and the bottom
status strip. Its native toolbar slides down at the top screen edge and hides when
the pointer leaves it. F6 provides keyboard access; F11 restores the normal window.
Open interface menus and keyboard focus keep their controls usable. Animation follows
the Windows client-area animation preference. Classic/failure fallback exits fullscreen
and restores native controls. WebView2 checks cover hidden/revealed chrome, inactive
windows, keyboard access, and restoration of the normal layout (UI-A11Y-001/003,
UI-INT-003).

The current Orbit and Classic controls add Scan downloads and failed-download Delete,
and display password failures as a persisted text badge without a dialog. Scanning
registers only root-level ZIP, RAR, PKG, exFAT and ELF files in place, labels
ambiguous/unmatched metadata, and preserves normal installation actions. Files show
the origin label **Local file**; **Scanning…** and **Scan complete** describe the
operation separately. Each non-private top-level folder is one extracted library
item, including folders containing packages rather than an eboot. Its complete file
set and SHA-256 hashes are verified through the existing journalled, same-volume,
no-overwrite move into Installed. Old per-file Scan rows inside that folder are
retired without deleting their content; other download ownership/history is preserved.
Startup also retires obsolete nested/unsupported Scan rows marked Missing or Changed,
so restarting cannot restore extracted assets as individual Downloads entries.
Each game card has one progress bar: extraction, verification or installation replaces
download progress while active. The latest installation failure remains on its matching
card as a red text badge with the recorded error; arithmetic overflow includes a short
numeric-range explanation without claiming the package is necessarily corrupt.
Empty, linked, private/staging and configured Installed directories are excluded.
Repeated scans do not duplicate folder items. Standalone packages remain downloads
until installation. The Save to control is now read-only path text, last in the
game details choices. Horizontal panels accept vertical wheel input; nested vertical
panels and editable controls retain their native behavior. Existing keyboard controls
remain available (UI-A11Y-001/003/005, UI-INT-003).

Synthetic WPF/WebView2 checks cover matching-file automatic acceptance, mismatched
offer confirmation, scan/idempotence/history restoration, password badge persistence,
safe failed-partial deletion, actual Orbit scan controls, final path-column ordering,
and wheel scrolling. The rendered path column and scan controls were visually reviewed;
badge text was checked in the rendered DOM.
Existing Orbit RAR/PS4/PS5 PKG download-to-library checks pass. Screen-reader behavior
and live provider success are not claimed.

Scope: contextual extraction/installation action, extraction feedback, and transfer/history disclosure.

The UI standards skill's referenced report template is absent from the installed package; this review records the applicable rules directly.

- UI-INT-001 / UI-FEED-004: extraction immediately shows a busy message and measured progress. Synthetic ZIP tests cover start/completion notifications and busy-action guards.
- UI-INT-005: PKG and RAR limitations appear beside the selected download. Unsupported install controls are hidden. The Release UI was checked with an existing PKG.
- UI-INT-024: one available primary action; transfer/history controls and file details live under the native More actions expander.
- UI-INT-003: success appears after extraction and verified movement complete. Unsupported executables remain in Downloads with their original ZIP preserved; tests cover both outcomes.
- UI-A11Y-001 / 002 / 008: native WPF buttons, list selection and expander retain keyboard semantics and focus indicators. The expander was checked with keyboard input in Release.
- UI-A11Y-005 / 009: state is expressed in text, with polite live-region metadata and a named progress control. Screen-reader announcements were not manually tested.

Release visual checks: all five saved downloads render; existing Alone in the Dark extraction is recognized; its executable check failure is visible. No game was executed. Core extraction tests retain traversal, link, expansion, duplicate-path and cancellation checks.

## Installation lifecycle update

Reviewed against UI-INT-003/004 and UI-A11Y-001/005: the native state projects each
source into Downloads or Installations, preserves its card identity and progress,
and exposes text-labelled Retry installation/Delete on failed attempts. Success
waits for verification and persisted library registration. Native buttons retain
keyboard behavior and focus styling. Synthetic lifecycle tests cover failure,
retry, deletion, retention, whole-folder movement, rescan and restart. Automated
Orbit checks are reported separately from manual visual/gameplay acceptance.
