// Unit tests of the engine disk (src/OsX86/wwwroot/engine/zstd-seekable.js and disk.js) and of the
// VGA text reader (vga-text.js, work item 11), run by
// Node from DiskScriptTests.cs (category unit). The browser APIs the disk uses are replaced by
// fakes: an in-memory origin private file system with exclusive sync access handles, and a Web
// Locks API. The zstd decoder is the real one of the vendored v86.wasm.
//
//   node disk-tests.js <repository root> [<pack directory made by pack-image.py>]
//
// Prints "ok <name>" or "not ok <name>: <error>" per test and exits 1 when one fails.
"use strict";

const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const zlib = require("node:zlib");

const repository = process.argv[2];
const packDirectory = process.argv[3];
if (!repository) {
  console.error("usage: node disk-tests.js <repository root> [<pack directory>]");
  process.exit(2);
}

// --- fakes of the browser ------------------------------------------------------------------------

class FakeFile {
  constructor() {
    this.data = new Uint8Array(0);
    this.open = false;
    this.flushes = 0;
  }

  ensure(size) {
    if (size > this.data.length) {
      const grown = new Uint8Array(size);
      grown.set(this.data);
      this.data = grown;
    }
  }
}

const fakeOpfs = { root: null, quotaBytes: null };

class FakeHandle {
  constructor(file) {
    this.file = file;
    this.closed = false;
  }

  getSize() {
    return this.file.data.length;
  }

  read(target, { at }) {
    const available = Math.max(0, Math.min(target.length, this.file.data.length - at));
    target.set(this.file.data.subarray(at, at + available));
    return available;
  }

  write(bytes, { at }) {
    if (fakeOpfs.quotaBytes !== null && at + bytes.length > fakeOpfs.quotaBytes) {
      throw Object.assign(new Error("quota"), { name: "QuotaExceededError" });
    }
    this.file.ensure(at + bytes.length);
    this.file.data.set(bytes, at);
    return bytes.length;
  }

  truncate(size) {
    const next = new Uint8Array(size);
    next.set(this.file.data.subarray(0, Math.min(size, this.file.data.length)));
    this.file.data = next;
  }

  flush() {
    this.file.flushes++;
  }

  close() {
    if (!this.closed) {
      this.closed = true;
      this.file.open = false;
    }
  }
}

const notFound = () => Object.assign(new Error("not found"), { name: "NotFoundError" });

class FakeDirectory {
  constructor() {
    this.entries = new Map();
  }

  async getDirectoryHandle(name, options) {
    let entry = this.entries.get(name);
    if (!entry) {
      if (!(options && options.create)) {
        throw notFound();
      }
      entry = new FakeDirectory();
      this.entries.set(name, entry);
    }
    return entry;
  }

  async getFileHandle(name, options) {
    let entry = this.entries.get(name);
    if (!entry) {
      if (!(options && options.create)) {
        throw notFound();
      }
      entry = new FakeFile();
      this.entries.set(name, entry);
    }
    return {
      createSyncAccessHandle: async () => {
        if (entry.open) {
          throw Object.assign(new Error("busy"), { name: "NoModificationAllowedError" });
        }
        entry.open = true;
        return new FakeHandle(entry);
      },
      getFile: async () => ({ size: entry.data.length, text: async () => new TextDecoder().decode(entry.data) }),
    };
  }

  /** The FakeFile at a relative path, or undefined. */
  file(relative) {
    let directory = this;
    const parts = relative.split("/");
    for (const part of parts.slice(0, -1)) {
      directory = directory.entries.get(part);
      if (!directory) {
        return undefined;
      }
    }
    return directory.entries.get(parts[parts.length - 1]);
  }
}

fakeOpfs.root = new FakeDirectory();

const heldLocks = new Set();
const fakeLocks = {
  request(name, options, callback) {
    return new Promise((resolve, reject) => {
      const attempt = () => {
        if (heldLocks.has(name)) {
          return false;
        }
        heldLocks.add(name);
        Promise.resolve(callback()).then((value) => {
          heldLocks.delete(name);
          resolve(value);
        }, (error) => {
          heldLocks.delete(name);
          reject(error);
        });
        return true;
      };
      if (attempt()) {
        return;
      }
      const timer = setInterval(() => attempt() && clearInterval(timer), 10);
      options.signal.addEventListener("abort", () => {
        clearInterval(timer);
        reject(Object.assign(new Error("aborted"), { name: "AbortError" }));
      });
    });
  },
};

Object.defineProperty(globalThis, "navigator", {
  configurable: true,
  value: { storage: { getDirectory: async () => fakeOpfs.root }, locks: fakeLocks },
});

const engineDirectory = path.join(repository, "src", "OsX86", "wwwroot", "engine");
const seekable = require(path.join(engineDirectory, "zstd-seekable.js"));
require(path.join(engineDirectory, "disk.js"));
require(path.join(engineDirectory, "vga-text.js"));
const disk = globalThis.OsX86Disk;
const vgaText = globalThis.OsX86VgaText;
const v86Module = new WebAssembly.Module(fs.readFileSync(path.join(repository, "src", "OsX86", "wwwroot", "v86", "v86.wasm")));

// --- fixtures ------------------------------------------------------------------------------------

/** A seekable zstd entry, as pack-image.py writes it: one frame per block, then the seek table. */
function packSeekable(content, blockSize) {
  const frames = [];
  const sizes = [];
  for (let at = 0; at < content.length; at += blockSize) {
    const block = content.subarray(at, Math.min(content.length, at + blockSize));
    const frame = zlib.zstdCompressSync(block, { params: { [zlib.constants.ZSTD_c_contentSizeFlag]: 1 } });
    frames.push(frame);
    sizes.push([frame.length, block.length]);
  }
  const body = Buffer.alloc(sizes.length * 8);
  sizes.forEach(([compressed, decompressed], index) => {
    body.writeUInt32LE(compressed, index * 8);
    body.writeUInt32LE(decompressed, index * 8 + 4);
  });
  const footer = Buffer.alloc(9);
  footer.writeUInt32LE(sizes.length, 0);
  footer.writeUInt8(0, 4);
  footer.writeUInt32LE(seekable.SEEKABLE_MAGIC, 5);
  const header = Buffer.alloc(8);
  header.writeUInt32LE(seekable.SKIPPABLE_MAGIC, 0);
  header.writeUInt32LE(body.length + 9, 4);
  return new Uint8Array(Buffer.concat([...frames, header, body, footer]));
}

/** Content that compresses but is not uniform: a counter pattern with a few random runs. */
function sampleContent(size, seed) {
  const bytes = new Uint8Array(size);
  let state = seed >>> 0;
  for (let index = 0; index < size; index++) {
    if (index % 7000 < 300) {
      state = (Math.imul(state, 1664525) + 1013904223) >>> 0;
      bytes[index] = state >>> 24;
    } else {
      bytes[index] = (index >>> 9) & 0xff;
    }
  }
  return bytes;
}

/** Writes a payload file into the fake OPFS. */
function putFile(relative, bytes) {
  const parts = relative.split("/");
  let directory = fakeOpfs.root;
  for (const part of parts.slice(0, -1)) {
    if (!directory.entries.has(part)) {
      directory.entries.set(part, new FakeDirectory());
    }
    directory = directory.entries.get(part);
  }
  const file = new FakeFile();
  file.data = new Uint8Array(bytes);
  directory.entries.set(parts[parts.length - 1], file);
  return file;
}

function decoder() {
  const instance = new disk.ZstdDecoder(v86Module);
  instance.instantiate();
  return instance;
}

const metadata = (overrides) => JSON.stringify({ format: 1, kind: "layer", image: "test-image", imageVersion: "1.0.0", engine: { v86: "0.5.473" }, created: "2026-11-14T00:00:00+00:00", complete: true, ...overrides });

// --- tests ---------------------------------------------------------------------------------------

const tests = [];
const test = (name, body) => tests.push({ name, body });

const BLOCK = 64 * 1024;
const CONTENT = sampleContent(5 * BLOCK + 1234, 7);
const ENTRY = packSeekable(CONTENT, BLOCK);

test("the seek table is read from the footer and places every frame", () => {
  const footer = seekable.readFooter(ENTRY.subarray(ENTRY.length - seekable.FOOTER_SIZE));
  assert.equal(footer.frameCount, 6);
  assert.equal(footer.withChecksums, false);
  const table = seekable.parseSeekTable(ENTRY.subarray(ENTRY.length - footer.tableSize), ENTRY.length);
  assert.equal(table.rawSize, CONTENT.length);
  assert.equal(table.frames[0].compressedOffset, 0);
  assert.equal(table.frames[5].decompressedOffset, 5 * BLOCK);
  assert.equal(table.frames[5].decompressedSize, 1234);
  seekable.checkAgainstManifest(table, CONTENT.length, BLOCK);
});

test("a damaged seek table or one that disagrees with the manifest is refused", () => {
  const footerAt = ENTRY.length - seekable.FOOTER_SIZE;
  const badMagic = ENTRY.slice();
  badMagic[ENTRY.length - 1] ^= 0xff;
  assert.throws(() => seekable.readFooter(badMagic.subarray(footerAt)), /magic/);
  const reserved = ENTRY.slice();
  reserved[footerAt + 4] = 0x04;
  assert.throws(() => seekable.readFooter(reserved.subarray(footerAt)), /reserved/);
  const footer = seekable.readFooter(ENTRY.subarray(footerAt));
  const tableBytes = ENTRY.subarray(ENTRY.length - footer.tableSize);
  assert.throws(() => seekable.parseSeekTable(tableBytes, ENTRY.length + 1), /entry has/);
  const skippable = tableBytes.slice();
  skippable[0] ^= 1;
  assert.throws(() => seekable.parseSeekTable(skippable, ENTRY.length), /malformed/);
  const table = seekable.parseSeekTable(tableBytes, ENTRY.length);
  assert.throws(() => seekable.checkAgainstManifest(table, CONTENT.length + 1, BLOCK), /manifest says/);
  assert.throws(() => seekable.checkAgainstManifest(table, CONTENT.length, BLOCK * 2), /blockSize/);
  assert.throws(() => seekable.readFooter(new Uint8Array(4)), /9 bytes/);
});

test("a read is planned frame by frame, across frame boundaries", () => {
  const footer = seekable.readFooter(ENTRY.subarray(ENTRY.length - seekable.FOOTER_SIZE));
  const { frames, rawSize } = seekable.parseSeekTable(ENTRY.subarray(ENTRY.length - footer.tableSize), ENTRY.length);
  assert.deepEqual(seekable.planRead(frames, rawSize, 10, 20), [{ index: 0, start: 10, end: 30, target: 0 }]);
  assert.deepEqual(seekable.planRead(frames, rawSize, BLOCK - 4, 8), [
    { index: 0, start: BLOCK - 4, end: BLOCK, target: 0 },
    { index: 1, start: 0, end: 4, target: 4 },
  ]);
  assert.equal(seekable.planRead(frames, rawSize, 0, rawSize).length, 6);
  assert.deepEqual(seekable.planRead(frames, rawSize, rawSize, 0), []);
  assert.throws(() => seekable.planRead(frames, rawSize, rawSize - 1, 2), RangeError);
  assert.throws(() => seekable.planRead(frames, rawSize, -1, 1), RangeError);
  assert.equal(seekable.frameIndexAt(frames, 5 * BLOCK + 1), 5);
});

test("the frame header gives the content size and refuses what is not a zstd frame", () => {
  const frame = ENTRY.subarray(0, 64);
  assert.equal(seekable.frameContentSize(frame), BLOCK);
  assert.throws(() => seekable.frameContentSize(new Uint8Array([1, 2, 3, 4, 5, 6])), /not a zstd frame/);
});

test("the zstd decoder of v86.wasm decompresses frames and survives a corrupt one", () => {
  const zstd = decoder();
  const footer = seekable.readFooter(ENTRY.subarray(ENTRY.length - seekable.FOOTER_SIZE));
  const { frames } = seekable.parseSeekTable(ENTRY.subarray(ENTRY.length - footer.tableSize), ENTRY.length);
  const first = frames[0];
  const bytes = zstd.decompress(ENTRY.subarray(first.compressedOffset, first.compressedOffset + first.compressedSize), first.decompressedSize);
  assert.deepEqual(bytes, CONTENT.subarray(0, BLOCK));
  assert.throws(() => zstd.decompress(ENTRY.subarray(0, first.compressedSize), BLOCK + 1), (error) => error.code === "image-corrupt");
  const garbage = ENTRY.slice(0, first.compressedSize);
  garbage.fill(0x55, 12);
  assert.throws(() => zstd.decompress(garbage, BLOCK), (error) => error.code === "image-corrupt");
  const again = zstd.decompress(ENTRY.subarray(0, first.compressedSize), BLOCK); // a fresh instance after the trap
  assert.deepEqual(again, CONTENT.subarray(0, BLOCK));
  assert.ok(zstd.stats().framesDecoded >= 2);
});

test("the frame cache stays within its bound and counts hits, misses and evictions", () => {
  const cache = new disk.FrameCache(2 * 1024 * 1024);
  const megabyte = new Uint8Array(1024 * 1024);
  cache.put("a", megabyte);
  cache.put("b", megabyte);
  assert.equal(cache.get("a"), megabyte); // a is now the most recent
  cache.put("c", megabyte); // evicts b
  assert.equal(cache.get("b"), undefined);
  assert.equal(cache.get("c"), megabyte);
  const stats = cache.stats();
  assert.equal(stats.cacheBytes, 2 * 1024 * 1024);
  assert.equal(stats.evictions, 1);
  assert.equal(stats.hits, 2);
  assert.equal(stats.misses, 1);
  cache.put("huge", new Uint8Array(3 * 1024 * 1024)); // larger than the cache: not kept
  assert.equal(cache.stats().cacheFrames, 2);
  assert.equal(new disk.FrameCache(1).maxBytes, 2 * 1024 * 1024); // clamped to the minimum
});

test("a seekable entry inside a payload reads any range, decoding each frame once", async () => {
  const prefix = new Uint8Array(777).fill(9);
  putFile("osx86/images/test-image/1.0.0/image.bin", Buffer.concat([prefix, ENTRY, new Uint8Array(55)]));
  const files = new Map();
  const cache = new disk.FrameCache(8 * 1024 * 1024);
  const zstd = decoder();
  const source = { path: "osx86/images/test-image/1.0.0/image.bin", offset: 777, size: ENTRY.length, rawSize: CONTENT.length, blockSize: BLOCK };
  const entry = await disk.openEntry(source, "rootfs", files, zstd, cache);
  for (const [offset, length] of [[0, 1], [BLOCK - 3, 9], [3 * BLOCK + 17, BLOCK + 100], [CONTENT.length - 10, 10]]) {
    assert.deepEqual(entry.read(offset, length), CONTENT.subarray(offset, offset + length));
  }
  const decoded = zstd.stats().framesDecoded;
  entry.read(BLOCK - 3, 9); // frames 0 and 1 are cached now
  assert.equal(zstd.stats().framesDecoded, decoded);
  assert.deepEqual(new Uint8Array(entry.readAll()), CONTENT);
  assert.throws(() => entry.read(CONTENT.length, 1), (error) => error.code === "protocol");
  await assert.rejects(disk.openEntry({ ...source, rawSize: CONTENT.length + 1 }, "rootfs", files, zstd, cache), (error) => error.code === "image-corrupt");
  await assert.rejects(disk.openEntry({ ...source, size: ENTRY.length + 10_000 }, "rootfs", files, zstd, cache), (error) => error.code === "image-corrupt");
  await assert.rejects(disk.openEntry({ path: "osx86/missing.bin", offset: 0, size: 1 }, "x", files, zstd, cache), (error) => error.code === "image-not-found");
  for (const file of files.values()) {
    file.close();
  }
});

test("sources are checked: relative paths, integers, whole files", async () => {
  assert.throws(() => disk.checkSource({ path: "/abs", offset: 0, size: 1 }, "x"), (error) => error.code === "protocol");
  assert.throws(() => disk.checkSource({ path: "a/../b", offset: 0, size: 1 }, "x"), (error) => error.code === "protocol");
  assert.throws(() => disk.checkSource({ path: "a", offset: -1, size: 1 }, "x"), (error) => error.code === "protocol");
  assert.throws(() => disk.checkSource({ path: "a", offset: 0, size: 1, rawSize: 4, blockSize: 0 }, "x"), (error) => error.code === "protocol");
  assert.throws(() => disk.checkSource({ path: "a", rawSize: 4, blockSize: 4 }, "x"), (error) => error.code === "protocol");
  assert.throws(() => disk.checkSource(null, "x"), (error) => error.code === "protocol");
  putFile("whole/kernel", new Uint8Array([1, 2, 3]));
  const files = new Map();
  const entry = await disk.openEntry({ path: "whole/kernel" }, "kernel", files, null, null);
  assert.deepEqual(new Uint8Array(entry.readAll()), new Uint8Array([1, 2, 3]));
  files.get("whole/kernel").close();
});

test("a discard layer takes unaligned writes and leaves the image untouched", async () => {
  const size = 3 * 4096 + 1000; // the last block is partial
  const content = sampleContent(size, 3);
  const payload = putFile("raw/disk.bin", content);
  const files = new Map();
  const base = await disk.openEntry({ path: "raw/disk.bin", offset: 0, size }, "hda", files, null, null);
  const target = new disk.V86Disk("hda", base, new disk.MemoryWriteLayer(size));
  let loaded = null;
  target.onload = (event) => (loaded = event);
  target.load();
  assert.equal(loaded.buffer, target);
  const written = new Uint8Array(5000).fill(0xab);
  await new Promise((resolve) => target.set(4000, written, resolve)); // spans blocks 0, 1 and 2
  const tail = new Uint8Array(10).fill(0xcd);
  target.write(size - 10, tail); // inside the partial last block
  const expected = content.slice();
  expected.set(written, 4000);
  expected.set(tail, size - 10);
  const read = await new Promise((resolve) => target.get(0, size, resolve));
  assert.deepEqual(read, expected);
  assert.deepEqual(target.read(3990, 20), expected.subarray(3990, 4010));
  assert.deepEqual(payload.data, content, "the image itself was written");
  assert.equal(target.stats().layer.mode, "discard");
  assert.equal(target.stats().layer.blocksWritten, 4);
  assert.throws(() => target.read(size - 1, 2), (error) => error.code === "protocol");
  assert.throws(() => target.write(0, new Uint8Array(0)), (error) => error.code === "protocol");
  target.close();
  assert.throws(() => target.read(0, 1), (error) => error.code === "invalid-state");
  assert.throws(() => target.get_state(), (error) => error.code === "invalid-state");
  files.get("raw/disk.bin").close();
});

const LAYER = "osx86/vms/test/hda.layer";
const DISK_SIZE = 64 * 4096 + 512;

test("a persistent layer keeps its blocks across a close and a reopen", async () => {
  const layer = await disk.WriteLayer.open(LAYER, DISK_SIZE, metadata());
  layer.writeBlock(5, new Uint8Array(4096).fill(7));
  layer.writeBlock(2, new Uint8Array(4096).fill(3));
  layer.writeBlock(5, new Uint8Array(4096).fill(8)); // rewritten in place: still two slots
  assert.equal(layer.stats().blocksWritten, 2);
  layer.close();
  const sidecar = JSON.parse(new TextDecoder().decode(fakeOpfs.root.file(`${LAYER}.meta.json`).data));
  assert.equal(sidecar.kind, "layer");
  const file = fakeOpfs.root.file(LAYER);
  assert.ok(file.flushes >= 2, "the layer was not flushed at close");
  assert.equal(new TextDecoder().decode(file.data.subarray(0, 11)), "OSX86-LAYER");

  const reopened = await disk.WriteLayer.open(LAYER, DISK_SIZE, metadata());
  assert.ok(reopened.has(5) && reopened.has(2) && !reopened.has(4));
  const out = new Uint8Array(4096);
  reopened.readBlock(5, out, 0);
  assert.equal(out[100], 8);
  reopened.readBlock(2, out, 0);
  assert.equal(out[0], 3);
  assert.equal(reopened.stats().layerBytes, reopened.dataOffset + 2 * 4096);
  reopened.close();
});

test("one writer per layer: a second open while the first is open is refused", async () => {
  const first = await disk.WriteLayer.open(LAYER, DISK_SIZE, metadata());
  await assert.rejects(disk.WriteLayer.open(LAYER, DISK_SIZE, metadata()), (error) => error.code === "layer-locked");
  first.close();
  const second = await disk.WriteLayer.open(LAYER, DISK_SIZE, metadata());
  second.close();
});

test("a layer of another image version, engine major or format is refused without writing", async () => {
  const file = fakeOpfs.root.file(LAYER);
  const before = file.data.slice();
  const sidecarBefore = fakeOpfs.root.file(`${LAYER}.meta.json`).data.slice();
  await assert.rejects(disk.WriteLayer.open(LAYER, DISK_SIZE, metadata({ imageVersion: "1.0.1" })), (error) => error.code === "layer-mismatch");
  await assert.rejects(disk.WriteLayer.open(LAYER, DISK_SIZE, metadata({ engine: { v86: "0.6.0" } })), (error) => error.code === "engine-version-mismatch");
  await assert.rejects(disk.WriteLayer.open(LAYER, DISK_SIZE, metadata({ format: 2 })), (error) => error.code === "format-unsupported");
  await assert.rejects(disk.WriteLayer.open(LAYER, DISK_SIZE + 4096, metadata()), (error) => error.code === "layer-mismatch");
  assert.deepEqual(file.data, before);
  const same = await disk.WriteLayer.open(LAYER, DISK_SIZE, metadata({ engine: { v86: "0.5.999" } })); // same major (0.5)
  same.close();
  file.data[12] = 9; // the format number of the layer header
  const damaged = file.data.slice();
  await assert.rejects(disk.WriteLayer.open(LAYER, DISK_SIZE, metadata()), (error) => error.code === "format-unsupported");
  assert.deepEqual(file.data, damaged);
  file.data = before;
  assert.deepEqual(fakeOpfs.root.file(`${LAYER}.meta.json`).data, sidecarBefore);
  assert.equal(disk.majorOf("0.5.473"), "0.5");
  assert.equal(disk.majorOf("2.1.0"), "2");
});

test("a disk on a persistent layer reads its writes back after a reopen", async () => {
  const size = 16 * 4096;
  const content = sampleContent(size, 11);
  putFile("raw/hdb.bin", content);
  const files = new Map();
  const base = await disk.openEntry({ path: "raw/hdb.bin", offset: 0, size }, "hdb", files, null, null);
  const layerPath = "osx86/vms/test2/hdb.layer";
  let target = new disk.V86Disk("hdb", base, await disk.WriteLayer.open(layerPath, size, metadata()));
  target.write(8192 + 100, new TextEncoder().encode("written in the guest"));
  target.close();
  target = new disk.V86Disk("hdb", base, await disk.WriteLayer.open(layerPath, size, metadata()));
  assert.equal(new TextDecoder().decode(target.read(8192 + 100, 20)), "written in the guest");
  assert.deepEqual(target.read(0, 8192), content.subarray(0, 8192));
  assert.equal(target.stats().layer.mode, "persistent");
  target.close();
  files.get("raw/hdb.bin").close();
});

test("a full quota surfaces as quota-exceeded", async () => {
  const layer = await disk.WriteLayer.open("osx86/vms/quota/hda.layer", DISK_SIZE, metadata());
  fakeOpfs.quotaBytes = layer.dataOffset + 4096;
  layer.writeBlock(0, new Uint8Array(4096));
  assert.throws(() => layer.writeBlock(1, new Uint8Array(4096)), (error) => error.code === "quota-exceeded");
  fakeOpfs.quotaBytes = null;
  layer.close();
});

if (packDirectory) {
  test("an entry made by pack-image.py decompresses to its sha256", async () => {
    const manifest = JSON.parse(fs.readFileSync(path.join(packDirectory, "manifest.json"), "utf8"));
    const chunks = manifest.chunks.map((_, index) => fs.readFileSync(path.join(packDirectory, `chunk-${String(index).padStart(5, "0")}.bin`)));
    putFile("osx86/images/packed/1.0.0/image.bin", Buffer.concat(chunks));
    const files = new Map();
    const zstd = decoder();
    for (const [name, entry] of Object.entries(manifest.files)) {
      const opened = await disk.openEntry({ path: "osx86/images/packed/1.0.0/image.bin", ...entry }, name, files, zstd, new disk.FrameCache(4 * 1024 * 1024));
      const whole = new Uint8Array(opened.readAll());
      assert.equal(crypto.createHash("sha256").update(whole).digest("hex"), entry.sha256, name);
      const middle = Math.floor(entry.rawSize / 2);
      const length = Math.min(4096 + 7, entry.rawSize - middle);
      assert.deepEqual(opened.read(middle, length), whole.subarray(middle, middle + length));
    }
    for (const file of files.values()) {
      file.close();
    }
  });
}

// --- work item 11: inline disks and the VGA text reader ---------------------------------------

test("an inline disk is read from the start options, alone and within its bounds", async () => {
  const bytes = Uint8Array.from({ length: 8192 }, (_, index) => index & 0xff);
  const source = { inline: Buffer.from(bytes).toString("base64") };
  const files = new Map();
  const entry = await disk.openEntry(source, "hdb", files, null, null);
  assert.equal(entry.rawSize, 8192);
  assert.equal(entry.file, null);
  assert.equal(files.size, 0);
  assert.deepEqual(entry.read(4000, 300), bytes.subarray(4000, 4300));
  assert.throws(() => entry.read(8000, 300), (error) => error.code === "protocol");
  const layered = new disk.V86Disk("hdb", entry, new disk.MemoryWriteLayer(entry.rawSize));
  layered.write(10, Uint8Array.of(1, 2, 3));
  assert.deepEqual(layered.read(9, 5), Uint8Array.of(9, 1, 2, 3, 13));
  assert.deepEqual(entry.read(10, 3), Uint8Array.of(10, 11, 12), "the inline bytes are not written");
  assert.throws(() => disk.checkSource({ inline: "AAAA", path: "x/y" }, "hdb"), (error) => error.code === "protocol");
  await assert.rejects(disk.openEntry({ inline: "" }, "hdb", files, null, null), (error) => error.code === "protocol");
});

function fakeVga(text, attribute, overrides) {
  const memory = new Uint8Array(262144);
  for (let index = 0; index < 80 * 25; index++) {
    memory[2 * index] = 0x20;
    memory[2 * index + 1] = 0x07;
  }
  for (let index = 0; index < text.length; index++) {
    memory[2 * index] = text.charCodeAt(index);
    memory[2 * index + 1] = attribute;
  }
  const vga = Object.assign({
    vga_memory: memory, start_address: 0, offset_register: 40, line_compare: 0x3ff, max_cols: 80, max_rows: 25,
    cursor_address: 80 + 5, cursor_scanline_start: 13, cursor_scanline_end: 14, max_scan_line: 15, attribute_mode: 8,
    graphical_mode: false, scan_line_to_screen_row: () => 1000,
  }, overrides || {});
  return { v86: { cpu: { devices: { vga } } } };
}

test("the VGA text reader gives characters, attributes and the cursor of v86's VGA", () => {
  const screen = vgaText.read(fakeVga("OSX86 ÉÍ", 0x1e), null);
  assert.equal(screen.source, "vga");
  assert.equal(screen.cols, 80);
  assert.equal(screen.rows, 25);
  assert.equal(screen.lines[0].slice(0, 8), "OSX86 ╔═");
  const attributes = Buffer.from(screen.attributes, "base64");
  assert.equal(attributes.length, 80 * 25);
  assert.equal(attributes[0], 0x1e);
  assert.equal(attributes[8], 0x07);
  assert.deepEqual(screen.cursor, { row: 1, col: 5, visible: true });
  assert.equal(screen.blink, true);
});

test("the VGA text reader hides a disabled cursor and follows the start address", () => {
  const hidden = vgaText.read(fakeVga("A", 0x07, { cursor_scanline_start: 0x20 | 13 }), null);
  assert.equal(hidden.cursor.visible, false);
  const scrolled = vgaText.read(fakeVga("", 0x07, { start_address: 80, cursor_address: 80 }), null);
  assert.deepEqual(scrolled.cursor, { row: 0, col: 0, visible: true });
});

test("without v86's VGA fields the reader falls back to the characters of screen-put-char", () => {
  const cells = new Uint16Array(80 * 25);
  cells[0] = 0x41;
  const screen = vgaText.read({ v86: { cpu: { devices: {} } } }, { cols: 80, rows: 25, cells });
  assert.equal(screen.source, "events");
  assert.equal(screen.lines[0][0], "A");
  assert.equal(screen.cursor, null);
  assert.equal(Buffer.from(screen.attributes, "base64")[0], 0x07);
  assert.equal(vgaText.CP437.length, 256);
});

(async () => {
  let failed = 0;
  for (const { name, body } of tests) {
    try {
      await body();
      console.log(`ok ${name}`);
    } catch (error) {
      failed++;
      console.log(`not ok ${name}: ${error && error.stack ? error.stack : error}`);
    }
  }
  console.log(`${tests.length - failed} passed, ${failed} failed`);
  process.exit(failed === 0 ? 0 : 1);
})();
