# Orbit normalisation (O2)

Mapping contract established before implementation from the retained private
research report and sanitised type evidence. No Orbit implementation is copied.

`OrbitCatalogParser.Parse(source, authenticatedPlaintext)` returns schema/feed
versions, revision and entries. Schema 1 and feeds 3, 4, 5, 6, 7 are supported. Feed 7 compatibility is documented in
[the live catalogue contract](orbit-live-catalogue.md).
The caller must authenticate using O1 first. No dispatcher, HTTP or UI connection
is added. Input is bounded to 8 MiB, depth 32 and 10,000 releases. Invalid JSON,
duplicate properties/IDs and invalid known field types produce redacted schema
errors. Unknown fields are ignored; unknown schema/feed generations are rejected.

| Input | Normalised field |
|---|---|
| gameId + platform | Entry GameId, Platform and source-scoped composite Id |
| id | Artifact Id: source namespace plus original release ID |
| title, description, cover | Entry Title, Description, CoverUrl |
| publisher, genre, tagline | Entry Publisher, Genre, Tagline |
| releaseDate, addedAt, newOnOrbitRank | Entry DateOnly, DateTimeOffset and nullable integer fields |
| hero, coverFallback, heroFallback, artworkLayout | Entry artwork fields |
| titleId, version, region | Per-artifact fields; entry fields remain unset |
| filename, sizeBytes, format, sha256 | Artifact FileName/Name, Size, Format, Sha256 |
| url, browserUrl | Artifact Url and independent BrowserUrl |
| sourceId, provider | Artifact ProviderSourceId and Provider |
| compression, extractedBytes, archiveContents, notes | Explicit artifact fields |
| sourceCheckedAt, sourceVerification, consoleVerified | Artifact upstream assertion fields |
| archivePassword | In-memory artifact property excluded from JSON serialization/deserialization |

IDs encode each composite component independently with URI escaping, prefixed by
the configured source ID and `orbit:game` / `orbit:release`. Grouping uses only
explicit gameId and platform. Entries/releases are sorted by ordinal identity;
conflicting game metadata within a group is a schema error. Regions, title IDs,
versions, sizes and formats belong to releases, never an arbitrary first choice.

Each artifact is an independently selectable alternative. Mirrors remain empty;
PartNumber/PartCount remain null even if filenames resemble multipart names.
Matching filenames, title IDs, providers or hashes do not merge releases. A2
admission remains unchanged; stable entry/artifact IDs retain its source scope.

Both address strings are preserved literally. HTTP(S) is accepted; this does not
validate transport capability. No URL is classified as a direct download by
filename or provider. `SourceVerification` preserves upstream `file-metadata`
versus other assertions; browserUrl preserves the explicit browser workflow.
Actual direct/page capability checks remain deferred. No provider code is added.

Content format is independent of zip/rar/7z compression. Unknown format stays
Unknown; neither containers nor ConsoleVerified imply emulator compatibility,
integrity, extraction success or execution. Missing SHA remains empty.

Passwords have no durable storage or display consumer. The explicitly ignored
property is transient and not included in settings, library records or cache by
this parser. Caller owns plaintext and returned object lifetimes; do not log or
serialize plaintext or display passwords. O3 must preserve this boundary before
introducing any consumer. No decrypted data, keys or real fixtures are committed.

O3 must require explicit release selection and guard Orbit against shared
DownloadAll batching before exposing entries. It must validate address/provider
capabilities and re-evaluate catalogue rights, persistence and sensitive data
handling. Source configuration, refresh/cache integration, extraction, firmware,
dependencies and emulator compatibility remain deferred. O1 is unchanged.

Orbit source is GPL-3.0-or-later; this independent implementation uses synthetic
fixtures. The earlier review found no express prohibition of this local synthetic
work, but no external catalogue-data grant. Third-party metadata/artwork/download
rights remain separate. The optional real-feed check is skipped because catalogue
reuse rights remain unresolved; no live requests are made.

Synthetic normalisation tests run in the Core suite or independently with
`dotnet run --project PS5CatalogBrowser.Tests -- --orbit-parser`. Existing JSON
adapter regressions remain in the Core suite; BrowserTests checks the unchanged
download workflow against loopback fixtures.
