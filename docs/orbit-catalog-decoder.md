# Orbit catalogue envelope decoder (O1)

`OrbitCatalogDecoder.Decode(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key)`
authenticates a feed envelope and returns a new plaintext byte array. It uses
built-in .NET 10 `AesGcm`, with no Orbit implementation code or new dependencies.

## Supported contract

| Offset | Bytes | Value |
|---|---:|---|
| 0 | 8 | ASCII `ORBITENC` |
| 8 | 1 | Envelope encoding version 1 |
| 9 | 1 | Key identifier 1 |
| 10 | 1 | Catalogue feed purpose 1 |
| 11 | 1 | Reserved, zero |
| 12 | 12 | Nonce |
| 24 | Variable | Ciphertext |
| Final 16 | 16 | Authentication tag |

AES-256-GCM uses the caller's 32-byte key and the **entire first 24 bytes** as
additional authenticated data. Plaintext must be 1 byte through 8 MiB inclusive;
the envelope adds 40 bytes. Length and header checks precede plaintext allocation,
which is bounded at 8 MiB. No plaintext is returned before authentication succeeds;
the private plaintext buffer is cleared on failure. The caller owns successful
output and its lifetime. No trailing NUL, compression or text conversion is added.

Malformed/unsupported envelopes throw `InvalidDataException`; incorrect key
length throws `ArgumentException`; failed authentication throws
`CryptographicException` (normally `AuthenticationTagMismatchException`). Errors
do not include key bytes or decoded content. Unknown envelope versions, key IDs,
purposes (including source-table purposes 2/3), and reserved values fail closed.
There are no alternative keys or algorithms. Limits are fixed, not configurable.

The key is injected for each call, never embedded, fetched or persisted. This
component has no URL, settings, cache, UI or catalogue adapter integration.

## JSON and trust boundary

Envelope version 1 is separate from JSON `schemaVersion` and `feedVersion`.
O1 does not select or validate JSON feed generations. O2 must parse the returned
bytes as UTF-8 JSON, reject invalid JSON, and validate the supported catalogue
schema/feed generation before treating anything as a catalogue. Authentication
alone can succeed for arbitrary nonempty bytes, as the synthetic tests demonstrate.

The intentionally distributed symmetric key is neither secret access-control
material nor a signing key. GCM authentication detects alteration relative to
that key; anyone holding it can create an authenticated envelope. It does not
establish trusted publisher identity, provenance or freshness. Live integration separately pins the official HTTPS source and reviews terms; see
[the live catalogue contract](orbit-live-catalogue.md).

## Format and permission evidence

Reviewed on 14 October 2026: the official
[Orbit repository README](https://github.com/saawant12/orbit-store-ps5#licence),
its third-party content disclaimer, and the already obtained
[1.3.0 source bundle](https://github.com/saawant12/orbit-store-ps5/releases/tag/v1.3.0).
Local bundle evidence includes `SOURCE-BUNDLE.md`, `THIRD-PARTY-NOTICES.md`,
`LICENSE`, `backend/catalog_crypto.h`, `backend/catalog_crypto.c`,
`backend/catalog_key.h` and `backend/catalog.c`.

Orbit application source is GPL-3.0-or-later. This decoder independently implements
the documented envelope using standard .NET cryptography; it does not copy or
translate Orbit's C functions or incorporate their dependencies. The source bundle
intentionally includes decoding material and tools for modifying the catalogue;
the key comment describes it as non-secret, and the crypto header says the
format is not an access-control boundary.

No separate explicit external-client catalogue licence or express restriction
on this synthetic format-decoder work was found in the reviewed material. That is
an unresolved permission question for future catalogue reuse, not a grant. Rights
to third-party metadata, artwork and downloadable files remain separate from the
application source licence. Intended use is solely the owner's private local use;
private use does not automatically establish those rights. Any future live Orbit
integration requires a separate terms review. O1 implements no automatic live-feed
retrieval, catalogue redistribution or persistent plaintext cache. No Orbit source,
actual catalogue fixtures or published key bytes are added to the repository.

## Verification

Synthetic tests independently seal envelopes with .NET cryptography and synthetic
keys: JSON round trip, one-byte and 8 MiB boundaries; invalid lengths, headers and
keys; tampered nonce/ciphertext/tag; incomplete header AAD; missing tag and truncated
ciphertext/data; and authenticated bytes rejected by JSON parsing. Run only these
tests with `dotnet run --project PS5CatalogBrowser.Tests -- --orbit-decoder`.
They also run in the normal Core regression suite.

An isolated read-only compatibility check streamed the existing source ZIP and
inner source archive, using its published key and bundled `catalog/catalog.enc`.
The production decoder authenticated the envelope; the in-memory result parsed
as JSON with `schemaVersion=1`, `feedVersion=6`, `revision=27`, 1,601 distinct game
identities and 4,050 releases. No plaintext file or catalogue records were emitted.
The separate `catalogue-v6.enc` snapshot was unavailable locally, so that specific
target was skipped; no live catalogue or replacement source archive was downloaded.
These results describe the bundled snapshot, not current mutable `main`.
