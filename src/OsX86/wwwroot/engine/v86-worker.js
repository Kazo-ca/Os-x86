// Os-x86 engine worker: hosts v86 in a Web Worker (milestone 0, work item 2; plan section 1.1,
// decision D3, trials E1/E5/E14). Started by v86-host.js; the C# side (V86Engine) speaks the
// protocol below through it. v86 (../v86/libv86.js, vendored with its patches) is loaded with
// importScripts after the "init" message; the guest kernel and initramfs are read from the OPFS
// (the only storage fast enough for a VM, trial E2) with a SyncAccessHandle, which only exists in
// a worker.
//
// Protocol: every message to the C# side is a JSON string; messages from it are JSON strings too
// (v86-host.js passes them through). The "init" and "attach-canvas" messages come from the host
// module itself as objects.
//
//   host -> worker
//     {type:"start", options:{kernelPath, initrdPath, cmdline, memoryMiB, vgaMemoryMiB, virtioConsole}}
//     {type:"stop"} {type:"pause"} {type:"resume"} {type:"query-state"}
//     {type:"write", channel:"serial0"|"virtio0".."virtio3", data:<base64>}
//   worker -> host
//     {type:"ready"}                                   v86 loaded, the worker accepts commands
//     {type:"state", state, reason?}                   stopped | starting | running | paused | stopping
//     {type:"error", code, message, fatal}             fatal: the engine is stopped afterwards
//     {type:"console", channel, data:<base64>}         bytes the guest wrote on that port
//     {type:"screen-size", cols, rows, bpp}            VGA mode change (bpp 0 = text mode)
//     {type:"event", name}                             v86 events (emulator-ready, emulator-started, ...)
//
// Error codes: invalid-state, opfs-unavailable, image-not-found, image-read-failed,
// v86-load-failed, v86-init-failed, download-error, protocol, worker-error (set by the host).
"use strict";

const STATES = { stopped: "stopped", starting: "starting", running: "running", paused: "paused", stopping: "stopping" };
const VIRTIO_PORTS = 4;
const VIRTIO_CHUNK = 1024;
const SERIAL_FLUSH_MS = 4;
const MAX_WRITE_BASE64_LENGTH = 2 * 1024 * 1024; // one write command carries at most ~1.5 MiB
const MAX_IMAGE_FILE_BYTES = 512 * 1024 * 1024; // a kernel or an initramfs read whole into memory
const MEMORY_MIB = { min: 16, max: 4096, default: 128 };
const VGA_MIB = { min: 1, max: 64, default: 2 };

let urls = null;
let emulator = null;
let state = STATES.stopped;
let generation = 0; // incremented by every start and stop, to drop stale v86 events
let pausing = false; // a pause is in progress: the next emulator-stopped is not a crash
let canvas = null;
let lastScreenSize = null;
let serialBuffer = [];
let serialFlushTimer = null;

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

// --- OPFS ------------------------------------------------------------------------------------

async function readOpfsFile(path) {
  if (!self.navigator || !navigator.storage || typeof navigator.storage.getDirectory !== "function") {
    throw new EngineError("opfs-unavailable", "the origin private file system is not available in this worker");
  }
  const parts = String(path).split("/").filter((part) => part.length > 0);
  if (parts.length === 0) {
    throw new EngineError("image-not-found", "empty OPFS path");
  }
  let directory;
  try {
    directory = await navigator.storage.getDirectory();
    for (const part of parts.slice(0, -1)) {
      directory = await directory.getDirectoryHandle(part);
    }
  } catch (error) {
    throw new EngineError(error && error.name === "NotFoundError" ? "image-not-found" : "opfs-unavailable", `${path}: ${error}`);
  }
  let handle;
  try {
    const file = await directory.getFileHandle(parts[parts.length - 1]);
    handle = await file.createSyncAccessHandle();
  } catch (error) {
    throw new EngineError(error && error.name === "NotFoundError" ? "image-not-found" : "image-read-failed", `${path}: ${error}`);
  }
  try {
    const size = handle.getSize();
    if (size > MAX_IMAGE_FILE_BYTES) {
      throw new EngineError("image-read-failed", `${path}: ${size} bytes is more than the ${MAX_IMAGE_FILE_BYTES} the engine loads in memory`);
    }
    const buffer = new ArrayBuffer(size);
    const read = handle.read(new Uint8Array(buffer), { at: 0 });
    if (read !== size) {
      throw new EngineError("image-read-failed", `${path}: read ${read} of ${size} bytes`);
    }
    return buffer;
  } finally {
    handle.close();
  }
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

// --- lifecycle -----------------------------------------------------------------------------------

function clamp(value, range) {
  const number = Number(value);
  if (!Number.isFinite(number) || number <= 0) {
    return range.default;
  }
  return Math.min(range.max, Math.max(range.min, Math.floor(number)));
}

function buildConfig(options, kernel, initrd) {
  const memoryMiB = clamp(options.memoryMiB, MEMORY_MIB);
  const vgaMiB = clamp(options.vgaMemoryMiB, VGA_MIB);
  const config = {
    wasm_path: urls.wasmUrl,
    bios: { url: urls.biosUrl },
    vga_bios: { url: urls.vgaBiosUrl },
    memory_size: memoryMiB * 1024 * 1024,
    vga_memory_size: vgaMiB * 1024 * 1024,
    bzimage: { buffer: kernel },
    initrd: { buffer: initrd },
    cmdline: options.cmdline || "console=ttyS0",
    autostart: true,
    disable_keyboard: true,
    disable_mouse: true,
    disable_speaker: true,
  };
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
  const myGeneration = ++generation;
  setState(STATES.starting);
  let kernel;
  let initrd;
  try {
    kernel = await readOpfsFile(options.kernelPath);
    initrd = await readOpfsFile(options.initrdPath);
  } catch (error) {
    if (myGeneration === generation) {
      fail(error instanceof EngineError ? error.code : "image-read-failed", error.message || error, true);
      setState(STATES.stopped, "start-failed");
    }
    return;
  }
  if (myGeneration !== generation) {
    return; // stopped while the image was being read
  }
  try {
    const instance = new V86(buildConfig(options, kernel, initrd));
    emulator = instance;
    attachListeners(instance, myGeneration);
  } catch (error) {
    emulator = null;
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
    importScripts(candidate.libUrl);
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
    fail("worker-error", error && error.stack ? error.stack : error, false);
  }
};
