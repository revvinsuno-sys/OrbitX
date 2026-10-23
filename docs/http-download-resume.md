# Direct HTTP resume (D1)

## Lifecycle and ownership

Before D1, admission saved a selected job and `.owner` reservation, HTTP wrote a new `payload.part`, and cancellation/failure deleted it. Retry removed empty staging and prepared a new reservation for the same job. Success verified size/SHA-256 and handed the file to A4 Pending → Promoted → Completed. Startup marked unfinished transfers Interrupted; A3 was the other deletion path. Browser/import writers own their separate lifecycle.

D1 keeps the same job, staging reservation and settings authority. A versioned HTTP checkpoint binds the immutable original selection, URL, expected filename/size/checksum, destination, marker and Windows file identity. Its cursor and SHA-256 describe only a flushed contiguous prefix. Initial identity is saved before receiving bytes; checkpoints flush content before atomically replacing settings. No marker or sidecar becomes another checkpoint authority.

An active HTTP writer exclusively opens the partial and holds its ownership marker against deletion. Pause stops and joins that writer, saves its checkpoint, then retains admission. Resume validates the original job and exact partial before any request; extra uncheckpointed bytes are truncated only during explicit resume after prefix verification. A short, replaced, changed, missing or linked partial needs attention and is preserved. Restart and Discard stop/join writers, then persist an A3 cleanup intent; deletion checks the Windows file identity and deletes the opened file by handle. Restart reuses the job and immutable selection, preparing a fresh staging marker. Unexpected sibling files are preserved and prevent preparing a new staging reservation. Completed files and unresolved A4 evidence cannot be restarted or discarded.

Startup performs local inspection only. Eligible interrupted jobs become Paused, others need attention. It never contacts a provider, starts a transfer, infers completion from length, or truncates/deletes a partial. A4 recovery takes precedence when an intent already exists.

## HTTP contract

Persistent resume requires the original authorised URL, known total length, strong ETag and trusted checkpoint. Weak/missing ETags, Last-Modified alone and unknown lengths do not qualify. `Accept-Ranges` is only a hint. A nonzero resume sends `Range: bytes=N-` and `If-Range` with the saved strong ETag. Only a 206 with matching ETag, exact start/end/total and consistent body length can append. A full 200, 412, 416, malformed range, changed validator/size or short/excess body retains the checkpoint and requires explicit Restart. HTTPS/redirect and HTML checks still apply; redirected URLs are never saved.

The complete file is checked against representation length, catalogue size and any supplied expected SHA-256, then hashed and handed to A4 once. A4 binds new HTTP intents to their final checkpoint and rechecks file identity and digest across promotion/recovery; existing saved A4 intents retain their original identity contract. An ETag proves server continuity; a calculated digest without a trusted expected checksum does not establish source authenticity.

## Controls and limits

Pause keeps eligible data and its artefact reservation. Cancel stops the writer and releases admission after termination; it keeps the record and owned data for explicit Restart/Discard. Discard explicitly removes proven owned incomplete data. Resume continues the original job. Restart confirms any downloaded data loss before removing retained bytes and starts the same selection from zero. Non-resumable downloads offer Restart, never Resume. Closing waits for direct writers and their final checkpoints before releasing settings ownership.

Checkpoints are bounded to 8 MiB or two seconds of transfer progress, plus initial identity, pause and EOF boundaries. Cursor updates follow a disk flush; failed settings saves roll back to the previous cursor. Recovery inspects path, marker, file identity and length without rewriting content. Explicit Resume additionally re-hashes the durable prefix before any HTTP request. Resumed responses may omit Content-Length when Content-Range provides the complete known total; the body must still end at precisely that total.

WebView2 transfers and browser sessions are unchanged: their existing pause/resume works only in the original native session. No cookies, credentials, redirected CDN capabilities or provider-specific refresh are captured. An expired original link requires user intervention. This version uses one stream, bounded checkpoints, no automatic retries/background resumes, and Windows file identities. Recovery checks the entire durable prefix, so large partials take time to validate. A full-length partial without separately persisted completion evidence cannot be promoted by length alone.

## Local verification

Run `dotnet run --project PS5CatalogBrowser.Tests -- --resume` for synthetic range/protocol, integrity, persistence, ownership, bounded checkpoint and real process-exit recovery cases. The subprocess exits without unwinding after content flush or cursor persistence; its parent reloads the actual settings and compares recovered bytes. `dotnet run --project PS5CatalogBrowser.BrowserTests -- --http-resume` drives real WPF Pause/Resume controls, closes/reopens windows over isolated settings, checks requested ranges and one library registration, and exercises non-resumable Restart, competing actions and writer-joined discard. Both cases are included in the full console suites. Fixtures, settings and staging are temporary; no provider or commercial download is used.
