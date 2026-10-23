# Orbit live catalogue contract

## Compatibility and provenance

Reviewed 15 October 2026 against the official
[1.3.5 source bundle](https://github.com/saawant12/orbit-store-ps5/releases/tag/v1.3.5),
application source commit `8043189a7d319be3ffdf828e08bf5a0f105a30fd`,
and the current official raw feeds. Only format/field evidence was inspected;
no GPL implementation was copied or translated, and no keys, real fixtures,
plaintext dumps or source bundle are included in this repository.

The release consumes
`https://raw.githubusercontent.com/saawant12/orbit-store-ps5/main/catalogue-v7.enc`.
The v6 compatibility endpoint has the same path with `catalogue-v6.enc`.

| Contract | v6 | v7 |
|---|---|---|
| Binary header | `ORBITENC`, version 1, key ID 1, purpose 1, reserved 0 | Same |
| Authentication | AES-256-GCM; complete 24-byte header as AAD; 16-byte tag | Same |
| JSON root | schemaVersion, feedVersion, revision, releases | Same |
| schemaVersion | 1 | 1 |
| feedVersion | 6 | 7 |
| Current observed revision | 31 | 31 |
| Current observed games/releases | 1,598 / 3,825 | 1,598 / 3,825 |

Both live envelopes authenticated with the existing production decoder using
published source-bundle material held in memory. All 3,825 release IDs and rows
were identical between these two snapshots. Mutable upstream counts may change.
The current release documents v7 as allowing `1fichier`, whose source identifier
older Orbit clients reject. No such rows were present in the observed snapshot.
Provider/sourceId are already retained as data by O2; this milestone adds no
1fichier downloader or automatic provider access. Unknown routes retain the
existing external-browser/completed-file-import workflow.

The root remains a flat release array, grouped only by explicit gameId/platform.
Observed release fields match O2: id, gameId, platform, title, titleId, filename,
url/browserUrl, cover/hero and fallbacks, publisher/genre/tagline/description,
dates/rank, artworkLayout, provider/sourceId, sizeBytes, format/version/region,
compression/extractedBytes/archiveContents/archivePassword, notes, sha256 and
source verification assertions. Existing nullable metadata remains nullable.
No new data model or cryptographic design is required.

Revision is global across feed generations; different generations can legitimately
contain different compatible rows at the same revision. The client rejects a
generation or revision rollback. At the same generation/revision it accepts only
unchanged release content, comparing a deterministic SHA-256 of release rows
ordered by ID and properties ordered by name. This hash is an in-memory comparison,
not a publisher signature or a persistent cache. Configuring a different endpoint
or key resets that source's session snapshot and validation history.

Upstream 1.3.5 declares a 16 MiB crypto bound. This application deliberately retains
O1/O2's 8 MiB bound; both observed feeds were 5,182,434 bytes including envelope
overhead. Larger future feeds fail closed while retaining the valid session data.

## Retrieval, publication and persistence

Sources must be explicitly configured. Decoding keys are read from the selected
local file; explicit setup may retrieve a published key as described below.
Only the exact official raw v6/v7 URLs are accepted; URL credentials,
queries, alternate hosts and all redirects are rejected. Normal OS certificate
validation is used, with no certificate callbacks or TLS bypass. A dedicated
cookie-free transport without default credentials reuses HttpTransfer's streaming
infrastructure; game-file transport is unchanged. Timeouts cover retrieval and
decode; shutdown cancels pending catalogue operations. Parsing and local key I/O
run off the UI thread. Encrypted responses are bounded by advertised and streamed
length; unsupported transfer encodings are rejected.

The entire authenticated feed is parsed and validated before replacing that
source's entries in one UI-dispatcher turn. Failed requests retain the prior valid
session snapshot and show a source-local error. Concurrent refreshes for one source
issue one request. Edited, disabled, removed or closed sources cannot receive an
obsolete result. Test Connection validates without publication. Automatic startup
refresh is opt-in through explicit source setup (checkbox initially enabled), with
automatic attempts at least six hours apart during a session; manual requests may
run sooner. Startup refetches because no live catalogue survives restart.

Only configuration, refresh metadata/counts and redacted source errors are saved.
Neither encrypted live feeds nor decrypted catalogues are cached. Existing jobs
and library records retain their immutable selected-release transfer metadata.
Refresh never queues downloads, installs, changes mirrors or rewrites those records;
removed selected releases clear the Browse selection instead of selecting another.
Orbit Download All stays disabled. Catalogue cover URLs now use the existing viewport-limited thumbnail loader and explicit coverFallback when the primary fails. Artwork responses remain bounded to 4 MiB with dimension checks, four concurrent requests and a 128-image memory cache; no artwork files are persisted. Sizes are displayed in decimal GB without changing stored byte counts or immutable selection fingerprints.

An explicitly linked local fallback remains independently configured, loaded and
identified. Browse prefers the valid live snapshot while retaining the fallback
in memory. Before a successful live load, or after disabling/removing the live
source, the local entries remain available. Without an explicit link, only proven
identical release snapshots are hidden as duplicates. Unrelated sources are never
merged by title, filename, checksum or provider.

## Managed decoding keys

Keys live under `%LOCALAPPDATA%\PS5CatalogBrowser\keys`, using their complete
SHA-256 fingerprint as the filename (`.bin` for the 32-byte key, `.json` for release,
archive digest and origin metadata). Dropdown labels show the currently supported
key ID 1, release/manual provenance and a fingerprint prefix. Inventory loading
verifies size and full fingerprint; malformed metadata, linked files and damaged
keys are not offered. Existing binary key files are never overwritten or deleted.
A new published key stays available alongside older keys; source selection changes
only on explicit selection/save. Manual imports use the existing bounded parser,
copy only the normalized key and preserve the original input file. Existing
externally supplied file paths continue to work.

Adding/saving an enabled live source with an empty key field retrieves the latest stable
published key. Settings provides explicit download/import controls and cancellation;
opening Settings, startup catalogue refresh and background refresh do not download
or rotate keys. Disabled sources may be saved without a key and do not fetch one. Source edits are disabled during the asynchronous setup operation.
No configured catalogue, selected key, download or library record is changed by a
standalone key download. Setup failure does not add the proposed live source.

`OrbitPublishedKeyClient` reads only the pinned official GitHub latest-release API,
requiring a stable semantic release tag, one exact versioned source-bundle asset,
a positive size up to 512 MiB and the API's `sha256:` digest. Metadata is bounded to
1 MiB; the download has a five-minute cancellation-aware deadline. Initial release
URLs are pinned to the official repository; redirects may reach only HTTPS port
443 on `release-assets.githubusercontent.com` under its release-asset path. No
credentials, cookies, certificate bypass or unrelated hosts are used. Signed CDN
URLs are neither persisted nor reported in errors.

The complete bundle is downloaded to one temporary ZIP and verified against its
published size and digest before any key is imported. The ZIP must contain exactly
one version-matched application source archive (up to 64 MiB compressed); its tar
stream is bounded to 256 MiB expanded. Only the exact regular-file
`orbit-store-<version>/backend/catalog_key.h` is read, with a 4 KiB limit and the
existing non-executing key parser. Missing, duplicate or malformed key entries
fail. No archive paths are extracted, and no scripts/binaries are executed. The
upstream archive can contain other encrypted catalogue snapshots, but none is
imported or retained. Temporary ZIPs are removed in `finally`, including failures
and cancellation. A process crash can leave a `PS5OrbitKey-*.zip` temporary file;
it is not a catalogue cache or a selected key. Ordinary cleanup never deletes an
unverified pre-existing file.

The verified v1.3.5 source bundle is 342,122,770 bytes with SHA-256
`c0fe51d94b199daa77b620b5d596afc13b77902076db313891885d0e54339858`.
Its published key was retrieved through the production client, persisted outside
the checkout, and successfully authenticated the official v7 revision-31 feed.
Existing source settings were byte-identical before and after that validation.
The digest checks integrity against HTTPS release metadata, not an independent
publisher signature or a licence for catalogue content. No real key is committed.

`--orbit-keys` on Core tests exercises synthetic release metadata and source bundles,
archive/header bounds, digest/length failures, redirect restrictions, cancellation,
temporary cleanup, manual imports, inventory integrity and preservation of existing
keys. The Browser suite's same flag tests the real dropdown/setup buttons, automatic
setup, manual selection/save, updated keys without selection replacement, reuse,
cancellation and restart. Existing local/live Orbit integration checks remain.

## Published terms and limits

The current [official README licence and third-party disclaimer](https://github.com/saawant12/orbit-store-ps5#licence),
source bundle's SOURCE-BUNDLE.md, LICENSE and THIRD-PARTY-NOTICES.md were reviewed.
The source bundle deliberately includes catalogue snapshots, decoding material and
modification tools. Its crypto contract describes the distributed key as scraping
resistance rather than access control or a signing key. Public decoding material
is sufficient for the existing independently implemented envelope.

No express prohibition on this explicitly configured private local client was
found in the reviewed material. No explicit external catalogue-data licence was
found either; this remains a permission uncertainty, not an affirmative grant.
GPL-3.0-or-later covers Orbit application code, not automatically catalogue data,
third-party game content or artwork. Public availability, owning a game and private
use do not establish permission to obtain an arbitrary online copy. Applicable
rights, licences and provider terms still govern use. The application neither
redistributes feeds nor creates a public mirror and does not bypass authentication
or change game-download authorisation boundaries.

## Validation

`dotnet run --project PS5CatalogBrowser.Tests -- --orbit-live` exercises synthetic
encrypted HTTPS responses, key/tag/header/schema/generation failures, truncated
and oversized streams, cancellation/timeouts, HTTP errors, redirect/URL rejection,
revision comparisons and configuration persistence. `--orbit-live` on BrowserTests
checks startup and manual refresh, disabled/opt-out sources, concurrent requests,
whole-snapshot retention, immutable download/library records, removed release
selection, fallback display, no plaintext caching and WPF Settings/Browse bindings.
Full Core/Browser suites retain the existing admission, cleanup, native ownership,
completion/recovery, HTTP resume and OptiStore/homebrew regressions. No commercial
game downloads are used. Live provider download compatibility remains unverified.
