// Os-x86 storage worker: downloads, verifies and stores images in the origin private file
// system (milestone 0, work item 3; decisions 203 A, 177 A, E11; trials E2 and E3). Started by
// storage-host.js; the C# side (ImageStore) speaks the protocol below through it. Everything
// that needs a FileSystemSyncAccessHandle lives here, because that handle only exists in a
// dedicated worker. The C# side owns the decisions (it validates the manifest and computes the
// resume plan); this worker executes: fetch with cache "no-store", SHA-256 per chunk with
// SubtleCrypto compared to the manifest, write at the chunk's offset, flush, then record the
// chunk as verified in the resume state. A cut costs at most the chunk in progress.
//
// Protocol: every message is a JSON string; every command carries an `id` that its answers
// repeat. `progress` messages may precede the final answer of a download or a verification.
//
//   host -> worker
//     {type:"fetch-manifest", id, url, sha256}                      -> manifest | error
//     {type:"read-state", id, image, version}                       -> state
//     {type:"download", id, image, version, baseUrl, manifest, metadata, chunks:[i...], reset, parallelism}
//                                                                   -> progress*, done | error
//     {type:"cancel", id, target}                                   -> (the target ends with error "cancelled")
//     {type:"list", id}                                             -> images
//     {type:"delete", id, image, version}                           -> done
//     {type:"estimate", id}                                         -> estimate
//     {type:"read-manifest", id, image, version}                    -> manifest
//     {type:"verify", id, image, version, manifest}                 -> progress*, verified
//   worker -> host
//     {type:"ready", opfs, locks}                                   posted once at startup
//     {type:"manifest", id, text}
//     {type:"state", id, state:{manifestSha256, verified:[i...], complete} | null}
//     {type:"progress", id, bytesVerified, bytesTotal, chunksVerified, chunksTotal, bytesPerSecond}
//     {type:"done", id, bytesOnDisk}
//     {type:"images", id, images:[{image, version, size, bytesOnDisk, complete, format, engine}], repositoryBytes}
//     {type:"estimate", id, usage, quota, repositoryBytes}
//     {type:"verified", id, chunksTotal, bad:[i...]}
//     {type:"error", id?, code, message}                            without id: the worker itself failed
//
// Error codes: opfs-unavailable, manifest-fetch-failed, manifest-integrity, manifest-invalid,
// chunk-integrity, network, http, write-failed, quota-exceeded, locked, not-found, cancelled,
// protocol, worker-error.
//
// OPFS layout (StorageLayout.cs is the reference): osx86/images/<image>/<version>/ holds
// manifest.json (the published manifest), image.bin (the chunks concatenated), download.json
// and download.1.json (the resume state, written alternately with a sequence number so a torn
// write never loses the other copy) and image.meta.json (the versioned metadata, "complete"
// once every chunk is verified).
//
// Lock: one writer per image version (E11). navigator.locks.request(), waited for 2 s at most
// then "locked", held for the whole download, verification or deletion; a page reload or a
// crash releases it.
// Without the Web Locks API, the exclusivity of createSyncAccessHandle() on image.bin is the
// guard. flush(): after every chunk before the state records it, every 5 s while a file is
// open for writing (177 A), and at close.
"use strict";

const ROOT = "osx86";
const IMAGES = "images";
const MANIFEST_FILE = "manifest.json";
const PAYLOAD_FILE = "image.bin";
const STATE_FILES = ["download.json", "download.1.json"];
const METADATA_FILE = "image.meta.json";
const STATE_FORMAT = 1;
const MAX_MANIFEST_BYTES = 1024 * 1024;
const MAX_TEXT_BYTES = 1024 * 1024;
const MIN_CHUNK_SIZE = 64 * 1024;
const MAX_CHUNK_SIZE = 64 * 1024 * 1024;
const MAX_CHUNKS = 100000;
const MAX_PARALLELISM = 4;
const MAX_ATTEMPTS = 8;
const MAX_HASH_ATTEMPTS = 3;
const RETRY_BASE_MS = 500;
const RETRY_MAX_MS = 30000;
const IDLE_TIMEOUT_MS = 30000;
const FLUSH_INTERVAL_MS = 5000;
const PROGRESS_INTERVAL_MS = 200;
const SPEED_WINDOW_MS = 5000;
const OPEN_RETRIES = 5;
const OPEN_RETRY_MS = 200;
const LOCK_WAIT_MS = 2000;
const IMAGE_ID = /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/;
const VERSION = /^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;
const HEX64 = /^[0-9a-f]{64}$/;
const MAX_NAME = 64;

const cancellable = new Map(); // request id -> AbortController of a download or a verification

class StorageError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}

function send(message) {
  postMessage(JSON.stringify(message));
}

function fail(id, error) {
  const code = error instanceof StorageError ? error.code : "worker-error";
  const message = error && error.message ? error.message : String(error);
  send(id === undefined ? { type: "error", code, message } : { type: "error", id, code, message });
}

/** Sleeps, or resolves early when the signal aborts (the caller checks the signal afterwards). */
function sleep(ms, signal) {
  return new Promise((resolve) => {
    const timer = setTimeout(done, ms);
    function done() {
      clearTimeout(timer);
      if (signal) {
        signal.removeEventListener("abort", done);
      }
      resolve();
    }
    if (signal) {
      signal.addEventListener("abort", done, { once: true });
    }
  });
}

function hex(buffer) {
  const bytes = new Uint8Array(buffer);
  let text = "";
  for (let index = 0; index < bytes.length; index++) {
    text += bytes[index].toString(16).padStart(2, "0");
  }
  return text;
}

async function sha256(bytes) {
  return hex(await crypto.subtle.digest("SHA-256", bytes));
}

const cancelled = () => new StorageError("cancelled", "the operation was cancelled");

// --- names and OPFS ------------------------------------------------------------------------------

function opfsAvailable() {
  return !!(self.navigator && navigator.storage && typeof navigator.storage.getDirectory === "function"
    && typeof FileSystemFileHandle !== "undefined" && typeof FileSystemFileHandle.prototype.createSyncAccessHandle === "function");
}

function checkNames(image, version) {
  if (typeof image !== "string" || image.length > MAX_NAME || !IMAGE_ID.test(image)) {
    throw new StorageError("protocol", `'${image}' is not an image identifier`);
  }
  if (typeof version !== "string" || version.length > MAX_NAME || !VERSION.test(version)) {
    throw new StorageError("protocol", `'${version}' is not a semantic version`);
  }
}

function opfsError(error, context) {
  if (error instanceof StorageError) {
    return error;
  }
  const name = error && error.name;
  if (name === "QuotaExceededError") {
    return new StorageError("quota-exceeded", `${context}: the browser refused the write for lack of storage quota`);
  }
  if (name === "NoModificationAllowedError" || name === "InvalidModificationError") {
    return new StorageError("locked", `${context}: another writer holds the file`);
  }
  if (name === "NotFoundError") {
    return new StorageError("not-found", `${context}: not found`);
  }
  if (name === "SecurityError" || name === "NotAllowedError") {
    return new StorageError("opfs-unavailable", `${context}: the origin private file system is not allowed here (${name})`);
  }
  return new StorageError("write-failed", `${context}: ${error && error.message ? error.message : error}`);
}

const isGone = (error) => error && (error.name === "NotFoundError" || error.name === "NoModificationAllowedError" || error.name === "InvalidModificationError");

async function getDirectory(parts, create) {
  if (!opfsAvailable()) {
    throw new StorageError("opfs-unavailable", "the origin private file system is not available in this worker");
  }
  let directory;
  try {
    directory = await navigator.storage.getDirectory();
  } catch (error) {
    throw opfsError(error, "opfs root");
  }
  for (const part of parts) {
    try {
      directory = await directory.getDirectoryHandle(part, { create: !!create });
    } catch (error) {
      throw opfsError(error, parts.join("/"));
    }
  }
  return directory;
}

const imagesDirectory = (create) => getDirectory([ROOT, IMAGES], create);

async function imageDirectory(image, version, create) {
  checkNames(image, version);
  return getDirectory([ROOT, IMAGES, image, version], create);
}

/** The text of a small file, or null when it does not exist (or is being removed or written by someone else). */
async function readText(directory, name) {
  let file;
  try {
    file = await (await directory.getFileHandle(name)).getFile();
  } catch (error) {
    if (isGone(error)) {
      return null;
    }
    throw opfsError(error, name);
  }
  if (file.size > MAX_TEXT_BYTES) {
    throw new StorageError("manifest-invalid", `${name} is ${file.size} bytes, more than the ${MAX_TEXT_BYTES} accepted`);
  }
  return file.text();
}

function writeText(directory, name, text) {
  return withSyncHandle(directory, name, true, (handle) => {
    const bytes = new TextEncoder().encode(text);
    handle.truncate(0);
    handle.write(bytes, { at: 0 });
    handle.flush();
  });
}

/** Opens a sync access handle, retrying a few times while the browser releases one a terminated worker held. */
async function openSyncHandle(directory, name, create) {
  for (let attempt = 1; ; attempt++) {
    try {
      return await (await directory.getFileHandle(name, { create: !!create })).createSyncAccessHandle();
    } catch (error) {
      if (!(error && error.name === "NoModificationAllowedError") || attempt >= OPEN_RETRIES) {
        throw opfsError(error, name);
      }
      await sleep(OPEN_RETRY_MS);
    }
  }
}

async function withSyncHandle(directory, name, create, action) {
  const handle = await openSyncHandle(directory, name, create);
  try {
    return await action(handle);
  } catch (error) {
    throw opfsError(error, name);
  } finally {
    handle.close();
  }
}

async function removeIfPresent(directory, name) {
  try {
    await directory.removeEntry(name, { recursive: true });
  } catch (error) {
    if (!error || error.name !== "NotFoundError") {
      throw opfsError(error, name);
    }
  }
}

/** The bytes of a directory; entries that vanish or are locked meanwhile count for nothing. */
async function directoryBytes(directory) {
  let total = 0;
  for await (const [, handle] of directory) {
    try {
      total += handle.kind === "file" ? (await handle.getFile()).size : await directoryBytes(handle);
    } catch (error) {
      if (!isGone(error)) {
        throw opfsError(error, "directory size");
      }
    }
  }
  return total;
}

/** A file open for writing with a sync access handle, flushed every 5 s while dirty and at close (177 A). */
class WritableFile {
  constructor(handle, name) {
    this.handle = handle;
    this.name = name;
    this.dirty = false;
    this.flushError = null;
    this.timer = setInterval(() => {
      try {
        this.flush();
      } catch (error) {
        this.flushError = error; // rethrown by the next write, flush or close, with the request's id
      }
    }, FLUSH_INTERVAL_MS);
  }

  static async open(directory, name) {
    return new WritableFile(await openSyncHandle(directory, name, true), name);
  }

  size() {
    return this.handle.getSize();
  }

  rethrowFlushError() {
    if (this.flushError) {
      const error = this.flushError;
      this.flushError = null;
      throw error;
    }
  }

  truncate(size) {
    this.rethrowFlushError();
    try {
      this.handle.truncate(size);
      this.dirty = true;
    } catch (error) {
      throw opfsError(error, this.name);
    }
  }

  write(bytes, at) {
    this.rethrowFlushError();
    try {
      const written = this.handle.write(bytes, { at });
      if (written !== bytes.length) {
        throw new StorageError("write-failed", `${this.name}: wrote ${written} of ${bytes.length} bytes at ${at}`);
      }
      this.dirty = true;
    } catch (error) {
      throw opfsError(error, this.name);
    }
  }

  flush() {
    this.rethrowFlushError();
    if (!this.dirty) {
      return;
    }
    try {
      this.handle.flush();
      this.dirty = false;
    } catch (error) {
      throw opfsError(error, this.name);
    }
  }

  close() {
    clearInterval(this.timer);
    try {
      this.flush();
    } finally {
      this.handle.close();
    }
  }
}

// --- lock (E11: one writer per image version) ------------------------------------------------------

function locksAvailable() {
  return !!(self.navigator && navigator.locks && typeof navigator.locks.request === "function");
}

async function withImageLock(image, version, action) {
  if (!locksAvailable()) {
    return action(); // createSyncAccessHandle() on image.bin is exclusive: the second writer fails with "locked"
  }
  // Waits a little rather than failing at once: the browser releases the lock of a worker killed by
  // a page reload asynchronously. A lock that is really held still answers "locked" within 2 s.
  const name = `osx86:image:${image}@${version}`;
  const waiting = new AbortController();
  const timer = setTimeout(() => waiting.abort(), LOCK_WAIT_MS);
  let acquired = false;
  try {
    return await navigator.locks.request(name, { signal: waiting.signal }, () => {
      acquired = true;
      clearTimeout(timer);
      return action();
    });
  } catch (error) {
    if (!acquired && error && error.name === "AbortError") {
      throw new StorageError("locked", `${image} ${version}: another operation holds the lock`);
    }
    throw error;
  } finally {
    clearTimeout(timer);
  }
}

// --- manifests and state --------------------------------------------------------------------------

/** The checks the C# validator makes that matter here (bounds of what this worker allocates and names). */
function parseManifestText(text) {
  if (typeof text !== "string" || text.length > MAX_MANIFEST_BYTES) {
    throw new StorageError("manifest-invalid", "the manifest is missing or too large");
  }
  let manifest;
  try {
    manifest = JSON.parse(text);
  } catch (error) {
    throw new StorageError("manifest-invalid", `the manifest is not JSON: ${error.message}`);
  }
  if (!manifest || typeof manifest !== "object" || !Array.isArray(manifest.chunks)
    || !Number.isSafeInteger(manifest.size) || manifest.size < 1
    || !Number.isSafeInteger(manifest.chunkSize) || manifest.chunkSize < MIN_CHUNK_SIZE || manifest.chunkSize > MAX_CHUNK_SIZE
    || manifest.chunks.length > MAX_CHUNKS
    || manifest.chunks.length !== Math.ceil(manifest.size / manifest.chunkSize)
    || !manifest.chunks.every((chunk) => chunk && typeof chunk.sha256 === "string" && HEX64.test(chunk.sha256))) {
    throw new StorageError("manifest-invalid", "the manifest has no consistent size, chunkSize and chunks");
  }
  return manifest;
}

const chunkFileName = (index) => `chunk-${String(index).padStart(5, "0")}.bin`;
const chunkLength = (manifest, index) => Math.min(manifest.chunkSize, manifest.size - index * manifest.chunkSize);
const FETCH_OPTIONS = { cache: "no-store", credentials: "omit", referrerPolicy: "no-referrer" };

/** Reads a response body into a new buffer, refusing more than `limit` bytes before they are kept. */
async function readBounded(response, limit, what) {
  const declared = Number(response.headers.get("content-length"));
  if (Number.isFinite(declared) && declared > limit) {
    throw new StorageError("manifest-fetch-failed", `${what}: ${declared} bytes, more than the ${limit} accepted`);
  }
  if (!response.body) {
    return new Uint8Array(await response.arrayBuffer()).subarray(0, limit + 1);
  }
  const reader = response.body.getReader();
  const parts = [];
  let received = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      received += value.length;
      if (received > limit) {
        throw new StorageError("manifest-fetch-failed", `${what}: more than the ${limit} bytes accepted`);
      }
      parts.push(value);
    }
  } finally {
    reader.cancel().catch(() => {});
  }
  const bytes = new Uint8Array(received);
  let offset = 0;
  for (const part of parts) {
    bytes.set(part, offset);
    offset += part.length;
  }
  return bytes;
}

async function fetchManifest(id, url, expected) {
  if (typeof expected !== "string" || !HEX64.test(expected)) {
    throw new StorageError("protocol", "fetch-manifest needs a lowercase hex sha256");
  }
  let response;
  try {
    response = await fetch(String(url), FETCH_OPTIONS);
  } catch (error) {
    throw new StorageError("manifest-fetch-failed", `${url}: ${error.message}`);
  }
  if (!response.ok) {
    throw new StorageError("manifest-fetch-failed", `${url}: HTTP ${response.status}`);
  }
  const bytes = await readBounded(response, MAX_MANIFEST_BYTES, String(url));
  if (bytes.length > MAX_MANIFEST_BYTES) {
    throw new StorageError("manifest-fetch-failed", `${url}: more than the ${MAX_MANIFEST_BYTES} bytes accepted`);
  }
  const actual = await sha256(bytes);
  if (actual !== expected) {
    throw new StorageError("manifest-integrity", `the manifest hashes to ${actual}, not to the expected ${expected}: the image is refused`);
  }
  let text;
  try {
    text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes); // a BOM stays in the text: the C# side refuses it as invalid JSON
  } catch (error) {
    throw new StorageError("manifest-invalid", `the manifest is not UTF-8: ${error.message}`);
  }
  send({ type: "manifest", id, text });
}

function parseJsonOrNull(text) {
  if (text === null) {
    return null;
  }
  try {
    const value = JSON.parse(text);
    return value && typeof value === "object" ? value : null;
  } catch (error) {
    return null;
  }
}

function parseState(text) {
  const state = parseJsonOrNull(text);
  if (!state || state.format !== STATE_FORMAT || typeof state.manifestSha256 !== "string" || !HEX64.test(state.manifestSha256)
    || !Number.isSafeInteger(state.seq) || state.seq < 0
    || !Array.isArray(state.verified) || !state.verified.every((index) => Number.isSafeInteger(index) && index >= 0)) {
    return null;
  }
  return state;
}

/** The valid resume state with the highest sequence number among the two alternating files (a torn write spoils one at most). */
async function readStoredState(directory) {
  let best = null;
  for (const name of STATE_FILES) {
    const state = parseState(await readText(directory, name));
    if (state && (!best || state.seq > best.seq)) {
      best = state;
    }
  }
  return best;
}

async function readState(id, image, version) {
  let directory;
  try {
    directory = await imageDirectory(image, version, false);
  } catch (error) {
    if (error.code === "not-found") {
      send({ type: "state", id, state: null });
      return;
    }
    throw error;
  }
  const metadata = parseJsonOrNull(await readText(directory, METADATA_FILE));
  const state = await readStoredState(directory);
  if (metadata === null && state === null) {
    send({ type: "state", id, state: null });
    return;
  }
  send({
    type: "state",
    id,
    state: {
      manifestSha256: state ? state.manifestSha256 : null,
      verified: state ? state.verified : null,
      complete: !!(metadata && metadata.complete === true),
    },
  });
}

// --- download --------------------------------------------------------------------------------------

function retryDelay(attempt) {
  return Math.min(RETRY_MAX_MS, RETRY_BASE_MS * 2 ** (attempt - 1)) + Math.floor(Math.random() * 250);
}

class ChunkFetchError extends Error {
  constructor(code, message, retryable) {
    super(message);
    this.code = code;
    this.retryable = retryable;
  }
}

/**
 * Fetches one chunk into `target` (exactly `length` bytes), reporting every piece received to
 * `onBytes`. Its own AbortController follows the job's signal and an idle watchdog: a server
 * that stalls for 30 s costs a retry, not a hang. Rejects with a ChunkFetchError (retryable or
 * not) or with "cancelled".
 */
async function fetchChunk(url, length, target, jobSignal, onBytes) {
  const attempt = new AbortController();
  const onAbort = () => attempt.abort();
  jobSignal.addEventListener("abort", onAbort, { once: true });
  let watchdog = setTimeout(onAbort, IDLE_TIMEOUT_MS);
  const rearm = () => {
    clearTimeout(watchdog);
    watchdog = setTimeout(onAbort, IDLE_TIMEOUT_MS);
  };
  const translate = (error, received) => {
    if (jobSignal.aborted) {
      return cancelled();
    }
    if (error instanceof ChunkFetchError) {
      return error;
    }
    if (error && error.name === "AbortError") {
      return new ChunkFetchError("network", `${url}: no data for ${IDLE_TIMEOUT_MS / 1000} s after ${received} bytes`, true);
    }
    return new ChunkFetchError("network", `${url}: ${error && error.message ? error.message : error} after ${received} bytes`, true);
  };
  let reader = null;
  let received = 0;
  try {
    if (jobSignal.aborted) {
      throw cancelled();
    }
    let response;
    try {
      response = await fetch(url, { ...FETCH_OPTIONS, signal: attempt.signal });
    } catch (error) {
      throw translate(error, 0);
    }
    if (!response.ok) {
      const retryable = response.status >= 500 || response.status === 429 || response.status === 408;
      throw new ChunkFetchError(retryable ? "network" : "http", `${url}: HTTP ${response.status}`, retryable);
    }
    if (!response.body) {
      throw new ChunkFetchError("network", `${url}: no body`, true);
    }
    reader = response.body.getReader();
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }
        rearm();
        if (received + value.length > length) {
          throw new ChunkFetchError("network", `${url}: more than the ${length} bytes expected`, true);
        }
        target.set(value, received);
        received += value.length;
        onBytes(value.length);
      }
    } catch (error) {
      throw translate(error, received);
    }
    if (received !== length) {
      throw new ChunkFetchError("network", `${url}: received ${received} of ${length} bytes`, true);
    }
  } finally {
    clearTimeout(watchdog);
    jobSignal.removeEventListener("abort", onAbort);
    if (reader) {
      reader.cancel().catch(() => {});
    }
  }
}

class Download {
  constructor(id, command, manifest, directory, controller) {
    this.id = id;
    this.command = command;
    this.manifest = manifest;
    this.directory = directory;
    this.controller = controller;
    this.verified = new Set();
    this.state = null;
    this.payload = null;
    this.samples = [];
    this.startedAt = performance.now();
    this.lastProgress = 0;
    this.stateWrite = Promise.resolve();
  }

  get chunksTotal() {
    return this.manifest.chunks.length;
  }

  get bytesVerified() {
    let total = 0;
    for (const index of this.verified) {
      total += chunkLength(this.manifest, index);
    }
    return total;
  }

  /** Bytes received over the last five seconds (or since the start), whatever became of them. */
  bytesPerSecond() {
    const now = performance.now();
    while (this.samples.length > 0 && now - this.samples[0].at > SPEED_WINDOW_MS) {
      this.samples.shift();
    }
    if (this.samples.length === 0) {
      return 0;
    }
    const bytes = this.samples.reduce((sum, sample) => sum + sample.bytes, 0);
    const span = Math.max(100, Math.min(SPEED_WINDOW_MS, now - this.startedAt));
    return (bytes * 1000) / span;
  }

  received(bytes) {
    this.samples.push({ at: performance.now(), bytes });
    this.progress(false);
  }

  progress(force) {
    const now = performance.now();
    if (!force && now - this.lastProgress < PROGRESS_INTERVAL_MS) {
      return;
    }
    this.lastProgress = now;
    send({
      type: "progress",
      id: this.id,
      bytesVerified: this.bytesVerified,
      bytesTotal: this.manifest.size,
      chunksVerified: this.verified.size,
      chunksTotal: this.chunksTotal,
      bytesPerSecond: Math.round(this.bytesPerSecond()),
    });
  }

  throwIfCancelled() {
    if (this.controller.signal.aborted) {
      throw cancelled();
    }
  }

  /**
   * Opens the payload first (the exclusive handle is the guard without Web Locks), then settles
   * the files around it: a reset or a payload of the wrong size discards the previous state, the
   * manifest copy and the metadata are (re)written, and the state file is written once so the
   * next attempt sees a consistent set.
   */
  async prepare(manifestSha256) {
    const { command, directory } = this;
    this.payload = await WritableFile.open(directory, PAYLOAD_FILE);
    if (command.reset) {
      for (const name of STATE_FILES) {
        await removeIfPresent(directory, name);
      }
      await removeIfPresent(directory, METADATA_FILE);
      this.payload.truncate(0);
    }
    const stored = command.reset ? null : await readStoredState(directory);
    const sizeMatches = this.payload.size() === this.manifest.size;
    if (stored && stored.manifestSha256 === manifestSha256 && sizeMatches) {
      for (const index of stored.verified) {
        if (index < this.chunksTotal) {
          this.verified.add(index);
        }
      }
    }
    if (!sizeMatches) {
      this.payload.truncate(this.manifest.size);
      this.payload.flush();
    }
    if ((await readText(directory, MANIFEST_FILE)) !== command.manifest) {
      await writeText(directory, MANIFEST_FILE, command.manifest);
    }
    const metadata = parseJsonOrNull(await readText(directory, METADATA_FILE));
    if (!metadata || metadata.complete !== true || this.verified.size !== this.chunksTotal) {
      await writeText(directory, METADATA_FILE, command.metadata);
    }
    this.state = { format: STATE_FORMAT, manifestSha256, seq: stored ? stored.seq : 0, verified: [] };
    await this.recordVerified(null);
  }

  /** Writes the state, alternating between the two files, one write at a time and in order. */
  async recordVerified(index) {
    if (index !== null) {
      this.verified.add(index);
    }
    this.state.seq++;
    this.state.verified = [...this.verified].sort((a, b) => a - b);
    const text = JSON.stringify(this.state);
    const name = STATE_FILES[this.state.seq % STATE_FILES.length];
    this.stateWrite = this.stateWrite.then(() => writeText(this.directory, name, text));
    await this.stateWrite;
  }

  async fetchOne(index, buffer) {
    const length = chunkLength(this.manifest, index);
    const url = new URL(chunkFileName(index), this.command.baseUrl).href;
    const expected = this.manifest.chunks[index].sha256;
    const target = buffer.subarray(0, length);
    let attempt = 0;
    let hashAttempts = 0;
    for (;;) {
      this.throwIfCancelled();
      attempt++;
      try {
        await fetchChunk(url, length, target, this.controller.signal, (bytes) => this.received(bytes));
        const actual = await sha256(target);
        if (actual !== expected) {
          hashAttempts++;
          if (hashAttempts >= MAX_HASH_ATTEMPTS) {
            throw new StorageError("chunk-integrity", `${chunkFileName(index)} hashes to ${actual}, not to ${expected}, after ${hashAttempts} attempts`);
          }
          throw new ChunkFetchError("chunk-integrity", `${chunkFileName(index)} hashes to ${actual}, not to ${expected}`, true);
        }
        return;
      } catch (error) {
        if (!(error instanceof ChunkFetchError) || !error.retryable || attempt >= MAX_ATTEMPTS) {
          throw error instanceof ChunkFetchError ? new StorageError(error.code, `${error.message} (attempt ${attempt} of ${MAX_ATTEMPTS})`) : error;
        }
        await sleep(retryDelay(attempt), this.controller.signal);
      }
    }
  }

  async runSlot(queue, buffer) {
    for (;;) {
      const index = queue.shift();
      if (index === undefined) {
        return;
      }
      await this.fetchOne(index, buffer);
      this.throwIfCancelled();
      this.payload.write(buffer.subarray(0, chunkLength(this.manifest, index)), index * this.manifest.chunkSize);
      this.payload.flush();
      await this.recordVerified(index);
      this.progress(false);
    }
  }

  async run() {
    const queue = this.command.chunks.filter((index) => !this.verified.has(index));
    const slots = Math.max(1, Math.min(queue.length, this.command.parallelism));
    this.progress(true);
    const workers = [];
    try {
      const bufferSize = Math.min(this.manifest.chunkSize, this.manifest.size);
      for (let slot = 0; slot < slots; slot++) {
        workers.push(this.runSlot(queue, new Uint8Array(bufferSize)));
      }
      await Promise.all(workers);
    } catch (error) {
      this.controller.abort();
      await Promise.allSettled(workers);
      throw error;
    }
  }

  async complete() {
    const payload = this.payload;
    this.payload = null;
    payload.close();
    if (this.verified.size !== this.chunksTotal) {
      throw new StorageError("protocol", `${this.verified.size} of ${this.chunksTotal} chunks verified: the download command did not list every missing chunk`);
    }
    const metadata = parseJsonOrNull(await readText(this.directory, METADATA_FILE)) || parseJsonOrNull(this.command.metadata) || {};
    metadata.complete = true;
    await writeText(this.directory, METADATA_FILE, JSON.stringify(metadata));
    this.progress(true);
    send({ type: "done", id: this.id, bytesOnDisk: await directoryBytes(this.directory) });
  }

  async close() {
    const payload = this.payload;
    this.payload = null;
    if (payload) {
      try {
        payload.close();
      } catch (error) {
        // The chunks the state names are already flushed; a failed last flush loses nothing recorded.
      }
    }
    await this.stateWrite.catch(() => {});
  }
}

function validateDownloadCommand(command) {
  checkNames(command.image, command.version);
  if (typeof command.baseUrl !== "string" || typeof command.metadata !== "string" || command.metadata.length > MAX_TEXT_BYTES
    || !Array.isArray(command.chunks)) {
    throw new StorageError("protocol", "download needs baseUrl, manifest, metadata and chunks");
  }
  let base;
  try {
    base = new URL(command.baseUrl);
  } catch (error) {
    throw new StorageError("protocol", "baseUrl is not a URL");
  }
  if ((base.protocol !== "https:" && base.protocol !== "http:") || !base.pathname.endsWith("/")) {
    throw new StorageError("protocol", "baseUrl must be an http(s) directory ending with '/'");
  }
  const manifest = parseManifestText(command.manifest);
  if (manifest.image !== command.image || manifest.version !== command.version) {
    throw new StorageError("protocol", "the manifest does not describe the image of the command");
  }
  if (!command.chunks.every((index) => Number.isSafeInteger(index) && index >= 0 && index < manifest.chunks.length)) {
    throw new StorageError("protocol", "a chunk index is outside the manifest");
  }
  command.chunks = [...new Set(command.chunks)];
  command.parallelism = Math.max(1, Math.min(MAX_PARALLELISM, Number(command.parallelism) || 1));
  return manifest;
}

async function download(id, command) {
  const controller = new AbortController();
  cancellable.set(id, controller); // before the first await, so an early cancel is not lost
  try {
    const manifest = validateDownloadCommand(command);
    const manifestSha256 = await sha256(new TextEncoder().encode(command.manifest));
    await withImageLock(command.image, command.version, async () => {
      const directory = await imageDirectory(command.image, command.version, true);
      const job = new Download(id, command, manifest, directory, controller);
      try {
        await job.prepare(manifestSha256);
        job.throwIfCancelled();
        await job.run();
        job.throwIfCancelled();
        await job.complete();
      } finally {
        await job.close();
      }
    });
  } finally {
    cancellable.delete(id);
  }
}

// --- repository ------------------------------------------------------------------------------------

async function listImages() {
  const images = [];
  let repositoryBytes = 0;
  let root;
  try {
    root = await imagesDirectory(false);
  } catch (error) {
    if (error.code === "not-found") {
      return { images, repositoryBytes };
    }
    throw error;
  }
  for await (const [image, imageHandle] of root) {
    if (imageHandle.kind !== "directory" || !IMAGE_ID.test(image)) {
      continue;
    }
    for await (const [version, versionHandle] of imageHandle) {
      if (versionHandle.kind !== "directory" || !VERSION.test(version)) {
        continue;
      }
      const entry = await describeImage(image, version, versionHandle);
      if (entry) {
        repositoryBytes += entry.bytesOnDisk;
        images.push(entry);
      }
    }
  }
  return { images, repositoryBytes };
}

/** One version directory as the list reports it; null when it vanished meanwhile (a concurrent delete). */
async function describeImage(image, version, directory) {
  try {
    const metadata = parseJsonOrNull(await readText(directory, METADATA_FILE));
    const manifest = parseJsonOrNull(await readText(directory, MANIFEST_FILE));
    return {
      image,
      version,
      size: manifest && Number.isSafeInteger(manifest.size) ? manifest.size : 0,
      bytesOnDisk: await directoryBytes(directory),
      complete: !!(metadata && metadata.complete === true),
      format: metadata && Number.isSafeInteger(metadata.format) ? metadata.format : 0,
      engine: metadata && metadata.engine && typeof metadata.engine === "object" ? metadata.engine : null,
    };
  } catch (error) {
    if (error.code === "not-found") {
      return null;
    }
    throw error;
  }
}

async function deleteImage(image, version) {
  checkNames(image, version);
  await withImageLock(image, version, async () => {
    let root;
    let imageHandle;
    try {
      root = await imagesDirectory(false);
      imageHandle = await root.getDirectoryHandle(image);
      await imageHandle.removeEntry(version, { recursive: true });
    } catch (error) {
      if (error && (error.name === "NotFoundError" || error.code === "not-found")) {
        return;
      }
      throw opfsError(error, `${image}/${version}`);
    }
    try {
      await root.removeEntry(image); // not recursive: fails harmlessly when another version appeared meanwhile
    } catch (error) {
      if (!isGone(error)) {
        throw opfsError(error, image);
      }
    }
  });
}

async function estimate() {
  const { repositoryBytes } = await listImages();
  let usage = 0;
  let quota = 0;
  if (navigator.storage && typeof navigator.storage.estimate === "function") {
    const result = await navigator.storage.estimate();
    usage = Number(result.usage) || 0;
    quota = Number(result.quota) || 0;
  }
  return { usage, quota, repositoryBytes };
}

/** The local manifest copy, pinned to the hash the resume state recorded when the download accepted it. */
async function readManifest(image, version) {
  const directory = await imageDirectory(image, version, false);
  const text = await readText(directory, MANIFEST_FILE);
  if (text === null) {
    throw new StorageError("not-found", `${image} ${version}: no manifest in the repository`);
  }
  if (text.length > MAX_MANIFEST_BYTES) {
    throw new StorageError("manifest-invalid", `${image} ${version}: the stored manifest is too large`);
  }
  const state = await readStoredState(directory);
  if (state && state.manifestSha256 !== (await sha256(new TextEncoder().encode(text)))) {
    throw new StorageError("manifest-integrity", `${image} ${version}: the stored manifest is not the one the download verified`);
  }
  return text;
}

async function verify(id, command) {
  checkNames(command.image, command.version);
  const manifest = parseManifestText(command.manifest);
  const controller = new AbortController();
  cancellable.set(id, controller);
  try {
    return await withImageLock(command.image, command.version, async () => {
      const directory = await imageDirectory(command.image, command.version, false);
      return withSyncHandle(directory, PAYLOAD_FILE, false, async (handle) => {
        const buffer = new Uint8Array(Math.min(manifest.chunkSize, manifest.size));
        const bad = [];
        let lastProgress = 0;
        let bytesVerified = 0;
        const report = () => send({ type: "progress", id, bytesVerified, bytesTotal: manifest.size, chunksVerified: manifest.chunks.length - bad.length, chunksTotal: manifest.chunks.length, bytesPerSecond: 0 });
        for (let index = 0; index < manifest.chunks.length; index++) {
          if (controller.signal.aborted) {
            throw cancelled();
          }
          const length = chunkLength(manifest, index);
          const target = buffer.subarray(0, length);
          const read = handle.read(target, { at: index * manifest.chunkSize });
          if (read !== length || (await sha256(target)) !== manifest.chunks[index].sha256) {
            bad.push(index);
          } else {
            bytesVerified += length;
          }
          if (performance.now() - lastProgress > PROGRESS_INTERVAL_MS) {
            lastProgress = performance.now();
            report();
          }
        }
        report();
        return { chunksTotal: manifest.chunks.length, bad };
      });
    });
  } finally {
    cancellable.delete(id);
  }
}

// --- dispatch --------------------------------------------------------------------------------------

async function handle(message) {
  const id = message.id;
  switch (message.type) {
    case "fetch-manifest":
      await fetchManifest(id, message.url, message.sha256);
      break;
    case "read-state":
      await readState(id, message.image, message.version);
      break;
    case "download":
      await download(id, message); // answers "done" itself once the metadata is marked complete
      break;
    case "cancel": {
      const controller = cancellable.get(message.target);
      if (controller) {
        controller.abort();
      }
      break;
    }
    case "list": {
      const { images, repositoryBytes } = await listImages();
      send({ type: "images", id, images, repositoryBytes });
      break;
    }
    case "delete":
      await deleteImage(message.image, message.version);
      send({ type: "done", id });
      break;
    case "estimate":
      send({ type: "estimate", id, ...(await estimate()) });
      break;
    case "read-manifest":
      send({ type: "manifest", id, text: await readManifest(message.image, message.version) });
      break;
    case "verify":
      send({ type: "verified", id, ...(await verify(id, message)) });
      break;
    default:
      throw new StorageError("protocol", `unknown message type ${message.type}`);
  }
}

onmessage = async (event) => {
  let message;
  try {
    message = JSON.parse(String(event.data));
  } catch (error) {
    fail(undefined, new StorageError("protocol", `not a JSON message: ${error.message}`));
    return;
  }
  if (!message || typeof message.type !== "string" || !Number.isSafeInteger(message.id)) {
    fail(undefined, new StorageError("protocol", "message without a type or an id"));
    return;
  }
  try {
    await handle(message);
  } catch (error) {
    fail(message.id, error);
  }
};

self.onunhandledrejection = (event) => {
  event.preventDefault();
  fail(undefined, event.reason);
};

send({ type: "ready", opfs: opfsAvailable(), locks: locksAvailable() });
