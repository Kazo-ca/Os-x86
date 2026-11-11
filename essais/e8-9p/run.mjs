// Essai E8 — pilote de mesure (Playwright + Chrome installé, headless, profil temporaire supprimé à la fin).
// Usage : node run.mjs [--runs 2] [--first 1] [--port 8098] [--headed] [--wine] [--no-harden] [--only-harden] [--no-2g]
// Pour chaque run : boot (racine 9p) -> bench9p.sh sur /mnt/share (9p), /mnt/disk (ext4 sur hda async), /mnt/tmp2 (tmpfs)
//   -> [--wine] wine9p.sh -> page neuve : harden.sh (sans 2 Gio) + inspection côté hôte + 9p brut + « lecteur déconnecté »
//   -> page neuve : fichier creux de 2 Gio seul (peut tuer le renderer).
// Écrit results-<n>.json à côté du script. Playwright est pris dans ../e1-wine-v86/node_modules (rien à installer).
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "../e1-wine-v86/node_modules/playwright/index.mjs";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const RUNS = Number(args.runs || 2);
const FIRST = Number(args.first || 1);
const PORT = Number(args.port || 8098);
const BASE = `http://localhost:${PORT}`;
const MIN = 60_000;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
process.on("exit", () => server.kill());
await sleep(500);
const stats = async () => (await fetch(`${BASE}/__stats`)).json();
const resetStats = () => fetch(`${BASE}/__reset`);

// ---------- aides ----------
async function waitMark(page, name, timeoutMs) {
  await page.waitForFunction((n) => window.E8.marks.some((m) => m.name === n), name, { timeout: timeoutMs, polling: 250 });
  return page.evaluate(() => window.E8.marks);
}
async function waitLine(page, prefix, timeoutMs) {
  await page.waitForFunction((p) => window.E8.lines.some((x) => x.l.startsWith(p)), prefix, { timeout: timeoutMs, polling: 250 });
  return page.evaluate((p) => window.E8.lines.filter((x) => x.l.startsWith(p)).map((x) => x.l), prefix);
}
const send = (page, cmd) => page.evaluate((c) => window.E8.send(c + "\n"), cmd);
const sec = (ms) => (ms == null ? null : Math.round(ms) / 1000);
function parseRes(lines, label) {
  // "RES <label> <clé> <valeur...>" -> { clé: "valeur" }
  const out = {};
  for (const l of lines) {
    const m = /^RES (\S+) (\S+) (.*)$/.exec(l);
    if (m && m[1] === label) out[m[2]] = m[3];
  }
  return out;
}
async function processMemory(browser) {
  try {
    const cdp = await browser.newBrowserCDPSession();
    const { processInfo } = await cdp.send("SystemInfo.getProcessInfo");
    await cdp.detach();
    const out = execFileSync("pwsh", ["-NoProfile", "-Command", `Get-Process -Id ${processInfo.map((p) => p.id).join(",")} -ErrorAction SilentlyContinue | Select-Object Id,PrivateMemorySize64 | ConvertTo-Json -Compress`], { encoding: "utf8" });
    const procs = JSON.parse(out || "[]");
    const list = (Array.isArray(procs) ? procs : [procs]).map((p) => ({ type: processInfo.find((x) => x.id === p.Id)?.type, private_MiB: Math.round(p.PrivateMemorySize64 / 1048576) }));
    return { total_private_MiB: list.reduce((s, p) => s + p.private_MiB, 0), biggest_renderer_private_MiB: Math.max(0, ...list.filter((p) => p.type === "renderer").map((p) => p.private_MiB)) };
  } catch (e) { return { error: String(e) }; }
}

async function bootPage(context, label) {
  await resetStats();
  const page = await context.newPage();
  page.on("pageerror", (e) => log("pageerror", e.message.slice(0, 200)));
  page.on("console", (m) => { if (m.type() === "error") log("console.error", m.text().slice(0, 200)); });
  page.crashed = false;
  page.on("crash", () => { page.crashed = true; log("CRASH renderer", label); });
  const t = Date.now();
  await page.goto(`${BASE}/e8.html`);
  const marks = await waitMark(page, "shell-prompt", 30 * MIN);
  const r = { label, page_to_prompt_s: sec(marks.find((m) => m.name === "shell-prompt").t), nav_to_prompt_s: sec(Date.now() - t), download: await stats() };
  log(label, JSON.stringify(r));
  return { page, boot: r };
}

// ---------- phase 1 : bench sur une cible ----------
async function phaseBench(page, target, label) {
  await resetStats();
  const p9before = await page.evaluate(() => ({ ...window.E8.p9 }));
  const t0 = Date.now();
  await send(page, `sh /root/bench9p.sh ${target} ${label}`);
  await waitMark(page, `bench-done-${label}`, 40 * MIN);
  const lines = await page.evaluate(() => window.E8.lines.map((x) => x.l));
  const p9after = await page.evaluate(() => ({ ...window.E8.p9 }));
  const r = { target, label, wall_s: sec(Date.now() - t0), res: parseRes(lines, label), errors: lines.filter((l) => l.startsWith("ERR ")), mountinfo: lines.filter((l) => l.startsWith("MOUNTINFO")).pop(),
    p9_reads: p9after.reads - p9before.reads, p9_read_bytes: p9after.readBytes - p9before.readBytes, download: await stats() };
  log("bench", label, JSON.stringify(r.res));
  return r;
}

// ---------- phase 2 : durcissement ----------
async function phaseHarden(page, with2g) {
  const t0 = Date.now();
  await send(page, `sh /root/harden.sh /mnt/share ${with2g ? "" : "skip2g"}`);
  await waitMark(page, "harden-done", 20 * MIN);
  const guest = await page.evaluate(() => window.E8.hard.map((x) => x.l));
  const host = {};
  // Le second montage 9p échoue (un seul canal virtio-9p, déjà pris par la racine) : /mnt/share est un dossier de la racine 9p, donc "mnt/share/..." côté hôte.
  for (const p of ["mnt/share", "h/x_abs", "h/x_rel", "h/rootlink", "h/loop1", "h/hl1", "h/hl2", "h/zero", "h/sda", "h/fifo", "h/suidbb", "h/mv2_in", "h/big17", "h/sparse1", "h/sparse2"].map((q) => (q.startsWith("h/") ? "mnt/share/" + q : q))) {
    host[p] = await page.evaluate((q) => window.E8.hostStat(q), p);
  }
  host["read_x_abs"] = await page.evaluate(() => window.E8.hostRead("mnt/share/h/x_abs"));
  host["read_hl1"] = await page.evaluate(() => window.E8.hostRead("mnt/share/h/hl1"));
  host["names"] = await page.evaluate(() => window.E8.hostList("mnt/share/h/names"));
  host["root_children"] = await page.evaluate(() => window.E8.hostList("").length);
  host["mem"] = await page.evaluate(() => window.E8.hostMem());
  const r = { wall_s: sec(Date.now() - t0), guest, host };
  log("harden", guest.length, "lignes HARD");
  return r;
}

// ---------- phase 3 : 9p brut forgé côté hôte ----------
async function phaseRaw(page) {
  const raw = (id, fields) => page.evaluate(([i, f]) => window.E8.raw(i, f), [id, fields]);
  const out = {};
  const ROOTFID = 60000, F = 60001, G = 60002;
  out.attach_aname_etc = await raw(104, [["w", ROOTFID], ["w", 0xffffffff], ["s", "root"], ["s", "/etc"], ["w", 0]]);
  out.walk_dotdot_from_root = await raw(110, [["w", ROOTFID], ["w", F], ["h", 2], ["s", ".."], ["s", ".."]]);
  out.walk_slash = await raw(110, [["w", ROOTFID], ["w", F], ["h", 1], ["s", "/"]]);
  out.walk_abs_path_one_name = await raw(110, [["w", ROOTFID], ["w", F], ["h", 1], ["s", "/etc/passwd"]]);
  out.walk_rel_path_one_name = await raw(110, [["w", ROOTFID], ["w", F], ["h", 1], ["s", "etc/passwd"]]);
  out.walk_empty_name = await raw(110, [["w", ROOTFID], ["w", F], ["h", 1], ["s", ""]]);
  out.walk_dot = await raw(110, [["w", ROOTFID], ["w", F], ["h", 1], ["s", "."]]);
  out.walk_h = await raw(110, [["w", ROOTFID], ["w", G], ["h", 3], ["s", "mnt"], ["s", "share"], ["s", "h"]]);
  out.walk_dotdot_from_h = await raw(110, [["w", G], ["w", F], ["h", 3], ["s", ".."], ["s", ".."], ["s", ".."]]);
  // Création avec des noms que le noyau n'enverrait jamais : "../evil", "a/b", "." et ".." (mkdir).
  out.lcreate_dotdot_evil = await raw(14, [["w", G], ["s", "../evil"], ["w", 0x41], ["w", 0o644], ["w", 0]]);
  out.mkdir_slash = await raw(72, [["w", G], ["s", "a/b"], ["w", 0o755], ["w", 0]]);
  out.symlink_with_slash_name = await raw(16, [["w", G], ["s", "x/y"], ["s", "/etc/passwd"], ["w", 0]]);
  out.h_children_after = await page.evaluate(() => window.E8.hostList("mnt/share/h").map((x) => x.json));
  out.xattrcreate = await raw(32, [["w", F], ["s", "user.e8"], ["d", 5], ["w", 0]]);
  out.xattrwalk = await raw(30, [["w", G], ["w", 60003], ["s", "user.e8"]]);
  out.mkdir_dotdot_in_names = await raw(72, [["w", G], ["s", ".."], ["w", 0o755], ["w", 0]]);
  out.walk_dotdot_from_h_after = await raw(110, [["w", G], ["w", F], ["h", 1], ["s", ".."]]);
  out.fid_unknown_read = await raw(116, [["w", 61234], ["d", 0], ["w", 16]]);
  log("raw", JSON.stringify(out).slice(0, 300));
  return out;
}

// ---------- phase 4 : lecteur déconnecté ----------
async function phaseDisconnect(page) {
  const out = {};
  await send(page, "mkdir -p /mnt/share/h; for i in 1 2 3; do dd if=/dev/urandom of=/mnt/share/h/victim$i bs=1M count=4 2>/dev/null; done; sync; echo 3 > /proc/sys/vm/drop_caches; echo HARD d_prepared ok");
  await waitLine(page, "HARD d_prepared", 5 * MIN);
  for (const [i, mode] of [[1, "eio"], [2, "throw"], [3, "hang"]]) {
    await page.evaluate(([p, m]) => window.E8.faultOn(p, m, 1024 * 1024), [`mnt/share/h/victim${i}`, mode]);
    const t0 = Date.now();
    await send(page, `(cat /mnt/share/h/victim${i} > /dev/null 2>/root/d${i}.err; echo "HARD d_${mode} rc=$? $(head -c 100 /root/d${i}.err)") & P=$!; sleep 15; if kill -0 $P 2>/dev/null; then echo "HARD d_${mode}_state hung state=$(cut -d' ' -f3 /proc/$P/stat) wchan=$(cat /proc/$P/wchan 2>/dev/null)"; kill -9 $P; sleep 1; kill -0 $P 2>/dev/null && echo "HARD d_${mode}_kill9 still-alive" || echo "HARD d_${mode}_kill9 dead"; else echo "HARD d_${mode}_state finished"; fi`);
    const lines = await waitLine(page, `HARD d_${mode}_state`, 2 * MIN);
    const all = await page.evaluate(() => window.E8.hard.map((x) => x.l));
    out[mode] = { result: all.filter((l) => l.startsWith(`HARD d_${mode}`)), wall_s: sec(Date.now() - t0), faults: await page.evaluate(() => window.E8.faults.splice(0)) };
    await page.evaluate(() => window.E8.faultOff());
    // Le montage répond-il encore ?
    // Toujours victim1 : après un "throw"/"hang", la page de victimN reste verrouillée par le processus bloqué en état D.
    await send(page, `echo "HARD d_${mode}_after $(ls /mnt/share/h | wc -l) files, cat victim1: $(cat /mnt/share/h/victim1 | wc -c) bytes, new file: $(echo ok > /mnt/share/h/after_${mode} && cat /mnt/share/h/after_${mode})"`);
    const after = await waitLine(page, `HARD d_${mode}_after`, 2 * MIN);
    out[mode].after = after.pop();
    log("disconnect", mode, JSON.stringify(out[mode].result), out[mode].after);
  }
  return out;
}

// ---------- boucle principale ----------
const profileDir = fs.mkdtempSync(path.join(os.tmpdir(), "e8-chrome-"));
const context = await chromium.launchPersistentContext(profileDir, { channel: "chrome", headless: !args.headed });
const browser = context.browser();
log("navigateur chrome", browser?.version(), "profil temporaire");

for (let n = FIRST; n < FIRST + RUNS; n++) {
  const result = { run: n, date: new Date().toISOString(), browser: browser?.version(), headless: !args.headed, v86: "0.5.473 (npm, E1)", ports: { server: PORT } };
  try {
    if (!args["only-harden"]) {
      const { page, boot } = await bootPage(context, "boot");
      result.boot = boot;
      result.bench = {};
      result.bench.share = await phaseBench(page, "/mnt/share", "share");
      result.bench.disk = await phaseBench(page, "/mnt/disk", "disk");
      result.bench.tmpfs = await phaseBench(page, "/mnt/tmp2", "tmpfs");
      result.memory_after_bench = await processMemory(browser);
      if (args.wine) {
        await send(page, "mkdir -p /mnt/disk; mount -t ext4 /dev/sda /mnt/disk; sh /root/wine9p.sh");
        const t0 = Date.now();
        await waitMark(page, "wine-done", 40 * MIN);
        const lines = await page.evaluate(() => window.E8.lines.map((x) => x.l));
        result.wine = { wall_s: sec(Date.now() - t0), res: parseRes(lines, "wine") };
        log("wine", JSON.stringify(result.wine));
      }
      await page.close();
    }
    if (!args["no-harden"]) {
      const { page, boot } = await bootPage(context, "boot-harden");
      result.harden_boot = boot;
      result.harden = await phaseHarden(page, false);
      result.raw9p = await phaseRaw(page);
      result.disconnect = await phaseDisconnect(page);
      result.memory_after_harden = await processMemory(browser);
      await page.close();
      if (!args["no-2g"]) {
        const { page: p2 } = await bootPage(context, "boot-2g");
        const t0 = Date.now();
        await send(p2, "mkdir -p /mnt/share && mount -t 9p -o trans=virtio,cache=loose host9p /mnt/share && mkdir -p /mnt/share/h && cd /mnt/share/h && echo \"MARK harden-before-2G $(cut -d' ' -f1 /proc/uptime)\" && (truncate -s 2G sparse2; echo \"HARD c_sparse_2G rc=$? $(stat -c '%s %b' sparse2 2>&1)\"; dd if=/dev/zero of=sparse2 bs=4k seek=524287 count=1 conv=notrunc 2>&1 | tail -1; echo \"HARD c_sparse_2G_write rc=$?\"; rm -f sparse2; echo \"HARD c_sparse_2G_rm rc=$?\"; echo \"MARK harden-after-2G $(cut -d' ' -f1 /proc/uptime)\")");
        const crash = new Promise((r) => p2.once("crash", () => r("crash")));
        const done = waitMark(p2, "harden-after-2G", 5 * MIN).then(() => "done", (e) => "timeout: " + e.message.slice(0, 80));
        const outcome = await Promise.race([done, crash]);
        const hard = outcome === "crash" ? [] : await p2.evaluate(() => window.E8.hard.map((x) => x.l)).catch(() => []);
        const mem = outcome === "crash" ? null : await p2.evaluate(() => window.E8.hostMem()).catch(() => null);
        result.sparse2g = { outcome, wall_s: sec(Date.now() - t0), hard, hostMem: mem, memory: await processMemory(browser) };
        log("2G", JSON.stringify(result.sparse2g));
        await p2.close().catch(() => {});
      }
    }
  } catch (e) {
    result.error = String(e.stack || e);
    log("ERREUR run", n, e.message);
  }
  fs.writeFileSync(path.join(ROOT, `results-${n}.json`), JSON.stringify(result, null, 2));
  log(`results-${n}.json écrit`);
}
await context.close();
server.kill();
fs.rmSync(profileDir, { recursive: true, force: true });
log("profil temporaire supprimé");
process.exit(0);
