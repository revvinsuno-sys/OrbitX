# Provider flow investigation — 15 October 2026

## AkiraBox ready-link handling and visible instructions

The subsequent implementation recognises AkiraBox `.to` as well as `.com` file pages.
Orbit's release dialog displays instructions beside each available method, with
`aria-describedby` connecting them to their buttons. The provider window keeps its
method guidance visible. AkiraBox says to complete verification and wait without
clicking Download, consistent with the official
[Zero installation guide](https://github.com/saawant12/orbit-store-ps5/blob/main/guides/orbit-zero-installation.md#akirabox-downloads).

The window observes only the visible, enabled `a#download` ready link on the selected
file page for up to two minutes. It requires an official HTTPS host, the same source
file ID, and an exact selected filename on the generated `/download/<token>/<filename>`
route. It follows that link in the same WebView without invoking ad click handlers.
Verification stays manual. Matching native filenames are now accepted automatically; other offers ask for confirmation. Existing identity,
ownership, integrity and persistence checks remain authoritative. No cookies or
signed URLs are exported or persisted. Reload returns to the original selected page.

Validation: `--provider-policy`, `--akirabox`, `--provider-flow` and
`--orbit-frontend` pass; the latter includes direct/browser RAR and PS4/PS5 PKG
download-to-library fixtures. Controlled AkiraBox pages cover disabled links,
automatic matching-file acceptance with verified synthetic bytes, blocked ad handlers, and
the unavailable-link error. A rendered release dialog was visually inspected:
instructions wrap below their controls, with scrolling inside the existing modal.
The installed UI standards report template remains absent; applicable interaction
feedback and accessibility rules were reviewed directly. Screen-reader acceptance
was not performed.

An opt-in probe of the user's supplied live page generated and followed its ready
link automatically, then redirected to `eufb.akirabox.xyz`. WebView2 reported
`NavigationCompleted(Unknown)` before any native file offer. No game file was
downloaded. This confirms ready-link detection, not working live transfer or
installation. The previous live-decline probe was retired when matching-file auto-accept was enabled; `--akirabox` uses synthetic attachments only. The earlier investigation below
records its original, pre-implementation evidence boundary.

Baseline: canonical checkout `D:\Games\PS5\PS5-Catalog-Browser`, branch
`codex/exfat-game-installer`, HEAD `ae450e1390838f1acd9ccccadbe974be552088c3`.
No remote is configured. The audit's `007f1bb` is its immediate predecessor.
This investigation uses generated loopback files only; no live provider or commercial
download was attempted. The earlier audit's Vikingfile 4/4 and AkiraBox 0/3 are
supplied observations, not newly verified provider compatibility.

## Observed Windows sequence

`--provider-flow` runs real WebView2 against controlled pages. The passive fixture
requires one simulated verification click, then automatically redirects without a
provider Download click. Observed order:

1. `NavigationStarting`, `NavigationCompleted(Success)` for the provider fixture.
2. After verification, `NavigationStarting` for the handoff and redirect.
3. `NavigationCompleted(ConnectionAborted)`.
4. `DownloadStarting(AwaitingConfirmation)`; the existing confirmation is shown.
5. `ApplicationAdmission(Downloading)`, `OfferDecision(admitted)`.
6. `NativeStateChanged(Completed/None)`.
7. `OwnerReleased(Downloaded)`, `ApplicationState(Downloaded)`.
8. `WindowClosing`, `WindowClosed` after the successful transfer.

The admitted bytes, catalogue SHA-256, selected release fingerprint, saved completion,
and library registration were checked. A verification-only page that emits no file
navigation generated no native offer, confirmation, job or owner during the bounded
observation interval. This establishes a limitation of those signals for that
fixture, not proof that every provider requires an additional click.

The truncated-file fixture instead emitted `NativeStateChanged(Interrupted/NetworkFailed)`
and `ApplicationState(Interrupted)`. It published no destination/library item. Its native
owner remained held. Closure requested cancellation and was vetoed while termination
was unconfirmed; this preserves A5. Cancelling an interrupted operation did not
produce a terminal acknowledgement in this runtime. No ownership workaround was added.

Each window freezes its selected Orbit entry and mirror at construction, capturing
the existing `DownloadAdmission` identity. Navigation and popup events belong to that
window but do not establish file identity. Only admitted native operations create
jobs, tied to this frozen selection and the owning window. The manager keeps its
existing state subscription, integrity/persistence path, terminal guards and admission
release. Cancel/interrupt requests are logged separately from native termination.
The manager's diagnostic observer cannot propagate exceptions into ownership logic.

## Correction and diagnostic controls

The original navigation-completion handler unconditionally replaced the page status
with a load failure. It could overwrite a valid pending offer or transfer. The local
correction gives pending confirmation priority and reports the latest admitted
transfer's actual application state separately from navigation status. Deterministic
tests inject the opposite ordering while the offer confirmation callback is active.
A genuine interrupted transfer remains explicitly visible even if its page is open.
Duplicate terminal/cancellation callbacks remain covered by `--ownership`.

Enable **Record sanitised diagnostics (this window only)** before reproducing a flow.
**Export diagnostics** saves JSON through a file picker. Recording defaults off,
keeps the most recent 256 events in memory, and does not alter settings. Disabling
recording stops collection but retains the current buffer until the window is released.
Sequence numbers and UTC timestamps expose truncation/order. A random session ID and
a SHA-256 fingerprint associate the trace with the selected release without exporting
its title, filename, IDs or catalogue URLs. URL-derived records contain hostname and
a fixed route class only: local fixture, catalogue direct route, provider page route,
unclassified HTTPS, or unsafe/unsupported. These are structural classifications,
never content validation. Navigation IDs and user-initiated flags are recorded where
WebView2 supplies them. Native state includes its enum interrupt reason; application
state records admission, cancellation request and owner release.

Paths, queries, fragments, URL credentials, cookies, bodies and native capability
URLs are never retained in the diagnostic buffer. URL-less lifecycle events use
the session association; the native offer's hostname is on `DownloadStarting`.
Export sanitisation, default-off behavior and bounded eviction are tested.

## Popup evidence

All popup fixtures use known synthetic endpoints. With popup blocking enabled:

| Fixture ground truth | Observed decision |
| --- | --- |
| Advert endpoint | Blocked, target unverified |
| User navigation to an HTML page | Blocked, target unverified |
| Native attachment in a new window | Blocked, target unverified |
| Redirect to an attachment | Blocked, target unverified |
| Missing/no-file page | Blocked, target unverified |
| Nonlocal insecure HTTP | Blocked as unsafe |
| Unexpected HTTPS origin | Blocked, target unverified; no request sent |

No blocked target was fetched, so the handler itself cannot distinguish advert content
from an attachment based on these events. Labels above describe fixture ground truth,
not a production classifier. The earlier AkiraBox blocked URLs were not instrumented;
their purpose remains unknown. No hostname exception was introduced.

With the existing block option explicitly disabled, a user-initiated popup could be
confirmed into the current window. A synthetic redirect then produced a native offer,
requiring its separate download confirmation and normal validation. An automatic
non-user popup remained blocked with that option disabled. This demonstrates the
existing current-window option only. A native child-window implementation was not
demonstrated or added. It requires a separately agreed origin/navigation policy and
explicit parent/child lifetime ownership before implementation; a hostname alone
cannot authorise it.

## Orbit 1.3.5 comparison

The [1.3.5 release](https://github.com/saawant12/orbit-store-ps5/releases/tag/v1.3.5)
and [tagged download guide](https://github.com/saawant12/orbit-store-ps5/blob/v1.3.5/guides/downloads.md)
describe PS5 verification followed by automatic capture for both providers. They
describe a Download click for Vikingfile through Zero, and verification followed by
waiting for AkiraBox. The [Zero guide](https://github.com/saawant12/orbit-store-ps5/blob/v1.3.5/guides/orbit-zero.md)
is a separate platform workflow; it is not evidence that Windows WebView2 will emit
the same events.

The release's actual source bundle was inspected rather than assuming 1.3.0 behavior.
`SOURCES.json` reports version 1.3.5, source commit
`8043189a7d319be3ffdf828e08bf5a0f105a30fd`. The inner source archive SHA-256 matched
`SOURCE-SHA256SUMS`:
`74bdf1d37e821d1d622134420104ea99cfdd763d006d28c2fbdf3025580e3cc6`.
`backend/browser.c`, `backend/browser_platform.c` and `backend/browser_scan.c`
confirm that this release's PS5 capture path still scans browser network-process
memory, then validates selected-file candidates. That is outside this task's allowed
Windows mechanisms. No scanning, session interception, resolver or source code was
copied. Zero implementation internals were not established from these PS5 files.
Temporary downloaded source archives were removed.

## Minimum-interaction decision

| Journey | Confirmed behavior / next decision |
| --- | --- |
| Selected direct file | Existing supported Archive.org direct routes can start through the existing HTTP path. No additional resolver is needed. |
| Native offer after verification | A passive native file offer works in the fixture; retain current confirmation and validation. Automatic acceptance needs separately approved identity/security rules and explicit opt-in. |
| Provider verification | Remains user-assisted. Neither verification nor CAPTCHA automation was added. |
| Popup handoff | Existing confirmed current-window option works synthetically. No automatic popup or child-window admission is justified by this evidence. |
| Unknown/ambiguous offer | Require confirmation and existing selection/size/hash checks. Filename or page text alone cannot authorise it. |
| Embedded flow emits no offer | Stop at supported browser signals. Use the existing external-browser/completed-file import fallback; this investigation did not validate live external-provider transfers. |

The only interaction removable on current evidence is an additional provider Download
click when a provider itself emits a normal native offer after verification. This is
confirmed for the fixture, not for live AkiraBox/Vikingfile. The app confirmation
cannot be removed under the diagnostic task's policy. The AkiraBox acquisition issue
remains unsupported; the supplied Vikingfile click-and-confirm sequence is preserved.

## Validation and release boundary

Core suite passed. Provider-flow, selected Orbit direct/native/import, A2 admission,
A3 cleanup, A4 completion/recovery, A5 native ownership, HTTP resume, local Orbit,
library deletion UI, installation history, direct-download and presentation/source/key
UI checks passed. Persisted-state and secret-exclusion checks passed. Debug and Release
solution builds passed with zero warnings/errors. `git diff --check` passed.

The full Browser run stops in `LiveOrbitIntegrationTests.cs:67` at
`refresh preserves download history and library`. A rebuild from unchanged HEAD,
with all investigation changes temporarily removed and then restored, reproduces the
same failure. An older prebuilt Release test binary passed, so it is not a valid
substitute for rebuilding the current test sources. This unrelated baseline failure
was not changed. The diagnostic/messaging patch remains **uncommitted** under the
task's full-validation gate; HEAD is unchanged. Release application binaries include
the local uncommitted diagnostic correction. No deployment or remote publication occurred.

Settings were separately checked: `DownloadRoot` is `D:\Games\PS5\downloads` and
`InstalledGamesRoot` is `D:\Games\PS5\Installed`. The current source labels the first
field “Library and download root”; the independent installed folder appears under
Image Installation as “Installed games directory”. No loss of these settings was found.

Automatic approval review blocked recursive deletion of the generated temporary
`PS5ProviderFlow-*` test profiles and `PS5ProviderBaseline-01a13f32` backup directory
under `C:\Users\revvi\AppData\Local\Temp`, with reason `blocked by policy`.
These remain as cleanup work; no game data was touched.

## Review and commit follow-up

Reviewed against HEAD ef6fdec6c518162f3ee10f7ff313705fc1717b2d after explicit user authorisation to commit the existing patch. No new blocking review finding. The rebuilt Debug Core suite and targeted --provider-flow WebView2 suite pass. The full rebuilt Browser suite reproduces the documented LiveOrbitIntegrationTests.cs:67 baseline failure. These results supersede the earlier uncommitted release boundary; committing does not claim a fully passing Browser suite or live provider compatibility.
