> [Version française](../fr/stockage.md)

# Storage: image manifest, verified download, OPFS repository

Milestone 0, work item 3 (`j0/3-storage`). Storage is the piece that brings an image into the
browser and keeps it there: a manifest published next to 32 MiB chunks, a Web Worker that
downloads them, verifies them (SHA-256) and writes them into the origin private file system
(OPFS), an image repository by identifier and version, a resume state, a write lock, and the
versioned-format structure of plan section 13. Decisions 169, 177 A, 188, 202 A, 203 A, E11;
trials [E2](../essais/e2-stockage.md) (OPFS) and [E3](../essais/e3-distribution.md)
(distribution), whose code this work item turns into a product (both in French). The recipes
that produce the images and their manifests are work item 4; opening an image in the engine
and the write layers of the disks, work item 6a (see "Write layers" below and [`engine.md`](engine.md)).

## Where things are

| Path | Contents |
| --- | --- |
| `src/OsX86/Storage/` | `ImageManifest` (reader and validator), `StorageLayout` (OPFS names and paths), `StoredObjectMetadata` (versioned formats), `ResumePlan`, `DownloadStateMachine` and `DownloadProgress`, `ImageStore` (the façade), `ImageCatalog` and `KnownImage` (the known images, work item 6b), `IStorageWorkerChannel` and `JsStorageWorkerChannel` (JS interop), the protocol, the exceptions |
| `src/OsX86.Feature/wwwroot/storage/` | `storage-host.js` (module imported by the C# side, creates the worker) and `storage-worker.js` (download, SHA-256, OPFS, lock) |
| `tests/OsX86.Tests/Storage/` | `unit` tests (manifest, metadata, resume plan, state machine, `ImageStore` against a simulated worker, JS channel against a fake `IJSRuntime`) and the `guest` test (`ImageDownloadTests`, `storage.html`, `ChunkImageServer`); `ImageCatalogTests` (work item 6b) |

As for the engine, the files of `wwwroot/` are served under `_content/OsX86.Feature/`.

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
  the "same major version" rule (169) is applied by `ImageBoot.CreateStartOptions` since work
  item 6a, before anything is read or written.
- `size`: payload size in bytes (100,000 chunks of 64 MiB at most); `chunkSize`: the size of
  every chunk but the last (64 KiB to 64 MiB, because the worker keeps one chunk in memory per
  parallel stream; the recipes use 32 MiB, the right size according to E3).
- `chunks`: one lowercase hex SHA-256 per chunk, in order, exactly `ceil(size / chunkSize)`
  entries (100,000 at most: five digits in the names). The chunks are the files
  `chunk-00000.bin`, `chunk-00001.bin`, ... next to the manifest.
- `files`: the logical files of the payload (`kernel`, `initramfs`, `rootfs`, ...) with their
  offset and size; may be empty.

The recipes of work item 4 (`pack-image.py`) add optional fields that this reader ignores
without error, as it does any unknown field: `rawSize` and `blockSize` (the payload is a
seekable zstd stream of a raw ext4 disk image, attached as `hda`) and a `sha256` per `files`
entry. The repository stores the chunks as they are; decompression is not its job.

`ImageManifest.Parse(text)` validates all of it; `ImageManifest.Parse(bytes, expectedSha256)`
first compares the hash to the SHA-256 the C# side knows (decision 203 A: the manifest is the
root of trust, the caller must know its hash) and refuses the image (`manifest-integrity`) when
it differs. The manifest itself is capped at 1 MiB (the worker reads no more), must be valid
UTF-8 without a byte order mark, and a duplicate key in `files` or `engine` makes it invalid.

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
  with `network`. A cut costs at most the chunk in progress (E3). A server that stops answering
  costs one attempt after 30 s without a byte (every `fetch` has its own `AbortController`,
  aborted by that watchdog or by the cancellation). A 404 or a 403 fails at once (`http`).
- **Wrong hash**: the chunk is fetched again up to three times, then `chunk-integrity`.
- **Page reload, browser closed, crash**: the resume state is in the OPFS (`download.json` and
  `download.1.json`, written alternately with a sequence number: the manifest hash and the
  verified indices; a torn write spoils one copy at most, the newest valid one is read), written
  after the chunk's `flush()`, so it never names a chunk that is not on disk. An `image.bin` that
  is missing or not the size of the manifest voids the state: everything starts over. The next
  download only requests the missing chunks.
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
        download.json     the resume state: {"format":1,"seq":3,"manifestSha256":"…","verified":[0,1,2]}
        download.1.json   its alternate copy (the highest sequence number wins)
        image.meta.json   the versioned metadata (below), complete:true at the end
```

`ImageStore`: `ListAsync` (every version with its size, its real footprint, `Complete`, its
format and its engine versions; `IsSupported` says whether this version of Os-x86 can open it),
`DeleteAsync` (refused with `locked` during a download), `EstimateAsync`
(`navigator.storage.estimate()`: usage and quota of the origin, plus the repository footprint),
`ReadManifestAsync` (the local copy, validated and pinned to the hash the resume state recorded:
an unknown format or a modified copy is refused), `VerifyAsync` (every chunk re-read and hashed
in place under the lock, 1 s for 800 MiB according to E3), and
`ImageStore.Locate(manifest, "kernel")` which gives the OPFS path of `image.bin` with the
offset and size of the logical file. Since work item 6a the manifest also reads the `rawSize`
and `blockSize` of each file (`ImageFileEntry.IsCompressed`) and the `boot` section
(`ImageBootSection`: `kind`, `cmdline`, `disks`, `readyMarker`, `prompt`, `memoryMiB`), and the
engine reads the kernel, the initramfs and the disks at their offset in `image.bin`, with no
extraction.

### Write layers (work item 6a)

The writes to a disk never touch `image.bin`: they go to one layer per disk,
`osx86/layers/<set>/<disk>.layer`, with its versioned metadata `<disk>.layer.meta.json`
(`StoredObjectMetadata.ForLayer`, kind `layer`). The engine writes them (format described in
[`engine.md`](engine.md)); the storage measures and deletes them:

- `ImageStore.ListLayersAsync(set)`: one `StoredLayer` per disk (size, metadata); a layer an
  engine holds open is returned with `InUse` and no size (the engine reports it).
- `ImageStore.DeleteLayersAsync(set)`: deletes the set under the lock
  `osx86:layer:osx86/layers/<set>` the engine also takes; `locked` while an engine runs on it.
- `StorageLayout.LayerDirectory` and `LayerPath` give the paths; the set name follows the rule of
  image identifiers.
- Since work item 6b, **a VM has its own set**: `vm-<the 32 hexadecimal digits of its GUID>`,
  kept with it in the registry (`VmRecord.LayerSet`) and deleted by `x86 vm remove`
  ([VMs](vm.md#linux-base-by-default-work-item-6b)).

### State of each user (work item 8, 287 A)

Kazo 3.5 lets neither the command (the child `ca.kazo.x86.cli`) nor the `Ix86` service reach the
user's private directory `~/.config/ca.kazo.x86/`. The state of each user is therefore in the OPFS
of the Feature, next to the disks it describes:

```
osx86/
  users/
    <the 32 hexadecimal digits of the user's stable identifier>/
      registry.json          the VM registry and the imported profiles (format 1, see vm.md)
      network-rules.json     the network rules (work item 12)
      network-journal.json   the network journal (work item 12)
  .kazoos-marker             the marker of ADR 0250 of Kazo, which covers the whole of osx86/
```

- `ImageStore.ReadUserFileAsync` and `WriteUserFileAsync`, through the `read-user-file` (answer
  `text`, `null` when the file does not exist) and `write-user-file` messages of the worker;
  `UserStateStore` makes them the store of the registry (`IVmRegistryStore`) and of the network
  (`INetworkStore`), and turns any error of the worker into `storage-failed`.
- The worker only accepts an identifier of 32 hexadecimal digits (not the empty GUID), a name
  `[a-z][a-z0-9-]*.json` and a whole JSON document of at most 4 MiB; one lock per file
  (`osx86:user:<user>/<name>`).
- **Journaled write**: the whole text goes first to `<name>.tmp`, then to `<name>`, then the `.tmp`
  is removed. A write cut short (quota, tab closed) leaves either the old file intact or a whole
  `.tmp`; a read takes the file when it is a whole JSON document, else the `.tmp`. Checked in a real
  browser (`UserFileStorageTests`, a `guest` test).
- **Isolation**: the code of Os-x86 separates the users, as for the layers; the command passes the
  user running it, the service `Caller.UserId`. Any code of the page can talk to the worker: that
  is the known limit of the OPFS, one origin for the whole machine.
- An eviction erases the state with the disks; the command says so once (289 A) and `x86 vm list`
  starts again empty, without error. These files are not visible in the File Explorer.

## Catalog of the known images (work item 6b)

Where images come from is **configuration**, never an address written in the code: the hosting
(R2, Pages) is not settled yet. `ImageCatalog` lists, for each image version the app can download,
the address of its published manifest and the SHA-256 that manifest must have (203 A: the hash
comes from the configuration, never from the origin):

```json
{"format": 1, "images": [
  {"image": "linux-base", "version": "0.1.0",
   "manifestUrl": "images/build/linux-base/pack/manifest.json",
   "manifestSha256": "d5c9c8f0…"}
]}
```

- `ImageCatalog.Parse(text, baseAddress)` reads this format 1 (unknown properties ignored, comments
  and trailing commas accepted, 256 KiB at most); another format is refused
  (`format-unsupported`), an invalid or duplicated entry too (`manifest-invalid`, naming the
  problem). The address must end with `/manifest.json`; a relative one is resolved against the
  address of the page, since a worker would resolve it against its own script.
- `SourceOf(image, version)` returns the `ImageSource` of the download (directory of the manifest,
  hash), with the rule of `ImageSource`: https, http only on the loopback host.
- `StoreBootImageSource` (VM layer) uses it: an image complete in the repository is used as is;
  a missing or partial one is downloaded from the origin of the catalog (resume included); one the
  catalog does not list is `image-unavailable`.

**Local origin.** The dev host `OsX86.Os` serves each pack built in `images/build/<image>/pack/`
at the same path (manifest and chunks only, never published), and the build records
the hash of the pack's `manifest.sha256` as assembly metadata (`OsX86.DevPack`). At start,
`DevImages` reads each manifest, checks it against that hash and makes the catalog of them.
`-p:OsX86DevImages=false` leaves them out. The `guest` tests do the same with a local static
server (`StaticFileServer`). The production catalog comes with the hosting of the images.

### Write lock (E11) and `flush()` (177 A)

One writer per image version: the worker calls
`navigator.locks.request("osx86:image:<image>@<version>")` and holds it for the whole download
or deletion; a second writer waits 2 s at most (the time the browser takes to release the lock
of a worker killed by a reload) then gets `locked` instead of waiting forever.
The browser releases the lock when the page closes or crashes, with no lock file and no expiry
to manage: that is why it is preferred to a lock file. Without the Web Locks API (the worker
says so in `ready`), the exclusivity of `createSyncAccessHandle()` on `image.bin` plays the same
role: the second opener fails, translated to `locked`. Verification takes the same lock, so it
never reads under a download. The worker opens `image.bin` before it writes anything else, and
retries five times at 200 ms when the browser has not yet released the handle of a terminated
worker (page reload).

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
- Lock through `navigator.locks` (bounded wait of 2 s), released by the browser, rather than a
  lock file with an expiry; fallback on the exclusivity of the `SyncAccessHandle`.
- Two chunks in parallel by default, four at most; to re-measure on R2.
- One payload `image.bin` per version plus a map of the logical files, rather than one OPFS
  file per logical file: writes stay sequential per chunk and a file's offset comes from the
  manifest.
- `storage-worker.js` is one file of about 1,000 lines (60 of them the protocol at the top): a worker
  loads by URL, and splitting it would have added an `importScripts` and a second download to
  separate code that is reused nowhere else.
- Not verified here: Firefox and Safari (E3 saw Firefox behave like Chrome on the download; no
  Mac to measure Safari), the real R2 (origin to obtain, 202 A), loading the module under Kazo
  (work item 8).
