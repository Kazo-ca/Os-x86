// Essai E1 — pilote de mesure (Playwright + Chrome/Edge installé).
// Usage : node run.mjs [--runs 2] [--first 1] [--xmode xvfb|xorg] [--channel chrome|msedge] [--headed] [--mem 1024] [--port 8086]
// Pour chaque run n :
//   1. contexte neuf (cache vide)  -> boot "froid" jusqu'au prompt shell ;
//   2. bench invité (X, wineboot -u, wine cmd, notepad) ;
//   3. save_state() (taille, durée), envoi au serveur, restore_state() en page ;
//   4. rechargement depuis l'état (cache chaud) -> temps jusqu'à "Notepad vivant" ;
//   5. contexte neuf (cache vide) depuis l'état -> idem, à froid ;
//   6. rechargement simple (cache chaud) -> boot jusqu'au prompt.
// Écrit results-<n>.json à côté du script.
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(
  process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length),
);
const RUNS = Number(args.runs || 2);
const XMODE = args.xmode || "xvfb";
const CHANNEL = args.channel || "chrome";
const HEADLESS = !args.headed;
const MEM = Number(args.mem || 1024);
const PORT = Number(args.port || 8086);
const BASE = `http://localhost:${PORT}`;
const MIN = 60_000;

const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- serveur ----------
const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
process.on("exit", () => server.kill());
await sleep(500);
const stats = async () => (await fetch(`${BASE}/__stats`)).json();
const resetStats = () => fetch(`${BASE}/__reset`);

// ---------- aides ----------
function pageUrl(extra = {}) {
  const u = new URL(`${BASE}/boot.html`);
  u.searchParams.set("mem", String(MEM));
  for (const [k, v] of Object.entries(extra)) u.searchParams.set(k, v);
  return u.toString();
}
async function waitMark(page, name, timeoutMs, failNames = []) {
  await page.waitForFunction(
    ([n, fails]) => window.E1.marks.some((m) => m.name === n || fails.includes(m.name)),
    [name, failNames],
    { timeout: timeoutMs, polling: 250 },
  );
  return page.evaluate(() => window.E1.marks);
}
async function waitEvent(page, name, timeoutMs) {
  await page.waitForFunction((n) => window.E1.events.some((e) => e.name === n), name, { timeout: timeoutMs, polling: 100 });
}
async function marksOf(page) {
  return page.evaluate(() => ({ marks: window.E1.marks, events: window.E1.events, info: window.E1.info }));
}
function sec(ms) { return ms == null ? null : Math.round(ms) / 1000; }
function delta(marks, a, b) {
  const ma = marks.find((m) => m.name === a);
  const mb = marks.find((m) => m.name === b);
  return ma && mb ? { host_s: sec(mb.t - ma.t), guest_s: Math.round((mb.uptime - ma.uptime) * 100) / 100 } : null;
}

async function processMemory(browser) {
  // Mémoire des processus du navigateur (Windows) : somme des working sets + plus gros processus (renderer).
  try {
    const cdp = await browser.newBrowserCDPSession();
    const { processInfo } = await cdp.send("SystemInfo.getProcessInfo");
    await cdp.detach();
    const ids = processInfo.map((p) => p.id);
    const out = execFileSync("pwsh", ["-NoProfile", "-Command",
      `Get-Process -Id ${ids.join(",")} -ErrorAction SilentlyContinue | Select-Object Id,WorkingSet64,PrivateMemorySize64 | ConvertTo-Json -Compress`], { encoding: "utf8" });
    const procs = JSON.parse(out || "[]");
    const list = (Array.isArray(procs) ? procs : [procs]).map((p) => ({
      pid: p.Id, type: processInfo.find((x) => x.id === p.Id)?.type,
      ws_MiB: Math.round(p.WorkingSet64 / 1048576), private_MiB: Math.round(p.PrivateMemorySize64 / 1048576),
    }));
    const renderers = list.filter((p) => p.type === "renderer");
    return {
      total_ws_MiB: list.reduce((s, p) => s + p.ws_MiB, 0),
      total_private_MiB: list.reduce((s, p) => s + p.private_MiB, 0),
      biggest_renderer_private_MiB: Math.max(0, ...renderers.map((p) => p.private_MiB)),
      processes: list,
    };
  } catch (e) {
    return { error: String(e) };
  }
}
async function pageMemory(page) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("Performance.enable");
  const { metrics } = await cdp.send("Performance.getMetrics");
  await cdp.detach();
  const pick = (n) => metrics.find((m) => m.name === n)?.value;
  const ua = await page.evaluate(() => window.E1.measureMemory());
  return {
    jsHeapUsed_MiB: Math.round(pick("JSHeapUsedSize") / 1048576),
    jsHeapTotal_MiB: Math.round(pick("JSHeapTotalSize") / 1048576),
    measureUserAgentSpecificMemory_MiB: ua.bytes ? Math.round(ua.bytes / 1048576) : ua,
  };
}
async function guestMem(page, tag) {
  // Demande au shell invité l'état mémoire ; attend la ligne MEM <tag>.
  await page.evaluate((t) => window.E1.send(`echo "MEM ${t} $(awk '/MemTotal/{t=$2}/MemAvailable/{a=$2}END{print "total_kb=" t " avail_kb=" a " used_kb=" t-a}' /proc/meminfo)"\n`), tag);
  await page.waitForFunction((t) => window.E1.info.some((i) => i.line.startsWith("MEM " + t)), tag, { timeout: 2 * MIN });
  const info = await page.evaluate(() => window.E1.info);
  return info.filter((i) => i.line.startsWith("MEM " + tag)).pop()?.line;
}
async function pingGuest(page, name, timeoutMs = 5 * MIN) {
  // Envoie une commande qui imprime un MARK quand Notepad est trouvé (ou "notepad-missing").
  await page.evaluate((n) => window.E1.send(`\nif DISPLAY=:0 xdotool search --onlyvisible --name Notepad >/dev/null 2>&1; then echo "MARK ${n} $(cut -d' ' -f1 /proc/uptime)"; else echo "MARK notepad-missing 0"; fi\n`), name);
  const marks = await waitMark(page, name, timeoutMs, ["notepad-missing"]);
  return marks.find((m) => m.name === name) || marks.find((m) => m.name === "notepad-missing");
}

// ---------- phases ----------
async function phaseBoot(context, label, extra = {}) {
  await resetStats();
  const page = await context.newPage();
  page.on("pageerror", (e) => log("pageerror", e.message));
  page.on("console", (m) => { if (m.type() === "error") log("console.error", m.text().slice(0, 200)); });
  const tNav = Date.now();
  await page.goto(pageUrl(extra));
  const marks = await waitMark(page, "shell-prompt", 30 * MIN);
  const { events } = await marksOf(page);
  const st = await stats();
  const prompt = marks.find((m) => m.name === "shell-prompt");
  const bootReady = marks.find((m) => m.name === "boot-ready");
  const r = {
    label,
    nav_to_prompt_s: sec(Date.now() - tNav),
    page_to_prompt_s: sec(prompt.t),
    page_to_boot_ready_s: bootReady ? sec(bootReady.t) : null,
    guest_uptime_at_boot_ready_s: bootReady?.uptime ?? null,
    emulator_ready_s: sec(events.find((e) => e.name === "emulator-ready")?.t),
    emulator_started_s: sec(events.find((e) => e.name === "emulator-started")?.t),
    download: st,
  };
  log(label, JSON.stringify(r));
  return { page, result: r };
}

async function phaseBench(page) {
  await resetStats();
  await page.evaluate((m) => window.E1.bench(m), XMODE);
  const t0 = Date.now();
  await waitMark(page, "bench-done", 120 * MIN, ["x-failed", "notepad-failed"]);
  // si échec partiel, laisser le script finir quand même (il continue après les MARK *-failed)
  await waitMark(page, "bench-done", 60 * MIN).catch(() => {});
  const { marks, info } = await marksOf(page);
  const st = await stats();
  const r = {
    wall_s: sec(Date.now() - t0),
    x_ready: delta(marks, "bench-start", "x-ready"),
    wineboot: delta(marks, "wineboot-start", "wineboot-done"),
    cmd_echo: delta(marks, "cmd-start", "cmd-done"),
    notepad_visible: delta(marks, "notepad-start", "notepad-visible"),
    notepad_typed: delta(marks, "notepad-visible", "notepad-typed"),
    failed: marks.filter((m) => m.name.endsWith("-failed")).map((m) => m.name),
    info: info.map((i) => i.line),
    download: st,
    marks,
  };
  log("bench", JSON.stringify({ ...r, info: undefined, marks: undefined }));
  return r;
}

function crashGuard(page) {
  // Un plantage du renderer ne rejette pas toujours page.evaluate : on le transforme en erreur.
  return new Promise((_, reject) => page.once("crash", () => reject(new Error("page crashed (renderer)"))));
}
async function phaseState(page, browser) {
  const crashed = crashGuard(page);
  const save = await Promise.race([page.evaluate(() => window.E1.saveState()), crashed]);
  log("save_state", JSON.stringify(save));
  const uploaded = await Promise.race([page.evaluate(() => window.E1.uploadState("state.bin")), crashed]);
  log("upload", uploaded, "octets");
  const restore = await Promise.race([page.evaluate(() => window.E1.restoreState()), crashed]);
  log("restore_state (en page)", JSON.stringify(restore));
  const alive = await Promise.race([pingGuest(page, "alive-after-restore"), crashed]);
  const mem = { page: await pageMemory(page), browser: await processMemory(browser) };
  return { save, uploaded_bytes: Number(uploaded), restore_in_page: restore, alive_after_restore: alive, memory: mem };
}

async function phaseFromState(context, label) {
  await resetStats();
  const page = await context.newPage();
  const tNav = Date.now();
  await page.goto(pageUrl({ state: "state.bin" }));
  await waitEvent(page, "emulator-started", 20 * MIN);
  const { events } = await marksOf(page);
  const started = events.find((e) => e.name === "emulator-started");
  const alive = await pingGuest(page, "notepad-alive");
  const st = await stats();
  const r = {
    label,
    page_to_emulator_started_s: sec(started.t),
    page_to_notepad_alive_s: alive ? sec(alive.t) : null,
    notepad_found: alive?.name === "notepad-alive",
    nav_to_alive_s: sec(Date.now() - tNav),
    download: st,
  };
  log(label, JSON.stringify(r));
  await page.close();
  return r;
}

// ---------- boucle principale ----------
const browser = await chromium.launch({ channel: CHANNEL, headless: HEADLESS });
const version = browser.version();
log("navigateur", CHANNEL, version, HEADLESS ? "headless" : "headed", "xmode", XMODE, "mem", MEM);

const FIRST = Number(args.first || 1);
for (let n = FIRST; n < FIRST + RUNS; n++) {
  const result = { run: n, date: new Date().toISOString(), browser: { channel: CHANNEL, version, headless: HEADLESS }, xmode: XMODE, memory_size_MiB: MEM, v86: "0.5.473 (npm)" };
  try {
    const cold = await browser.newContext();
    const { page, result: boot } = await phaseBoot(cold, "cold-boot");
    result.cold_boot = boot;
    result.memory_after_boot = { page: await pageMemory(page), browser: await processMemory(browser) };
    result.bench = await phaseBench(page);
    if (XMODE === "xorg") await page.locator("#screen_container canvas").screenshot({ path: path.join(ROOT, "images", `screen-run${n}.png`) }).catch((e) => log("screenshot", e.message));
    result.state = await phaseState(page, browser);
    result.from_state_warm = await phaseFromState(cold, "from-state-warm");
    const { page: p2, result: warm } = await phaseBoot(cold, "warm-boot");
    result.warm_boot = warm;
    await p2.close();
    await page.close();
    await cold.close();
    const cold2 = await browser.newContext();
    result.from_state_cold = await phaseFromState(cold2, "from-state-cold");
    await cold2.close();
  } catch (e) {
    result.error = String(e.stack || e);
    log("ERREUR run", n, e.message);
  }
  fs.writeFileSync(path.join(ROOT, `results-${n}.json`), JSON.stringify(result, null, 2));
  log(`results-${n}.json écrit`);
}
await browser.close();
server.kill();
process.exit(0);
