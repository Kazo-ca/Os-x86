// Essai E7 — pilote interactif (dérivé de essais/e17-wine-dedie/drive.mjs) : lance server.mjs + Chrome
// (Playwright, canal installé, headless, profil temporaire supprimé à la fin) et un petit serveur HTTP de
// contrôle pour parler à l'invité (console série root, agent sur hvc1, console d'usager sur hvc2).
// Usage : node drive.mjs [--port 8142] [--ctl 8143] [--fs e7-fs.json] [--flat rootfs-flat] [--disk disk-b1.img] [--alpine] [--mem 1024] [--headed]
// Contrôle (JSON) :
//   POST /cmd     {text, until, timeout_ms}   -> console série : envoie text+"\n", attend que la sortie corresponde à until (regex)
//   GET  /serial?from=N                        -> sortie série depuis l'index N
//   POST /agent   {op, ...} [?timeout_ms=]      -> requête à l'agent (hvc1), réponse avec _ms (aller-retour)
//   GET  /events?from=N                        -> événements de l'agent (exit, open-url, console-closed, ready)
//   POST /console {text, until, timeout_ms}    -> console d'usager (hvc2) : envoie text, attend until (regex) sur sa sortie
//   GET  /console?from=N                       -> sortie de la console d'usager depuis l'index N
//   GET  /fault?path=srv/share-b1 | /fault?off=1 -> arme / désarme « partage disparu » (Rlerror EIO)
//   GET  /disk?name=disk-after.img             -> exporte le disque hda (mémoire) vers images/<name>
//   POST /inject  {path, file}               -> copie un fichier local (sous ce dossier) dans l'invité (9p, avant qu'il soit lu)
//   GET  /eval?js=... /read?path= POST /write {path,text} /shot?name=x[&canvas=1] /save?name= /mem /quit
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import http from "node:http";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const PORT = Number(args.port || 8142);
const CTL = Number(args.ctl || 8143);
const BASE = `http://localhost:${PORT}`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
await sleep(500);
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "e7-profile-"));
const context = await chromium.launchPersistentContext(profile, { channel: "chrome", headless: !args.headed, viewport: { width: 1100, height: 1500 } });
log("chrome", context.browser()?.version(), "profil", profile);
let page = await context.newPage();
page.on("pageerror", (e) => log("pageerror", e.message));
page.on("console", (m) => { if (m.type() === "error") log("console.error", m.text().slice(0, 200)); });
page.on("crash", () => log("CRASH renderer"));

function pageUrl(extra) {
  const u = new URL(`${BASE}/boot.html`);
  for (const k of ["fs", "flat", "state", "mem", "disk", "alpine"]) if (args[k]) u.searchParams.set(k, args[k] === true ? "1" : args[k]);
  for (const [k, v] of Object.entries(extra || {})) u.searchParams.set(k, v);
  return u.toString();
}
await page.goto(pageUrl());
log("page ouverte", page.url());

async function waitOutput(getter, start, until, timeout_ms, t0) {
  const re = until ? new RegExp(until, "m") : null;
  let out = "";
  while (Date.now() - t0 < timeout_ms) {
    out = await getter(start);
    if (re && re.test(out)) return { matched: true, ms: Date.now() - t0, output: out };
    if (!re && Date.now() - t0 > 1500) return { matched: null, ms: Date.now() - t0, output: out };
    await sleep(150);
  }
  return { matched: false, ms: Date.now() - t0, output: out };
}
async function cmd({ text, until, timeout_ms = 60000 }) {
  const t0 = Date.now();
  const start = await page.evaluate(() => window.E7.serial.length);
  await page.evaluate((t) => window.E7.send(t + "\n"), text);
  return waitOutput((s) => page.evaluate((s) => window.E7.serial.slice(s), s), start, until, timeout_ms, t0);
}
async function consoleCmd({ text, until, timeout_ms = 60000 }) {
  const t0 = Date.now();
  const start = await page.evaluate(() => window.E7.console.length);
  if (text !== undefined) await page.evaluate((t) => window.E7.consoleSend(t), text);
  return waitOutput((s) => page.evaluate((s) => window.E7.console.slice(s), s), start, until, timeout_ms, t0);
}
async function agent(req, timeoutMs = 60000) { return page.evaluate(([r, t]) => window.E7.agent(r, t).catch((e) => ({ ok: false, error: String(e.message || e) })), [req, timeoutMs]); }

async function processMemory() {
  try {
    const cdp = await context.browser().newBrowserCDPSession();
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

const ctl = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://localhost:${CTL}`);
  const body = await new Promise((r) => { const c = []; req.on("data", (d) => c.push(d)); req.on("end", () => r(Buffer.concat(c).toString())); });
  const reply = (obj, status = 200) => { res.writeHead(status, { "Content-Type": "application/json; charset=utf-8" }); res.end(JSON.stringify(obj)); };
  try {
    if (url.pathname === "/cmd") return reply(await cmd(JSON.parse(body || "{}")));
    if (url.pathname === "/serial") return reply({ text: await page.evaluate((s) => window.E7.serial.slice(s), Number(url.searchParams.get("from") || 0)), length: await page.evaluate(() => window.E7.serial.length) });
    if (url.pathname === "/agent") return reply(await agent(JSON.parse(body || "{}"), Number(url.searchParams.get("timeout_ms") || 60000)));
    if (url.pathname === "/events") return reply({ events: await page.evaluate((s) => window.E7.agentEvents.slice(s), Number(url.searchParams.get("from") || 0)) });
    if (url.pathname === "/console") {
      if (req.method === "POST") return reply(await consoleCmd(JSON.parse(body || "{}")));
      return reply({ text: await page.evaluate((s) => window.E7.console.slice(s), Number(url.searchParams.get("from") || 0)), length: await page.evaluate(() => window.E7.console.length) });
    }
    if (url.pathname === "/fault") return reply({ result: url.searchParams.get("off") ? await page.evaluate(() => window.E7.faultOff()) : await page.evaluate((p) => window.E7.faultOn(p), url.searchParams.get("path")), faults: await page.evaluate(() => window.E7.faults.length) });
    if (url.pathname === "/disk") { const t = Date.now(); const bytes = await page.evaluate((n) => window.E7.uploadDisk(n), url.searchParams.get("name") || "disk-after.img"); return reply({ bytes, ms: Date.now() - t }); }
    if (url.pathname === "/eval") return reply({ value: await page.evaluate(url.searchParams.get("js")) });
    if (url.pathname === "/read") return reply({ text: await page.evaluate(async (p) => { try { return await window.E7.readText(p); } catch (e) { return "ERR " + e; } }, url.searchParams.get("path")) });
    if (url.pathname === "/write") { const { path: p, text } = JSON.parse(body); await page.evaluate(([p, t]) => window.E7.writeFile(p, t), [p, text]); return reply({ ok: true }); }
    if (url.pathname === "/inject") {
      const { path: p, file } = JSON.parse(body);
      const local = path.normalize(path.join(ROOT, file));
      if (!local.startsWith(ROOT)) return reply({ error: "hors dossier" }, 400);
      const b64 = fs.readFileSync(local).toString("base64");
      await page.evaluate(([p, b]) => window.E7.writeBytesB64(p, b), [p, b64]);
      return reply({ ok: true, bytes: Buffer.byteLength(b64, "base64") });
    }
    if (url.pathname === "/shot") {
      const name = url.searchParams.get("name") || "shot";
      const file = path.join(ROOT, "images", name + ".png");
      if (url.searchParams.get("canvas")) await page.locator("#screen_container canvas").screenshot({ path: file }); else await page.screenshot({ path: file, fullPage: true });
      return reply({ file });
    }
    if (url.pathname === "/save") { const save = await page.evaluate(() => window.E7.saveState()); await page.evaluate(() => window.E7.dropState()); return reply({ save }); }
    if (url.pathname === "/goto") { await page.goto(pageUrl(Object.fromEntries(url.searchParams))); return reply({ url: page.url() }); }
    if (url.pathname === "/mem") {
      const cdp = await page.context().newCDPSession(page);
      await cdp.send("Performance.enable");
      const { metrics } = await cdp.send("Performance.getMetrics");
      await cdp.detach();
      const pick = (n) => metrics.find((m) => m.name === n)?.value;
      return reply({ jsHeapUsed_MiB: Math.round(pick("JSHeapUsedSize") / 1048576), browser: await processMemory() });
    }
    if (url.pathname === "/quit") { reply({ bye: true }); await shutdown(); return; }
    reply({ error: "unknown " + url.pathname }, 404);
  } catch (e) { reply({ error: String(e.stack || e) }, 500); }
});
ctl.listen(CTL, "127.0.0.1", () => log(`contrôle: http://localhost:${CTL}/`));

async function shutdown() {
  log("arrêt");
  ctl.close();
  try { await context.close(); } catch {}
  server.kill();
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch {}
  process.exit(0);
}
process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);
