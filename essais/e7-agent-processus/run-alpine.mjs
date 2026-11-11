// Essai E7 — tableau « image apportée sans agent » (195 A) : l'image Alpine nue de E6 (autologin root sur ttyS0,
// aucun agent, pas de virtio-console) démarrée dans la même page ; ce qu'il reste : écran VGA, clavier, souris,
// disque (9p), console série = UNE session root. Mesures : boot, aller-retour d'une commande série, blocage de la
// session par une commande longue, liste de processus « à la main » (ps), capture du canvas.
// Usage : node run-alpine.mjs [--port 8146] [--runs 1]
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const PORT = Number(args.port || 8146), RUNS = Number(args.runs || 1), BASE = `http://localhost:${PORT}`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
process.on("exit", () => server.kill());
await sleep(500);
const browser = await chromium.launch({ channel: "chrome", headless: !args.headed });
const out = { date: new Date().toISOString(), image: "E6 alpine-fs.json (Alpine 3.24 x86, sans agent)", runs: [] };
for (let n = 1; n <= RUNS; n++) {
  const context = await browser.newContext(); const page = await context.newPage(); const R = { run: n };
  const serial = async (text, until, timeoutMs = 60000) => { const t0 = Date.now(); const start = await page.evaluate(() => window.E7.serial.length); await page.evaluate((t) => window.E7.send(t + "\n"), text); const re = new RegExp(until, "m"); let o = ""; while (Date.now() - t0 < timeoutMs) { o = await page.evaluate((s) => window.E7.serial.slice(s), start); if (re.test(o)) return { matched: true, ms: Date.now() - t0, output: o }; await sleep(100); } return { matched: false, ms: Date.now() - t0, output: o }; };
  const sh = (t, ms) => serial(`${t}; echo E7""END`, "^E7END", ms);
  try {
    const t0 = Date.now();
    await page.goto(`${BASE}/boot.html?alpine=1&mem=512`);
    await page.waitForFunction(() => window.E7.marks.some((m) => m.name === "shell-prompt"), null, { timeout: 600000, polling: 250 });
    const marks = await page.evaluate(() => window.E7.marks);
    R.boot = { nav_to_prompt_s: (Date.now() - t0) / 1000, boot_ready: marks.find((m) => m.name === "boot-ready") };
    R.virtio_console_in_guest = (await sh("ls /dev/hvc* /dev/vport* 2>&1; cat /proc/cmdline")).output.split("\n").filter((l) => /hvc|vport|cmdline|root=/.test(l) && !/echo/.test(l));
    const rtts = []; for (let i = 0; i < 10; i++) rtts.push((await sh("true")).ms); R.serial_rtt_ms = rtts;
    const ps = await sh("ps -o pid,ppid,user,comm"); R.ps = { ms: ps.ms, lines: ps.output.split("\n").filter((l) => /^\s*\d/.test(l)).length };
    // Une seule session : pendant « sleep 8 », une seconde commande attend.
    const start = await page.evaluate(() => window.E7.serial.length);
    await page.evaluate(() => window.E7.send('sleep 8; echo SL""EEP-DONE\n')); await sleep(500);
    const second = await sh("echo second");
    R.single_session = { second_command_waited_ms: second.ms, second_after_sleep: /SLEEP-DONE[\s\S]*E7END/.test(await page.evaluate((s) => window.E7.serial.slice(s), start)) };
    R.what_remains = { screen_vga: !!(await page.evaluate(() => document.querySelector("#screen_container canvas"))), keyboard_mouse: "v86 (PS/2) — indépendants de l'invité", disk: "racine 9p (lecture/écriture) ; disque bloc possible (hda)", network: "virtio-net/ne2k vers le relais de la page (E6) — indépendant de l'invité", process_list: "seulement via le shell série root (texte), pas d'arrêt individuel structuré, pas de bouteille", shares: "pas de montage à la demande sans agent", console: "ttyS0 : une session root, bloquée par toute commande longue" };
    await page.locator("#screen_container canvas").screenshot({ path: path.join(ROOT, "images", `e7-alpine-run${n}.png`) }).catch(() => {});
    R.text_screen = (await page.evaluate(() => Array.from(document.querySelectorAll("#screen_container > div")).map((d) => d.textContent).join("\n"))).slice(0, 600);
  } catch (e) { R.error = String(e.stack || e); log("ERREUR", e.message); }
  out.runs.push(R); log(JSON.stringify(R).slice(0, 600));
  await context.close();
}
fs.writeFileSync(path.join(ROOT, "results-alpine.json"), JSON.stringify(out, null, 2));
await browser.close(); server.kill(); process.exit(0);
