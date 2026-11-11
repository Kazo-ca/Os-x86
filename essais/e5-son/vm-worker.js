// Essai E5 — v86 dans un Web Worker (sans écran : DummyScreenAdapter), avec pont audio direct
// worker -> AudioWorklet : les blocs SB16 ("dac-send-data" sur le bus v86) sont postés (transférés)
// sur un MessagePort tenu par le worklet, et les demandes "pump" du worklet reviennent ici sans
// passer par le fil principal de la page. Sortie série renvoyée à la page ligne par ligne,
// horodatée en temps absolu (performance.timeOrigin + performance.now()).
"use strict";
importScripts("lib/libv86.js");

let emu = null;
let dacPort = null;
let guard = false; // garde anti-requêtes périmées (équivalent PR v86 #1661)
const stats = { sent: 0, sentFrames: 0, served: 0, dropped: 0, enable: 0, disable: 0, rate: null, hello: false, enableEpochs: [] };
function resetStats() { Object.assign(stats, { sent: 0, sentFrames: 0, served: 0, dropped: 0, enable: 0, disable: 0, enableEpochs: [] }); }
const epoch = () => performance.timeOrigin + performance.now();

let line = "";
let flushTimer = null;
function serialByte(byte) {
  const c = String.fromCharCode(byte);
  line += c;
  if (c === "\n") { postMessage({ type: "serial", text: line, t: epoch() }); line = ""; if (flushTimer) { clearTimeout(flushTimer); flushTimer = null; } }
  else if (!flushTimer) flushTimer = setTimeout(() => { flushTimer = null; if (line) { postMessage({ type: "serial", text: line, t: epoch() }); line = ""; } }, 50);
}

function onPump(m) {
  if (!emu) return;
  if (guard && m.received < stats.sent) { stats.dropped++; return; }
  stats.served++;
  emu.bus.send("dac-request-data");
}

function start(cfg) {
  guard = !!cfg.guard;
  dacPort = cfg.port;
  dacPort.onmessage = (e) => {
    if (e.data.type === "pump") onPump(e.data);
    else if (e.data.type === "hello") stats.hello = true;
  };
  const config = { ...cfg.config, disable_speaker: true, disable_keyboard: true, disable_mouse: true };
  emu = new V86(config);
  for (const ev of ["emulator-ready", "emulator-started", "emulator-stopped", "download-error"]) {
    emu.add_listener(ev, () => postMessage({ type: "event", name: ev, t: epoch() }));
  }
  emu.add_listener("serial0-output-byte", serialByte);
  emu.bus.register("dac-send-data", (data) => {
    stats.sent++; stats.sentFrames += data[0].length;
    dacPort.postMessage({ type: "queue", value: data }, [data[0].buffer, data[1].buffer]);
  });
  emu.bus.register("dac-enable", () => { stats.enable++; stats.enableEpochs.push(epoch()); if (stats.enableEpochs.length > 50) stats.enableEpochs.shift(); dacPort.postMessage({ type: "enable" }); });
  emu.bus.register("dac-disable", () => { stats.disable++; dacPort.postMessage({ type: "disable" }); });
  emu.bus.register("dac-tell-sampling-rate", (rate) => { stats.rate = rate; dacPort.postMessage({ type: "rate", value: rate }); });
  emu.bus.send("speaker-has-initialized");
  postMessage({ type: "started", t: epoch() });
}

// Charge artificielle : bloque le fil du worker `block` ms toutes les `period` ms pendant `seconds` s.
function stall({ block, period, seconds }) {
  const end = performance.now() + seconds * 1000;
  const tick = () => {
    if (performance.now() >= end) { postMessage({ type: "stall-done", t: epoch() }); return; }
    const t = performance.now() + block;
    while (performance.now() < t) { /* busy */ }
    setTimeout(tick, Math.max(0, period - block));
  };
  setTimeout(tick, 0);
}

onmessage = async (e) => {
  const m = e.data;
  try {
    switch (m.type) {
      case "start": start(m); break;
      case "serial": emu.serial0_send(m.text); break;
      case "stall": stall(m); break;
      case "reset": resetStats(); break;
      case "stats": postMessage({ type: "stats", id: m.id, stats: { ...stats, guard, t: epoch() } }); break;
      case "save": {
        const t = performance.now();
        const s = await emu.save_state();
        postMessage({ type: "saved", id: m.id, ms: performance.now() - t, state: s }, [s]);
        break;
      }
      case "stop": await emu.stop(); postMessage({ type: "stopped", id: m.id }); break;
    }
  } catch (err) {
    postMessage({ type: "error", id: m.id, error: String(err && err.stack || err) });
  }
};
