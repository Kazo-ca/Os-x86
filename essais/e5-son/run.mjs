// Essai E5 — pilote de mesure (Playwright + Chrome installé, headless).
// Usage : node run.mjs [--runs 2] [--first 1] [--scenarios a,b,...] [--reuse-state] [--channel chrome|msedge] [--headed] [--mem 256] [--port 8733]
// Phase 0 (une fois) : boot à froid en mode main jusqu'au prompt + MARK snd-ready, save_state -> images/state-snd.bin.
// Puis, pour chaque run et chaque scénario : page depuis l'état, geste (clic), lecture (aplay / PipeWire),
// rapport du worklet (RMS par bloc, trous, sous-alimentation, rejoues via l'escalier de niveaux), CPU (CDP SystemInfo).
// Écrit results-<n>.json à côté du script.
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(
  process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length),
);
const RUNS = Number(args.runs || 2);
const FIRST = Number(args.first || 1);
const CHANNEL = args.channel || "chrome";
const HEADLESS = !args.headed;
const MEM = Number(args.mem || 256);
const PORT = Number(args.port || 8733);
const BASE = `http://localhost:${PORT}`;
const STATE = "state-snd.bin";
const MIN = 60_000;
const LEVELS = [0, 1, 2, 3, 4, 5, 6, 7].map((k) => 0.9 * 0.78 ** (7 - k)); // comme gen-wav.py
const RATIOS = LEVELS.map((l) => l / 0.5); // gauche / droite attendu par niveau

const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const sec = (ms) => (ms == null ? null : Math.round(ms) / 1000);
const r2 = (x) => (x == null ? null : Math.round(x * 100) / 100);

// ---------- scénarios ----------
// mode : main (v86 + haut-parleur natif, fil principal) | worker (vm-worker.js + pont direct worker->worklet)
// load : none | main (fil principal bloqué 300 ms / 500 ms) | worker (fil du worker bloqué 300/500) | guest (boucle CPU dans l'invité)
const SCENARIOS = {
  "main-idle":        { mode: "main",   load: "none" },
  "main-mainload":    { mode: "main",   load: "main" },
  "main-gesture":     { mode: "main",   load: "none", lateGesture: true },
  "worker-idle":      { mode: "worker", load: "none" },
  "worker-mainload":  { mode: "worker", load: "main" },
  "worker-guestload": { mode: "worker", load: "guest" },
  "worker-stall":     { mode: "worker", load: "worker", guard: false },
  "worker-stall-guard": { mode: "worker", load: "worker", guard: true },
  "worker-stall-guard-lead": { mode: "worker", load: "worker", guard: true, lead: 250 },
  "worker-gesture":   { mode: "worker", load: "none", lateGesture: true },
  "worker-pipewire":  { mode: "worker", load: "none", pipewire: true },
  "main-pipewire":    { mode: "main",   load: "none", pipewire: true },
};
const SCEN_LIST = args.scenarios ? String(args.scenarios).split(",") : Object.keys(SCENARIOS);

// ---------- serveur ----------
const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
process.on("exit", () => server.kill());
await sleep(500);
const stats = async () => (await fetch(`${BASE}/__stats`)).json();
const resetStats = () => fetch(`${BASE}/__reset`);

// ---------- aides page ----------
function pageUrl(extra = {}) {
  const u = new URL(`${BASE}/boot.html`);
  u.searchParams.set("mem", String(MEM));
  for (const [k, v] of Object.entries(extra)) u.searchParams.set(k, v);
  return u.toString();
}
async function waitMark(page, name, timeoutMs, failNames = []) {
  await page.waitForFunction(([n, fails]) => window.E5.marks.some((m) => m.name === n || fails.includes(m.name)), [name, failNames], { timeout: timeoutMs, polling: 100 });
  return page.evaluate(() => window.E5.marks);
}
async function waitEvent(page, name, timeoutMs) {
  await page.waitForFunction((n) => window.E5.events.some((e) => e.name === n), name, { timeout: timeoutMs, polling: 100 });
  return page.evaluate((n) => window.E5.events.find((e) => e.name === n), name);
}
const markOf = (marks, name) => marks.find((m) => m.name === name);
const lastMark = (marks, name) => marks.filter((m) => m.name === name).pop();
async function openPage(context, scen, extra = {}) {
  await resetStats();
  const page = await context.newPage();
  page.on("pageerror", (e) => log("pageerror", e.message));
  page.on("console", (m) => { if (m.type() === "error" || m.type() === "warning") log("console." + m.type(), m.text().slice(0, 160)); });
  const q = { mode: scen.mode, ...extra };
  if (scen.guard) q.guard = "1";
  if (scen.lead) q.lead = String(scen.lead);
  await page.goto(pageUrl(q));
  await page.waitForFunction(() => window.E5.ready || window.E5.setupError, null, { timeout: 2 * MIN, polling: 100 });
  const err = await page.evaluate(() => window.E5.setupError);
  if (err) throw new Error("setup page: " + err);
  return page;
}
async function guestAlive(page, tag, timeoutMs = 3 * MIN) {
  await page.evaluate((t) => window.E5.send(`\necho "MARK ${t} $(cut -d' ' -f1 /proc/uptime)"\n`), tag);
  const marks = await waitMark(page, tag, timeoutMs);
  return lastMark(marks, tag);
}

// ---------- CPU (CDP SystemInfo : temps CPU cumulé par processus Chrome) ----------
async function cpuSample(browser) {
  const cdp = await browser.newBrowserCDPSession();
  const { processInfo } = await cdp.send("SystemInfo.getProcessInfo");
  await cdp.detach();
  return { t: performance.now(), procs: processInfo.map((p) => ({ id: p.id, type: p.type, cpu: p.cpuTime })) };
}
function cpuDelta(a, b) {
  const wall = (b.t - a.t) / 1000;
  const byType = {};
  const procs = [];
  for (const pb of b.procs) {
    const pa = a.procs.find((p) => p.id === pb.id);
    if (!pa) continue;
    const d = pb.cpu - pa.cpu;
    procs.push({ id: pb.id, type: pb.type, cpu_s: r2(d), pct: r2((d / wall) * 100) });
    byType[pb.type] = (byType[pb.type] || 0) + d;
  }
  const renderer = procs.filter((p) => p.type === "renderer").sort((x, y) => y.cpu_s - x.cpu_s)[0];
  const audio = procs.find((p) => /audio/i.test(p.type));
  return {
    wall_s: r2(wall),
    renderer_pct: renderer ? renderer.pct : null,
    audio_service_pct: audio ? audio.pct : null,
    total_pct: r2((Object.values(byType).reduce((s, x) => s + x, 0) / wall) * 100),
    by_type_pct: Object.fromEntries(Object.entries(byType).map(([k, v]) => [k, r2((v / wall) * 100)])),
  };
}

// ---------- analyse du rapport du worklet ----------
function toEpoch(clock, t) { return clock.epoch + (t - clock.contextTime) * 1000; }
function analyze(rep, clock, tStartEpoch, tEndEpoch) {
  const n = rep.nBlocks;
  const blockMs = (rep.blockFrames / rep.sampleRate) * 1000;
  const SIL = 0.02;
  const inWin = [];
  for (let i = 0; i < n; i++) {
    const e = toEpoch(clock, rep.histT[i]);
    if (e >= tStartEpoch - 200 && e <= tEndEpoch + 1500) inWin.push({ i, e, l: rep.histL[i], r: rep.histR[i] });
  }
  const sounding = inWin.filter((b) => b.r > SIL || b.l > SIL);
  if (!sounding.length) return { blocks_in_window: inWin.length, sounding_blocks: 0, first_sound_epoch: null };
  const first = sounding[0], last = sounding[sounding.length - 1];
  // Trous : blocs silencieux entre premier et dernier son.
  const gaps = [];
  let cur = null;
  for (const b of inWin) {
    if (b.e < first.e || b.e > last.e) continue;
    const silent = !(b.r > SIL || b.l > SIL);
    if (silent) { if (!cur) cur = { start_epoch: b.e, blocks: 0 }; cur.blocks++; }
    else if (cur) { gaps.push({ at_ms: r2(cur.start_epoch - tStartEpoch), ms: r2(cur.blocks * blockMs) }); cur = null; }
  }
  // Escalier de niveaux (gauche / droite) : rejoues et sauts.
  const seq = [];
  for (const b of sounding) {
    if (b.r < SIL) continue;
    const ratio = b.l / b.r;
    let best = 0, bd = Infinity;
    RATIOS.forEach((rr, k) => { const d = Math.abs(Math.log(ratio / rr)); if (d < bd) { bd = d; best = k; } });
    seq.push({ e: b.e, k: best });
  }
  const segs = [];
  for (const s of seq) {
    const sg = segs[segs.length - 1];
    if (sg && sg.k === s.k) { sg.n++; sg.end = s.e; }
    else segs.push({ k: s.k, n: 1, start: s.e, end: s.e });
  }
  const solid = segs.filter((s) => s.n >= 4); // >= ~21 ms : ignore les transitions
  const anomalies = [];
  for (let i = 1; i < solid.length; i++) {
    const a = solid[i - 1].k, b = solid[i].k;
    if (b !== (a + 1) % 8) anomalies.push({ at_ms: r2(solid[i].start - tStartEpoch), from: a, to: b, kind: b === (a + 7) % 8 || b === (a + 6) % 8 ? "replay" : "skip" });
  }
  const mean = (arr) => (arr.length ? arr.reduce((s, x) => s + x, 0) / arr.length : null);
  return {
    blocks_in_window: inWin.length, sounding_blocks: sounding.length,
    first_sound_epoch: first.e, last_sound_epoch: last.e,
    audible_s: r2((last.e - first.e) / 1000 + blockMs / 1000),
    gaps_count: gaps.length, gaps_total_ms: r2(gaps.reduce((s, g) => s + g.ms, 0)), gaps_max_ms: r2(Math.max(0, ...gaps.map((g) => g.ms))), gaps: gaps.slice(0, 40),
    level_segments: solid.length, anomalies_count: anomalies.length, replays: anomalies.filter((a) => a.kind === "replay").length, skips: anomalies.filter((a) => a.kind === "skip").length, anomalies: anomalies.slice(0, 40),
    mean_rms_right: r2(mean(sounding.map((b) => b.r))),
  };
}
async function collect(page, scen, clock0, tStartEpoch, tEndEpoch) {
  const rep = await page.evaluate(async () => {
    const r = await window.E5.audioReport();
    if (!r) return null;
    return { ...r, histL: Array.from(r.histL), histR: Array.from(r.histR), histT: Array.from(r.histT) };
  });
  const clock1 = await page.evaluate(() => window.E5.clockMap());
  const source = await page.evaluate(() => window.E5.sourceStats());
  if (source && source.sentFrames != null && source.rate) source.received_s = r2(source.sentFrames / source.rate);
  const audio = await page.evaluate(() => window.E5.audioState());
  const an = rep ? analyze(rep, clock1, tStartEpoch, tEndEpoch) : null;
  return {
    audio_context: audio,
    clock_drift_ms: r2((toEpoch(clock1, clock0.contextTime) - clock0.epoch)),
    worklet: rep && {
      sampleRate: rep.sampleRate, srcRate: rep.srcRate, nBlocks: rep.nBlocks, receivedCount: rep.receivedCount, receivedFrames: rep.receivedFrames,
      received_s_at_src_rate: r2(rep.receivedFrames / rep.srcRate),
      underrunFrames: rep.underrunFrames, underrun_ms: r2((rep.underrunFrames / rep.sampleRate) * 1000), underrunEventCount: rep.underrunEventCount, underrunEvents: rep.underrunEvents.slice(0, 30).map((u) => ({ at_ms: r2(toEpoch(clock1, u.t) - tStartEpoch), ms: r2((u.frames / rep.sampleRate) * 1000) })),
      pumpCount: rep.pumpCount, firstQueueTime_epoch: rep.firstQueueTime == null ? null : toEpoch(clock1, rep.firstQueueTime), firstSoundTime_epoch: rep.firstSoundTime == null ? null : toEpoch(clock1, rep.firstSoundTime),
    },
    source,
    analysis: an,
  };
}

// ---------- phases ----------
async function phaseMakeState(browser) {
  const ctx = await browser.newContext();
  const scen = { mode: "main" };
  const page = await openPage(ctx, scen);
  const t0 = Date.now();
  const marks = await waitMark(page, "shell-prompt", 10 * MIN);
  await waitMark(page, "snd-ready", 3 * MIN, ["snd-failed"]).catch(() => {});
  const all = await page.evaluate(() => ({ marks: window.E5.marks, info: window.E5.info, events: window.E5.events }));
  const st = await stats();
  const r = {
    page_to_prompt_s: sec(markOf(marks, "shell-prompt").t),
    page_to_boot_ready_s: sec(markOf(all.marks, "boot-ready")?.t),
    snd: markOf(all.marks, "snd-ready") ? "ready" : markOf(all.marks, "snd-failed") ? "failed" : "unknown",
    snd_info: all.info.map((i) => i.line),
    download_boot: st,
  };
  log("boot", JSON.stringify(r));
  if (r.snd !== "ready") throw new Error("snd-sb16 non chargé : " + JSON.stringify(all.info));
  const save = await page.evaluate(async () => { const s = await window.E5.saveState(); const n = await window.E5.uploadState("state-snd.bin", s.state); return { ms: s.ms, bytes: s.state.byteLength, uploaded: n }; });
  log("state", JSON.stringify(save));
  r.state = save;
  r.wall_s = sec(Date.now() - t0);
  await page.close();
  await ctx.close();
  return r;
}

async function startLoad(page, scen) {
  if (scen.load === "main") await page.evaluate(() => window.E5.mainLoad({ block: 300, period: 500, seconds: 14 }));
  else if (scen.load === "worker") await page.evaluate(() => window.E5.workerStall({ block: 300, period: 500, seconds: 14 }));
  else if (scen.load === "guest") await page.evaluate(() => window.E5.send("sh /root/sound.sh cpu 14 &\n"));
}

const PW_CONF = [
  "mkdir -p /etc/pipewire/pipewire.conf.d",
  "cat > /etc/pipewire/pipewire.conf.d/sb16.conf <<'EOF'",
  "context.properties = { default.clock.rate = 44100 default.clock.allowed-rates = [ 44100 ] default.clock.quantum = 2048 default.clock.min-quantum = 1024 default.clock.max-quantum = 4096 }",
  "context.objects = [",
  "  { factory = adapter",
  "    args = { factory.name = api.alsa.pcm.sink node.name = \"sb16\" node.description = \"SB16 v86\" media.class = \"Audio/Sink\"",
  "             api.alsa.path = \"plughw:0,0\" audio.format = \"S16LE\" audio.rate = 44100 audio.channels = 2 audio.position = \"FL,FR\" node.pause-on-idle = false } }",
  "]",
  "EOF",
  "echo MARK pw-conf-ok 0",
].join("\n");

async function phasePlayback(browser, context, name, scen) {
  const page = await openPage(context, scen, { state: STATE });
  const r = { scenario: name, ...scen };
  const started = await waitEvent(page, "emulator-started", 3 * MIN);
  r.page_to_emulator_started_s = sec(started.t);
  const alive = await guestAlive(page, "alive");
  r.page_to_guest_alive_s = sec(alive.t);
  // Geste (clic Playwright) -> AudioContext.resume().
  const stateBefore = await page.evaluate(() => window.E5.audioState());
  r.audio_before_gesture = stateBefore;
  if (!scen.lateGesture) { await page.click("#btn_audio"); await page.waitForFunction(() => window.E5.gesture, null, { timeout: 10000 }); r.gesture = await page.evaluate(() => window.E5.gesture); }
  await sleep(300);
  await page.evaluate(() => window.E5.resetMeter());
  // Base : CPU au repos (VM vivante, contexte audio actif, pas de lecture) pendant 6 s.
  const i0 = await cpuSample(browser);
  await sleep(6000);
  r.cpu_idle = cpuDelta(i0, await cpuSample(browser));
  const idleRep = await page.evaluate(async () => { const x = await window.E5.audioReport(); return x && { nBlocks: x.nBlocks, underrunFrames: x.underrunFrames, received: x.receivedCount }; });
  r.idle_worklet = idleRep;
  await page.evaluate(() => window.E5.resetMeter());
  if (scen.pipewire) {
    await page.evaluate((c) => window.E5.send(c + "\n"), PW_CONF);
    await waitMark(page, "pw-conf-ok", MIN);
  }
  const clock0 = await page.evaluate(() => window.E5.clockMap());
  const c0 = await cpuSample(browser);
  await startLoad(page, scen);
  const cmd = scen.pipewire ? "sh /root/sound.sh pipewire" : "sh /root/sound.sh aplay";
  await page.evaluate((c) => window.E5.send(c + "\n"), cmd);
  if (scen.lateGesture) {
    await sleep(3000); r.audio_during_late = await page.evaluate(() => window.E5.audioState());
    // Politique autoplay : resume() sans geste (course contre 1,5 s : Chrome laisse la promesse en attente quand il refuse).
    r.resume_without_gesture = await page.evaluate(() => Promise.race([window.E5.ctx.resume().then(() => ({ resolved: true, state: window.E5.ctx.state })), new Promise((res) => setTimeout(() => res({ resolved: false, state: window.E5.ctx.state }), 1500))])); r.late_rep_before = await page.evaluate(async () => { const x = await window.E5.audioReport(); return x && { nBlocks: x.nBlocks, received: x.receivedCount, now: x.now }; }); await page.click("#btn_audio"); await page.waitForFunction(() => window.E5.gesture, null, { timeout: 10000 }); r.gesture = await page.evaluate(() => window.E5.gesture); }
  const doneName = scen.pipewire ? "pw-done" : "aplay-done";
  const fails = scen.pipewire ? ["pw-failed", "pw-mute-failed"] : [];
  let marks;
  try { marks = await waitMark(page, doneName, 2 * MIN, fails); } catch (e) { r.timeout = String(e.message).slice(0, 100); marks = await page.evaluate(() => window.E5.marks); }
  if (scen.pipewire && !markOf(marks, "pw-done")) { try { marks = await waitMark(page, "pw-done", MIN); } catch { /* laisser */ } }
  await sleep(700);
  r.cpu_playback = cpuDelta(c0, await cpuSample(browser));
  const startName = scen.pipewire ? "pw-two-streams" : "aplay-start";
  const mStart = lastMark(marks, startName) || lastMark(marks, "pw-start");
  const mDone = lastMark(marks, doneName);
  const tStart = mStart ? mStart.epoch : clock0.nowEpoch;
  const tEnd = mDone ? mDone.epoch : Date.now();
  r.marks = marks.map((m) => ({ name: m.name, t: sec(m.t), uptime: m.uptime }));
  r.info = await page.evaluate(() => window.E5.info.map((i) => i.line));
  r.guest_play_s = mStart && mDone ? r2(mDone.uptime - mStart.uptime) : null;
  r.host_play_s = mStart && mDone ? r2((mDone.epoch - mStart.epoch) / 1000) : null;
  const col = await collect(page, scen, clock0, tStart, tEnd);
  Object.assign(r, col);
  if (col.analysis && col.analysis.first_sound_epoch && mStart) r.latency_mark_to_first_sound_ms = r2(col.analysis.first_sound_epoch - mStart.epoch);
  if (col.worklet && col.worklet.firstQueueTime_epoch && mStart) r.latency_mark_to_first_block_ms = r2(col.worklet.firstQueueTime_epoch - mStart.epoch);
  // Décomposition : marque série -> dac-enable (côté invité : aplay ouvre ALSA, snd-sb16 lance la DMA) -> premier bloc au worklet (pont) -> premier son rendu.
  const enables = (col.source && col.source.enableEpochs) || [];
  const fq = col.worklet && col.worklet.firstQueueTime_epoch;
  const en = fq ? enables.filter((e) => e <= fq + 5).pop() : null;
  if (en && mStart) { r.latency_mark_to_dac_enable_ms = r2(en - mStart.epoch); r.latency_dac_enable_to_first_block_ms = r2(fq - en); }
  if (col.analysis && col.analysis.first_sound_epoch && fq) r.latency_first_block_to_first_sound_ms = r2(col.analysis.first_sound_epoch - fq);
  if (col.worklet && col.analysis && col.analysis.first_sound_epoch) {
    const a = col.analysis.first_sound_epoch - tStart + 50, b = col.analysis.last_sound_epoch - tStart - 50;
    const inWin = col.worklet.underrunEvents.filter((u) => u.at_ms >= a && u.at_ms <= b);
    r.underrun_in_window = { count: inWin.length, total_ms: r2(inWin.reduce((s, u) => s + u.ms, 0)), max_ms: r2(Math.max(0, ...inWin.map((u) => u.ms))) };
  }
  if (scen.pipewire && col.worklet) {
    // RMS droite moyen par fenêtre : deux flux / B muet / B rétabli (B est un son de 220 Hz : les puissances s'ajoutent).
    const rep = await page.evaluate(async () => { const x = await window.E5.audioReport(); return { histR: Array.from(x.histR), histT: Array.from(x.histT) }; });
    const clock = await page.evaluate(() => window.E5.clockMap());
    // Moyenne du RMS droit sur les blocs sonores (> 0,02) entre deux instants (epoch ms).
    const winE = (a, b) => { const v = []; for (let i = 0; i < rep.histT.length; i++) { const e = toEpoch(clock, rep.histT[i]); if (e >= a && e <= b && rep.histR[i] > 0.02) v.push(rep.histR[i]); } return v.length ? r2(v.reduce((s, x) => s + x, 0) / v.length) : null; };
    const two = lastMark(marks, "pw-two-streams"), muted = lastMark(marks, "pw-muted-B"), un = lastMark(marks, "pw-unmuted-B"), done = lastMark(marks, "pw-done");
    const E = (m) => (m ? m.epoch : 0);
    r.pipewire_rms_right = {
      two_streams: winE(E(two) + 500, E(muted) - 100), B_muted: winE(E(muted) + 500, E(un) - 100), B_unmuted: winE(E(un) + 500, E(done)),
      before_mute_1s: winE(E(muted) - 1100, E(muted) - 100), after_mute_1s: winE(E(muted) + 300, E(muted) + 1300),
      mute_mark_to_unmute_s: muted && un ? r2((un.epoch - muted.epoch) / 1000) : null,
      launch_to_first_sound_s: col.analysis && col.analysis.first_sound_epoch && two ? r2((col.analysis.first_sound_epoch - (two.epoch - 2000)) / 1000) : null, // pw-play lancé 2 s (sleep) avant la marque
    };
  }
  r.download = await stats();
  log(name, JSON.stringify({ gesture: r.gesture, lat_ms: r.latency_mark_to_first_sound_ms, audible_s: r.analysis?.audible_s, gaps: r.analysis?.gaps_count, gap_max: r.analysis?.gaps_max_ms, replays: r.analysis?.replays, skips: r.analysis?.skips, underrun_win: r.underrun_in_window, received_s: r.source?.received_s, lat_guest: r.latency_mark_to_dac_enable_ms, lat_bridge: r.latency_dac_enable_to_first_block_ms, cpu_idle: r.cpu_idle?.renderer_pct, cpu_play: r.cpu_playback?.renderer_pct, pw: r.pipewire_rms_right, timeout: r.timeout }));
  await page.close();
  return r;
}

// ---------- boucle principale ----------
const browser = await chromium.launch({ channel: CHANNEL, headless: HEADLESS });
const version = browser.version();
log("navigateur", CHANNEL, version, HEADLESS ? "headless" : "headed", "mem", MEM, "scénarios", SCEN_LIST.join(","));

let boot = null;
if (!(args["reuse-state"] && fs.existsSync(path.join(ROOT, "images", STATE)))) {
  boot = await phaseMakeState(browser);
  fs.writeFileSync(path.join(ROOT, "results-boot.json"), JSON.stringify(boot, null, 2));
}

for (let n = FIRST; n < FIRST + RUNS; n++) {
  const result = { run: n, date: new Date().toISOString(), browser: { channel: CHANNEL, version, headless: HEADLESS }, memory_size_MiB: MEM, v86: "0.5.473 (npm, commit 315b2b0)", boot, scenarios: {} };
  const context = await browser.newContext();
  for (const name of SCEN_LIST) {
    const scen = SCENARIOS[name];
    if (!scen) { log("scénario inconnu", name); continue; }
    try {
      result.scenarios[name] = await phasePlayback(browser, context, name, scen);
    } catch (e) {
      result.scenarios[name] = { scenario: name, ...scen, error: String(e.stack || e) };
      log("ERREUR", name, e.message);
    }
    fs.writeFileSync(path.join(ROOT, `results-${n}.json`), JSON.stringify(result, null, 2));
  }
  await context.close();
  log(`results-${n}.json écrit`);
}
await browser.close();
server.kill();
process.exit(0);
