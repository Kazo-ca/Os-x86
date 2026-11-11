// Essai E14 — pilote interactif (dérivé d'E17) : lance server.mjs + Chrome (Playwright, canal installé,
// profil temporaire supprimé à la fin) et un petit serveur HTTP de contrôle.
// Usage : node drive.mjs [--port 8162] [--ctl 8163] [--guest dos|wine] [--poll 33] [--route vga|pty] [--mem N] [--headed] [--state x.bin]
// Contrôle (JSON) :
//   POST /cmd   {text, until, timeout_ms}   -> ligne sur ttyS0 (shell de contrôle), attend que la sortie corresponde à until (regex)
//   POST /keys  {keys:[{key,ctrl}], text, delay_ms, measure}  -> touches par le jeu de Kazo (mesure touche→écran par touche)
//   GET  /screen                            -> lignes de l'écran du terminal (texte)
//   GET  /wait?re=...&timeout_ms=           -> attend que l'écran contienne la regex
//   GET  /stats                             -> pollStats, keySamples, rejectedKeys, events, graphical
//   GET  /set?poll=33&route=vga&cols=80&rows=25  -> réglages
//   GET  /eval?js=...  /serial?from=N  /serial1?from=N  /shot?name=x  /mem  /cpu?ms=5000  /save?name=x.bin  /goto?...  /quit
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import http from "node:http";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const PORT = Number(args.port || 8162);
const CTL = Number(args.ctl || 8163);
const BASE = `http://localhost:${PORT}`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
await sleep(500);
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "e14-profile-"));
const context = await chromium.launchPersistentContext(profile, { channel: "chrome", headless: !args.headed, viewport: { width: 1000, height: 900 } });
log("chrome", context.browser()?.version(), "profil", profile);
let page = await context.newPage();
page.on("pageerror", (e) => log("pageerror", e.message));
page.on("console", (m) => { if (m.type() === "error") log("console.error", m.text().slice(0, 200)); });
page.on("crash", () => log("CRASH renderer"));

function pageUrl(extra) {
  const u = new URL(`${BASE}/terminal.html`);
  for (const k of ["guest", "poll", "route", "cols", "rows", "mem", "state", "fs", "flat"]) if (args[k]) u.searchParams.set(k, args[k]);
  for (const [k, v] of Object.entries(extra || {})) u.searchParams.set(k, v);
  return u.toString();
}
await page.goto(pageUrl());
log("page ouverte", page.url());

export async function cmd({ text, until, timeout_ms = 60000 }) {
  const t0 = Date.now();
  const start = await page.evaluate(() => window.E14.serial.length);
  await page.evaluate((t) => window.E14.send(t + "\n"), text);
  const re = until ? new RegExp(until, "m") : null;
  let out = "";
  while (Date.now() - t0 < timeout_ms) {
    out = await page.evaluate((s) => window.E14.serial.slice(s), start);
    if (re && re.test(out)) return { matched: true, ms: Date.now() - t0, output: out };
    if (!re && Date.now() - t0 > 1500) return { matched: null, ms: Date.now() - t0, output: out };
    await sleep(100);
  }
  return { matched: false, ms: Date.now() - t0, output: out };
}
async function keys({ keys = [], text, delay_ms = 150, measure = true }) {
  const list = keys.slice();
  if (text) for (const ch of text) list.push(ch === "\n" ? { key: "Enter" } : { key: ch });
  const n0 = await page.evaluate(() => window.E14.keySamples.length);
  for (const k of list) {
    await page.evaluate(([k, m]) => window.E14.sendKey(k, m), [k, measure]);
    if (measure) await page.waitForFunction(() => !window.E14.keyPending, null, { timeout: 5000, polling: 5 }).catch(() => {});
    await sleep(delay_ms);
  }
  return { sent: list.length, samples: await page.evaluate((n) => window.E14.keySamples.slice(n), n0) };
}
async function waitScreen(re, timeout_ms = 30000) {
  const t0 = Date.now();
  try { await page.waitForFunction((r) => new RegExp(r).test((window.E14.screen() || []).join("\n")), re, { timeout: timeout_ms, polling: 50 }); return { matched: true, ms: Date.now() - t0, screen: await page.evaluate(() => window.E14.screen()) }; }
  catch { return { matched: false, ms: Date.now() - t0, screen: await page.evaluate(() => window.E14.screen()) }; }
}
async function processInfo() {
  const cdp = await context.browser().newBrowserCDPSession();
  const { processInfo } = await cdp.send("SystemInfo.getProcessInfo");
  await cdp.detach();
  return processInfo;
}
async function processMemory() {
  try {
    const info = await processInfo();
    const out = execFileSync("pwsh", ["-NoProfile", "-Command", `Get-Process -Id ${info.map((p) => p.id).join(",")} -ErrorAction SilentlyContinue | Select-Object Id,WorkingSet64,PrivateMemorySize64 | ConvertTo-Json -Compress`], { encoding: "utf8" });
    const procs = JSON.parse(out || "[]");
    const list = (Array.isArray(procs) ? procs : [procs]).map((p) => ({ pid: p.Id, type: info.find((x) => x.id === p.Id)?.type, ws_MiB: Math.round(p.WorkingSet64 / 1048576), private_MiB: Math.round(p.PrivateMemorySize64 / 1048576) }));
    return { total_private_MiB: list.reduce((s, p) => s + p.private_MiB, 0), biggest_renderer_private_MiB: Math.max(0, ...list.filter((p) => p.type === "renderer").map((p) => p.private_MiB)), processes: list };
  } catch (e) { return { error: String(e) }; }
}
/** CPU (% d'un cœur) du renderer et de tous les processus Chrome sur une fenêtre de ms. */
async function cpu(ms = 5000) {
  const info = await processInfo();
  const ids = info.map((p) => p.id).join(",");
  const ps = () => JSON.parse(execFileSync("pwsh", ["-NoProfile", "-Command", `Get-Process -Id ${ids} -ErrorAction SilentlyContinue | Select-Object Id,@{n='cpu';e={$_.TotalProcessorTime.TotalMilliseconds}} | ConvertTo-Json -Compress`], { encoding: "utf8" }) || "[]");
  const norm = (x) => (Array.isArray(x) ? x : [x]);
  const a = norm(ps()); const t0 = Date.now();
  await sleep(ms);
  const b = norm(ps()); const dt = Date.now() - t0;
  const per = b.map((p) => ({ pid: p.Id, type: info.find((x) => x.id === p.Id)?.type, cpu_pct: Math.round(((p.cpu - (a.find((q) => q.Id === p.Id)?.cpu ?? p.cpu)) / dt) * 1000) / 10 }));
  return { window_ms: dt, renderer_pct: Math.max(0, ...per.filter((p) => p.type === "renderer").map((p) => p.cpu_pct)), total_pct: Math.round(per.reduce((s, p) => s + p.cpu_pct, 0) * 10) / 10, processes: per };
}

const ctl = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://localhost:${CTL}`);
  const body = await new Promise((r) => { const c = []; req.on("data", (d) => c.push(d)); req.on("end", () => r(Buffer.concat(c).toString())); });
  const reply = (obj, status = 200) => { res.writeHead(status, { "Content-Type": "application/json" }); res.end(JSON.stringify(obj)); };
  try {
    const q = Object.fromEntries(url.searchParams);
    if (url.pathname === "/cmd") return reply(await cmd(JSON.parse(body || "{}")));
    if (url.pathname === "/keys") return reply(await keys(JSON.parse(body || "{}")));
    if (url.pathname === "/screen") return reply({ screen: await page.evaluate(() => window.E14.screen()), graphical: await page.evaluate(() => window.E14.graphical) });
    if (url.pathname === "/wait") return reply(await waitScreen(q.re, Number(q.timeout_ms || 30000)));
    if (url.pathname === "/stats") return reply(await page.evaluate(() => ({ pollStats: window.E14.pollStats, pollOverruns: window.E14.pollOverruns, keySamples: window.E14.keySamples, rejectedKeys: window.E14.rejectedKeys, events: window.E14.events, screenSizeEvents: window.E14.screenSizeEvents, graphical: window.E14.graphical, marks: window.E14.marks, serial1Bytes: window.E14.serial1Bytes })));
    if (url.pathname === "/reset") { await page.evaluate(() => window.E14.resetStats()); return reply({ ok: true }); }
    if (url.pathname === "/set") {
      await page.evaluate((q) => { if (q.poll) window.E14.setPoll(Number(q.poll)); if (q.route) window.E14.setRoute(q.route); if (q.cols) window.E14.resize(Number(q.cols), Number(q.rows || 25)); if (q.strict != null) document.getElementById("strict").checked = q.strict === "1"; if (q.ctrlc) document.getElementById("ctrlc").value = q.ctrlc; }, q);
      return reply({ ok: true });
    }
    if (url.pathname === "/guestwrite") { const marker = await page.evaluate((q) => window.E14.guestWrite(q.text, q.cmd), q); await page.waitForFunction(() => !window.E14.keyPending, null, { timeout: 5000, polling: 5 }).catch(() => {}); return reply({ marker, sample: await page.evaluate(() => window.E14.keySamples.at(-1)) }); }
    if (url.pathname === "/serial") return reply({ text: await page.evaluate((s) => window.E14.serial.slice(s), Number(q.from || 0)), length: await page.evaluate(() => window.E14.serial.length) });
    if (url.pathname === "/serial1") return reply({ text: await page.evaluate((s) => window.E14.serial1.slice(s), Number(q.from || 0)), length: await page.evaluate(() => window.E14.serial1.length) });
    if (url.pathname === "/eval") return reply({ value: await page.evaluate(q.js) });
    if (url.pathname === "/shot") { const file = path.join(ROOT, "images", (q.name || "shot") + ".png"); if (q.term) await page.locator("#term").screenshot({ path: file }); else await page.screenshot({ path: file, fullPage: true }); return reply({ file: path.relative(ROOT, file) }); }
    if (url.pathname === "/mem") {
      const cdp = await page.context().newCDPSession(page); await cdp.send("Performance.enable");
      const { metrics } = await cdp.send("Performance.getMetrics"); await cdp.detach();
      const ua = await page.evaluate(() => window.E14.measureMemory());
      return reply({ jsHeapUsed_MiB: Math.round(metrics.find((m) => m.name === "JSHeapUsedSize").value / 1048576), measureUserAgentSpecificMemory_MiB: ua.bytes ? Math.round(ua.bytes / 1048576) : ua, browser: await processMemory() });
    }
    if (url.pathname === "/cpu") return reply(await cpu(Number(q.ms || 5000)));
    if (url.pathname === "/save") { const save = await page.evaluate(() => window.E14.saveState()); const bytes = await page.evaluate((n) => window.E14.uploadState(n), q.name || "state.bin"); return reply({ save, uploaded_bytes: Number(bytes) }); }
    if (url.pathname === "/goto") { await page.goto(pageUrl(q)); return reply({ url: page.url() }); }
    if (url.pathname === "/quit") { reply({ bye: true }); await shutdown(); return; }
    reply({ error: "unknown " + url.pathname }, 404);
  } catch (e) { reply({ error: String(e.stack || e) }, 500); }
});
ctl.listen(CTL, "127.0.0.1", () => log(`contrôle: http://localhost:${CTL}/`));

async function shutdown() {
  log("arrêt"); ctl.close();
  try { await context.close(); } catch {}
  server.kill();
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch {}
  process.exit(0);
}
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);
