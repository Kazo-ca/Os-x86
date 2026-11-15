// Os-x86 engine worker: hosts v86 in a Web Worker (milestone 0, work items 2 and 6a; plan section
// 1.1, decision D3, trials E1/E5/E14). Started by v86-host.js; the C# side (V86Engine) speaks the
// protocol below through it. v86 (../v86/libv86.js, vendored with its patches) is loaded with
// importScripts after the "init" message, with zstd-seekable.js and disk.js next to this file.
// Everything the guest boots comes from the image payload in the OPFS (the only storage fast
// enough for a VM, trial E2), read with a SyncAccessHandle, which only exists in a worker: the
// kernel and the initramfs are decompressed whole, the disks are read by frames on demand
// (disk.js) and written through a copy-on-write layer; the published image is never modified.
//
// Protocol: every message to the C# side is a JSON string; messages from it are JSON strings too
// (v86-host.js passes them through). The "init" and "attach-canvas" messages come from the host
// module itself as objects.
//
//   host -> worker
//     {type:"start", options:{kind:"linux"|"dos", kernel?:source, initrd?:source,
//                             disks?:{hda:{source, layer:{mode:"discard"}|{mode:"persistent", path, metadata}}, hdb?:...},
//                             cmdline, memoryMiB, vgaMemoryMiB, virtioConsole, cacheMiB}}
//         source = {path, offset, size} (raw bytes) or {path, offset, size, rawSize, blockSize} (seekable zstd)
//     {type:"stop"} {type:"pause"} {type:"resume"} {type:"query-state"}
//     {type:"write", channel:"serial0"|"virtio0".."virtio3", data:<base64>}
//     {type:"read-screen"}                             the VGA text screen (trial E14)
//     {type:"query-disks"}                             the disk and cache counters
//     {type:"disk-benchmark", disk, sequentialMiB, randomReads}   reads outside the guest, for the measures
//   worker -> host
//     {type:"ready"}                                   v86 loaded, the worker accepts commands
//     {type:"state", state, reason?}                   stopped | starting | running | paused | stopping
//     {type:"error", code, message, fatal}             fatal: the engine is stopped afterwards
//     {type:"console", channel, data:<base64>}         bytes the guest wrote on that port
//     {type:"screen-size", cols, rows, bpp}            VGA mode change (bpp 0 = text mode)
//     {type:"screen", cols, rows, lines:[...]}         answer to read-screen (text mode; empty lines otherwise)
//     {type:"disks", disks:{hda:{...}}, cache:{...}, decoder:{...}}   answer to query-disks
//     {type:"benchmark", disk, sequential:{...}, random:{...}}        answer to disk-benchmark
//     {type:"event", name}                             v86 events (emulator-ready, emulator-started, ...)
//
// Error codes: invalid-state, opfs-unavailable, image-not-found, image-read-failed, image-corrupt,
// disk-io-failed, quota-exceeded, layer-locked, layer-mismatch, format-unsupported,
// engine-version-mismatch, v86-load-failed, v86-init-failed, download-error, protocol,
// worker-error (set by the host).
"use strict";

const STATES = { stopped: "stopped", starting: "starting", running: "running", paused: "paused", stopping: "stopping" };
const VIRTIO_PORTS = 4;
const VIRTIO_CHUNK = 1024;
const SERIAL_FLUSH_MS = 4;
const MAX_WRITE_BASE64_LENGTH = 2 * 1024 * 1024; // one write command carries at most ~1.5 MiB
const MEMORY_MIB = { min: 16, max: 4096, default: 128 };
const VGA_MIB = { min: 1, max: 64, default: 2 };
const CACHE_MIB = { min: 2, max: 512, default: 32 };
const DISK_NAMES = ["hda", "hdb"];
const BOOT_KINDS = ["linux", "dos"];
const MAX_TEXT_COLUMNS = 256;
const MAX_TEXT_ROWS = 128;
const BENCHMARK_MAX_MIB = 1024;
const BENCHMARK_MAX_READS = 100000;
const CP437 = "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\u000b\f\r\u000e\u000f\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001a\u001b\u001c\u001d\u001e\u001f !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~\u007fÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

let urls = null;
let emulator = null;
let state = STATES.stopped;
let generation = 0; // incremented by every start and stop, to drop stale v86 events
let pausing = false; // a pause is in progress: the next emulator-stopped is not a crash
let canvas = null;
let lastScreenSize = null;
let serialBuffer = [];
let serialFlushTimer = null;
let decoderPromise = null; // the zstd decoder (disk.js), created once from v86.wasm
let frameCache = null; // the frame cache of the running guest
let disks = new Map(); // name -> V86Disk of the running guest
let textScreen = { cols: 80, rows: 25, cells: new Uint16Array(80 * 25) };

function send(message) {
  postMessage(JSON.stringify(message));
}

function setState(next, reason) {
  state = next;
  send({ type: "state", state: next, reason: reason || null });
}

function fail(code, message, fatal) {
  send({ type: "error", code, message: String(message), fatal: !!fatal });
}

class EngineError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}

const errorCode = (error, fallback) => (error && typeof error.code === "string" ? error.code : fallback);

// --- base64 (binary payloads inside JSON) ---------------------------------------------------

function toBase64(bytes) {
  let binary = "";
  for (let index = 0; index < bytes.length; index += 0x8000) {
    binary += String.fromCharCode.apply(null, bytes.subarray(index, index + 0x8000));
  }
  return btoa(binary);
}

function fromBase64(text) {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

// --- console ports ---------------------------------------------------------------------------

function flushSerial() {
  serialFlushTimer = null;
  if (serialBuffer.length === 0) {
    return;
  }
  const bytes = Uint8Array.from(serialBuffer);
  serialBuffer = [];
  send({ type: "console", channel: "serial0", data: toBase64(bytes) });
}

function onSerialByte(byte) {
  serialBuffer.push(byte);
  if (serialBuffer.length >= 4096) {
    flushSerial();
  } else if (serialFlushTimer === null) {
    serialFlushTimer = setTimeout(flushSerial, SERIAL_FLUSH_MS);
  }
}

function writeToGuest(channel, data) {
  if (!emulator || (state !== STATES.running && state !== STATES.paused && state !== STATES.starting)) {
    fail("invalid-state", `cannot write to ${channel}: the engine is ${state}`, false);
    return;
  }
  const match = /^virtio([0-3])$/.exec(channel);
  if (channel !== "serial0" && !match) {
    fail("protocol", `unknown console channel ${channel}`, false);
    return;
  }
  if (data.length > MAX_WRITE_BASE64_LENGTH) {
    fail("protocol", `a write of ${data.length} base64 characters is more than the engine accepts in one message`, false);
    return;
  }
  let bytes;
  try {
    bytes = fromBase64(data);
  } catch (error) {
    fail("protocol", `the write data is not base64: ${error}`, false);
    return;
  }
  if (channel === "serial0") {
    emulator.serial_send_bytes(0, bytes);
    return;
  }
  const event = `virtio-console${match[1]}-input-bytes`;
  for (let index = 0; index < bytes.length; index += VIRTIO_CHUNK) {
    emulator.bus.send(event, bytes.subarray(index, Math.min(index + VIRTIO_CHUNK, bytes.length)));
  }
}

// --- display bridge ----------------------------------------------------------------------------

function applyScreenSize(size) {
  lastScreenSize = size;
  send({ type: "screen-size", cols: size[0], rows: size[1], bpp: size[2] });
  if (size[2] === 0) {
    resizeTextScreen(size[0], size[1]);
  }
  if (!canvas) {
    return;
  }
  // Text mode: 9x16 cells; graphical mode: the mode's own size. Cleared to black until the
  // graphical guests of the next milestones draw the VGA buffer here.
  const width = size[2] === 0 ? size[0] * 9 : size[0];
  const height = size[2] === 0 ? size[1] * 16 : size[1];
  if (width > 0 && height > 0 && (canvas.width !== width || canvas.height !== height)) {
    canvas.width = width;
    canvas.height = height;
  }
  const context = canvas.getContext("2d");
  if (context) {
    context.fillStyle = "#000";
    context.fillRect(0, 0, canvas.width, canvas.height);
  }
}

/** The VGA text screen, kept from v86's screen-put-char events (the way trial E14 read DOS). */
function resizeTextScreen(cols, rows) {
  const width = Math.min(MAX_TEXT_COLUMNS, Math.max(1, cols | 0));
  const height = Math.min(MAX_TEXT_ROWS, Math.max(1, rows | 0));
  if (textScreen.cols !== width || textScreen.rows !== height) {
    textScreen = { cols: width, rows: height, cells: new Uint16Array(width * height) };
  }
}

function putChar(event) {
  const row = event[0];
  const col = event[1];
  if (row >= 0 && row < textScreen.rows && col >= 0 && col < textScreen.cols) {
    textScreen.cells[row * textScreen.cols + col] = event[2] & 0xff;
  }
}

function readScreen() {
  const lines = [];
  for (let row = 0; row < textScreen.rows; row++) {
    let line = "";
    for (let col = 0; col < textScreen.cols; col++) {
      const code = textScreen.cells[row * textScreen.cols + col];
      line += code === 0 ? " " : CP437[code];
    }
    lines.push(line);
  }
  return { type: "screen", cols: textScreen.cols, rows: textScreen.rows, lines };
}

// --- image sources and disks -------------------------------------------------------------------

function clamp(value, range) {
  const number = Number(value);
  if (!Number.isFinite(number) || number <= 0) {
    return range.default;
  }
  return Math.min(range.max, Math.max(range.min, Math.floor(number)));
}

function decoder() {
  if (!decoderPromise) {
    decoderPromise = self.OsX86Disk.ZstdDecoder.create(urls.wasmUrl).catch((error) => {
      decoderPromise = null; // the next start tries again
      throw error;
    });
  }
  return decoderPromise;
}

function checkStartOptions(options) {
  // The form of work item 2 (whole files by path) is a source without offset and size.
  if (!options.kernel && typeof options.kernelPath === "string") {
    options.kernel = { path: options.kernelPath };
  }
  if (!options.initrd && typeof options.initrdPath === "string") {
    options.initrd = { path: options.initrdPath };
  }
  const kind = options.kind === undefined ? "linux" : options.kind;
  if (!BOOT_KINDS.includes(kind)) {
    throw new EngineError("protocol", `unknown boot kind ${kind}`);
  }
  if (kind === "linux" && (!options.kernel || !options.initrd)) {
    throw new EngineError("protocol", "a linux boot needs a kernel and an initrd");
  }
  const diskOptions = options.disks && typeof options.disks === "object" ? options.disks : {};
  for (const name of Object.keys(diskOptions)) {
    if (!DISK_NAMES.includes(name)) {
      throw new EngineError("protocol", `unknown disk ${name} (${DISK_NAMES.join(", ")})`);
    }
    const disk = diskOptions[name];
    self.OsX86Disk.checkSource(disk && disk.source, name);
    const layer = disk.layer || {};
    if (layer.mode === "persistent") {
      if (typeof layer.path !== "string" || typeof layer.metadata !== "string") {
        throw new EngineError("protocol", `${name}: a persistent layer needs a path and its metadata`);
      }
    } else if (layer.mode !== undefined && layer.mode !== "discard") {
      throw new EngineError("protocol", `${name}: unknown layer mode ${layer.mode}`);
    }
  }
  if (kind === "dos" && Object.keys(diskOptions).length === 0) {
    throw new EngineError("protocol", "a dos boot needs a disk");
  }
  return { kind, diskOptions };
}

/** Opens what the start options name: the kernel and initrd whole, the disks on their layers. */
async function openImage(options, kind, diskOptions) {
  const sources = [options.kernel, options.initrd, ...Object.values(diskOptions).map((disk) => disk.source)];
  const needsDecoder = sources.some((source) => source && source.rawSize !== undefined && source.rawSize !== null);
  const zstd = needsDecoder ? await decoder() : null;
  const cache = new self.OsX86Disk.FrameCache(clamp(options.cacheMiB, CACHE_MIB) * 1024 * 1024);
  const files = new Map();
  const opened = new Map();
  try {
    let kernel = null;
    let initrd = null;
    if (kind === "linux") {
      kernel = (await self.OsX86Disk.openEntry(options.kernel, "kernel", files, zstd, cache)).readAll();
      initrd = (await self.OsX86Disk.openEntry(options.initrd, "initrd", files, zstd, cache)).readAll();
    }
    for (const name of Object.keys(diskOptions)) {
      const disk = diskOptions[name];
      const base = await self.OsX86Disk.openEntry(disk.source, name, files, zstd, cache);
      const layer = disk.layer && disk.layer.mode === "persistent"
        ? await self.OsX86Disk.WriteLayer.open(disk.layer.path, base.rawSize, disk.layer.metadata)
        : new self.OsX86Disk.MemoryWriteLayer(base.rawSize);
      opened.set(name, new self.OsX86Disk.V86Disk(name, base, layer));
    }
    return { kernel, initrd, disks: opened, cache, files };
  } catch (error) {
    for (const disk of opened.values()) {
      disk.close();
    }
    for (const file of files.values()) {
      file.close();
    }
    throw error;
  }
}

function closeDisks(reason) {
  const open = disks;
  disks = new Map();
  const files = new Set();
  for (const disk of open.values()) {
    files.add(disk.base.file);
    try {
      disk.close(); // flushes the layer (177 A) and releases its lock
    } catch (error) {
      fail(errorCode(error, "disk-io-failed"), `${disk.name}: ${error && error.message ? error.message : error} (${reason})`, false);
    }
  }
  for (const file of files) {
    file.close();
  }
  if (frameCache) {
    frameCache.clear();
  }
}

function flushDisks() {
  for (const disk of disks.values()) {
    try {
      disk.flush();
    } catch (error) {
      fail(errorCode(error, "disk-io-failed"), `${disk.name}: ${error && error.message ? error.message : error}`, false);
    }
  }
}

async function diskStats() {
  const result = { type: "disks", disks: {}, cache: frameCache ? frameCache.stats() : null, decoder: null };
  for (const [name, disk] of disks) {
    result.disks[name] = disk.stats();
  }
  if (decoderPromise) {
    try {
      result.decoder = (await decoderPromise).stats();
    } catch (error) {
      result.decoder = null;
    }
  }
  return result;
}

/** Reads a disk outside the guest, for the measures of the work item (sequential then random 4 KiB). */
function benchmark(message) {
  const disk = disks.get(String(message.disk || "hda"));
  if (!disk) {
    throw new EngineError("invalid-state", `no disk ${message.disk || "hda"} is open`);
  }
  const sequentialBytes = Math.min(disk.byteLength, clamp(message.sequentialMiB, { min: 1, max: BENCHMARK_MAX_MIB, default: 64 }) * 1024 * 1024);
  const randomReads = clamp(message.randomReads, { min: 1, max: BENCHMARK_MAX_READS, default: 2000 });
  const chunk = 1024 * 1024;
  let started = performance.now();
  for (let offset = 0; offset < sequentialBytes; offset += chunk) {
    disk.read(offset, Math.min(chunk, sequentialBytes - offset));
  }
  const sequentialMs = performance.now() - started;
  const blocks = Math.floor(disk.byteLength / 4096);
  let seed = 0x2545f491;
  started = performance.now();
  for (let index = 0; index < randomReads; index++) {
    seed = (Math.imul(seed, 1103515245) + 12345) >>> 0;
    disk.read((seed % blocks) * 4096, 4096);
  }
  const randomMs = performance.now() - started;
  return {
    type: "benchmark",
    disk: disk.name,
    sequential: { bytes: sequentialBytes, ms: Math.round(sequentialMs), mbPerSecond: Math.round((sequentialBytes / 1e6 / (sequentialMs / 1000)) * 10) / 10 },
    random: { reads: randomReads, ms: Math.round(randomMs), readsPerSecond: Math.round(randomReads / (randomMs / 1000)) },
  };
}

// --- lifecycle -----------------------------------------------------------------------------------

function buildConfig(options, kind, image) {
  const memoryMiB = clamp(options.memoryMiB, MEMORY_MIB);
  const vgaMiB = clamp(options.vgaMemoryMiB, VGA_MIB);
  const config = {
    wasm_path: urls.wasmUrl,
    bios: { url: urls.biosUrl },
    vga_bios: { url: urls.vgaBiosUrl },
    memory_size: memoryMiB * 1024 * 1024,
    vga_memory_size: vgaMiB * 1024 * 1024,
    autostart: true,
    disable_keyboard: true,
    disable_mouse: true,
    disable_speaker: true,
  };
  if (kind === "linux") {
    config.bzimage = { buffer: image.kernel };
    config.initrd = { buffer: image.initrd };
    config.cmdline = options.cmdline || "console=ttyS0";
  }
  for (const [name, disk] of image.disks) {
    config[name] = disk;
  }
  if (options.virtioConsole !== false) {
    config.virtio_console = true;
  }
  return config;
}

function attachListeners(instance, myGeneration) {
  const live = () => emulator === instance && myGeneration === generation;
  for (const name of ["emulator-ready", "emulator-loaded"]) {
    instance.add_listener(name, () => live() && send({ type: "event", name }));
  }
  instance.add_listener("emulator-started", () => {
    if (!live()) {
      return;
    }
    send({ type: "event", name: "emulator-started" });
    if (state === STATES.starting || state === STATES.paused) {
      setState(STATES.running);
    }
  });
  instance.add_listener("emulator-stopped", () => {
    if (!live()) {
      return;
    }
    send({ type: "event", name: "emulator-stopped" });
    if (state === STATES.running && !pausing) {
      // v86 stopped on its own (guest halt, internal error): the engine is no longer running.
      destroyEmulator("emulator-stopped");
    }
  });
  instance.add_listener("download-error", (detail) => {
    if (!live()) {
      return;
    }
    fail("download-error", `v86 could not load a file: ${detail && detail.file_name ? detail.file_name : JSON.stringify(detail)}`, true);
    destroyEmulator("download-error");
  });
  instance.add_listener("serial0-output-byte", (byte) => live() && onSerialByte(byte));
  for (let port = 0; port < VIRTIO_PORTS; port++) {
    instance.add_listener(`virtio-console${port}-output-bytes`, (bytes) => {
      if (live()) {
        send({ type: "console", channel: `virtio${port}`, data: toBase64(bytes) });
      }
    });
  }
  instance.add_listener("screen-set-size", (size) => live() && applyScreenSize(size));
  instance.add_listener("screen-put-char", (event) => live() && putChar(event));
}

async function start(options) {
  if (!urls || typeof V86 !== "function") {
    fail("v86-load-failed", "v86 is not loaded in the worker (no init message)", true);
    return;
  }
  if (state !== STATES.stopped) {
    fail("invalid-state", `cannot start: the engine is ${state}`, false);
    return;
  }
  let kind;
  let diskOptions;
  try {
    ({ kind, diskOptions } = checkStartOptions(options));
  } catch (error) {
    fail(errorCode(error, "protocol"), error.message || error, false);
    return;
  }
  const myGeneration = ++generation;
  setState(STATES.starting);
  let image;
  try {
    image = await openImage(options, kind, diskOptions);
  } catch (error) {
    if (myGeneration === generation) {
      fail(errorCode(error, "image-read-failed"), error.message || error, true);
      setState(STATES.stopped, "start-failed");
    }
    return;
  }
  if (myGeneration !== generation) {
    for (const disk of image.disks.values()) {
      disk.close();
    }
    for (const file of image.files.values()) {
      file.close();
    }
    return; // stopped while the image was being read
  }
  disks = image.disks;
  frameCache = image.cache;
  if (disks.size === 0) {
    for (const file of image.files.values()) {
      file.close(); // the kernel and initrd are in memory: the payload is not needed any more
    }
  }
  try {
    textScreen.cells.fill(0);
    const instance = new V86(buildConfig(options, kind, image));
    emulator = instance;
    attachListeners(instance, myGeneration);
  } catch (error) {
    emulator = null;
    closeDisks("start-failed");
    fail("v86-init-failed", error && error.message ? error.message : error, true);
    setState(STATES.stopped, "start-failed");
  }
}

function destroyEmulator(reason) {
  const instance = emulator;
  emulator = null;
  generation++;
  flushSerial();
  if (instance) {
    try {
      instance.destroy();
    } catch (error) {
      // destroy() is best effort once the loop has stopped; the state is what matters.
    }
  }
  closeDisks(reason);
  setState(STATES.stopped, reason);
}

async function stop() {
  if (state === STATES.stopped) {
    setState(STATES.stopped, "already-stopped");
    return;
  }
  if (state === STATES.stopping) {
    return;
  }
  const instance = emulator;
  setState(STATES.stopping);
  generation++; // the listeners of this instance go quiet from here on
  if (instance) {
    try {
      await instance.stop();
    } catch (error) {
      // the loop may already be stopped (paused engine); fall through to destroy
    }
  }
  if (emulator === instance) {
    destroyEmulator("stopped");
  }
}

async function pause() {
  if (state !== STATES.running || !emulator) {
    fail("invalid-state", `cannot pause: the engine is ${state}`, false);
    return;
  }
  const instance = emulator;
  pausing = true;
  try {
    await instance.stop();
  } finally {
    pausing = false;
  }
  if (emulator === instance && state === STATES.running) {
    flushSerial();
    flushDisks();
    setState(STATES.paused);
  }
}

function resume() {
  if (state !== STATES.paused || !emulator) {
    fail("invalid-state", `cannot resume: the engine is ${state}`, false);
    return;
  }
  emulator.run(); // emulator-started reports "running"
}

function init(v86Urls) {
  if (urls) {
    fail("protocol", "the worker is already initialised", false);
    return;
  }
  const candidate = v86Urls && typeof v86Urls === "object" ? v86Urls : {};
  for (const key of ["libUrl", "wasmUrl", "biosUrl", "vgaBiosUrl"]) {
    let url;
    try {
      url = new URL(String(candidate[key]), self.location.href);
    } catch (error) {
      fail("v86-load-failed", `${key} is not a URL`, true);
      return;
    }
    if (url.origin !== self.location.origin) {
      fail("v86-load-failed", `${key} must be served from this origin`, true);
      return;
    }
  }
  try {
    importScripts(new URL("./zstd-seekable.js", self.location.href).href, new URL("./disk.js", self.location.href).href, candidate.libUrl);
  } catch (error) {
    fail("v86-load-failed", `${candidate.libUrl}: ${error && error.message ? error.message : error}`, true);
    return;
  }
  urls = candidate;
  send({ type: "ready" });
}

onmessage = async (event) => {
  let message = event.data;
  const fromHostModule = typeof message !== "string"; // "init" and the canvas come from v86-host.js as objects
  if (typeof message === "string") {
    try {
      message = JSON.parse(message);
    } catch (error) {
      fail("protocol", `not a JSON message: ${error}`, false);
      return;
    }
  }
  if (!message || typeof message.type !== "string") {
    fail("protocol", "message without a type", false);
    return;
  }
  try {
    switch (message.type) {
      case "init":
        if (!fromHostModule) {
          fail("protocol", "init is not a protocol command", false);
          break;
        }
        init(message.v86);
        break;
      case "start":
        await start(message.options || {});
        break;
      case "stop":
        await stop();
        break;
      case "pause":
        await pause();
        break;
      case "resume":
        resume();
        break;
      case "query-state":
        send({ type: "state", state, reason: "query" });
        break;
      case "write":
        writeToGuest(String(message.channel), String(message.data || ""));
        break;
      case "read-screen":
        send(readScreen());
        break;
      case "query-disks":
        send(await diskStats());
        break;
      case "disk-benchmark":
        send(benchmark(message));
        break;
      case "attach-canvas":
        if (!fromHostModule) {
          fail("protocol", "attach-canvas is not a protocol command", false);
          break;
        }
        canvas = message.canvas;
        if (lastScreenSize) {
          applyScreenSize(lastScreenSize);
        }
        break;
      case "detach-canvas":
        canvas = null;
        break;
      default:
        fail("protocol", `unknown message type ${message.type}`, false);
    }
  } catch (error) {
    fail(errorCode(error, "worker-error"), error && error.stack && !error.code ? error.stack : error.message || error, false);
  }
};
