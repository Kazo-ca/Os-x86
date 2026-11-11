// Essai E4 — pilote interactif : lance server.mjs + Chrome (Playwright, headless, profil temporaire)
// et expose un petit serveur HTTP de contrôle pour envoyer des commandes série à l'invité,
// évaluer du JS dans la page, lire des fichiers de l'invité et prendre des captures.
// Usage : node drive.mjs [--port 8094] [--ctl 8095] [--state state.bin] [--fs alpine-fs.json] [--headed] [--e1state]
//   --e1state : charge l'état sauvegardé de E1 (images/state.bin via le repli du serveur)
// Contrôle (JSON) :
//   POST /cmd   {text, until, timeout_ms}  -> envoie text+"\n" sur la console série, attend que la sortie corresponde à until (regex)
//   GET  /eval?js=...                      -> page.evaluate(js)
//   GET  /read?path=/root/x                -> contenu texte du fichier invité (9p)
//   POST /write {path, text}               -> crée le fichier dans l'invité (9p)
//   GET  /shot?name=x [&canvas=1]          -> capture images/x.png (page entière ou canvas VGA)
//   GET  /goto?state=...&fs=...            -> recharge la page avec ces paramètres
//   GET  /quit
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import http from "node:http";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const PORT = Number(args.port || 8094);
const CTL = Number(args.ctl || 8095);
const BASE = `http://localhost:${PORT}`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
await sleep(500);
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "e4-profile-"));
const context = await chromium.launchPersistentContext(profile, { channel: "chrome", headless: !args.headed, viewport: { width: 1400, height: 1800 } });
log("chrome", context.browser()?.version(), "profil", profile);
let page = await context.newPage();
page.on("pageerror", (e) => log("pageerror", e.message));
page.on("console", (m) => { if (m.type() === "error") log("console.error", m.text().slice(0, 200)); });
page.on("crash", () => log("CRASH renderer"));

function pageUrl(extra) {
  const u = new URL(`${BASE}/boot.html`);
  if (args.e1state) u.searchParams.set("state", "state.bin");
  if (args.state) u.searchParams.set("state", args.state);
  if (args.fs) u.searchParams.set("fs", args.fs);
  for (const [k, v] of Object.entries(extra || {})) u.searchParams.set(k, v);
  return u.toString();
}
await page.goto(pageUrl());
log("page ouverte", page.url());

async function cmd({ text, until, timeout_ms = 60000 }) {
  const t0 = Date.now();
  const start = await page.evaluate(() => window.E4.serial.length);
  await page.evaluate((t) => window.E4.send(t + "\n"), text);
  const re = until ? new RegExp(until, "m") : null;
  let out = "";
  while (Date.now() - t0 < timeout_ms) {
    out = await page.evaluate((s) => window.E4.serial.slice(s), start);
    if (re && re.test(out)) return { matched: true, ms: Date.now() - t0, output: out };
    if (!re && Date.now() - t0 > 1500) return { matched: null, ms: Date.now() - t0, output: out };
    await sleep(150);
  }
  return { matched: false, ms: Date.now() - t0, output: out };
}

const ctl = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://localhost:${CTL}`);
  const body = await new Promise((r) => { const c = []; req.on("data", (d) => c.push(d)); req.on("end", () => r(Buffer.concat(c).toString())); });
  const reply = (obj, status = 200) => { res.writeHead(status, { "Content-Type": "application/json" }); res.end(JSON.stringify(obj)); };
  try {
    if (url.pathname === "/cmd") return reply(await cmd(JSON.parse(body || "{}")));
    if (url.pathname === "/eval") return reply({ value: await page.evaluate(url.searchParams.get("js")) });
    if (url.pathname === "/read") {
      const v = await page.evaluate(async (p) => { try { return await window.E4.readText(p); } catch (e) { return "ERR " + e; } }, url.searchParams.get("path"));
      return reply({ text: v });
    }
    if (url.pathname === "/write") { const { path: p, text } = JSON.parse(body); await page.evaluate(([p, t]) => window.E4.writeFile(p, t), [p, text]); return reply({ ok: true }); }
    if (url.pathname === "/shot") {
      const name = url.searchParams.get("name") || "shot";
      const file = path.join(ROOT, "images", name + ".png");
      if (url.searchParams.get("canvas")) await page.locator("#screen_container canvas").screenshot({ path: file });
      else await page.screenshot({ path: file, fullPage: !url.searchParams.get("desk"), clip: url.searchParams.get("desk") ? await page.locator("#desk").boundingBox() : undefined });
      return reply({ file });
    }
    if (url.pathname === "/goto") {
      const extra = Object.fromEntries(url.searchParams);
      await page.goto(pageUrl(extra));
      return reply({ url: page.url() });
    }
    if (url.pathname === "/click") {  // clic Playwright dans la page (coordonnées page) : exerce le chemin DOM -> xdotool
      await page.mouse.click(Number(url.searchParams.get("x")), Number(url.searchParams.get("y")));
      return reply({ ok: true });
    }
    if (url.pathname === "/drag") {  // glisser Playwright : x,y -> x2,y2
      const q = url.searchParams;
      await page.mouse.move(Number(q.get("x")), Number(q.get("y")));
      await page.mouse.down();
      await page.mouse.move(Number(q.get("x2")), Number(q.get("y2")), { steps: 8 });
      await page.mouse.up();
      return reply({ ok: true });
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
