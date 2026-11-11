// Essai E14 — mesure automatique (Playwright + Chrome installé, headless ; dérivé d'E17).
// Usage : node run.mjs --guest dos|wine [--runs 3] [--first 1] [--port 8166] [--headed] [--mem N]
// DOS  : boot FreeDOS ; par fréquence de sondage (16/33/50/100 ms) : latence touche→écran (frappe d'une ligne),
//        défilement (dir /s), CPU du renderer ; puis KEYTEST (accords Alt/F), GFX (signal mode graphique),
//        EDIT (Ctrl+N, frappe, capture), CHOICE, mémoire. Écrit results-dos-<n>.json.
// Wine : boot Debian ; voie (a) VGA tty1 : wine cmd, consoletest.exe, code de sortie, Ctrl+C, latence par fréquence ;
//        voie (b) pty ttyS1 : idem + redimensionnement ; shell bash (ls, nano, less, vi) ; CPU/mémoire. results-wine-<n>.json.
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const GUEST = args.guest || "dos";
const RUNS = Number(args.runs || 3), FIRST = Number(args.first || 1);
const PORT = Number(args.port || 8166);
const HEADLESS = !args.headed;
const BASE = `http://localhost:${PORT}`;
const POLLS = [16, 33, 50, 100];
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const server = spawn(process.execPath, [path.join(ROOT, "server.mjs"), String(PORT)], { stdio: ["ignore", "inherit", "inherit"] });
process.on("exit", () => server.kill());
await sleep(500);
const browser = await chromium.launch({ channel: "chrome", headless: HEADLESS });
const version = browser.version();
log("chrome", version, HEADLESS ? "headless" : "headed", "guest", GUEST);

let page, context;
const ev = (js, arg) => page.evaluate(js, arg);
const E = (fn, arg) => page.evaluate(fn, arg);
async function keys(list, delay = 120, measure = true) {
  const n0 = await E(() => window.E14.keySamples.length);
  for (const k of list) {
    await E(([k, m]) => window.E14.sendKey(k, m), [k, measure]);
    if (measure) await page.waitForFunction(() => !window.E14.keyPending, null, { timeout: 5000, polling: 5 }).catch(() => {});
    await sleep(delay);
  }
  return E((n) => window.E14.keySamples.slice(n), n0);
}
const text = (s) => [...s].map((ch) => (ch === "\n" ? { key: "Enter" } : { key: ch }));
async function chord(k) { await E((k) => { window.E14.sendChord({ key: "Escape" }); window.E14.sendChord(k); }, k); await sleep(500); }
async function screen() { return E(() => window.E14.screen()); }
async function waitScreen(re, timeout = 30000) {
  const t0 = Date.now();
  try { await page.waitForFunction((r) => new RegExp(r).test((window.E14.screen() || []).join("\n")), re, { timeout, polling: 30 }); return { matched: true, ms: Date.now() - t0 }; }
  catch { return { matched: false, ms: Date.now() - t0, screen: await screen() }; }
}
async function cmd(textLine, until, timeout = 60000) {
  const t0 = Date.now();
  const start = await E(() => window.E14.serial.length);
  await E((t) => window.E14.send(t + "\n"), textLine);
  const re = until ? new RegExp(until, "m") : null;
  let out = "";
  while (Date.now() - t0 < timeout) {
    out = await E((s) => window.E14.serial.slice(s), start);
    if (re && re.test(out)) return { matched: true, ms: Date.now() - t0, output: out };
    if (!re && Date.now() - t0 > 1000) return { matched: null, ms: Date.now() - t0, output: out };
    await sleep(100);
  }
  return { matched: false, ms: Date.now() - t0, output: out };
}
async function setPoll(ms) { await E((m) => { window.E14.setPoll(m); window.E14.resetStats(); }, ms); }
async function stats() { return E(() => ({ pollStats: window.E14.pollStats, pollOverruns: window.E14.pollOverruns, keySamples: window.E14.keySamples, rejectedKeys: window.E14.rejectedKeys, graphical: window.E14.graphical, screenSizeEvents: window.E14.screenSizeEvents, events: window.E14.events.filter((e) => e.name !== "vt-unknown-csi" && e.name !== "vt-unknown-esc"), vtUnknown: window.E14.events.filter((e) => e.name.startsWith("vt-unknown")).map((e) => e.seq) })); }
const summarize = (samples) => {
  const vals = (f) => samples.map(f).filter((x) => x != null).sort((a, b) => a - b);
  const stat = (v) => (v.length ? { n: v.length, min: v[0], med: v[Math.floor(v.length / 2)], p90: v[Math.floor(v.length * 0.9)], max: v[v.length - 1], avg: Math.round((v.reduce((a, b) => a + b, 0) / v.length) * 10) / 10 } : null);
  return { change_ms: stat(vals((s) => s.change_ms)), render_ms: stat(vals((s) => s.render_ms)), timeouts: samples.filter((s) => s.timeout).length };
};
async function processInfo() { const cdp = await browser.newBrowserCDPSession(); const { processInfo } = await cdp.send("SystemInfo.getProcessInfo"); await cdp.detach(); return processInfo; }
async function cpu(ms = 5000) {
  const info = await processInfo(); const ids = info.map((p) => p.id).join(",");
  const ps = () => { const x = JSON.parse(execFileSync("pwsh", ["-NoProfile", "-Command", `Get-Process -Id ${ids} -ErrorAction SilentlyContinue | Select-Object Id,@{n='cpu';e={$_.TotalProcessorTime.TotalMilliseconds}} | ConvertTo-Json -Compress`], { encoding: "utf8" }) || "[]"); return Array.isArray(x) ? x : [x]; };
  const a = ps(); const t0 = Date.now(); await sleep(ms); const b = ps(); const dt = Date.now() - t0;
  const per = b.map((p) => ({ type: info.find((x) => x.id === p.Id)?.type, cpu_pct: Math.round(((p.cpu - (a.find((q) => q.Id === p.Id)?.cpu ?? p.cpu)) / dt) * 1000) / 10 }));
  return { window_ms: dt, renderer_pct: Math.max(0, ...per.filter((p) => p.type === "renderer").map((p) => p.cpu_pct)), gpu_pct: per.find((p) => p.type === "GPU")?.cpu_pct ?? null, total_pct: Math.round(per.reduce((s, p) => s + p.cpu_pct, 0) * 10) / 10 };
}
async function memory() {
  const info = await processInfo();
  const out = execFileSync("pwsh", ["-NoProfile", "-Command", `Get-Process -Id ${info.map((p) => p.id).join(",")} -ErrorAction SilentlyContinue | Select-Object Id,PrivateMemorySize64 | ConvertTo-Json -Compress`], { encoding: "utf8" });
  const procs = JSON.parse(out || "[]"); const list = (Array.isArray(procs) ? procs : [procs]).map((p) => ({ type: info.find((x) => x.id === p.Id)?.type, private_MiB: Math.round(p.PrivateMemorySize64 / 1048576) }));
  const cdp = await page.context().newCDPSession(page); await cdp.send("Performance.enable"); const { metrics } = await cdp.send("Performance.getMetrics"); await cdp.detach();
  return { jsHeapUsed_MiB: Math.round(metrics.find((m) => m.name === "JSHeapUsedSize").value / 1048576), renderer_private_MiB: Math.max(0, ...list.filter((p) => p.type === "renderer").map((p) => p.private_MiB)), total_private_MiB: list.reduce((s, p) => s + p.private_MiB, 0) };
}
async function shot(name) { const file = path.join(ROOT, "images", name + ".png"); await page.locator("#term").screenshot({ path: file }); return path.relative(ROOT, file).replace(/\\/g, "/"); }

// ------------------------------------------------------------------ DOS
async function runDos(n) {
  const result = { run: n, guest: "dos", date: new Date().toISOString(), browser: { channel: "chrome", version, headless: HEADLESS }, v86: "0.5.473 (npm)", image: "FD14LITE.img (FreeDOS 1.4 LiteUSB, 32 MiB) + E14", memory_MiB: 64 };
  const tNav = Date.now();
  await page.goto(`${BASE}/terminal.html?guest=dos&chord=1&poll=33`);
  const boot = await waitScreen("E14 READY", 60000);
  result.boot = { nav_to_prompt_s: Math.round(Date.now() - tNav) / 1000, screen_size_events: await E(() => window.E14.screenSizeEvents.map((e) => [e.w, e.h, e.bpp])) };
  log("boot", JSON.stringify(result.boot));
  result.by_poll = {};
  for (const P of POLLS) {
    await setPoll(P);
    await keys(text("cls\n"), 50, false); await sleep(300);
    const samples = await keys(text("echo Kazo terminal E14 abcdefghij\n"), 100, true);
    const typing = summarize(samples);
    await E(() => window.E14.resetStats());
    const t0 = Date.now();
    await keys(text("dir /s \\packages\n"), 30, false);
    const scroll = await waitScreen("bytes free[\\s\\S]*C:\\\\>$", 60000);
    const scrollWall = Date.now() - t0;
    const sScroll = await stats();
    await E(() => window.E14.resetStats());
    await keys(text("for %f in (1 2 3 4 5 6) do dir /s \\packages\n"), 30, false);
    await sleep(500);
    const cpuScroll = await cpu(5000);
    await waitScreen("bytes free[\\s\\S]*C:\\\\>$", 90000);
    await sleep(1500);
    await E(() => window.E14.resetStats());
    const cpuIdle = await cpu(5000);
    const sIdle = await stats();
    result.by_poll[P] = { typing, typing_samples: samples.map((s) => [s.key, s.change_ms, s.render_ms]), scroll: { wall_ms: scrollWall, matched: scroll.matched, poll: sScroll.pollStats, overruns: sScroll.pollOverruns }, cpu_scrolling: cpuScroll, cpu_idle: cpuIdle, poll_idle: sIdle.pollStats };
    log("poll", P, "typing", JSON.stringify(typing), "scroll", scrollWall, "ms", JSON.stringify(sScroll.pollStats), "cpu scroll", cpuScroll.renderer_pct, "idle", cpuIdle.renderer_pct);
  }
  await setPoll(33);
  // Lecture ansi (get_text_screen de l'adaptateur) contre lecture brute : coût par tour.
  await E(() => { window.E14.vgaRead = "ansi"; window.E14.resetStats(); });
  await keys(text("dir /s \\packages\n"), 30, false); await waitScreen("bytes free[\\s\\S]*C:\\\\>$", 60000); await sleep(1000);
  result.ansi_adapter = (await stats()).pollStats;
  await E(() => { window.E14.vgaRead = "raw"; window.E14.resetStats(); });
  // KEYTEST : touches du jeu Kazo + accords.
  await keys(text("cls\nkeytest\n"), 50, false); await sleep(600);
  await keys([{ key: "a" }, { key: "A" }, { key: "!" }, { key: "\\" }, { key: "ArrowUp" }, { key: "ArrowLeft" }, { key: "Tab" }, { key: "Backspace" }, { key: "a", ctrl: true }, { key: "Enter" }], 120, true);
  await chord({ key: "f" }); await chord({ key: "1" }); await chord({ key: "0" });
  await keys([{ key: "Escape" }], 300, false); await sleep(400);
  await keys(text("echo rc=%errorlevel%\n"), 50, false); await sleep(600);
  result.keytest = { screen: (await screen()).filter((l) => /\d\d\/\d\d|rc=/.test(l)).map((l) => l.trim()) };
  log("keytest", JSON.stringify(result.keytest));
  // GFX : signal mode graphique.
  const nEv = await E(() => window.E14.screenSizeEvents.length);
  await keys(text("gfx"), 50, false);
  const tG = performance.now(); await E(() => window.E14.sendKey({ key: "Enter" }, false));
  await page.waitForFunction(() => window.E14.graphical === true, null, { timeout: 5000, polling: 5 }).catch(() => {});
  const tGraph = performance.now() - tG;
  const gEvents = await E((n) => window.E14.screenSizeEvents.slice(n).map((e) => [e.w, e.h, e.bpp]), nEv);
  const screenInGfx = await screen();
  await keys([{ key: " " }], 100, false);
  await page.waitForFunction(() => window.E14.graphical === false, null, { timeout: 5000, polling: 5 }).catch(() => {});
  result.gfx = { enter_to_graphical_ms: Math.round(tGraph), events: gEvents, back_to_text: await E(() => window.E14.graphical === false), text_frozen_rows_in_gfx: screenInGfx.length };
  log("gfx", JSON.stringify(result.gfx));
  // CHOICE.
  await keys(text("cls\nchoice /c:yn Continuer\n"), 40, false); await sleep(800);
  await keys([{ key: "n" }], 200, false); await sleep(500);
  await keys(text("echo rc=%errorlevel%\n"), 40, false); await sleep(600);
  result.choice = { screen: (await screen()).filter((l) => /Continuer|rc=/.test(l)).map((l) => l.trim()) };
  // GUESS.
  await keys(text("cls\nguess\n"), 40, false); await sleep(600);
  let guessOk = false;
  for (const d of "0123456789") { await keys([{ key: d }], 250, false); const s = (await screen()).join("\n"); if (/OK, gagne/.test(s)) { guessOk = true; break; } }
  result.guess = { won: guessOk, screen: (await screen()).filter((l) => /Chiffre|OK/.test(l)).slice(-3).map((l) => l.trim()) };
  // EDIT : Ctrl+N (jeu Kazo), frappe, accord Alt+F pour le menu, capture.
  await keys(text("cls\nedit\n"), 40, false); await sleep(1500);
  await keys([{ key: "n", ctrl: true }], 300, false); await sleep(500);
  await keys(text("Texte tape dans FreeDOS EDIT depuis le terminal de Kazo (Ctrl+N).\nLigne 2."), 25, false); await sleep(400);
  await keys([{ key: "ArrowUp" }, { key: "Home" }], 150, false);
  const editScreen = await screen();
  result.edit = { shot: await shot(`e14-dos-run${n}-edit`), has_text: editScreen.some((l) => /Texte tape dans FreeDOS EDIT/.test(l)), title: editScreen[0].trim() };
  await chord({ key: "f" }); result.edit.menu_opened_by_chord = (await screen()).some((l) => /Save as/.test(l));
  await keys([{ key: "Escape" }], 300, false);
  log("edit", JSON.stringify(result.edit));
  result.memory = await memory();
  result.rejected_keys = (await stats()).rejectedKeys;
  return result;
}

// ------------------------------------------------------------------ Wine / Linux
let exitBase = 0;   // nombre d'événements « exit » déjà vus au lancement du programme (le code peut arriver avant waitExit)
async function vgaProgram(command, untilRe, timeout = 120000) {
  await E(() => window.E14.setRoute("vga"));
  exitBase = await E(() => window.E14.events.filter((e) => e.name === "exit").length);
  const t0 = Date.now();
  await cmd(`sh /root/e14/vga-run.sh ${command}`, null, 1000);
  const w = await waitScreen(untilRe, timeout);
  return { start_ms: Date.now() - t0, matched: w.matched };
}
async function ptyProgram(command, untilRe, timeout = 120000, opts = "") {
  await E(() => { window.E14.setRoute("pty"); window.E14.vt.resize(window.E14.vt.cols, window.E14.vt.rows); });
  exitBase = await E(() => window.E14.events.filter((e) => e.name === "exit").length);
  const t0 = Date.now();
  await cmd(`sh /root/e14/pty-run.sh ${opts} ${command}`, null, 1000);
  const w = await waitScreen(untilRe, timeout);
  return { start_ms: Date.now() - t0, matched: w.matched };
}
async function waitExit(timeout = 30000) {
  const n0 = exitBase;
  const t0 = Date.now();
  try { await page.waitForFunction((n) => window.E14.events.filter((e) => e.name === "exit").length > n, n0, { timeout, polling: 50 }); } catch { return { code: null, ms: Date.now() - t0 }; }
  return { code: await E(() => window.E14.events.filter((e) => e.name === "exit").at(-1).code), ms: Date.now() - t0 };
}
async function runWine(n) {
  const result = { run: n, guest: "wine", date: new Date().toISOString(), browser: { channel: "chrome", version, headless: HEADLESS }, v86: "0.5.473 (npm)", image: "Debian trixie i386 + WineHQ stable 11.0 (E17) + outils console", memory_MiB: Number(args.mem || 1024) };
  result.injected = null;
  const tNav = Date.now();
  await page.goto(`${BASE}/terminal.html?guest=wine&poll=33${args.mem ? "&mem=" + args.mem : ""}`);
  await page.waitForFunction(() => window.E14.marks.some((m) => m.name === "boot-ready"), null, { timeout: 20 * 60000, polling: 250 });
  await sleep(1500);
  const p = await cmd("echo PROMPT-OK", "PROMPT-OK", 60000);
  // Scripts invités injectés par 9p dans un dossier neuf (cache=loose : un fichier déjà lu ne se rafraîchit pas).
  await cmd("mkdir -p /root/e14; echo MK-OK", "^MK-OK", 20000);
  for (const f of ["vga-run.sh", "pty-run.sh", "pty-child.sh", "shell-probe.sh"]) {
    const data = fs.readFileSync(path.join(ROOT, "guest", f));
    await E(([p, b64]) => window.E14.emu.create_file(p, Uint8Array.from(atob(b64), (c) => c.charCodeAt(0))), ["root/e14/" + f, data.toString("base64")]);
  }
  // cache=loose : le dossier tout juste créé peut encore être listé vide côté invité ; on attend que les 4 fichiers soient vus.
  const seen = await cmd("for i in 1 2 3 4 5 6 7 8 9 10; do n=$(ls /root/e14 | wc -l); [ \"$n\" -ge 4 ] && break; sleep 1; done; chmod +x /root/e14/*.sh; ls -la /root/e14; echo CH-OK", "^CH-OK", 40000);
  result.injected = seen.output.split("\n").filter((l) => /\.sh/.test(l)).map((l) => l.trim());
  result.boot = { nav_to_prompt_s: Math.round(Date.now() - tNav) / 1000, tty1_text_mode: await E(() => window.E14.graphical === false) };
  log("boot", JSON.stringify(result.boot));
  const probe = await cmd("fgconsole; cat /sys/class/tty/tty1/active 2>/dev/null; ls /dev/ttyS1 /dev/tty1; cat /proc/tty/driver/serial | sed -n 2,3p; echo PROBE-DONE", "^PROBE-DONE", 20000);
  result.probe = probe.output.split("\n").slice(1, -1).map((l) => l.trim()).filter(Boolean);

  // ---- voie (a) : console VGA tty1
  result.vga = {};
  const a1 = await vgaProgram("sh -c 'echo hello-tty1; stty size'", "hello-tty1", 20000);
  result.vga.hello = { ...a1, screen_tail: (await screen()).filter((l) => l.trim()).slice(-2) };
  const ex0 = await waitExit(10000);
  result.vga.hello.exit = ex0;
  // Latence « écriture invité → écran » par ttyS0 → tty1.
  const gw = [];
  for (let i = 0; i < 5; i++) { await E(() => window.E14.guestWrite()); await page.waitForFunction(() => !window.E14.keyPending, null, { timeout: 5000, polling: 5 }).catch(() => {}); gw.push(await E(() => window.E14.keySamples.at(-1))); await sleep(200); }
  result.vga.guest_write = summarize(gw);
  log("vga guest-write", JSON.stringify(result.vga.guest_write));
  // wine cmd sur tty1.
  const t0 = Date.now();
  const wc = await vgaProgram("wine cmd", "[A-Z]:\\\\[^>]*>", 300000);
  result.vga.wine_cmd = { start_ms: wc.start_ms, matched: wc.matched, first_screen: (await screen()).filter((l) => l.trim()).slice(-4) };
  log("vga wine cmd", JSON.stringify(result.vga.wine_cmd));
  if (wc.matched) {
    result.vga.by_poll = {};
    for (const P of POLLS) {
      await setPoll(P);
      const samples = await keys(text("echo Kazo E14 abcdefghij\n"), 100, true);
      await sleep(300);
      result.vga.by_poll[P] = { typing: summarize(samples), samples: samples.map((s) => [s.key, s.change_ms, s.render_ms]) };
      log("vga poll", P, JSON.stringify(result.vga.by_poll[P].typing));
    }
    await setPoll(33);
    await keys(text("ver\ndir c:\\windows\\system32\\*.exe\n"), 40, false); await waitScreen("[A-Z]:\\\\[^>]*>\\s*$", 60000);
    result.vga.dir_screen = (await screen()).filter((l) => l.trim()).slice(-6);
    await keys(text("color 1e\necho couleur\n"), 40, false); await sleep(1500);
    result.vga.shot_cmd = await shot(`e14-wine-run${n}-vga-cmd`);
    // consoletest.exe dans cmd : stdin/stdout, couleurs, code de sortie.
    await keys(text("z:\\root\\consoletest.exe\n"), 40, false); await waitScreen("consoletest:", 60000);
    const cs = await keys(text("bonjour\ncolor\nexit 5\n"), 60, true);
    await waitScreen("sortie avec 5", 30000); await sleep(500);
    await keys(text("echo rc=%errorlevel%\n"), 40, false); await sleep(1500);
    result.vga.consoletest = { typing: summarize(cs), screen: (await screen()).filter((l) => /echo\[|couleur|sortie|rc=/.test(l)).map((l) => l.trim()) };
    await keys([{ key: "Escape" }], 100, false);
    await keys(text("exit\n"), 40, false);
    const exCmd = await waitExit(30000);
    result.vga.wine_cmd.exit = exCmd;
    log("vga consoletest", JSON.stringify(result.vga.consoletest), "cmd exit", JSON.stringify(exCmd));
  }
  // Ctrl+C (transmis) sur consoletest --loop lancé directement sur tty1 ; code de sortie attendu : 130 (SIGINT par le noyau).
  const lp = await vgaProgram("wine /root/consoletest.exe --loop", "tick 2", 120000);
  await keys([{ key: "c", ctrl: true }], 100, false);
  result.vga.ctrlc = { ...lp, exit: await waitExit(30000), screen_tail: (await screen()).filter((l) => l.trim()).slice(-3) };
  log("vga ctrl-c", JSON.stringify(result.vga.ctrlc));
  result.vga.cpu_idle_33 = await cpu(5000);

  // ---- voie (b) : pty sur ttyS1
  result.pty = {};
  const b1 = await ptyProgram("/root/e14/shell-probe.sh", "PROBE-SHELL-OK", 20000);
  result.pty.hello = { ...b1, exit: await waitExit(10000), screen_tail: (await screen()).filter((l) => l.trim()).slice(-3) };
  log("pty hello", JSON.stringify(result.pty.hello));
  const wcp = await ptyProgram("wine cmd", "[A-Z]:\\\\[^>]*>", 300000);
  result.pty.wine_cmd = { start_ms: wcp.start_ms, matched: wcp.matched, first_screen: (await screen()).filter((l) => l.trim()).slice(-4) };
  log("pty wine cmd", JSON.stringify(result.pty.wine_cmd));
  if (wcp.matched) {
    result.pty.by_poll = {};
    for (const P of POLLS) {
      await setPoll(P);
      const samples = await keys(text("echo Kazo E14 abcdefghij\n"), 100, true);
      await sleep(300);
      result.pty.by_poll[P] = { typing: summarize(samples), samples: samples.map((s) => [s.key, s.change_ms, s.render_ms]) };
      log("pty poll", P, JSON.stringify(result.pty.by_poll[P].typing));
    }
    await setPoll(33);
    await keys(text("ver\ndir c:\\windows\\system32\\*.exe\n"), 40, false); await waitScreen("[A-Z]:\\\\[^>]*>\\s*$", 60000);
    result.pty.dir_screen = (await screen()).filter((l) => l.trim()).slice(-6);
    await keys(text("color 1e\necho couleur\n"), 40, false); await sleep(1500);
    result.pty.shot_cmd = await shot(`e14-wine-run${n}-pty-cmd`);
    await keys(text("z:\\root\\consoletest.exe\n"), 40, false); await waitScreen("consoletest:", 60000);
    const cs = await keys(text("bonjour\ncolor\nexit 5\n"), 60, true);
    await waitScreen("sortie avec 5", 30000); await sleep(500);
    await keys(text("echo rc=%errorlevel%\n"), 40, false); await sleep(1500);
    result.pty.consoletest = { typing: summarize(cs), screen: (await screen()).filter((l) => /echo\[|couleur|sortie|rc=/.test(l)).map((l) => l.trim()) };
    await keys(text("exit\n"), 40, false);
    result.pty.wine_cmd.exit = await waitExit(30000);
    log("pty consoletest", JSON.stringify(result.pty.consoletest), "exit", JSON.stringify(result.pty.wine_cmd.exit));
  }
  const lpp = await ptyProgram("wine /root/consoletest.exe --loop", "tick 2", 120000);
  await keys([{ key: "c", ctrl: true }], 100, false);
  result.pty.ctrlc = { ...lpp, exit: await waitExit(30000), screen_tail: (await screen()).filter((l) => l.trim()).slice(-3) };
  log("pty ctrl-c", JSON.stringify(result.pty.ctrlc));
  // Redimensionnement : 100x30 déclaré au lancement.
  await E(() => window.E14.resize(100, 30));
  const rs = await ptyProgram("sh -c 'stty size; printf \"%100s|\\n\" x | tr \" \" -'", "^-+x\\|", 20000, "-s 30x100");
  result.pty.resize = { ...rs, exit: await waitExit(10000), screen_tail: (await screen()).filter((l) => l.trim()).slice(-4), vt_size: await E(() => [window.E14.vt.cols, window.E14.vt.rows]) };
  await E(() => window.E14.resize(80, 25));
  log("pty resize", JSON.stringify(result.pty.resize));

  // ---- shell Linux (119, 195) par la voie (b)
  result.shell = {};
  const sh = await ptyProgram("bash -i", "[#$]\\s*$", 30000);
  result.shell.start = sh;
  const shKeys = await keys(text("ls --color=always /etc | head -20\n"), 60, true);
  await sleep(800);
  result.shell.ls = { typing: summarize(shKeys), colored_cells: await E(() => window.E14.vt.screen.flat().filter((c) => c.fg !== 7 && c.ch !== " ").length), shot: await shot(`e14-wine-run${n}-bash-ls`) };
  await keys(text("less /etc/services\n"), 40, false); result.shell.less_start = await waitScreen("Network services|:\\s*$", 30000);
  await keys([{ key: "ArrowDown" }, { key: "ArrowDown" }, { key: " " }], 200, false); await sleep(500);
  await keys(text("/tcp\n"), 60, false); await sleep(800);
  result.shell.less = { screen_tail: (await screen()).slice(-1).map((l) => l.trim()), shot: await shot(`e14-wine-run${n}-bash-less`) };
  await keys([{ key: "q" }], 300, false);
  await keys(text("nano /tmp/e14.txt\n"), 40, false); result.shell.nano_start = await waitScreen("GNU nano", 30000); await sleep(300);
  await keys(text("Texte tape dans nano depuis le terminal de Kazo.\nLigne 2"), 25, false); await sleep(500);
  result.shell.nano = { shot: await shot(`e14-wine-run${n}-bash-nano`), screen_bottom: (await screen()).slice(-2).map((l) => l.trim()) };
  await keys([{ key: "o", ctrl: true }], 300, false); await keys([{ key: "Enter" }], 300, false); await keys([{ key: "x", ctrl: true }], 300, false); await sleep(800);
  await keys(text("cat /tmp/e14.txt; echo NANO-DONE\n"), 40, false); await waitScreen("NANO-DONE", 10000);
  result.shell.nano.saved = (await screen()).some((l) => /^Texte tape dans nano/.test(l));
  await keys(text("vi /tmp/e14.txt\n"), 40, false); result.shell.vi_start = await waitScreen("e14.txt|^~", 30000); await sleep(300);
  await keys(text("Gonouvelle ligne vi"), 40, false); await keys([{ key: "Escape" }], 300, false); await keys(text(":wq\n"), 60, false); await sleep(800);
  await keys(text("tail -1 /tmp/e14.txt; echo VI-DONE\n"), 40, false); await waitScreen("VI-DONE", 10000);
  result.shell.vi = { saved: (await screen()).some((l) => /nouvelle ligne vi/.test(l)) };
  await keys(text("exit\n"), 40, false);
  result.shell.exit = await waitExit(10000);
  result.shell.vt_unknown = (await stats()).vtUnknown;
  log("shell", JSON.stringify(result.shell));
  result.pty.cpu_idle_33 = await cpu(5000);
  result.memory = await memory();
  result.marks = await E(() => window.E14.marks);
  return result;
}

for (let n = FIRST; n < FIRST + RUNS; n++) {
  context = await browser.newContext({ viewport: { width: 1000, height: 900 } });
  page = await context.newPage();
  page.on("pageerror", (e) => log("pageerror", e.message));
  page.on("crash", () => log("CRASH renderer"));
  let result;
  try { result = GUEST === "dos" ? await runDos(n) : await runWine(n); }
  catch (e) { log("ERREUR run", n, e.stack || e); result = { run: n, guest: GUEST, error: String(e.stack || e) }; try { result.screen = await screen(); } catch {} }
  fs.writeFileSync(path.join(ROOT, `results-${GUEST}-${n}.json`), JSON.stringify(result, null, 1));
  log("écrit", `results-${GUEST}-${n}.json`);
  await context.close();
}
await browser.close();
server.kill();
process.exit(0);
