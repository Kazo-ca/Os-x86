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
//     {type:"error", id?, code, message}
//
// Error codes: opfs-unavailable, manifest-fetch-failed, manifest-integrity, manifest-invalid,
// chunk-integrity, network, http, write-failed, quota-exceeded, locked, not-found, cancelled,
// protocol, worker-error.
//
// OPFS layout (StorageLayout.cs is the reference): osx86/images/<image>/<version>/ holds
// manifest.json (the published manifest), image.bin (the chunks concatenated), download.json
// (the resume state) and image.meta.json (the versioned metadata, "complete" once every chunk
// is verified).
//
// Lock: one writer per image version (E11). navigator.locks.request() with ifAvailable, held
// for the whole download or deletion; a page reload or a crash releases it. Without the Web
// Locks API, the exclusivity of createSyncAccessHandle() on image.bin is the guard.
// flush(): after every chunk before the state records it, every 5 s while a file is open for
// writing (177 A), and at close.
"use strict";

const ROOT = "osx86";
const IMAGES = "images";
const MANIFEST_FILE = "manifest.json";
const PAYLOAD_FILE = "image.bin";
const STATE_FILE = "download.json";
const METADATA_FILE = "image.meta.json";
const STATE_FORMAT = 1;
const MAX_MANIFEST_BYTES = 1024 * 1024;
const MAX_TEXT_BYTES = 1024 * 1024;
const MAX_PARALLELISM = 4;
const MAX_ATTEMPTS = 8;
const MAX_HASH_ATTEMPTS = 3;
const RETRY_BASE_MS = 500;
const RETRY_MAX_MS = 30000;
const FLUSH_INTERVAL_MS = 5000;
const PROGRESS_INTERVAL_MS = 200;
const SPEED_WINDOW_MS = 5000;
const IMAGE_ID = /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/;
const VERSION = /^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;
const HEX64 = /^[0-9a-f]{64}$/;
const MAX_NAME = 64;

const downloads = new Map(); // id -> AbortController of a cancellable request

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

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

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
  if (name === "NoModificationAllowedError") {
    return new StorageError("locked", `${context}: another writer holds the file`);
  }
  if (name === "NotFoundError") {
    return new StorageError("not-found", `${context}: not found`);
  }
  return new StorageError("write-failed", `${context}: ${error && error.message ? error.message : error}`);
}

async function getDirectory(parts, create) {
  if (!opfsAvailable()) {
    throw new StorageError("opfs-unavailable", "the origin private file system is not available in this worker");
  }
  let directory = await navigator.storage.getDirectory();
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

async function readText(directory, name) {
  let file;
  try {
    file = await (await directory.getFileHandle(name)).getFile();
  } catch (error) {
    if (error && error.name === "NotFoundError") {
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

async function withSyncHandle(directory, name, create, action) {
  let handle;
  try {
    handle = await (await directory.getFileHandle(name, { create: !!create })).createSyncAccessHandle();
  } catch (error) {
    throw opfsError(error, name);
  }
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

async function directoryBytes(directory) {
  let total = 0;
  for await (const [, handle] of directory) {
    total += handle.kind === "file" ? (await handle.getFile()).size : await directoryBytes(handle);
  }
  return total;
}

/** A file open for writing with a sync access handle, flushed every 5 s while dirty and at close (177 A). */
class WritableFile {
  constructor(handle, name) {
    this.handle = handle;
    this.name = name;
    this.dirty = false;
    this.timer = setInterval(() => this.flush(), FLUSH_INTERVAL_MS);
  }

  static async open(directory, name) {
    try {
      return new WritableFile(await (await directory.getFileHandle(name, { create: true })).createSyncAccessHandle(), name);
    } catch (error) {
      throw opfsError(error, name);
    }
  }

  size() {
    return this.handle.getSize();
  }

  truncate(size) {
    try {
      this.handle.truncate(size);
      this.dirty = true;
    } catch (error) {
      throw opfsError(error, this.name);
    }
  }

  write(bytes, at) {
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

  read(bytes, at) {
    return this.handle.read(bytes, { at });
  }

  flush() {
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
  const name = `osx86:image:${image}@${version}`;
  return navigator.locks.request(name, { ifAvailable: true }, (lock) => {
    if (!lock) {
      throw new StorageError("locked", `${image} ${version}: another writer holds the lock`);
    }
    return action();
  });
}

// --- manifests and state --------------------------------------------------------------------------

function parseManifestText(text) {
  let manifest;
  try {
    manifest = JSON.parse(text);
  } catch (error) {
    throw new StorageError("manifest-invalid", `the manifest is not JSON: ${error.message}`);
  }
  if (!manifest || typeof manifest !== "object" || !Array.isArray(manifest.chunks)
    || !Number.isSafeInteger(manifest.size) || manifest.size < 1
    || !Number.isSafeInteger(manifest.chunkSize) || manifest.chunkSize < 1
    || manifest.chunks.length !== Math.ceil(manifest.size / manifest.chunkSize)
    || !manifest.chunks.every((chunk) => chunk && typeof chunk.sha256 === "string" && HEX64.test(chunk.sha256))) {
    throw new StorageError("manifest-invalid", "the manifest has no consistent size, chunkSize and chunks");
  }
  return manifest;
}

const chunkFileName = (index) => `chunk-${String(index).padStart(5, "0")}.bin`;
const chunkLength = (manifest, index) => Math.min(manifest.chunkSize, manifest.size - index * manifest.chunkSize);

async function fetchManifest(id, url, expected) {
  if (typeof expected !== "string" || !HEX64.test(expected)) {
    throw new StorageError("protocol", "fetch-manifest needs a lowercase hex sha256");
  }
  let response;
  try {
    response = await fetch(String(url), { cache: "no-store", credentials: "omit" });
  } catch (error) {
    throw new StorageError("manifest-fetch-failed", `${url}: ${error.message}`);
  }
  if (!response.ok) {
    throw new StorageError("manifest-fetch-failed", `${url}: HTTP ${response.status}`);
  }
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.length > MAX_MANIFEST_BYTES) {
    throw new StorageError("manifest-fetch-failed", `${url}: ${bytes.length} bytes, more than the ${MAX_MANIFEST_BYTES} accepted`);
  }
  const actual = await sha256(bytes);
  if (actual !== expected) {
    throw new StorageError("manifest-integrity", `the manifest hashes to ${actual}, not to the expected ${expected}: the image is refused`);
  }
  send({ type: "manifest", id, text: new TextDecoder().decode(bytes) });
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
  const metadataText = await readText(directory, METADATA_FILE);
  const stateText = await readText(directory, STATE_FILE);
  if (metadataText === null && stateText === null) {
    send({ type: "state", id, state: null });
    return;
  }
  const state = parseState(stateText);
  const metadata = parseJsonOrNull(metadataText);
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
  if (!state || state.format !== STATE_FORMAT || typeof state.manifestSha256 !== "string"
    || !Array.isArray(state.verified) || !state.verified.every((index) => Number.isSafeInteger(index) && index >= 0)) {
    return null;
  }
  return state;
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

/** Fetches one chunk into `target`; resolves with the byte count, rejects with a ChunkFetchError. */
async function fetchChunk(url, length, target, signal) {
  let response;
  try {
    response = await fetch(url, { cache: "no-store", credentials: "omit", signal });
  } catch (error) {
    if (error && error.name === "AbortError") {
      throw new StorageError("cancelled", "the download was cancelled");
    }
    throw new ChunkFetchError("network", `${url}: ${error.message}`, true);
  }
  if (!response.ok) {
    const retryable = response.status >= 500 || response.status === 429 || response.status === 408;
    throw new ChunkFetchError(retryable ? "network" : "http", `${url}: HTTP ${response.status}`, retryable);
  }
  if (!response.body) {
    throw new ChunkFetchError("network", `${url}: no body`, true);
  }
  const reader = response.body.getReader();
  let received = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      if (received + value.length > length) {
        throw new ChunkFetchError("network", `${url}: more than the ${length} bytes expected`, true);
      }
      target.set(value, received);
      received += value.length;
    }
  } catch (error) {
    if (error instanceof ChunkFetchError) {
      throw error;
    }
    if (error && error.name === "AbortError") {
      throw new StorageError("cancelled", "the download was cancelled");
    }
    throw new ChunkFetchError("network", `${url}: ${error.message} after ${received} bytes`, true);
  }
  if (received !== length) {
    throw new ChunkFetchError("network", `${url}: received ${received} of ${length} bytes`, true);
  }
  return received;
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

  bytesPerSecond() {
    const now = performance.now();
    while (this.samples.length > 0 && now - this.samples[0].at > SPEED_WINDOW_MS) {
      this.samples.shift();
    }
    if (this.samples.length === 0) {
      return 0;
    }
    const bytes = this.samples.reduce((sum, sample) => sum + sample.bytes, 0);
    const span = Math.max(now - this.samples[0].at, 100);
    return (bytes * 1000) / span;
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

  async prepare(manifestSha256) {
    const { command, directory } = this;
    if (command.reset) {
      await removeIfPresent(directory, PAYLOAD_FILE);
      await removeIfPresent(directory, STATE_FILE);
      await removeIfPresent(directory, METADATA_FILE);
    }
    const storedManifest = await readText(directory, MANIFEST_FILE);
    if (storedManifest !== command.manifest) {
      await writeText(directory, MANIFEST_FILE, command.manifest);
    }
    const metadata = parseJsonOrNull(await readText(directory, METADATA_FILE));
    if (!metadata || metadata.complete !== true) {
      await writeText(directory, METADATA_FILE, command.metadata);
    }
    const state = parseState(await readText(directory, STATE_FILE));
    if (state && state.manifestSha256 === manifestSha256) {
      for (const index of state.verified) {
        if (index < this.chunksTotal) {
          this.verified.add(index);
        }
      }
    }
    this.state = { format: STATE_FORMAT, manifestSha256, verified: [...this.verified].sort((a, b) => a - b) };
    this.payload = await WritableFile.open(directory, PAYLOAD_FILE);
    if (this.payload.size() !== this.manifest.size) {
      this.payload.truncate(this.manifest.size);
      this.payload.flush();
    }
  }

  async recordVerified(index) {
    this.verified.add(index);
    this.state.verified = [...this.verified].sort((a, b) => a - b);
    const text = JSON.stringify(this.state);
    // One state write at a time, in order: the chunk's flush() precedes it, so the state never names an unflushed chunk.
    this.stateWrite = this.stateWrite.then(() => writeText(this.directory, STATE_FILE, text));
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
        await fetchChunk(url, length, target, this.controller.signal);
        this.samples.push({ at: performance.now(), bytes: length });
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
        await sleep(retryDelay(attempt));
      }
    }
  }

  throwIfCancelled() {
    if (this.controller.signal.aborted) {
      throw new StorageError("cancelled", "the download was cancelled");
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
    for (let slot = 0; slot < slots; slot++) {
      workers.push(this.runSlot(queue, new Uint8Array(this.manifest.chunkSize)));
    }
    try {
      await Promise.all(workers);
    } catch (error) {
      this.controller.abort();
      await Promise.allSettled(workers);
      throw error;
    }
  }

  async complete() {
    this.payload.close();
    this.payload = null;
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
    if (this.payload) {
      try {
        this.payload.close();
      } catch (error) {
        // The chunks the state names are already flushed; a failed last flush loses nothing recorded.
      }
      this.payload = null;
    }
    await this.stateWrite.catch(() => {});
  }
}

function validateDownloadCommand(command) {
  checkNames(command.image, command.version);
  if (typeof command.baseUrl !== "string" || typeof command.manifest !== "string" || typeof command.metadata !== "string"
    || !Array.isArray(command.chunks)) {
    throw new StorageError("protocol", "download needs baseUrl, manifest, metadata and chunks");
  }
  const manifest = parseManifestText(command.manifest);
  if (manifest.image !== command.image || manifest.version !== command.version) {
    throw new StorageError("protocol", "the manifest does not describe the image of the command");
  }
  if (!command.chunks.every((index) => Number.isSafeInteger(index) && index >= 0 && index < manifest.chunks.length)) {
    throw new StorageError("protocol", "a chunk index is outside the manifest");
  }
  command.parallelism = Math.max(1, Math.min(MAX_PARALLELISM, Number(command.parallelism) || 1));
  return manifest;
}

async function download(id, command) {
  const manifest = validateDownloadCommand(command);
  const manifestSha256 = await sha256(new TextEncoder().encode(command.manifest));
  const controller = new AbortController();
  downloads.set(id, controller);
  try {
    await withImageLock(command.image, command.version, async () => {
      const directory = await imageDirectory(command.image, command.version, true);
      const job = new Download(id, command, manifest, directory, controller);
      try {
        await job.prepare(manifestSha256);
        await job.run();
        await job.complete();
      } finally {
        await job.close();
      }
    });
  } finally {
    downloads.delete(id);
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
      repositoryBytes += entry.bytesOnDisk;
      images.push(entry);
    }
  }
  return { images, repositoryBytes };
}

async function describeImage(image, version, directory) {
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
}

async function deleteImage(image, version) {
  checkNames(image, version);
  await withImageLock(image, version, async () => {
    let root;
    try {
      root = await imagesDirectory(false);
      await (await root.getDirectoryHandle(image)).removeEntry(version, { recursive: true });
    } catch (error) {
      if (error && (error.name === "NotFoundError" || error.code === "not-found")) {
        return;
      }
      throw opfsError(error, `${image}/${version}`);
    }
    let empty = true;
    for await (const _ of await root.getDirectoryHandle(image)) {
      empty = false;
      break;
    }
    if (empty) {
      await removeIfPresent(root, image);
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

async function readManifest(image, version) {
  const directory = await imageDirectory(image, version, false);
  const text = await readText(directory, MANIFEST_FILE);
  if (text === null) {
    throw new StorageError("not-found", `${image} ${version}: no manifest in the repository`);
  }
  return text;
}

async function verify(id, command) {
  checkNames(command.image, command.version);
  const manifest = parseManifestText(String(command.manifest));
  const directory = await imageDirectory(command.image, command.version, false);
  const controller = new AbortController();
  downloads.set(id, controller);
  try {
    return await withSyncHandle(directory, PAYLOAD_FILE, false, async (handle) => {
      const buffer = new Uint8Array(manifest.chunkSize);
      const bad = [];
      let lastProgress = 0;
      let bytesVerified = 0;
      for (let index = 0; index < manifest.chunks.length; index++) {
        if (controller.signal.aborted) {
          throw new StorageError("cancelled", "the verification was cancelled");
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
          send({ type: "progress", id, bytesVerified, bytesTotal: manifest.size, chunksVerified: index + 1 - bad.length, chunksTotal: manifest.chunks.length, bytesPerSecond: 0 });
        }
      }
      return { chunksTotal: manifest.chunks.length, bad };
    });
  } finally {
    downloads.delete(id);
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
      await download(id, message);
      break;
    case "cancel": {
      const controller = downloads.get(message.target);
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
  if (!message || typeof message.type !== "string" || (message.type !== "cancel" && !Number.isSafeInteger(message.id))) {
    fail(undefined, new StorageError("protocol", "message without a type or an id"));
    return;
  }
  try {
    await handle(message);
  } catch (error) {
    fail(message.id, error);
  }
};

send({ type: "ready", opfs: opfsAvailable(), locks: locksAvailable() });
