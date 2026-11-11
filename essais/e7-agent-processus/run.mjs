// Essai E7 — mesure automatique (Playwright + Chrome installé, headless ; dérivé de E17/run.mjs).
// Usage : node run.mjs [--runs 3] [--first 1] [--port 8144] [--mem 1024] [--disk disk-b1.img] [--headed]
// Pour chaque run n, dans un contexte Chrome neuf : boot à froid → agent (ping, list-processes) → Xorg + twm →
// montages (ext4 de b1, partage 9p lié) → inotify → A = wine notepad (b1) → écritures hors compte → B = wine
// winemine (b2), C = xterm (b2) → fenêtres → arbres (kill pid/group/cgroup) → console d'usager b2 (R134.5,
// programme graphique et démon lancés puis console fermée, 167 A) → seconde console : bombe de processus →
// partage disparu (EIO) → disque ext4 (agent en marche, Node sur l'image exportée) → open-url → kill A (critère :
// B et la VM continuent) → kill de toute la bouteille b1 → mémoire. Écrit results-<n>.json et images/e7-run<n>*.png.
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";
import { openExt4 } from "./ext4-ls.mjs";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const RUNS = Number(args.runs || 3), FIRST = Number(args.first || 1), PORT = Number(args.port || 8144), MEM = Number(args.mem || 1024);
const DISK = args.disk || "disk-b1.img", HEADLESS = !args.headed;
const BASE = `http://localhost:${PORT}`;
const MIN = 60_000;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const sec = (ms) => (ms == null ? null : Math.round(ms) / 1000);

const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
process.on("exit", () => server.kill());
await sleep(500);
const stats = async () => (await fetch(`${BASE}/__stats`)).json();

let page;
const agent = (req, timeoutMs = 60000) => page.evaluate(([r, t]) => window.E7.agent(r, t).catch((e) => ({ ok: false, error: String(e.message || e) })), [req, timeoutMs]);
const events = (from = 0) => page.evaluate((s) => window.E7.agentEvents.slice(s), from);
const eventCount = () => page.evaluate(() => window.E7.agentEvents.length);
async function waitOutput(kind, start, until, timeoutMs) {
  const t0 = Date.now(); const re = new RegExp(until, "m"); let out = "";
  while (Date.now() - t0 < timeoutMs) { out = await page.evaluate(([k, s]) => window.E7[k].slice(s), [kind, start]); if (re.test(out)) return { matched: true, ms: Date.now() - t0, output: out }; await sleep(150); }
  return { matched: false, ms: Date.now() - t0, output: out };
}
// Le tty fait écho de la commande : la marque de fin est écrite « E7""END » dans la commande et « E7END » dans la sortie.
async function serial(text, until, timeoutMs = 60000) { const start = await page.evaluate(() => window.E7.serial.length); await page.evaluate((t) => window.E7.send(t + "\n"), text); return waitOutput("serial", start, until, timeoutMs); }
const sh = (text, timeoutMs = 60000) => serial(`${text}; echo E7""END`, "^E7END", timeoutMs);
async function cons(text, until, timeoutMs = 60000) { const start = await page.evaluate(() => window.E7.console.length); if (text !== null) await page.evaluate((t) => window.E7.consoleSend(t), text); return waitOutput("console", start, until, timeoutMs); }
const csh = (text, timeoutMs = 60000) => cons(`${text}; echo C7""END\n`, "^C7END", timeoutMs);
const exec = (prefix, program, argv = [], extra = {}) => agent({ op: "exec-in-prefix", prefix, program, args: argv, timeout_ms: 60000, ...extra }, 130000);
const procs = async () => { const r = await agent({ op: "list-processes" }); return { ms: r._ms, list: r.processes || [], error: r.error }; };
const windows = async () => { const r = await agent({ op: "list-windows" }, 60000); return { ms: r._ms, list: r.windows || [], error: r.error }; };
async function waitWindow(re, timeoutMs, pid) {
  const t0 = Date.now(); let last = null, polls = 0;
  while (Date.now() - t0 < timeoutMs) { last = await windows(); polls++; const w = last.list.find((x) => re.test(x.title) && (!pid || x.pid === pid)); if (w) return { ms: Date.now() - t0, window: w, polls, list_ms: last.ms }; await sleep(1000); }
  return { ms: Date.now() - t0, window: null, polls, list: last?.list, error: last?.error };
}
async function waitGone(pid, timeoutMs = 30000) { const t0 = Date.now(); let n = 0; while (Date.now() - t0 < timeoutMs) { const p = await procs(); n++; if (!p.list.some((x) => x.pid === pid && x.state !== "Z")) return { ms: Date.now() - t0, polls: n }; await sleep(100); } return { ms: Date.now() - t0, polls: n, still: true }; }
const byPrefix = (list, prefix) => list.filter((p) => p.prefix === prefix && p.state !== "Z");
const summary = (list) => list.map((p) => `${p.pid}/${p.ppid} pg${p.pgid} ${p.prefix || "-"} ${p.comm}`);
async function processMemory(browser) {
  try {
    const cdp = await browser.newBrowserCDPSession(); const { processInfo } = await cdp.send("SystemInfo.getProcessInfo"); await cdp.detach();
    const out = execFileSync("pwsh", ["-NoProfile", "-Command", `Get-Process -Id ${processInfo.map((p) => p.id).join(",")} -ErrorAction SilentlyContinue | Select-Object Id,WorkingSet64,PrivateMemorySize64 | ConvertTo-Json -Compress`], { encoding: "utf8" });
    const procs = JSON.parse(out || "[]"); const list = (Array.isArray(procs) ? procs : [procs]).map((p) => ({ pid: p.Id, type: processInfo.find((x) => x.id === p.Id)?.type, private_MiB: Math.round(p.PrivateMemorySize64 / 1048576) }));
    return { total_private_MiB: list.reduce((s, p) => s + p.private_MiB, 0), biggest_renderer_private_MiB: Math.max(0, ...list.filter((p) => p.type === "renderer").map((p) => p.private_MiB)) };
  } catch (e) { return { error: String(e) }; }
}
async function pageMemory() { const cdp = await page.context().newCDPSession(page); await cdp.send("Performance.enable"); const { metrics } = await cdp.send("Performance.getMetrics"); await cdp.detach(); return { jsHeapUsed_MiB: Math.round(metrics.find((m) => m.name === "JSHeapUsedSize").value / 1048576) }; }
const stat = (xs) => { const v = xs.filter((x) => typeof x === "number").sort((a, b) => a - b); return v.length ? { n: v.length, min: v[0], median: v[Math.floor(v.length / 2)], max: v[v.length - 1], mean: Math.round(v.reduce((a, b) => a + b, 0) / v.length * 10) / 10 } : null; };
const hostRead = (name) => { const t0 = performance.now(); try { const e = openExt4(path.join(ROOT, "images", name)); const l = e.walk("/"); e.close(); return { ms: Math.round((performance.now() - t0) * 10) / 10, count: l.length, files: l.filter((x) => !x.dir).map((x) => `${x.size} ${x.uid} ${x.path}`).sort() }; } catch (e) { return { error: String(e.message) }; } };

const browser = await chromium.launch({ channel: "chrome", headless: HEADLESS });
log("chrome", browser.version(), "runs", RUNS, "disk", DISK);

for (let n = FIRST; n < FIRST + RUNS; n++) {
  const R = { run: n, date: new Date().toISOString(), browser: { channel: "chrome", version: browser.version(), headless: HEADLESS }, memory_size_MiB: MEM, v86: "0.5.473 (npm, lib de E17)", image: "e7-fs.json (Debian trixie i386 + WineHQ 11.0 + agent)", steps: {}, errors: [] };
  const step = async (name, fn) => { const t0 = Date.now(); try { R.steps[name] = await fn(); } catch (e) { R.errors.push({ step: name, error: String(e.stack || e) }); log("ERREUR", name, e.message); R.steps[name] = { error: String(e.message || e) }; } R.steps[name].wall_s = sec(Date.now() - t0); log(name, JSON.stringify(R.steps[name]).slice(0, 300)); };
  const context = await browser.newContext();
  page = await context.newPage();
  page.on("pageerror", (e) => log("pageerror", e.message)); page.on("crash", () => log("CRASH renderer"));
  let A, B, C, console1 = null, console2 = null;
  try {
    await fetch(`${BASE}/__reset`);
    const u = new URL(`${BASE}/boot.html`); u.searchParams.set("mem", String(MEM)); u.searchParams.set("disk", DISK);
    const tNav = Date.now(); await page.goto(u.toString());

    await step("boot", async () => {
      await page.waitForFunction(() => window.E7.marks.some((m) => m.name === "shell-prompt"), null, { timeout: 15 * MIN, polling: 250 });
      const marks = await page.evaluate(() => window.E7.marks); const ready = marks.find((m) => m.name === "boot-ready");
      const ev = await events(); const agentReady = ev.find((e) => e.event === "ready");
      return { nav_to_prompt_s: sec(Date.now() - tNav), page_to_boot_ready_s: sec(ready?.t), guest_uptime_s: ready?.uptime, agent_ready_s: sec(agentReady?.t), agent_ready: agentReady, disk_load: await page.evaluate(() => window.E7.diskLoad), download: await stats() };
    });

    await step("inject_guest_files", async () => {
      // Fichiers corrigés après la construction de l'image (E7 : liste des fenêtres plus rapide, contrôles, bombe, x-up) :
      // injectés par 9p avant leur première lecture par l'invité (piège E17 : un fichier déjà lu n'est pas remplacé).
      const files = [["guest/list-windows.sh", "usr/local/lib/e7/list-windows.sh"], ["guest/verify-console.sh", "usr/local/lib/e7/verify-console.sh"], ["guest/bomb.sh", "usr/local/lib/e7/bomb.sh"], ["guest/x-up.sh", "root/x-up.sh"], ["images/e7-rawsock", "usr/local/bin/e7-rawsock"]];
      for (const [src, dst] of files) await page.evaluate(([p, b]) => window.E7.writeBytesB64(p, b), [dst, fs.readFileSync(path.join(ROOT, src)).toString("base64")]);
      const r = await sh("chmod 755 /usr/local/lib/e7/*.sh /root/x-up.sh /usr/local/bin/e7-rawsock; modprobe sd_mod; ls -l /usr/local/bin/e7-rawsock");
      return { files: files.map((f) => f[1]), ok: /e7-rawsock/.test(r.output) };
    });

    await step("agent_ping", async () => {
      const r = []; for (let i = 0; i < 10; i++) { const p = await agent({ op: "ping" }); r.push(p._ms); if (!p.ok) throw new Error(p.error); }
      const p = await agent({ op: "ping" });
      const size = (await sh("ls -l /usr/local/bin/e7-agent; grep -E 'VmRSS|VmSize' /proc/$(pgrep -x e7-agent)/status; ls -l /dev/hvc*")).output;
      return { rtt_ms: stat(r), rss_kb: p.rss_kb, cgroup: p.cgroup, info: size.split("\n").filter((l) => /e7-agent|Vm|hvc/.test(l) && !/echo/.test(l)) };
    });

    await step("list_processes_idle", async () => { const r = []; let last; for (let i = 0; i < 5; i++) { last = await procs(); r.push(last.ms); } return { ms: stat(r), count: last.list.length, zombies: last.list.filter((p) => p.state === "Z").length, sample: summary(last.list).slice(0, 12) }; });

    await step("x_up", async () => { const r = await serial("sh /root/x-up.sh", "MARK x-(ready|failed)", 3 * MIN); return { ms: r.ms, ok: /x-ready/.test(r.output), lines: r.output.split("\n").filter((l) => /^X /.test(l)) }; });

    await step("mounts_setup", async () => {
      const r = await sh("mount /dev/sda /home/wine-b1/disk; mount --bind /srv/share-b1 /home/wine-b1/share; grep -E 'sda|share' /proc/mounts");
      const m = await agent({ op: "mounts" });
      return { serial: r.output.split("\n").filter((l) => /^(\/dev|host9p)/.test(l)), mounts_ms: m._ms, mounts: m.mounts };
    });

    await step("inotify_start", async () => {
      const r = await sh("sysctl -q -w fs.inotify.max_user_watches=524288; rm -f /tmp/ino.log; (inotifywait -m -r -e create,modify,moved_to,attrib,delete --exclude '^/tmp/ino' -o /tmp/ino.log /tmp /var /dev/shm /run /etc /root /home/wine-b2 /usr/share /usr/lib /usr/local /opt /srv /mnt 2>/tmp/ino.err &) ; sleep 1", 10 * MIN);
      let ready = false; const t0 = Date.now();
      while (Date.now() - t0 < 10 * MIN) { const w = await sh("grep -c . /tmp/ino.err; cat /tmp/ino.err | head -3"); if (/Watches established/.test(w.output)) { ready = true; break; } await sleep(3000); }
      return { setup_ms: Date.now() - t0 + r.ms, ready };
    });

    await step("launch_A_notepad_b1", async () => {
      const t0 = Date.now(); A = await agent({ op: "launch", prefix: "b1", program: "wine", args: ["notepad"] }); if (!A.ok) throw new Error(A.error);
      const w = await waitWindow(/Notepad/, 6 * MIN); await sleep(3000);
      A.program_pid = w.window?.pid;  // pid réel de notepad.exe (par _NET_WM_PID) ; A.pid est celui du lancement (start.exe de Wine)
      const launched = (await procs()).list.find((p) => p.pid === A.pid);
      return { launch: A, launch_pid_comm: launched?.comm, program_pid: A.program_pid, visible_s: sec(w.ms), window: w.window, polls: w.polls, list_windows_ms: w.list_ms, elapsed_s: sec(Date.now() - t0), error: w.error };
    });

    await step("writes_outside_account", async () => {
      await sh("pkill inotifywait; sleep 1; sort -u /tmp/ino.log > /tmp/ino.sorted; wc -l /tmp/ino.sorted");
      const raw = (await serial("cat /tmp/ino.sorted; echo E7\"\"END", "^E7END", 2 * MIN)).output;
      const lines = raw.split("\n").map((l) => l.trim()).filter((l) => /^[A-Z_,]+ \//.test(l));
      const paths = [...new Set(lines.map((l) => l.replace(/^[A-Z_,]+ /, "")))].filter((p) => !p.startsWith("/tmp/ino"));
      const byTop = {}; for (const p of paths) { const k = p.split("/").slice(0, 3).join("/"); byTop[k] = (byTop[k] || 0) + 1; }
      const readable = {};
      for (const p of paths.slice(0, 40)) { const r = await exec("b2", "test", ["-r", p]); readable[p] = r.exit_code === 0 ? "lisible par b2" : `non lisible par b2 (rc ${r.exit_code})`; }
      const perms = (await sh("ls -ld /tmp/.X11-unix /tmp/.X11-unix/X0 /tmp/.wine-1001 /tmp/.wine-1001/* /home/wine-b1 /home/wine-b1/.wine /dev/shm /var/tmp 2>&1 | grep -v echo")).output.split("\n").filter((l) => /^[dlsc-]/.test(l));
      return { events: lines.length, paths_outside_b1_home: paths.length, by_top_dir: byTop, paths: paths.slice(0, 120), readable_by_b2: readable, perms, events_sample: lines.slice(0, 30) };
    });

    await step("launch_B_winemine_b2", async () => {
      const t0 = Date.now(); B = await agent({ op: "launch", prefix: "b2", program: "wine", args: ["winemine"] }); if (!B.ok) throw new Error(B.error);
      const w = await waitWindow(/WineMine/, 6 * MIN); return { launch: B, visible_s: sec(w.ms), window: w.window, elapsed_s: sec(Date.now() - t0), error: w.error };
    });
    await step("launch_C_xterm_b2", async () => {
      const t0 = Date.now(); C = await agent({ op: "launch", prefix: "b2", program: "xterm", args: ["-T", "xterm-b2", "-geometry", "60x10+600+500"] }); if (!C.ok) throw new Error(C.error);
      const w = await waitWindow(/xterm-b2/, 2 * MIN); return { launch: C, visible_s: sec(w.ms), window: w.window, elapsed_s: sec(Date.now() - t0), error: w.error };
    });
    await page.locator("#screen_container canvas").screenshot({ path: path.join(ROOT, "images", `e7-run${n}-abc.png`) }).catch(() => {});

    await step("processes_with_ABC", async () => {
      const r = []; let last; for (let i = 0; i < 5; i++) { last = await procs(); r.push(last.ms); }
      const wr = []; let w; for (let i = 0; i < 3; i++) { w = await windows(); wr.push(w.ms); }
      return { list_ms: stat(r), count: last.list.length, zombies: last.list.filter((p) => p.state === "Z").length, b1: summary(byPrefix(last.list, "b1")), b2: summary(byPrefix(last.list, "b2")), windows_ms: stat(wr), windows: w.list };
    });

    await step("tree_kill", async () => {
      const out = {};
      for (const scope of ["pid", "group", "cgroup"]) {
        const l = await agent({ op: "launch", prefix: "b1", program: "/bin/sh", args: ["/usr/local/lib/e7/tree.sh", "900"] }); if (!l.ok) throw new Error(l.error);
        await sleep(2500);
        const before = byPrefix((await procs()).list, "b1").filter((p) => /sleep|^sh$/.test(p.comm));
        const k = scope === "cgroup" ? await agent({ op: "kill", scope: "cgroup", prefix: "b1", seq: l.seq, signal: 9 }) : await agent({ op: "kill", scope, pid: l.pid, signal: 9 });
        await sleep(1500);
        const after = byPrefix((await procs()).list, "b1").filter((p) => /sleep|^sh$/.test(p.comm));
        out[scope] = { launch_pid: l.pid, cgroup: l.cgroup, before: before.map((p) => `${p.pid}/${p.ppid} pgid=${p.pgid} ${p.comm}`), kill: { ok: k.ok, count: k.count, ms: k.ms, rtt_ms: k._ms, error: k.error }, survivors: after.map((p) => `${p.pid}/${p.ppid} pgid=${p.pgid} ${p.comm}`) };
        if (after.length) { await agent({ op: "kill", scope: "cgroup", prefix: "b1", seq: l.seq, signal: 9 }); await sleep(500); }
      }
      return out;
    });

    // --- console d'usager b2 (hvc2) : R134.5, puis programme graphique + démon lancés depuis la console, puis fermeture (167 A).
    await step("console_open", async () => {
      const r = await agent({ op: "open-console", prefix: "b2" }); if (!r.ok) throw new Error(r.error); console1 = r;
      const p = await cons(null, "\\$ $", 20000);
      const id = await csh("id; tty", 20000);
      return { open: r, prompt_ms: p.ms, id: id.output.split("\n").filter((l) => /uid=|\/dev\//.test(l)) };
    });
    await step("console_checks_R134_5", async () => {
      const r = await cons(`sh /usr/local/lib/e7/verify-console.sh ${A?.program_pid || A?.pid || 1} 2>&1\n`, "RES verify-done", 5 * MIN);
      const res = r.output.split("\n").map((l) => l.trim()).filter((l) => l.startsWith("RES "));
      const checks = res.filter((l) => l.startsWith("RES check ")).map((l) => { const m = /^RES check (\S+) (\S+) ?(.*)$/.exec(l); return { name: m[1], result: m[2], detail: m[3] }; });
      const agentAlive = await agent({ op: "ping" });
      return { ms: r.ms, who: res.find((l) => l.startsWith("RES who")), checks, held: checks.filter((c) => c.result === "HELD").length, leak: checks.filter((c) => c.result === "LEAK").map((c) => c.name), agent_ping_after_ms: agentAlive._ms, agent_ok: agentAlive.ok };
    });
    await step("console_graphical_and_daemon", async () => {
      const r = await csh("sleep 700 & echo DAEMON=$!; (wine winecfg >/dev/null 2>&1 &)", 30000);
      const w = await waitWindow(/Wine configuration/, 4 * MIN);
      return { daemon_pid: Number(r.output.match(/DAEMON=(\d+)/)?.[1]), winecfg_visible_s: sec(w.ms), window: w.window, error: w.error };
    });
    await step("console_close", async () => {
      const e0 = await eventCount(); const daemon = R.steps.console_graphical_and_daemon?.daemon_pid;
      const before = byPrefix((await procs()).list, "b2");
      const k = await agent({ op: "close-console" });
      await sleep(3000);
      const after = byPrefix((await procs()).list, "b2"); const w = await windows();
      const r = { close: { ok: k.ok, error: k.error, pid: k.pid }, events: (await events(e0)).map((e) => e.event), b2_before: before.length, b2_after: after.length, shell_gone: !after.some((p) => p.pid === console1.pid), daemon_sleep_still: after.some((p) => p.pid === daemon), winecfg_window_still: w.list.some((x) => /Wine configuration/.test(x.title)), winecfg_cgroup: after.find((p) => /winecfg/.test(p.comm))?.cgroup };
      const kc = await agent({ op: "kill", scope: "cgroup", prefix: "b2", seq: console1.seq, signal: 9 });
      await sleep(1500); const after2 = byPrefix((await procs()).list, "b2"); const w2 = await windows();
      return { ...r, cgroup_kill_of_console: { ok: kc.ok, count: kc.count, ms: kc.ms }, after_cgroup_kill: { daemon_still: after2.some((p) => p.pid === daemon), winecfg_window_still: w2.list.some((x) => /Wine configuration/.test(x.title)), B_window_still: w2.list.some((x) => /WineMine/.test(x.title)), C_window_still: w2.list.some((x) => /xterm-b2/.test(x.title)), b2_count: after2.length } };
    });

    await step("fork_bomb", async () => {
      const r = await agent({ op: "open-console", prefix: "b2" }); if (!r.ok) throw new Error(r.error); console2 = r;
      await cons(null, "\\$ $", 20000);
      const pingBefore = (await agent({ op: "ping" }))._ms, listBefore = (await procs()).ms;
      await cons("sh /usr/local/lib/e7/bomb.sh\n", "bomb start", 20000);
      const pings = [], counts = [];
      for (let i = 0; i < 6; i++) { await sleep(2000); const p = await agent({ op: "ping" }, 30000); pings.push(p.ok ? p._ms : "timeout"); const l = await procs(); counts.push({ b2: byPrefix(l.list, "b2").length, total: l.list.length, list_ms: l.ms }); }
      const b1alive = byPrefix((await procs()).list, "b1").map((p) => p.comm);
      const wins = (await windows()).list.map((w) => w.title);
      const k = await agent({ op: "kill", scope: "cgroup", prefix: "b2", seq: console2.seq, signal: 9 }, 60000);
      await sleep(1000); const l = await procs(); const pingAfter = (await agent({ op: "ping" }))._ms;
      return { ping_before_ms: pingBefore, list_before_ms: listBefore, pings_during_ms: pings, counts_during: counts, b1_alive_during: b1alive, windows_during: wins, kill_console_cgroup: { ok: k.ok, count: k.count, ms: k.ms, rtt_ms: k._ms, error: k.error }, b2_after: summary(byPrefix(l.list, "b2")), total_after: l.list.length, ping_after_ms: pingAfter, B_window_after: (await windows()).list.some((x) => /WineMine/.test(x.title)) };
    });

    await step("mount_gone", async () => {
      const before = await exec("b1", "cat", ["/home/wine-b1/share/hello.txt"]);
      const m0 = await agent({ op: "mounts" });
      const armed = await page.evaluate((p) => window.E7.faultOn(p), "srv/share-b1");
      const t0 = Date.now();
      const m1 = await agent({ op: "mounts", timeout_ms: 15000 }, 40000);
      const detect_ms = Date.now() - t0;
      const cached = await exec("b1", "cat", ["/home/wine-b1/share/hello.txt"]);
      const fresh = await exec("b1", "cat", ["/home/wine-b1/share/never-read.txt"]);
      const ls = await exec("b1", "ls", ["-la", "/home/wine-b1/share/"]);
      const lsSub = await exec("b1", "ls", ["/home/wine-b1/share/sub"]);
      const faults = await page.evaluate(() => window.E7.faults.length);
      await page.evaluate(() => window.E7.faultOff());
      const after = await exec("b1", "cat", ["/home/wine-b1/share/never-read.txt"]);
      const m2 = await agent({ op: "mounts" });
      const pick = (m) => m.mounts?.find((x) => x.path.includes("share"));
      return { armed, before: { exit: before.exit_code, out: before.output, ms: before.ms }, mounts_before: pick(m0), mounts_during: pick(m1), mounts_during_rtt_ms: m1._ms, detect_ms, cached_read: { exit: cached.exit_code, out: cached.output, ms: cached.ms }, fresh_read: { exit: fresh.exit_code, out: fresh.output, ms: fresh.ms }, ls: { exit: ls.exit_code, out: ls.output, ms: ls.ms }, ls_sub: { exit: lsSub.exit_code, out: lsSub.output, ms: lsSub.ms }, eio_replies: faults, after_restore: { exit: after.exit_code, out: after.output, ms: after.ms }, mounts_after: pick(m2) };
    });

    await step("disk", async () => {
      const list1 = await exec("b1", "find", ["/home/wine-b1/disk", "-printf", "%y %s %u %p\\n"]);
      const w = await exec("b1", "wine", ["cmd", "/c", "echo written by wine cmd > Z:\\home\\wine-b1\\disk\\drive_c\\from-wine.txt & dir Z:\\home\\wine-b1\\disk\\drive_c"], { timeout_ms: 120000 });
      const cp = await exec("b1", "cp", ["/home/wine-b1/disk/drive_c/users/wine-b1/Documents/readme.txt", "/home/wine-b1/disk/drive_c/users/wine-b1/Documents/copy-by-exec.txt"]);
      const list2 = await exec("b1", "find", ["/home/wine-b1/disk", "-printf", "%y %s %u %p\\n"]);
      const guestFiles = list2.output.split("\n").filter((l) => l.startsWith("f ")).map((l) => { const m = /^f (\d+) (\S+) \/home\/wine-b1\/disk(.*)$/.exec(l); return m ? `${m[1]} ${m[2] === "wine-b1" ? 1001 : m[2]} ${m[3]}` : l; }).sort();
      const t1 = Date.now(); const export1 = await page.evaluate(() => window.E7.uploadDisk("disk-dirty.img")); const exportMs = Date.now() - t1;
      const dirty = hostRead("disk-dirty.img");
      const sync = await sh("sync");
      const export2 = await page.evaluate(() => window.E7.uploadDisk("disk-synced.img"));
      const synced = hostRead("disk-synced.img");
      const umount = await sh("umount /home/wine-b1/disk");
      const export3 = await page.evaluate(() => window.E7.uploadDisk("disk-unmounted.img"));
      const unmounted = hostRead("disk-unmounted.img");
      await sh("mount /dev/sda /home/wine-b1/disk");
      const same = (h) => h.files ? JSON.stringify(h.files) === JSON.stringify(guestFiles) : null;
      const diff = (h) => h.files ? { only_guest: guestFiles.filter((x) => !h.files.includes(x)), only_host: h.files.filter((x) => !guestFiles.includes(x)) } : null;
      return { agent_find_ms: list1.ms, agent_find_count: list1.output.split("\n").filter(Boolean).length, wine_cmd: { exit: w.exit_code, ms: w.ms, out: w.output.slice(0, 600) }, cp: { exit: cp.exit_code, ms: cp.ms }, guest_files: guestFiles, export: { bytes: [export1, export2, export3], first_export_ms: exportMs }, host_dirty: { ...dirty, consistent_with_guest: same(dirty), diff: diff(dirty) }, host_synced: { ...synced, consistent_with_guest: same(synced), diff: diff(synced) }, host_unmounted: { ...unmounted, consistent_with_guest: same(unmounted), diff: diff(unmounted) }, sync_ms: sync.ms, umount_ms: umount.ms };
    });

    await step("open_url", async () => { const e0 = await eventCount(); const r = await exec("b1", "e7-open-url", ["https://example.test/doc?x=1"]); await sleep(500); return { exit: r.exit_code, ms: r.ms, events: (await events(e0)).filter((e) => e.event === "open-url") }; });

    await step("kill_A", async () => {
      const before = await windows(); const e0 = await eventCount();
      const target = A.program_pid || A.pid;
      const k = await agent({ op: "kill", pid: target, signal: 15 });
      const gone = await waitGone(target, 30000);
      const t0 = Date.now(); let winGone = null; for (let i = 0; i < 40; i++) { const w = await windows(); if (!w.list.some((x) => /Notepad/.test(x.title))) { winGone = Date.now() - t0; break; } await sleep(500); }
      const after = await windows(); const p = await procs();
      return { kill: { ok: k.ok, rtt_ms: k._ms, ms: k.ms, error: k.error }, process_gone_ms: gone.ms, process_gone_polls: gone.polls, still: gone.still, window_gone_ms: winGone, exit_events: (await events(e0)).filter((e) => e.event === "exit"), launch_pid_still: p.list.some((x) => x.pid === A.pid && x.state !== "Z"), B_window_still: after.list.some((x) => /WineMine/.test(x.title)), C_window_still: after.list.some((x) => /xterm-b2/.test(x.title)), B_process_still: p.list.some((x) => x.pid === B.pid && x.state !== "Z"), vm_ping_ms: (await agent({ op: "ping" }))._ms, b1_remaining: summary(byPrefix(p.list, "b1")), windows_before: before.list.map((w) => `${w.prefix}:${w.title}`), windows_after: after.list.map((w) => `${w.prefix}:${w.title}`) };
    });
    await page.locator("#screen_container canvas").screenshot({ path: path.join(ROOT, "images", `e7-run${n}.png`) }).catch(() => {});

    await step("kill_bottle_b1", async () => {
      const k = await agent({ op: "kill", scope: "cgroup", prefix: "b1", signal: 9 }, 60000);
      await sleep(1000); const p = await procs(); const w = await windows();
      return { kill: { ok: k.ok, count: k.count, ms: k.ms, rtt_ms: k._ms, error: k.error }, b1_remaining: summary(byPrefix(p.list, "b1")), b2_remaining: summary(byPrefix(p.list, "b2")), windows: w.list.map((x) => `${x.prefix}:${x.title}`), zombies: p.list.filter((x) => x.state === "Z").length, vm_ping_ms: (await agent({ op: "ping" }))._ms };
    });

    await step("memory", async () => ({ page: await pageMemory(), browser: await processMemory(browser), guest: (await sh("free -k | head -2")).output.split("\n").filter((l) => /Mem/.test(l)), agent_rss_kb: (await agent({ op: "ping" })).rss_kb, rtt_all_ops: Object.entries((await page.evaluate(() => window.E7.rtts)).reduce((acc, r) => ((acc[r.op] ||= []).push(Math.round(r.ms * 10) / 10), acc), {})).map(([op, v]) => ({ op, ...stat(v) })) }));
    R.agent_log_tail = (await sh("tail -30 /var/log/e7-agent.log; cat /var/log/e7-agent.out")).output.split("\n").slice(0, 40);
    R.download = await stats();
  } catch (e) { R.errors.push({ step: "run", error: String(e.stack || e) }); log("ERREUR run", n, e.message); }
  fs.writeFileSync(path.join(ROOT, `results-${n}.json`), JSON.stringify(R, null, 2));
  log(`results-${n}.json écrit (${R.errors.length} erreurs)`);
  await context.close();
}
await browser.close();
server.kill();
process.exit(0);
