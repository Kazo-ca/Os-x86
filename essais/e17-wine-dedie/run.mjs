// Essai E17 — mesure automatique (Playwright + Chrome installé, headless ; dérivé de essais/e1-wine-v86/run.mjs).
// Usage : node run.mjs [--runs 2] [--first 1] [--fs stable-fs.json] [--flat rootfs-flat] [--xmode xorg] [--mem 1024] [--port 8134] [--headed]
// Pour chaque run n, dans un contexte Chrome neuf (cache vide) :
//   1. boot à froid jusqu'au prompt shell ;
//   2. bench invité (Xorg, twm, wine cmd, puis notepad / winecfg / winemine avec géométrie et frappe) ;
//   3. capture du canvas VGA (images/e17-run<n>.png), mémoire (page, processus Chrome), save_state (taille, durée).
// Écrit results-<n>.json à côté du script.
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const RUNS = Number(args.runs || 2);
const FIRST = Number(args.first || 1);
const FS = args.fs || "stable-fs.json";
const FLAT = args.flat || "rootfs-flat";
const XMODE = args.xmode || "xorg";
const MEM = Number(args.mem || 1024);
const PORT = Number(args.port || 8134);
const HEADLESS = !args.headed;
const BASE = `http://localhost:${PORT}`;
const MIN = 60_000;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
process.on("exit", () => server.kill());
await sleep(500);
const stats = async () => (await fetch(`${BASE}/__stats`)).json();
const resetStats = () => fetch(`${BASE}/__reset`);

function pageUrl() {
  const u = new URL(`${BASE}/boot.html`);
  u.searchParams.set("fs", FS); u.searchParams.set("flat", FLAT); u.searchParams.set("mem", String(MEM));
  return u.toString();
}
async function waitMark(page, name, timeoutMs, failNames = []) {
  await page.waitForFunction(([n, fails]) => window.E17.marks.some((m) => m.name === n || fails.includes(m.name)), [name, failNames], { timeout: timeoutMs, polling: 250 });
  return page.evaluate(() => window.E17.marks);
}
const sec = (ms) => (ms == null ? null : Math.round(ms) / 1000);
function delta(marks, a, b) {
  const ma = marks.find((m) => m.name === a), mb = marks.find((m) => m.name === b);
  return ma && mb ? { host_s: sec(mb.t - ma.t), guest_s: Math.round((mb.uptime - ma.uptime) * 100) / 100 } : null;
}
async function processMemory(browser) {
  try {
    const cdp = await browser.newBrowserCDPSession();
    const { processInfo } = await cdp.send("SystemInfo.getProcessInfo");
    await cdp.detach();
    const ids = processInfo.map((p) => p.id);
    const out = execFileSync("pwsh", ["-NoProfile", "-Command", `Get-Process -Id ${ids.join(",")} -ErrorAction SilentlyContinue | Select-Object Id,WorkingSet64,PrivateMemorySize64 | ConvertTo-Json -Compress`], { encoding: "utf8" });
    const procs = JSON.parse(out || "[]");
    const list = (Array.isArray(procs) ? procs : [procs]).map((p) => ({ pid: p.Id, type: processInfo.find((x) => x.id === p.Id)?.type, ws_MiB: Math.round(p.WorkingSet64 / 1048576), private_MiB: Math.round(p.PrivateMemorySize64 / 1048576) }));
    const renderers = list.filter((p) => p.type === "renderer");
    return { total_private_MiB: list.reduce((s, p) => s + p.private_MiB, 0), biggest_renderer_private_MiB: Math.max(0, ...renderers.map((p) => p.private_MiB)), processes: list };
  } catch (e) { return { error: String(e) }; }
}
async function pageMemory(page) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("Performance.enable");
  const { metrics } = await cdp.send("Performance.getMetrics");
  await cdp.detach();
  const pick = (n) => metrics.find((m) => m.name === n)?.value;
  const ua = await page.evaluate(() => window.E17.measureMemory());
  return { jsHeapUsed_MiB: Math.round(pick("JSHeapUsedSize") / 1048576), measureUserAgentSpecificMemory_MiB: ua.bytes ? Math.round(ua.bytes / 1048576) : ua };
}
function imageSize() {
  const flat = path.join(ROOT, "images", FLAT);
  let bytes = 0, n = 0;
  try { for (const f of fs.readdirSync(flat)) { bytes += fs.statSync(path.join(flat, f)).size; n++; } } catch {}
  const json = fs.statSync(path.join(ROOT, "images", FS)).size;
  const files = JSON.parse(fs.readFileSync(path.join(ROOT, "images", FS), "utf8"));
  return { flat_files_all_branches: n, flat_MiB_all_branches: Math.round(bytes / 1048576 * 10) / 10, fs_json_KiB: Math.round(json / 1024), fs_json_entries: files.size ?? undefined };
}

const browser = await chromium.launch({ channel: "chrome", headless: HEADLESS });
const version = browser.version();
log("navigateur chrome", version, HEADLESS ? "headless" : "headed", "fs", FS, "xmode", XMODE, "mem", MEM);

for (let n = FIRST; n < FIRST + RUNS; n++) {
  const result = { run: n, date: new Date().toISOString(), browser: { channel: "chrome", version, headless: HEADLESS }, fs: FS, xmode: XMODE, memory_size_MiB: MEM, v86: "0.5.473 (npm)", image: imageSize() };
  const context = await browser.newContext();
  try {
    await resetStats();
    const page = await context.newPage();
    page.on("pageerror", (e) => log("pageerror", e.message));
    page.on("crash", () => log("CRASH renderer"));
    const tNav = Date.now();
    await page.goto(pageUrl());
    let marks = await waitMark(page, "shell-prompt", 30 * MIN);
    const events = await page.evaluate(() => window.E17.events);
    const prompt = marks.find((m) => m.name === "shell-prompt"), bootReady = marks.find((m) => m.name === "boot-ready");
    result.cold_boot = {
      nav_to_prompt_s: sec(Date.now() - tNav), page_to_prompt_s: sec(prompt.t),
      page_to_boot_ready_s: bootReady ? sec(bootReady.t) : null, guest_uptime_at_boot_ready_s: bootReady?.uptime ?? null,
      emulator_started_s: sec(events.find((e) => e.name === "emulator-started")?.t), download: await stats(),
    };
    log("cold-boot", JSON.stringify(result.cold_boot));
    result.memory_after_boot = { page: await pageMemory(page), browser: await processMemory(browser) };

    await resetStats();
    await page.evaluate((m) => window.E17.bench(m, "notepad winecfg winemine"), XMODE);
    const t0 = Date.now();
    marks = await waitMark(page, "bench-done", 25 * MIN, ["x-failed"]);
    const info = await page.evaluate(() => window.E17.info.map((i) => i.line));
    result.bench = {
      wall_s: sec(Date.now() - t0),
      x_ready: delta(marks, "bench-start", "x-ready"),
      cmd_echo: delta(marks, "cmd-start", "cmd-done"),
      notepad_visible: delta(marks, "notepad-start", "notepad-visible"),
      notepad_typed: delta(marks, "notepad-visible", "notepad-typed"),
      winecfg_visible: delta(marks, "winecfg-start", "winecfg-visible"),
      winemine_visible: delta(marks, "winemine-start", "winemine-visible"),
      windows: info.filter((l) => l.startsWith("WIN ")),
      failed: marks.filter((m) => m.name.endsWith("-failed")).map((m) => m.name),
      info, download: await stats(), marks,
    };
    log("bench", JSON.stringify({ ...result.bench, info: undefined, marks: undefined }));
    await sleep(1500);
    await page.locator("#screen_container canvas").screenshot({ path: path.join(ROOT, "images", `e17-run${n}.png`) }).catch((e) => log("screenshot", e.message));
    result.memory_after_bench = { page: await pageMemory(page), browser: await processMemory(browser) };
    result.save_state = await page.evaluate(() => window.E17.saveState());
    await page.evaluate(() => window.E17.dropState());
    log("save_state", JSON.stringify(result.save_state));
    await page.close();
  } catch (e) {
    result.error = String(e.stack || e);
    log("ERREUR run", n, e.message);
  }
  await context.close();
  fs.writeFileSync(path.join(ROOT, `results-${n}.json`), JSON.stringify(result, null, 2));
  log(`results-${n}.json écrit`);
}
await browser.close();
server.kill();
process.exit(0);
