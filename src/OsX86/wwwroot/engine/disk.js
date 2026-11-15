// Os-x86 engine: disks for v86 read from the image payload in the OPFS (milestone 0, work item
// 6a; plan section 13, decisions 169, 177 A, 211 A, E11). Loaded by v86-worker.js with
// importScripts, after zstd-seekable.js.
//
// - ZstdDecoder: the zstd decoder v86 embeds (the zstd_* exports of v86.wasm), instantiated a
//   second time in this worker with stub imports, exactly as v86's own zstd_decompress_worker
//   does. No decoder is added to the repository; this instance lives apart from the emulator, so
//   it exists before the emulator does (the kernel is decompressed first) and a read never
//   re-enters the running CPU.
// - OpfsFile: a FileSystemSyncAccessHandle on a file of the OPFS (worker only).
// - SeekableEntry: a logical file of image.bin (offset, size, rawSize, blockSize of the manifest):
//   the seek table is read once, frames are decompressed on demand into a bounded, measured
//   cache (FrameCache). RawEntry: an uncompressed logical file.
// - WriteLayer: the copy-on-write layer of a disk, one file in the OPFS: a 4 KiB header (format
//   1), a block map (u32 per 4 KiB block of the disk: 0 = not written, n = slot n - 1), then the
//   written blocks appended in order of first write. The published image is never modified. One
//   writer per layer (navigator.locks, plus the exclusivity of the sync access handle); flush()
//   every 5 s while open and at close (177 A). MemoryWriteLayer: the "discard" mode, nothing is
//   written anywhere. A torn block after a power loss is what a real disk does too.
// - V86Disk: what v86 gets as `hda` (get, set, load, byteLength): reads come from the layer
//   where a block was written, from the image otherwise; writes go to the layer.
"use strict";

(function (root) {
  const seekable = root.OsX86ZstdSeekable;
  if (!seekable) {
    throw new Error("zstd-seekable.js must be loaded before disk.js");
  }

  const LAYER_MAGIC = "OSX86-LAYER\0";
  const LAYER_FORMAT = 1;
  const LAYER_HEADER_SIZE = 4096;
  const LAYER_BLOCK_SIZE = 4096;
  const MAX_LAYER_BLOCKS = 1 << 28; // 1 TiB of disk at 4 KiB per block
  const FLUSH_INTERVAL_MS = 5000;
  const LOCK_WAIT_MS = 2000;
  const OPEN_RETRIES = 5;
  const OPEN_RETRY_MS = 200;
  const MAX_SEEK_TABLE_BYTES = 8 * seekable.MAX_FRAMES + 32;
  const MAX_WHOLE_ENTRY_BYTES = 512 * 1024 * 1024; // a kernel or an initramfs read whole into memory
  const DEFAULT_CACHE_BYTES = 32 * 1024 * 1024;
  const MIN_CACHE_BYTES = 2 * 1024 * 1024;
  const MAX_CACHE_BYTES = 512 * 1024 * 1024;

  class DiskError extends Error {
    constructor(code, message) {
      super(message);
      this.name = "DiskError";
      this.code = code;
    }
  }

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  function opfsError(error, context) {
    if (error instanceof DiskError) {
      return error;
    }
    const name = error && error.name;
    if (name === "NotFoundError") {
      return new DiskError("image-not-found", `${context}: not found`);
    }
    if (name === "NoModificationAllowedError") {
      return new DiskError("layer-locked", `${context}: already open by another engine`);
    }
    if (name === "QuotaExceededError") {
      return new DiskError("quota-exceeded", `${context}: the browser refused the write for lack of storage quota`);
    }
    if (name === "SecurityError" || name === "NotAllowedError") {
      return new DiskError("opfs-unavailable", `${context}: the origin private file system is not allowed here (${name})`);
    }
    return new DiskError("disk-io-failed", `${context}: ${error && error.message ? error.message : error}`);
  }

  function splitPath(path) {
    const parts = String(path).split("/");
    if (parts.length === 0 || parts.some((part) => part === "" || part === "." || part === "..")) {
      throw new DiskError("protocol", `'${path}' is not a relative OPFS path`);
    }
    return parts;
  }

  // --- OPFS --------------------------------------------------------------------------------------

  async function getDirectory(parts, create) {
    if (!root.navigator || !navigator.storage || typeof navigator.storage.getDirectory !== "function") {
      throw new DiskError("opfs-unavailable", "the origin private file system is not available in this worker");
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

  /** A sync access handle on one OPFS file, opened by path; the handle is retried while the browser releases a dead worker's. */
  class OpfsFile {
    constructor(handle, path) {
      this.handle = handle;
      this.path = path;
    }

    static async open(path, create) {
      const parts = splitPath(path);
      const directory = await getDirectory(parts.slice(0, -1), create);
      const name = parts[parts.length - 1];
      for (let attempt = 1; ; attempt++) {
        try {
          const file = await directory.getFileHandle(name, { create: !!create });
          return new OpfsFile(await file.createSyncAccessHandle(), path);
        } catch (error) {
          if (!(error && error.name === "NoModificationAllowedError") || attempt >= OPEN_RETRIES) {
            throw opfsError(error, path);
          }
          await sleep(OPEN_RETRY_MS);
        }
      }
    }

    size() {
      return this.handle.getSize();
    }

    /** Reads exactly `length` bytes at `at` into a new array (or `into`); a short read is an error. */
    read(at, length, into) {
      const target = into || new Uint8Array(length);
      let read;
      try {
        read = this.handle.read(target.subarray(0, length), { at });
      } catch (error) {
        throw opfsError(error, this.path);
      }
      if (read !== length) {
        throw new DiskError("disk-io-failed", `${this.path}: read ${read} of ${length} bytes at ${at}`);
      }
      return target;
    }

    write(bytes, at) {
      let written;
      try {
        written = this.handle.write(bytes, { at });
      } catch (error) {
        throw opfsError(error, this.path);
      }
      if (written !== bytes.length) {
        throw new DiskError("disk-io-failed", `${this.path}: wrote ${written} of ${bytes.length} bytes at ${at}`);
      }
    }

    truncate(size) {
      try {
        this.handle.truncate(size);
      } catch (error) {
        throw opfsError(error, this.path);
      }
    }

    flush() {
      try {
        this.handle.flush();
      } catch (error) {
        throw opfsError(error, this.path);
      }
    }

    close() {
      try {
        this.handle.close();
      } catch (error) {
        // closing twice, or a handle the browser already invalidated: nothing to keep
      }
    }
  }

  // --- the decoder of v86 ------------------------------------------------------------------------

  class ZstdDecoder {
    constructor(module) {
      this.module = module;
      this.instance = null;
      this.framesDecoded = 0;
      this.decodeMs = 0;
    }

    /** Compiles v86.wasm from its URL (the browser caches the compiled module across workers). */
    static async create(wasmUrl) {
      let module;
      try {
        const response = await fetch(wasmUrl, { credentials: "omit" });
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }
        module = await WebAssembly.compileStreaming(response);
      } catch (error) {
        throw new DiskError("v86-load-failed", `${wasmUrl}: ${error && error.message ? error.message : error}`);
      }
      const decoder = new ZstdDecoder(module);
      decoder.instantiate();
      return decoder;
    }

    /** A fresh instance: every import of v86.wasm is a stub (the CPU never runs here), as in v86's zstd worker. */
    instantiate() {
      const env = {};
      for (const entry of WebAssembly.Module.imports(this.module)) {
        if (entry.kind === "function") {
          env[entry.name] = () => {
            throw new Error(`the zstd decoder unexpectedly called ${entry.name}`);
          };
        } else if (entry.kind === "table") {
          env[entry.name] = new WebAssembly.Table({ element: "anyfunc", initial: 1024 });
        } else if (entry.kind === "memory") {
          env[entry.name] = new WebAssembly.Memory({ initial: 256 });
        }
      }
      const instance = new WebAssembly.Instance(this.module, { env });
      const exports = instance.exports;
      for (const name of ["memory", "zstd_create_ctx", "zstd_get_src_ptr", "zstd_read", "zstd_read_free", "zstd_free_ctx"]) {
        if (!exports[name]) {
          throw new DiskError("v86-load-failed", `v86.wasm does not export ${name}: this v86 cannot decompress the image`);
        }
      }
      this.instance = instance;
    }

    /**
     * Decompresses one frame whose content size is known (from the seek table). The frame header is
     * checked first: the decoder traps on garbage instead of reporting it, and a trap throws the
     * instance away (its allocator state is unknown afterwards).
     */
    decompress(frame, size) {
      const declared = seekable.frameContentSize(frame);
      if (declared !== null && declared !== size) {
        throw new DiskError("image-corrupt", `a frame declares ${declared} bytes, the seek table says ${size}`);
      }
      if (!this.instance) {
        this.instantiate();
      }
      const exports = this.instance.exports;
      const started = performance.now();
      try {
        const context = exports.zstd_create_ctx(frame.length);
        new Uint8Array(exports.memory.buffer).set(frame, exports.zstd_get_src_ptr(context) >>> 0);
        const pointer = exports.zstd_read(context, size) >>> 0;
        const out = new Uint8Array(size);
        out.set(new Uint8Array(exports.memory.buffer, pointer, size));
        exports.zstd_read_free(pointer, size);
        exports.zstd_free_ctx(context);
        this.framesDecoded++;
        this.decodeMs += performance.now() - started;
        return out;
      } catch (error) {
        this.instance = null;
        throw new DiskError("image-corrupt", `a zstd frame could not be decompressed: ${error && error.message ? error.message : error}`);
      }
    }

    stats() {
      return { framesDecoded: this.framesDecoded, decodeMs: Math.round(this.decodeMs * 10) / 10 };
    }
  }

  // --- frames and entries ------------------------------------------------------------------------

  /** A bounded LRU of decompressed frames, shared by every entry of the engine; measured. */
  class FrameCache {
    constructor(maxBytes) {
      this.maxBytes = Math.min(MAX_CACHE_BYTES, Math.max(MIN_CACHE_BYTES, maxBytes || DEFAULT_CACHE_BYTES));
      this.entries = new Map();
      this.bytes = 0;
      this.hits = 0;
      this.misses = 0;
      this.evictions = 0;
    }

    get(key) {
      const bytes = this.entries.get(key);
      if (bytes === undefined) {
        this.misses++;
        return undefined;
      }
      this.entries.delete(key);
      this.entries.set(key, bytes); // most recently used at the end
      this.hits++;
      return bytes;
    }

    put(key, bytes) {
      if (bytes.length > this.maxBytes) {
        return;
      }
      if (this.entries.has(key)) {
        this.bytes -= this.entries.get(key).length;
        this.entries.delete(key);
      }
      while (this.bytes + bytes.length > this.maxBytes && this.entries.size > 0) {
        const oldest = this.entries.keys().next().value;
        this.bytes -= this.entries.get(oldest).length;
        this.entries.delete(oldest);
        this.evictions++;
      }
      this.entries.set(key, bytes);
      this.bytes += bytes.length;
    }

    clear() {
      this.entries.clear();
      this.bytes = 0;
    }

    stats() {
      return { cacheBytes: this.bytes, cacheMaxBytes: this.maxBytes, cacheFrames: this.entries.size, hits: this.hits, misses: this.misses, evictions: this.evictions };
    }
  }

  /** A logical file stored as a seekable zstd stream at [offset, offset + size) of a payload file. */
  class SeekableEntry {
    constructor(file, offset, size, rawSize, blockSize, frames, decoder, cache, key) {
      this.file = file;
      this.offset = offset;
      this.size = size;
      this.rawSize = rawSize;
      this.blockSize = blockSize;
      this.frames = frames;
      this.decoder = decoder;
      this.cache = cache;
      this.key = key;
      this.compressedBytesRead = 0;
    }

    /** Reads and checks the seek table once (two reads: the footer, then the table it sizes). */
    static open(file, source, decoder, cache, key) {
      const { offset, size, rawSize, blockSize } = source;
      const fileSize = file.size();
      if (offset + size > fileSize) {
        throw new DiskError("image-corrupt", `${file.path}: the entry ends at ${offset + size}, the file has ${fileSize} bytes`);
      }
      if (size < seekable.FOOTER_SIZE) {
        throw new DiskError("image-corrupt", `${file.path}: an entry of ${size} bytes cannot hold a seek table`);
      }
      let frames;
      try {
        const footer = seekable.readFooter(file.read(offset + size - seekable.FOOTER_SIZE, seekable.FOOTER_SIZE));
        if (footer.tableSize > size || footer.tableSize > MAX_SEEK_TABLE_BYTES) {
          throw new seekable.SeekableFormatError(`a seek table of ${footer.tableSize} bytes does not fit the entry`);
        }
        const table = seekable.parseSeekTable(file.read(offset + size - footer.tableSize, footer.tableSize), size);
        seekable.checkAgainstManifest(table, rawSize, blockSize);
        frames = table.frames;
      } catch (error) {
        if (error instanceof seekable.SeekableFormatError) {
          throw new DiskError("image-corrupt", `${file.path}: ${error.message}`);
        }
        throw error;
      }
      return new SeekableEntry(file, offset, size, rawSize, blockSize, frames, decoder, cache, key);
    }

    /** The decompressed frame `index`, from the cache or decoded now. */
    frame(index) {
      const cacheKey = `${this.key}:${index}`;
      const cached = this.cache.get(cacheKey);
      if (cached !== undefined) {
        return cached;
      }
      const bytes = this.decodeFrame(index);
      this.cache.put(cacheKey, bytes);
      return bytes;
    }

    decodeFrame(index) {
      const frame = this.frames[index];
      const compressed = this.file.read(this.offset + frame.compressedOffset, frame.compressedSize);
      this.compressedBytesRead += frame.compressedSize;
      return this.decoder.decompress(compressed, frame.decompressedSize);
    }

    /** `length` decompressed bytes at `offset` (within the entry), into `into` when given. */
    read(offset, length, into) {
      const out = into || new Uint8Array(length);
      let plan;
      try {
        plan = seekable.planRead(this.frames, this.rawSize, offset, length);
      } catch (error) {
        throw new DiskError("protocol", error.message);
      }
      for (const piece of plan) {
        out.set(this.frame(piece.index).subarray(piece.start, piece.end), piece.target);
      }
      return out;
    }

    /** The whole entry decompressed, frame by frame without the cache (a kernel, an initramfs). */
    readAll() {
      if (this.rawSize > MAX_WHOLE_ENTRY_BYTES) {
        throw new DiskError("image-read-failed", `${this.file.path}: ${this.rawSize} bytes is more than the ${MAX_WHOLE_ENTRY_BYTES} the engine loads in memory`);
      }
      const out = new Uint8Array(this.rawSize);
      for (let index = 0; index < this.frames.length; index++) {
        out.set(this.decodeFrame(index), this.frames[index].decompressedOffset);
      }
      return out.buffer;
    }
  }

  /** A logical file stored uncompressed at [offset, offset + size) of a payload file. */
  class RawEntry {
    constructor(file, offset, size) {
      this.file = file;
      this.offset = offset;
      this.size = size;
      this.rawSize = size;
      this.compressedBytesRead = 0;
    }

    static open(file, source) {
      const fileSize = file.size();
      if (source.offset + source.size > fileSize) {
        throw new DiskError("image-corrupt", `${file.path}: the entry ends at ${source.offset + source.size}, the file has ${fileSize} bytes`);
      }
      return new RawEntry(file, source.offset, source.size);
    }

    read(offset, length, into) {
      if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(length) || offset < 0 || length < 0 || offset + length > this.rawSize) {
        throw new DiskError("protocol", `read of ${length} bytes at ${offset} is outside the ${this.rawSize} bytes of the entry`);
      }
      this.compressedBytesRead += length;
      return this.file.read(this.offset + offset, length, into);
    }

    readAll() {
      if (this.rawSize > MAX_WHOLE_ENTRY_BYTES) {
        throw new DiskError("image-read-failed", `${this.file.path}: ${this.rawSize} bytes is more than the ${MAX_WHOLE_ENTRY_BYTES} the engine loads in memory`);
      }
      return this.read(0, this.rawSize).buffer;
    }
  }

  /** Validates a source {path, offset, size, rawSize?, blockSize?} of the start options. */
  function checkSource(source, what) {
    if (!source || typeof source !== "object") {
      throw new DiskError("protocol", `${what}: a source is required`);
    }
    splitPath(source.path);
    const compressed = source.rawSize !== undefined && source.rawSize !== null;
    if (source.offset === undefined && source.size === undefined) {
      if (compressed) {
        throw new DiskError("protocol", `${what}: a compressed source needs its offset and size`);
      }
      return false; // the whole file, uncompressed (the form of work item 2)
    }
    const { offset, size } = source;
    if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(size) || size < 0) {
      throw new DiskError("protocol", `${what}: offset and size must be non-negative integers`);
    }
    if (compressed) {
      if (!Number.isSafeInteger(source.rawSize) || source.rawSize < 0 || !Number.isSafeInteger(source.blockSize) || source.blockSize <= 0 || source.blockSize > 1 << 30) {
        throw new DiskError("protocol", `${what}: rawSize and blockSize must be integers (blockSize at most 1 GiB)`);
      }
    }
    return compressed;
  }

  /** Opens a source as an entry; `files` caches the payload handles by path. */
  async function openEntry(source, what, files, decoder, cache) {
    const compressed = checkSource(source, what);
    let file = files.get(source.path);
    if (!file) {
      file = await OpfsFile.open(source.path, false);
      files.set(source.path, file);
    }
    if (compressed) {
      return SeekableEntry.open(file, source, decoder, cache, `${source.path}@${source.offset}`);
    }
    const range = source.offset === undefined ? { offset: 0, size: file.size() } : source;
    return RawEntry.open(file, range);
  }

  // --- write layers ------------------------------------------------------------------------------

  function acquireLock(name) {
    if (!root.navigator || !navigator.locks || typeof navigator.locks.request !== "function") {
      return Promise.resolve(() => {}); // the exclusivity of the sync access handle is the guard
    }
    return new Promise((resolve, reject) => {
      const waiting = new AbortController();
      const timer = setTimeout(() => waiting.abort(), LOCK_WAIT_MS);
      let granted = false;
      navigator.locks
        .request(name, { signal: waiting.signal }, () => {
          granted = true;
          clearTimeout(timer);
          return new Promise((release) => resolve(release)); // held until release() is called
        })
        .catch((error) => {
          clearTimeout(timer);
          if (!granted) {
            reject(error && error.name === "AbortError" ? new DiskError("layer-locked", `${name}: another engine holds this write layer`) : opfsError(error, name));
          }
        });
    });
  }

  function u32le(view, at, value) {
    view.setUint32(at, value, true);
  }

  /** The persistent copy-on-write layer of one disk, one file in the OPFS (see the header of this file). */
  class WriteLayer {
    constructor(file, release, diskSize, blockSize, blockCount, map, slots) {
      this.file = file;
      this.release = release;
      this.diskSize = diskSize;
      this.blockSize = blockSize;
      this.blockCount = blockCount;
      this.map = map;
      this.slots = slots;
      this.mapOffset = LAYER_HEADER_SIZE;
      this.dataOffset = LAYER_HEADER_SIZE + Math.ceil((blockCount * 4) / LAYER_HEADER_SIZE) * LAYER_HEADER_SIZE;
      this.dirty = false;
      this.flushError = null;
      this.writes = 0;
      this.timer = setInterval(() => {
        try {
          this.flush();
        } catch (error) {
          this.flushError = error; // rethrown by the next write or flush
        }
      }, FLUSH_INTERVAL_MS);
    }

    /**
     * Opens or creates the layer of a disk of `diskSize` bytes. `metadataText` is the versioned
     * sidecar (StoredObjectMetadata, kind "layer") the C# side prepared: written next to the layer
     * when it is created, compared when it is reopened (same image version, same engine major).
     */
    static async open(path, diskSize, metadataText) {
      const parts = splitPath(path);
      const directoryPath = parts.slice(0, -1).join("/");
      const release = await acquireLock(`osx86:layer:${directoryPath}`);
      let file = null;
      try {
        const blockCount = Math.ceil(diskSize / LAYER_BLOCK_SIZE);
        if (blockCount > MAX_LAYER_BLOCKS) {
          throw new DiskError("protocol", `a disk of ${diskSize} bytes is larger than a write layer can map`);
        }
        const directory = await getDirectory(parts.slice(0, -1), true);
        const sidecarName = `${parts[parts.length - 1]}.meta.json`;
        const existing = await readSmallFile(directory, sidecarName);
        file = await OpfsFile.open(path, true);
        const mapOffset = LAYER_HEADER_SIZE;
        const dataOffset = mapOffset + Math.ceil((blockCount * 4) / LAYER_HEADER_SIZE) * LAYER_HEADER_SIZE;
        const size = file.size();
        let map;
        let slots;
        if (size === 0) {
          // A new layer: the sidecar first (what the layer belongs to), then the header and an empty map.
          await writeSmallFile(directory, sidecarName, metadataText);
          const header = new Uint8Array(LAYER_HEADER_SIZE);
          const view = new DataView(header.buffer);
          header.set(new TextEncoder().encode(LAYER_MAGIC), 0);
          u32le(view, 12, LAYER_FORMAT);
          u32le(view, 16, LAYER_BLOCK_SIZE);
          u32le(view, 20, diskSize % 0x100000000);
          u32le(view, 24, Math.floor(diskSize / 0x100000000));
          u32le(view, 28, blockCount);
          u32le(view, 32, mapOffset);
          u32le(view, 36, dataOffset);
          file.write(header, 0);
          file.write(new Uint8Array(dataOffset - mapOffset), mapOffset);
          file.flush();
          map = new Uint32Array(blockCount);
          slots = 0;
        } else {
          if (existing === null) {
            throw new DiskError("layer-mismatch", `${path}: the layer has no metadata sidecar`);
          }
          checkSidecar(existing, metadataText, path);
          if (size < dataOffset) {
            throw new DiskError("layer-mismatch", `${path}: the layer file is truncated (${size} bytes)`);
          }
          const header = file.read(0, LAYER_HEADER_SIZE);
          const view = new DataView(header.buffer);
          const magic = new TextDecoder().decode(header.subarray(0, LAYER_MAGIC.length));
          if (magic !== LAYER_MAGIC) {
            throw new DiskError("layer-mismatch", `${path}: not a write layer`);
          }
          const format = view.getUint32(12, true);
          if (format !== LAYER_FORMAT) {
            throw new DiskError("format-unsupported", `${path}: the write layer has format ${format}; this version reads format ${LAYER_FORMAT} only. Nothing was written.`);
          }
          const storedBlockSize = view.getUint32(16, true);
          const storedDiskSize = view.getUint32(20, true) + view.getUint32(24, true) * 0x100000000;
          if (storedBlockSize !== LAYER_BLOCK_SIZE || storedDiskSize !== diskSize || view.getUint32(28, true) !== blockCount || view.getUint32(32, true) !== mapOffset || view.getUint32(36, true) !== dataOffset) {
            throw new DiskError("layer-mismatch", `${path}: the write layer was made for a disk of ${storedDiskSize} bytes in blocks of ${storedBlockSize}, not ${diskSize} in blocks of ${LAYER_BLOCK_SIZE}`);
          }
          const mapBytes = file.read(mapOffset, blockCount * 4);
          map = new Uint32Array(mapBytes.buffer, 0, blockCount);
          slots = Math.floor((size - dataOffset) / LAYER_BLOCK_SIZE);
          for (let block = 0; block < blockCount; block++) {
            if (map[block] > slots) {
              throw new DiskError("layer-mismatch", `${path}: block ${block} points at slot ${map[block] - 1}, the file has ${slots} slots`);
            }
          }
        }
        return new WriteLayer(file, release, diskSize, LAYER_BLOCK_SIZE, blockCount, map, slots);
      } catch (error) {
        if (file) {
          file.close();
        }
        release();
        throw error instanceof DiskError ? error : opfsError(error, path);
      }
    }

    has(block) {
      return this.map[block] !== 0;
    }

    /** Copies the stored block into `into` at `at`. */
    readBlock(block, into, at) {
      const slot = this.map[block] - 1;
      this.file.read(this.dataOffset + slot * this.blockSize, this.blockSize, into.subarray(at, at + this.blockSize));
    }

    /** Stores a whole block (`bytes` is blockSize long); a new block gets the next slot, then its map entry. */
    writeBlock(block, bytes) {
      this.rethrowFlushError();
      let slot = this.map[block] - 1;
      const fresh = slot < 0;
      if (fresh) {
        slot = this.slots;
      }
      this.file.write(bytes, this.dataOffset + slot * this.blockSize);
      if (fresh) {
        this.slots++;
        this.map[block] = slot + 1;
        const entry = new Uint8Array(4);
        new DataView(entry.buffer).setUint32(0, slot + 1, true);
        this.file.write(entry, this.mapOffset + block * 4);
      }
      this.dirty = true;
      this.writes++;
    }

    rethrowFlushError() {
      if (this.flushError) {
        const error = this.flushError;
        this.flushError = null;
        throw error;
      }
    }

    flush() {
      this.rethrowFlushError();
      if (!this.dirty) {
        return;
      }
      this.file.flush();
      this.dirty = false;
    }

    close() {
      clearInterval(this.timer);
      try {
        this.flush();
      } finally {
        this.file.close();
        this.release();
      }
    }

    stats() {
      return { mode: "persistent", blocksWritten: this.slots, layerBytes: this.dataOffset + this.slots * this.blockSize, writes: this.writes };
    }
  }

  /** Reads a small text file of a directory, or null when absent. */
  async function readSmallFile(directory, name) {
    let file;
    try {
      file = await (await directory.getFileHandle(name)).getFile();
    } catch (error) {
      if (error && error.name === "NotFoundError") {
        return null;
      }
      throw opfsError(error, name);
    }
    if (file.size > 64 * 1024) {
      throw new DiskError("layer-mismatch", `${name}: the metadata is more than 64 KiB`);
    }
    return file.text();
  }

  async function writeSmallFile(directory, name, text) {
    let handle;
    try {
      handle = await (await directory.getFileHandle(name, { create: true })).createSyncAccessHandle();
    } catch (error) {
      throw opfsError(error, name);
    }
    try {
      const bytes = new TextEncoder().encode(text);
      handle.truncate(0);
      handle.write(bytes, { at: 0 });
      handle.flush();
    } catch (error) {
      throw opfsError(error, name);
    } finally {
      handle.close();
    }
  }

  function parseMetadata(text, what) {
    let value;
    try {
      value = JSON.parse(text);
    } catch (error) {
      throw new DiskError("layer-mismatch", `${what}: the metadata is not JSON`);
    }
    if (!value || typeof value !== "object" || !Number.isSafeInteger(value.format) || typeof value.image !== "string" || typeof value.imageVersion !== "string" || !value.engine || typeof value.engine.v86 !== "string") {
      throw new DiskError("layer-mismatch", `${what}: the metadata lacks format, image, imageVersion or engine.v86`);
    }
    return value;
  }

  /** The major version of a semantic version; for 0.x, "0.x" (semver: the minor is the breaking one before 1.0). */
  function majorOf(version) {
    const match = /^(\d+)\.(\d+)\./.exec(String(version));
    if (!match) {
      return String(version);
    }
    return match[1] === "0" ? `0.${match[2]}` : match[1];
  }

  /** Decision 169: a layer is reopened only by the same image version and the same engine major version; nothing is written otherwise. */
  function checkSidecar(existingText, expectedText, path) {
    const existing = parseMetadata(existingText, path);
    const expected = parseMetadata(expectedText, "the start options");
    if (existing.format !== expected.format) {
      throw new DiskError("format-unsupported", `${path}: the write layer has metadata format ${existing.format}; this version reads format ${expected.format} only. Nothing was written.`);
    }
    if (existing.kind !== "layer") {
      throw new DiskError("layer-mismatch", `${path}: the metadata describes a ${existing.kind}, not a write layer`);
    }
    if (existing.image !== expected.image || existing.imageVersion !== expected.imageVersion) {
      throw new DiskError("layer-mismatch", `${path}: the write layer belongs to ${existing.image} ${existing.imageVersion}, not to ${expected.image} ${expected.imageVersion}. Nothing was written.`);
    }
    if (majorOf(existing.engine.v86) !== majorOf(expected.engine.v86)) {
      throw new DiskError("engine-version-mismatch", `${path}: the write layer was written by v86 ${existing.engine.v86}; this engine is v86 ${expected.engine.v86} (another major version). Nothing was written.`);
    }
  }

  /** The "discard" layer: written blocks stay in memory and vanish with the engine. */
  class MemoryWriteLayer {
    constructor(diskSize) {
      this.diskSize = diskSize;
      this.blockSize = LAYER_BLOCK_SIZE;
      this.blocks = new Map();
      this.writes = 0;
    }

    has(block) {
      return this.blocks.has(block);
    }

    readBlock(block, into, at) {
      into.set(this.blocks.get(block), at);
    }

    writeBlock(block, bytes) {
      this.blocks.set(block, bytes.slice());
      this.writes++;
    }

    flush() {}

    close() {
      this.blocks.clear();
    }

    stats() {
      return { mode: "discard", blocksWritten: this.blocks.size, layerBytes: this.blocks.size * this.blockSize, writes: this.writes };
    }
  }

  // --- the disk v86 sees -------------------------------------------------------------------------

  /** The loadable v86 takes as hda/hdb: reads merge the image and the layer, writes go to the layer. */
  class V86Disk {
    constructor(name, base, layer) {
      this.name = name;
      this.base = base;
      this.layer = layer;
      this.byteLength = base.rawSize;
      this.blockSize = layer.blockSize;
      this.onload = undefined;
      this.onprogress = undefined;
      this.reads = 0;
      this.bytesRead = 0;
      this.writesRequested = 0;
      this.bytesWritten = 0;
      this.closed = false;
    }

    load() {
      if (this.onload) {
        this.onload({ buffer: this });
      }
    }

    /** v86: `get(offset, length, callback)`; the callback receives a Uint8Array. */
    get(offset, length, callback) {
      callback(this.read(offset, length));
    }

    read(offset, length) {
      this.checkRange(offset, length);
      const out = this.base.read(offset, length);
      const first = Math.floor(offset / this.blockSize);
      const last = Math.floor((offset + length - 1) / this.blockSize);
      for (let block = first; block <= last; block++) {
        if (this.layer.has(block)) {
          this.overlay(block, offset, length, out);
        }
      }
      this.reads++;
      this.bytesRead += length;
      return out;
    }

    overlay(block, offset, length, out) {
      const blockStart = block * this.blockSize;
      if (blockStart >= offset && blockStart + this.blockSize <= offset + length) {
        this.layer.readBlock(block, out, blockStart - offset);
        return;
      }
      const whole = new Uint8Array(this.blockSize);
      this.layer.readBlock(block, whole, 0);
      const from = Math.max(offset, blockStart);
      const to = Math.min(offset + length, blockStart + this.blockSize);
      out.set(whole.subarray(from - blockStart, to - blockStart), from - offset);
    }

    /** v86: `set(offset, data, callback)`. */
    set(offset, data, callback) {
      this.write(offset, data);
      callback();
    }

    write(offset, data) {
      const length = data.length;
      this.checkRange(offset, length);
      const first = Math.floor(offset / this.blockSize);
      const last = Math.floor((offset + length - 1) / this.blockSize);
      for (let block = first; block <= last; block++) {
        const blockStart = block * this.blockSize;
        const blockLength = Math.min(this.blockSize, this.byteLength - blockStart);
        if (blockStart >= offset && blockStart + blockLength <= offset + length && blockLength === this.blockSize) {
          this.layer.writeBlock(block, data.subarray(blockStart - offset, blockStart - offset + this.blockSize));
          continue;
        }
        // A partial block: read it whole (layer or image), patch, store whole (the last block is padded).
        const whole = new Uint8Array(this.blockSize);
        if (this.layer.has(block)) {
          this.layer.readBlock(block, whole, 0);
        } else {
          this.base.read(blockStart, blockLength, whole);
        }
        const from = Math.max(offset, blockStart);
        const to = Math.min(offset + length, blockStart + blockLength);
        whole.set(data.subarray(from - offset, to - offset), from - blockStart);
        this.layer.writeBlock(block, whole);
      }
      this.writesRequested++;
      this.bytesWritten += length;
    }

    checkRange(offset, length) {
      if (this.closed) {
        throw new DiskError("invalid-state", `${this.name}: the disk is closed`);
      }
      if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(length) || offset < 0 || length <= 0 || offset + length > this.byteLength) {
        throw new DiskError("protocol", `${this.name}: ${length} bytes at ${offset} is outside the disk of ${this.byteLength} bytes`);
      }
    }

    flush() {
      this.layer.flush();
    }

    close() {
      if (this.closed) {
        return;
      }
      this.closed = true;
      this.layer.close();
    }

    stats() {
      return {
        bytes: this.byteLength,
        reads: this.reads,
        bytesRead: this.bytesRead,
        writes: this.writesRequested,
        bytesWritten: this.bytesWritten,
        compressedBytesRead: this.base.compressedBytesRead,
        layer: this.layer.stats(),
      };
    }

    get_state() {
      throw new DiskError("invalid-state", "the state of an Os-x86 disk is not saved");
    }

    set_state() {
      throw new DiskError("invalid-state", "the state of an Os-x86 disk is not restored");
    }
  }

  root.OsX86Disk = {
    LAYER_FORMAT,
    LAYER_BLOCK_SIZE,
    LAYER_HEADER_SIZE,
    DEFAULT_CACHE_BYTES,
    DiskError,
    OpfsFile,
    ZstdDecoder,
    FrameCache,
    SeekableEntry,
    RawEntry,
    WriteLayer,
    MemoryWriteLayer,
    V86Disk,
    checkSource,
    openEntry,
    majorOf,
  };
})(typeof self !== "undefined" ? self : globalThis);
