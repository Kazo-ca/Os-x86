// Essai E7 — écritures hors compte (102) : qu'écrit un programme Wine de la bouteille b1 ailleurs que dans son
// home et son préfixe, et b2 peut-il le lire ? Méthode : (1) inventaire des dossiers où wine-b1 PEUT écrire
// (`find / -xdev -type d -perm -o+w` + ses propres dossiers) ; (2) inotifywait -m -r sur ces dossiers seulement
// (les arbres root 755 sont hors de portée d'un usager de bouteille : inutile de les surveiller, et sur 9p la mise en
// place récursive de /usr/lib prend plus de 10 min, voir run.mjs) ; (3) `launch b1 wine notepad` (premier wine de b1
// sous X : création des clés d'affichage, wineserver, explorer…) ; (4) liste des chemins, lisibilité par b2.
// Usage : node run-writes.mjs [--runs 2] [--port 8148]
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const PORT = Number(args.port || 8148), RUNS = Number(args.runs || 2), BASE = `http://localhost:${PORT}`, MIN = 60000;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
process.on("exit", () => server.kill());
await sleep(500);
const browser = await chromium.launch({ channel: "chrome", headless: !args.headed });
const out = { date: new Date().toISOString(), runs: [] };
for (let n = 1; n <= RUNS; n++) {
  const context = await browser.newContext(); const page = await context.newPage(); const R = { run: n };
  const agent = (req, t = 60000) => page.evaluate(([r, t]) => window.E7.agent(r, t).catch((e) => ({ ok: false, error: String(e.message || e) })), [req, t]);
  const serial = async (text, until, timeoutMs = 60000) => { const t0 = Date.now(); const start = await page.evaluate(() => window.E7.serial.length); await page.evaluate((t) => window.E7.send(t + "\n"), text); const re = new RegExp(until, "m"); let o = ""; while (Date.now() - t0 < timeoutMs) { o = await page.evaluate((s) => window.E7.serial.slice(s), start); if (re.test(o)) return { matched: true, ms: Date.now() - t0, output: o }; await sleep(150); } return { matched: false, ms: Date.now() - t0, output: o }; };
  const sh = (t, ms) => serial(`${t}; echo E7""END`, "^E7END", ms);
  const exec = (prefix, program, a = []) => agent({ op: "exec-in-prefix", prefix, program, args: a, timeout_ms: 60000 }, 70000);
  const lines = (o) => o.split("\n").map((l) => l.replace(/\r$/, "")).filter((l) => l.startsWith("/"));
  try {
    await page.goto(`${BASE}/boot.html?mem=1024`);
    await page.waitForFunction(() => window.E7.marks.some((m) => m.name === "shell-prompt"), null, { timeout: 15 * MIN, polling: 250 });
    await page.evaluate(([p, b]) => window.E7.writeBytesB64(p, b), ["root/x-up.sh", fs.readFileSync(path.join(ROOT, "guest/x-up.sh")).toString("base64")]);
    await page.evaluate(([p, b]) => window.E7.writeBytesB64(p, b), ["usr/local/lib/e7/list-windows.sh", fs.readFileSync(path.join(ROOT, "guest/list-windows.sh")).toString("base64")]);
    await sh("chmod 755 /root/x-up.sh /usr/local/lib/e7/*.sh");
    const inv = await sh("find / -xdev -type d \\( -perm -o+w -o -user wine-b1 \\) 2>/dev/null | grep -v '^/home/wine-b1/' | grep -v '^/proc'", 10 * MIN);
    R.writable_dirs_for_b1 = { ms: inv.ms, dirs: lines(inv.output) };
    const r = await serial("sh /root/x-up.sh", "MARK x-(ready|failed)", 3 * MIN); R.x_up_ms = r.ms;
    const watch = R.writable_dirs_for_b1.dirs.filter((d) => !/^\/home\/wine-b1$/.test(d)).join(" ");
    await sh(`sysctl -q -w fs.inotify.max_user_watches=524288; rm -f /tmp/ino.log /tmp/ino.err; (inotifywait -m -r -e create,modify,moved_to,attrib,delete --exclude '^/tmp/ino' -o /tmp/ino.log ${watch} 2>/tmp/ino.err &)`);
    const t0 = Date.now(); let ready = false; while (Date.now() - t0 < 5 * MIN) { const w = await sh("cat /tmp/ino.err"); if (/Watches established/.test(w.output)) { ready = true; break; } await sleep(1000); }
    R.inotify = { ready, setup_ms: Date.now() - t0, watched: watch.split(" ").length };
    const stamp = await sh("touch /tmp/stamp-before; find /tmp /var/tmp /dev/shm -xdev -newer /tmp/stamp-before 2>/dev/null | wc -l");
    const l = await agent({ op: "launch", prefix: "b1", program: "wine", args: ["notepad"] });
    const t1 = Date.now(); let win = null; while (Date.now() - t1 < 6 * MIN) { const w = await agent({ op: "list-windows" }, 60000); win = (w.windows || []).find((x) => /Notepad/.test(x.title)); if (win) break; await sleep(1000); }
    R.notepad = { launch: l.ok, visible_s: (Date.now() - t1) / 1000, window: win };
    await sleep(5000);
    await sh("pkill inotifywait; sleep 1; sort -u /tmp/ino.log > /tmp/ino.sorted; wc -l /tmp/ino.sorted");
    const raw = (await serial('cat /tmp/ino.sorted; echo E7""END', "^E7END", 2 * MIN)).output;
    const evs = raw.split("\n").map((x) => x.trim()).filter((x) => /^[A-Z_,]+ \//.test(x));
    const paths = [...new Set(evs.map((x) => x.replace(/^[A-Z_,]+ /, "")))].filter((p) => !p.startsWith("/tmp/ino"));
    const newer = lines((await sh("find /tmp /var/tmp /dev/shm /run -xdev -newer /tmp/stamp-before 2>/dev/null")).output);
    const readable = {}; for (const p of [...new Set([...paths, ...newer])].slice(0, 60)) { const t = await exec("b2", "test", ["-r", p]); const s = await exec("b2", "ls", ["-ld", p]); readable[p] = { readable_by_b2: t.exit_code === 0, ls: (s.output || "").trim().slice(0, 120) }; }
    R.writes = { inotify_events: evs.length, inotify_paths: paths, find_newer_paths: newer, readable_by_b2: readable, events_sample: evs.slice(0, 40) };
    R.b1_home_after = lines((await sh("find /home/wine-b1 -xdev -newer /tmp/stamp-before -not -path '*/drive_c/*' 2>/dev/null | head -40")).output);
    R.x_sockets = (await sh("ls -la /tmp/.X11-unix/ /tmp/.wine-1001 2>&1")).output.split("\n").filter((x) => /^[dsc-]/.test(x));
    await agent({ op: "kill", scope: "cgroup", prefix: "b1", signal: 9 });
  } catch (e) { R.error = String(e.stack || e); log("ERREUR", e.message); }
  out.runs.push(R); log(JSON.stringify(R).slice(0, 1500));
  fs.writeFileSync(path.join(ROOT, "results-writes.json"), JSON.stringify(out, null, 2));
  await context.close();
}
await browser.close(); server.kill(); process.exit(0);
