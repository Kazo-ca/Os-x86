// Essai E4 — pilote de mesure (Playwright + Chrome installé, headless, profil temporaire).
// Usage : node run.mjs [--runs 2] [--first 1] [--port 8094] [--headed] [--hz 20] [--poll 50]
// Pour chaque run n :
//   1. contexte neuf -> boot à froid de l'image E4 (images/alpine-fs.json) jusqu'au prompt ;
//   2. sh /root/demo.sh composite : Xorg (sans WM), xterm + xclock + xeyes + xmessage (transient), agent ;
//   3. pour chaque approche (composite = agent XComposite/XGetImage ; crop = découpage du canvas VGA) :
//      repos (horloge seule), latence écriture -> affichage (x10), déplacement, recouvrement, charge (xterm qui défile) ;
//   4. results-<n>.json + captures images/e4-run<n>-*.png.
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const RUNS = Number(args.runs || 2);
const FIRST = Number(args.first || 1);
const PORT = Number(args.port || 8094);
const HZ = Number(args.hz || 20);
const POLL = Number(args.poll || 50);
const HEADLESS = !args.headed;
const BASE = `http://localhost:${PORT}`;
const MIN = 60_000;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
process.on("exit", () => server.kill());
await sleep(500);

// ---------- aides ----------
async function waitMark(page, name, timeoutMs, failNames = []) {
  await page.waitForFunction(([n, fails]) => window.E4.marks.some((m) => m.name === n || fails.includes(m.name)), [name, failNames], { timeout: timeoutMs, polling: 200 });
  return page.evaluate(() => window.E4.marks);
}
const send = (page, text) => page.evaluate((t) => window.E4.send(t + "\n"), text);
async function serialCmd(page, text, markName, timeoutMs = 2 * MIN) {
  // Envoie une commande et attend "MARK <markName> <uptime>" ; renvoie la sortie série entre les deux.
  const start = await page.evaluate(() => window.E4.serial.length);
  await send(page, `${text}; echo MARK ${markName} $(cut -d' ' -f1 /proc/uptime)`);
  const marks = await waitMark(page, markName, timeoutMs);
  const out = await page.evaluate((s) => window.E4.serial.slice(s), start);
  return { mark: marks.find((m) => m.name === markName), out };
}
const sec = (ms) => (ms == null ? null : Math.round(ms) / 1000);
const r1 = (x) => (x == null || Number.isNaN(x) ? null : Math.round(x * 10) / 10);
const stats = (arr) => {
  const a = arr.filter((x) => x != null).sort((x, y) => x - y);
  if (!a.length) return { n: 0 };
  return { n: a.length, min: r1(a[0]), median: r1(a[Math.floor(a.length / 2)]), mean: r1(a.reduce((s, x) => s + x, 0) / a.length), max: r1(a[a.length - 1]) };
};
async function windowsOf(page) { return page.evaluate(() => window.E4.windowList()); }
function findWin(wins, cls) { return wins.find((w) => w.class && w.class.toLowerCase().includes(cls)); }
async function summary(page) {
  return page.evaluate(() => {
    const st = window.E4.stats; const dt = (performance.now() - st.t_start) / 1000; const g = window.E4.guestCpu();
    return { mode: st.mode, poll_ms: st.pollMs, duration_s: Math.round(dt * 10) / 10, polls: st.polls, json_err: st.json_err, seq_applied: st.seq_applied,
      page_handler_ms_per_s: Math.round(st.handler_ms / dt * 10) / 10, page_read_ms_per_s: Math.round(st.read_ms / dt * 10) / 10, page_draw_ms_per_s: Math.round(st.draw_ms / dt * 10) / 10, page_detect_ms_per_s: Math.round(st.detect_ms / dt * 10) / 10,
      frames_applied: st.frames_applied, frames_per_s: Math.round(st.frames_applied / dt * 10) / 10, bytes_read: st.bytes_read, bytes_per_s: Math.round(st.bytes_read / dt),
      frames_by_window: st.frames_by_window, guest_cpu: g, agent_capture: st.last_json?.capture, agent_capture_first: st.first_json?.capture, changes_seen: window.E4.changes.length };
  });
}
async function guestCpuBaseline(page, seconds = 10) {
  const { out } = await serialCmd(page, `head -1 /proc/stat; sleep ${seconds}; head -1 /proc/stat`, "cpu-base", MIN);
  const lines = out.split("\n").filter((l) => l.startsWith("cpu ")).map((l) => l.trim().split(/\s+/).slice(1, 8).map(Number));
  if (lines.length < 2) return null;
  const sum = (s) => s.reduce((x, y) => x + y, 0);
  const total = sum(lines[1]) - sum(lines[0]); const idle = lines[1][3] + lines[1][4] - lines[0][3] - lines[0][4];
  return { seconds, busy_pct: r1(100 * (total - idle) / total) };
}

// ---------- phases de mesure ----------
async function latencyTest(page, xtermId, n = 10, tag = "c") {
  // Écrit une ligne directement dans le pty de l'xterm (sans passer par X11 input), horodate :
  //  t_send  : envoi de la commande série (hôte) ; t_mark : réception du MARK imprimé juste après l'écriture ;
  //  t_seen  : la toile de la fenêtre xterm a changé dans la page. Aussi : horodatages invité de l'agent (dommage, capture).
  const res = [];
  for (let i = 0; i < n; i++) {
    const before = await page.evaluate(() => window.E4.changes.length);
    const tSend = await page.evaluate(() => performance.now());
    await send(page, `printf 'ligne %s\\n' ${i} > $(cat /root/e4-pty); echo MARK chg${tag}${i} $(cut -d' ' -f1 /proc/uptime)`);
    let seen = null;
    try {
      await page.waitForFunction(([id, b]) => window.E4.changes.slice(b).some((c) => c.id === id && c.seq != null), [xtermId, before], { timeout: 8000, polling: 10 });
      seen = await page.evaluate(([id, b]) => window.E4.changes.slice(b).find((c) => c.id === id && c.seq != null), [xtermId, before]);
    } catch { /* non vu */ }
    const marks = await page.evaluate(() => window.E4.marks);
    const mark = marks.find((m) => m.name === `chg${tag}${i}`);
    const agent = await page.evaluate((id) => { const w = window.E4.windows.get(id); return w?.lastInfo || null; }, xtermId);
    res.push({ i, send_to_seen_ms: seen ? r1(seen.t - tSend) : null, mark_to_seen_ms: seen && mark ? r1(seen.t - mark.t) : null, send_to_mark_ms: mark ? r1(mark.t - tSend) : null,
      guest_damage_to_capture_ms: agent && agent.damage_t && agent.capture_t ? r1(agent.capture_t - agent.damage_t) : null, mark_uptime: mark?.uptime, agent_damage_t: agent?.damage_t });
    await sleep(600);
  }
  return { samples: res, send_to_seen: stats(res.map((r) => r.send_to_seen_ms)), mark_to_seen: stats(res.map((r) => r.mark_to_seen_ms)), send_to_mark: stats(res.map((r) => r.send_to_mark_ms)), guest_damage_to_capture: stats(res.map((r) => r.guest_damage_to_capture_ms)), missed: res.filter((r) => r.send_to_seen_ms == null).length };
}
async function moveTest(page, id, x, y) {
  const before = await page.evaluate(() => window.E4.changes.length);
  await page.evaluate(([id, x, y]) => window.E4.moveWindow(id, x, y), [id, x, y]);
  try {
    await page.waitForFunction(([id, b]) => window.E4.changes.slice(b).some((c) => c.id === id && c.move_ms != null), [id, before], { timeout: 8000, polling: 20 });
    return (await page.evaluate(([id, b]) => window.E4.changes.slice(b).find((c) => c.id === id && c.move_ms != null), [id, before])).move_ms;
  } catch { return null; }
}
async function dominantColor(page, id) {
  // Couleur la plus fréquente de la toile d'une fenêtre (fond de l'horloge, tel que réellement rendu).
  return page.evaluate((id) => {
    const w = window.E4.windows.get(id); if (!w) return null;
    const d = w.ctx.getImageData(0, 0, w.w, w.h).data; const m = new Map();
    for (let i = 0; i < d.length; i += 4) { const k = (d[i] << 16) | (d[i + 1] << 8) | d[i + 2]; m.set(k, (m.get(k) || 0) + 1); }
    const [k, n] = [...m.entries()].sort((a, b) => b[1] - a[1])[0];
    return { rgb: [k >> 16, (k >> 8) & 255, k & 255], count: n, total: d.length / 4 };
  }, id);
}
async function purpleCount(page, id, rgb) {
  // Pixels de la couleur de fond de l'horloge dans la toile de la fenêtre donnée : preuve de recouvrement ou non.
  return page.evaluate(([id, rgb]) => {
    const w = window.E4.windows.get(id); if (!w) return null;
    const d = w.ctx.getImageData(0, 0, w.w, w.h).data; let n = 0;
    for (let i = 0; i < d.length; i += 4) if (d[i] === rgb[0] && d[i + 1] === rgb[1] && d[i + 2] === rgb[2]) n++;
    return { purple: n, total: d.length / 4 };
  }, [id, rgb]);
}
async function measureMode(page, mode, n, wins0) {
  const xterm = findWin(wins0, "xterm"), clock = findWin(wins0, "xclock");
  const r = { mode };
  await page.evaluate(([m, p]) => window.E4.start(m, p), [mode, POLL]);
  await sleep(3000);
  await page.locator("#desk").screenshot({ path: path.join(ROOT, "images", `e4-run${n}-${mode}.png`) });
  // repos : seule l'horloge change (1 Hz)
  await page.evaluate(() => window.E4.resetStats());
  await sleep(20000);
  r.idle = await summary(page);
  // latence
  await page.evaluate(() => window.E4.resetStats());
  r.latency = await latencyTest(page, xterm.id, 10, mode);  // nom de marque propre au mode ("composite"/"crop" : la 1re lettre ne suffit pas)
  // déplacement de l'xterm : la div bouge, xdotool windowmove, l'agent confirme la nouvelle géométrie
  r.move_ms = [await moveTest(page, xterm.id, 300, 400), await moveTest(page, xterm.id, 40, 40)];
  // recouvrement : l'horloge posée sur l'xterm ; une ligne écrite ; la toile de l'xterm contient-elle l'horloge ?
  await moveTest(page, clock.id, xterm.x + 60, xterm.y + 40);
  await sleep(500);
  await serialCmd(page, `printf 'recouvrement\\n' > $(cat /root/e4-pty); sleep 1`, "ovl-" + mode, MIN);
  await sleep(1500);
  const geo = await page.evaluate((id) => window.E4.windows.get(id)?.info, xterm.id);
  const clockGeo = await page.evaluate((id) => window.E4.windows.get(id)?.info, clock.id);
  const dom = await dominantColor(page, clock.id);
  r.overlap = { clock_color: dom, xterm_canvas: await purpleCount(page, xterm.id, dom.rgb), clock_canvas: await purpleCount(page, clock.id, dom.rgb),
    vga_in_xterm_rect: await page.evaluate(([g, c]) => window.E4.screenCount(g.x, g.y, g.w, g.h, c), [geo, dom.rgb]), xterm_geometry: geo, clock_geometry: clockGeo };
  await page.locator("#desk").screenshot({ path: path.join(ROOT, "images", `e4-run${n}-${mode}-overlap.png`) });
  await moveTest(page, clock.id, clock.x, clock.y);
  await sleep(500);
  // charge : 15 s de texte qui défile dans l'xterm (+ horloge)
  await page.evaluate(() => window.E4.resetStats());
  await send(page, `timeout 15 sh -c 'i=0; while :; do i=$((i+1)); echo "defilement $i $(cut -d" " -f1 /proc/uptime)"; done' > $(cat /root/e4-pty) 2>/dev/null &`);
  await sleep(16000);
  r.load = await summary(page);
  r.windows = await windowsOf(page);
  await page.evaluate(() => window.E4.stop());
  return r;
}

// ---------- boucle principale ----------
for (let n = FIRST; n < FIRST + RUNS; n++) {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "e4-run-"));
  const context = await chromium.launchPersistentContext(profile, { channel: "chrome", headless: HEADLESS, viewport: { width: 1400, height: 1900 } });
  const result = { run: n, date: new Date().toISOString(), browser: { channel: "chrome", version: context.browser()?.version(), headless: HEADLESS }, agent_hz: HZ, page_poll_ms: POLL, v86: "0.5.473 (npm)" };
  try {
    const page = await context.newPage();
    page.on("pageerror", (e) => log("pageerror", e.message));
    page.on("crash", () => log("CRASH renderer"));
    const tNav = Date.now();
    await page.goto(`${BASE}/boot.html?fs=alpine-fs.json`);
    const marks = await waitMark(page, "shell-prompt", 10 * MIN);
    result.boot = { nav_to_prompt_s: sec(Date.now() - tNav), page_to_prompt_s: sec(marks.find((m) => m.name === "shell-prompt").t) };
    log("boot", JSON.stringify(result.boot));
    // Injection par 9p (emu.create_file) du demo.sh courant et de l'agent recompilé (images/e4agent, voir build-agent.ps1) :
    // l'image 9p n'a pas été reconstruite après les dernières corrections (garde-fou disque < 4 Go).
    const tInj = Date.now();
    await page.evaluate(([p, t]) => window.E4.writeFile(p, t), ["/root/demo-e4.sh", fs.readFileSync(path.join(ROOT, "guest", "demo.sh"), "utf8")]);
    await page.evaluate(([p, b]) => window.E4.writeBytesB64(p, b), ["/root/e4agent", fs.readFileSync(path.join(ROOT, "images", "e4agent")).toString("base64")]);
    await serialCmd(page, "chmod +x /root/e4agent /root/demo-e4.sh; ls -la /root/e4agent", "injected", MIN);
    result.inject_s = sec(Date.now() - tInj);
    await send(page, `AGENT=/root/e4agent sh /root/demo-e4.sh composite ${HZ}`);
    const m2 = await waitMark(page, "agent-started", 8 * MIN, ["x-failed"]);
    const t = (a, b) => { const ma = m2.find((m) => m.name === a), mb = m2.find((m) => m.name === b); return ma && mb ? sec(mb.t - ma.t) : null; };
    result.demo = { x_ready_s: t("demo-start", "x-ready"), xterm_sized_s: t("x-ready", "xterm-sized"), clients_s: t("x-ready", "clients-ready"), agent_s: t("clients-ready", "agent-started") };
    log("demo", JSON.stringify(result.demo));
    // CPU invité sans agent en marche ? L'agent est déjà lancé par demo.sh : on le tue pour la base, puis on le relance.
    await serialCmd(page, "pkill e4agent; sleep 1", "agent-killed", MIN);
    result.guest_cpu_baseline_no_agent = await guestCpuBaseline(page, 10);
    log("cpu base", JSON.stringify(result.guest_cpu_baseline_no_agent));
    await serialCmd(page, `/root/e4agent -m composite -z ${HZ} -o /root/e4 2>>/root/e4agent.log & sleep 1`, "agent2", MIN);
    await page.evaluate(([m, p]) => window.E4.start(m, p), ["composite", POLL]);
    await sleep(4000);
    const wins0 = await windowsOf(page);
    result.windows_initial = wins0;
    log("fenêtres", wins0.map((w) => `${w.id} ${w.class} "${w.name}" ${w.w}x${w.h}+${w.x}+${w.y}${w.transient_for ? " transient→" + w.transient_for : ""}${w.type ? " " + w.type : ""}`).join(" | "));
    result.composite = await measureMode(page, "composite", n, wins0);
    log("composite", JSON.stringify({ idle: result.composite.idle, latency: result.composite.latency.send_to_seen, load: result.composite.load, overlap: result.composite.overlap }));
    // agent en mode géométrie (pas de redirection ni de capture), page en découpage
    await serialCmd(page, `pkill e4agent; sleep 0.5; /root/e4agent -m geometry -z ${HZ} -o /root/e4 2>>/root/e4agent.log & sleep 1`, "agent-geom", MIN);
    result.crop = await measureMode(page, "crop", n, wins0);
    log("crop", JSON.stringify({ idle: result.crop.idle, latency: result.crop.latency.send_to_seen, load: result.crop.load, overlap: result.crop.overlap }));
    // charge sans agent : même boucle de 15 s, agent tué, /proc/stat avant/après (part de la charge elle-même et de X)
    await serialCmd(page, "pkill e4agent; sleep 0.5", "agent-off", MIN);
    {
      const { out } = await serialCmd(page, `head -1 /proc/stat; timeout 15 sh -c 'i=0; while :; do i=$((i+1)); echo "defilement $i $(cut -d" " -f1 /proc/uptime)"; done' > $(cat /root/e4-pty) 2>/dev/null; head -1 /proc/stat`, "load-base", 2 * MIN);
      const lines = out.split("\n").filter((l) => l.startsWith("cpu ")).map((l) => l.trim().split(/\s+/).slice(1, 8).map(Number));
      if (lines.length >= 2) { const sum = (s) => s.reduce((x, y) => x + y, 0); const total = sum(lines[1]) - sum(lines[0]); const idle = lines[1][3] + lines[1][4] - lines[0][3] - lines[0][4]; result.guest_cpu_load_no_agent = { busy_pct: r1(100 * (total - idle) / total), ticks: total }; }
    }
    result.agent_log = (await serialCmd(page, "cat /root/e4agent.log | tail -5", "alog", MIN)).out;
    const png = await page.evaluate(() => window.E4.screenPng());
    if (png) fs.writeFileSync(path.join(ROOT, "images", `e4-run${n}-vga.png`), Buffer.from(png.split(",")[1], "base64"));
  } catch (e) {
    result.error = String(e.stack || e);
    log("ERREUR run", n, e.message);
  }
  fs.writeFileSync(path.join(ROOT, `results-${n}.json`), JSON.stringify(result, null, 2));
  log(`results-${n}.json écrit`);
  await context.close();
  fs.rmSync(profile, { recursive: true, force: true });
}
server.kill();
process.exit(0);
