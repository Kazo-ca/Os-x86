> [Version française](../fr/stockage.md)

# Storage: image manifest, verified download, OPFS repository

Milestone 0, work item 3 (`j0/3-storage`). Storage is the piece that brings an image into the
browser and keeps it there: a manifest published next to 32 MiB chunks, a Web Worker that
downloads them, verifies them (SHA-256) and writes them into the origin private file system
(OPFS), an image repository by identifier and version, a resume state, a write lock, and the
versioned-format structure of plan section 13. Decisions 169, 177 A, 188, 202 A, 203 A, E11;
trials [E2](../essais/e2-stockage.md) (OPFS) and [E3](../essais/e3-distribution.md)
(distribution), whose code this work item turns into a product (both in French). The recipes
that produce the images and their manifests are work item 4; opening an image in the engine,
work item 6.

## Where things are

| Path | Contents |
| --- | --- |
| `src/OsX86/Storage/` | `ImageManifest` (reader and validator), `StorageLayout` (OPFS names and paths), `StoredObjectMetadata` (versioned formats), `ResumePlan`, `DownloadStateMachine` and `DownloadProgress`, `ImageStore` (the façade), `IStorageWorkerChannel` and `JsStorageWorkerChannel` (JS interop), the protocol, the exceptions |
| `src/OsX86/wwwroot/storage/` | `storage-host.js` (module imported by the C# side, creates the worker) and `storage-worker.js` (download, SHA-256, OPFS, lock) |
| `tests/OsX86.Tests/Storage/` | `unit` tests (manifest, metadata, resume plan, state machine, `ImageStore` against a simulated worker, JS channel against a fake `IJSRuntime`) and the `guest` test (`ImageDownloadTests`, `storage.html`, `ChunkImageServer`) |

As for the engine, the files of `wwwroot/` are served under `_content/OsX86/`.

## The manifest (`manifest.json`)

The schema is the reference for the recipes of work item 4 as for this reader. Format 1:

```json
{
  "format": 1,
  "image": "alpine-test",
  "version": "1.0.0",
  "engine": { "v86": "0.5.473" },
  "size": 838860800,
  "chunkSize": 33554432,
  "chunks": [ { "sha256": "<64 lowercase hex>" }, { "sha256": "..." } ],
  "files": { "kernel": { "offset": 0, "size": 12345 }, "initramfs": { "offset": 12345, "size": 678 } }
}
```

- `format`: integer, the format number; this version reads format 1 only. Any other number
  refuses the image, says so (`format-unsupported`) and **writes nothing** (decision 169). A
  compatible field addition does not bump the number: unknown fields are ignored.
- `image`: identifier in lowercase letters, digits and dashes (1 to 64 characters); `version`:
  a semantic version. They are also the OPFS directory names.
- `engine`: the engine versions the image is built for, `v86` at least. The reader exposes it;
  the "same major version" rule (169) is applied by work item 6.
- `size`: payload size in bytes; `chunkSize`: the size of every chunk but the last (64 KiB to
  512 MiB; the recipes use 32 MiB, the right size according to E3).
- `chunks`: one lowercase hex SHA-256 per chunk, in order, exactly `ceil(size / chunkSize)`
  entries (100,000 at most: five digits in the names). The chunks are the files
  `chunk-00000.bin`, `chunk-00001.bin`, ... next to the manifest.
- `files`: the logical files of the payload (`kernel`, `initramfs`, `rootfs`, ...) with their
  offset and size; may be empty.

`ImageManifest.Parse(text)` validates all of it; `ImageManifest.Parse(bytes, expectedSha256)`
first compares the hash to the SHA-256 the C# side knows (decision 203 A: the manifest is the
root of trust, the caller must know its hash) and refuses the image (`manifest-integrity`) when
it differs. The manifest itself is capped at 1 MiB.

## How it works

```
C# (ImageStore) ──JSON──> JsStorageWorkerChannel ──import──> storage-host.js ──postMessage──> storage-worker.js
                <──JSON── [JSInvokable] OnWorkerMessage <──── callback <──── postMessage <────┘  (fetch, SubtleCrypto, OPFS)
```

The C# side decides, the worker executes. `ImageStore.DownloadAsync(source, progress)`:

1. **`fetch-manifest`**: the worker fetches `manifest.json` (`cache: 'no-store'`, no
   credentials), hashes it with `SubtleCrypto` and compares it to the expected hash; the C# side
   compares again and validates the manifest. Nothing is written until the manifest is accepted.
2. **`read-state`**: the worker reads what the OPFS already holds for that image and version
   (resume state, metadata). The C# side computes the **resume plan** (`ResumePlan`): verified
   chunks are kept, the others are requested in order; a state that belongs to another manifest
   (different hash) or names a chunk outside the manifest is discarded (`reset`) and everything
   starts over.
3. **`download`**: the worker takes the lock of the image, writes the local copy of the manifest
   and the metadata, opens `image.bin` with a `FileSystemSyncAccessHandle` (sized to `size`),
   then, for each requested chunk, fetches the file `chunk-NNNNN.bin` into a preallocated
   buffer, hashes it (`SubtleCrypto.digest('SHA-256')`, 1.1 to 1.25 GB/s according to E3; the
   SHA-256 of interpreted .NET would do 35 MB/s), compares it to the manifest, writes it at its
   offset, `flush()`es, and only then adds it to the resume state. The `progress` messages give
   the verified bytes and chunks, the totals, and the receive rate over the last five seconds.
   When every chunk is verified, the metadata becomes `complete: true` and the worker answers
   `done`.

Two chunks at a time by default (`ImageSource.Parallelism`, 1 to 4). E3 measured a single
stream; a second one covers the latency of the next request without doubling the memory (one
32 MiB buffer per stream). The real R2 hosting was not measured (202 A): to adjust once it is.

### Resume, retries, cancellation

- **Network error or 5xx** (408 and 429 too): the chunk is retried with a growing delay
  (500 ms, 1 s, 2 s, ... 30 s at most, with jitter), 8 attempts; beyond that the download fails
  with `network`. A cut costs at most the chunk in progress (E3). A 404 or a 403 fails at once
  (`http`).
- **Wrong hash**: the chunk is fetched again up to three times, then `chunk-integrity`.
- **Page reload, browser closed, crash**: the resume state is in the OPFS (`download.json`: the
  manifest hash and the verified indices), written after the chunk's `flush()`, so it never
  names a chunk that is not on disk. The next download only requests the missing chunks.
- **Cancellation**: the C# token sends `cancel`; the worker aborts the fetches in progress,
  closes the file, releases the lock and answers `cancelled` (the C# side waits for that
  acknowledgement up to 5 s so the lock is free when the caller goes on). Verified chunks stay.
- **Quota**: a `QuotaExceededError` of the browser becomes `quota-exceeded`.

`DownloadPhase` and `DownloadStateMachine` name the phases (`FetchingManifest`, `Planning`,
`Downloading`, `Completed`, `Failed`, `Cancelled`) and the allowed transitions; nothing can
have been written before `Downloading`.

## The repository in the OPFS

```
osx86/
  images/
    <image>/
      <version>/
        manifest.json     the copy of the published manifest
        image.bin         the chunks concatenated (the payload)
        download.json     the resume state: {"format":1,"manifestSha256":"…","verified":[0,1,2]}
        image.meta.json   the versioned metadata (below), complete:true at the end
```

`ImageStore`: `ListAsync` (every version with its size, its real footprint, `Complete`, its
format and its engine versions; `IsSupported` says whether this version of Os-x86 can open it),
`DeleteAsync` (refused with `locked` during a download), `EstimateAsync`
(`navigator.storage.estimate()`: usage and quota of the origin, plus the repository footprint),
`ReadManifestAsync` (the local copy, validated: an unknown format is refused), `VerifyAsync`
(every chunk re-read and hashed in place, 1 s for 800 MiB according to E3), and
`ImageStore.Locate(manifest, "kernel")` which gives the OPFS path of `image.bin` with the
offset and size of the logical file: what work item 6 will hand to the engine. `IEngine` and
the v86 worker are unchanged in this work item; the v86 worker reads whole files today and
will take an offset and a size (or work item 6 will extract the file) when it reads an image
from the repository.

### Write lock (E11) and `flush()` (177 A)

One writer per image version: the worker calls
`navigator.locks.request("osx86:image:<image>@<version>", { ifAvailable: true })` and holds it
for the whole download or deletion; a second writer gets `locked` at once instead of waiting.
The browser releases the lock when the page closes or crashes, with no lock file and no expiry
to manage: that is why it is preferred to a lock file. Without the Web Locks API (the worker
says so in `ready`), the exclusivity of `createSyncAccessHandle()` on `image.bin` plays the same
role: the second opener fails, translated to `locked`. A read (verification) needs no lock; it
fails with `locked` while a download holds the file.

`flush()`: after every chunk before the state names it, every 5 s while a file is open for
writing (the worker's `WritableFile` class, ready for the disks of the next work items), and at
close.

## Versioned formats (plan, section 13)

Every stored object carries a metadata file next to it, `image.meta.json` today, the same file
next to every disk, layer and restore point tomorrow (`StoredObjectKind`: `image`, `disk`,
`layer`, `restore-point`). Format 1:

```json
{ "format": 1, "kind": "image", "image": "alpine-test", "imageVersion": "1.0.0",
  "engine": { "v86": "0.5.473" }, "created": "2026-11-12T10:00:00+00:00", "complete": true }
```

`StoredObjectMetadata.Parse` refuses an unknown `format`, says so, and writes nothing (169). The
migrations of decision 188 (read the previous major format, convert after a restore point)
come with the first format change; this work item lays the structure and the reader. The resume
state carries its `format` too.

### What is not protected

As plan section 13 says: a factory reset of Kazo erases the OPFS; Safari erases an origin's
storage after seven days without interaction (E10); the JavaScript of a Kazo app on the same
origin can read and write the same OPFS (D61.1, 182); and no browser grants `persist()` without
a user gesture (E2), so an image can be evicted by the browser: `EstimateAsync` is there to show
it, persistence and the eviction warning belong to Kazo's ADR 0250 and work item 8.

## Errors

`StorageException` with a code of `StorageErrorCodes`: `opfs-unavailable`,
`manifest-fetch-failed`, `manifest-integrity`, `manifest-invalid`, `format-unsupported`,
`chunk-integrity`, `network`, `http`, `write-failed`, `quota-exceeded`, `locked`, `not-found`,
`protocol`, `worker-error`, `disposed`. A cancellation is an `OperationCanceledException`. A
worker error outside any request (crash, malformed message) is raised through
`UnroutedErrorReported` and fails the pending requests.

## Tests

- `dotnet test --filter Category=unit`: the manifest (field-by-field validation, unknown
  formats, hashes), the metadata, the resume plan, the state machine, `ImageStore` against
  `SimulatedStorageWorkerChannel` (full download, resume, reset, refusal before any write,
  errors, cancellation, list, delete, estimate, verify), `JsStorageWorkerChannel` against a fake
  `IJSRuntime`.
- `dotnet test --filter Category=guest`: `ImageDownloadTests` opens
  `tests/OsX86.Tests/Storage/storage.html` in a headless Chromium; `ChunkImageServer` serves an
  800 MiB image generated on the fly (deterministic pattern, never on disk, never committed;
  `OSX86_STORAGE_TEST_MIB` changes the size) and simulates, as E3 did, two 503s and three
  connections cut after 20 MiB. The image downloads, verifies in place, is listed and counted in
  the estimate; a second worker is refused by the lock; after a page reload in the middle of a
  download, the resume state names only served chunks and the next download requests only the
  missing ones. Measured on the development machine: 800 MiB in 7 s with the faults, resume of
  22 chunks out of 26 in 3.7 s. Not part of the `unit` suite; the CI runs it on the engine and
  storage paths, nightly and on demand (`guest-tests.yml`).
- By hand: serve the repository root and open `tests/OsX86.Tests/Storage/storage.html`.

## Decisions taken in this work item

- The C# side decides, the worker executes: manifest validation and the resume plan are in C#
  (testable without a browser); the worker downloads only the list it is given and re-checks the
  names before creating a directory.
- Lock through `navigator.locks` (`ifAvailable`), released by the browser, rather than a lock
  file with an expiry; fallback on the exclusivity of the `SyncAccessHandle`.
- Two chunks in parallel by default, four at most; to re-measure on R2.
- One payload `image.bin` per version plus a map of the logical files, rather than one OPFS
  file per logical file: writes stay sequential per chunk and a file's offset comes from the
  manifest.
- `storage-worker.js` is one file of 880 lines (60 of them the protocol at the top): a worker
  loads by URL, and splitting it would have added an `importScripts` and a second download to
  separate code that is reused nowhere else.
- Not verified here: Firefox and Safari (E3 saw Firefox behave like Chrome on the download; no
  Mac to measure Safari), the real R2 (origin to obtain, 202 A), loading the module under Kazo
  (work item 8).
