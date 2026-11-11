// Essai E6 — pilote de mesure (Playwright + Chrome installe).
// Lance le serveur statique, la cible locale et deux relais (un normal, un a MAX_SEC court),
// puis pilote v86 : montee des bouteilles, decisions appliquees, limites du relais,
// verification du confinement, debit avec/sans netns, comparaison 1 VM / 2 VM, mode inbrowser.
// Usage : node run.mjs [--runs 2] [--mem 512] [--headed]
// Ecrit results-<n>.json. Aucune connexion a l'Internet reel (cibles 127.0.0.1 seulement).
import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const A = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith("--") ? [a.slice(2), all[i + 1]?.startsWith("--") || all[i + 1] === undefined ? true : all[i + 1]] : [])).filter((x) => x.length));
const RUNS = Number(A.runs || 2);
const MEM = Number(A.mem || 512);
const HEADLESS = !A.headed;
const SRV = 8096, RELAY = 9099, T1 = 8080, T2 = 8443;
const BASE = `http://localhost:${SRV}`;
const MIN = 60_000;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------- services ----------
const procs = [];
function start(name, args) { const p = spawn(process.execPath, args, { cwd: ROOT, stdio: ["ignore", "inherit", "inherit"] }); p._name = name; procs.push(p); return p; }
start("server", ["server.mjs", String(SRV)]);
start("target", ["target.mjs", String(T1), String(T2)]);
start("relay", ["relay.mjs", String(RELAY), "--maxstreams", "4", "--maxsec", "300"]);
process.on("exit", () => procs.forEach((p) => { try { p.kill(); } catch {} }));
await sleep(900);
const relayStats = async (port = RELAY) => (await fetch(`http://localhost:${port}/__relay`)).json();
const relayReset = async (port = RELAY) => fetch(`http://localhost:${port}/__reset`);
const relayMaxsec = async (s) => (await fetch(`http://localhost:${RELAY}/__relay/maxsec?s=${s}`)).text();
const relayDecision = async (ip, ports, label) => (await fetch(`http://localhost:${RELAY}/__relay/decision?ip=${ip}&ports=${ports}&label=${label}`)).text();
const targetStats = async (port = T1) => (await fetch(`http://localhost:${port}/__stats`)).json();

// ---------- aides invite ----------
function pageUrl(extra = {}) { const u = new URL(`${BASE}/boot.html`); u.searchParams.set("mem", String(MEM)); for (const [k, v] of Object.entries(extra)) u.searchParams.set(k, v); return u.toString(); }
async function waitPrompt(page, t = 20 * MIN) { await page.waitForFunction(() => window.E6.marks.some((m) => m.name === "shell-prompt"), null, { timeout: t, polling: 200 }); }
async function waitMark(page, name, t = 5 * MIN) { await page.waitForFunction((n) => window.E6.marks.some((m) => m.name === n), name, { timeout: t, polling: 200 }); }
// envoie une commande terminee par un echo sentinelle, attend la sentinelle, renvoie les lignes RES/sortie entre-temps
async function run(page, cmd, { tag = "D" + Math.random().toString(36).slice(2, 7), timeout = 3 * MIN } = {}) {
  const before = await page.evaluate(() => window.E6.serial.length);
  await page.evaluate(([c, t]) => window.E6.send(`${c}; echo "SENT_${t} rc=$?"\n`), [cmd, tag]);
  await page.waitForFunction(([t, b]) => new RegExp("SENT_" + t + " rc=[0-9]").test(window.E6.serial.slice(b)), [tag, before], { timeout, polling: 150 });
  const out = await page.evaluate(([b]) => window.E6.serial.slice(b), [before]);
  const m = new RegExp(`SENT_${tag} rc=(\\d+)`).exec(out);
  return { rc: m ? Number(m[1]) : null, out };
}
async function guestMemUsedKb(page) { const r = await run(page, `awk '/MemTotal/{t=$2}/MemAvailable/{a=$2}END{print t-a}' /proc/meminfo`); const m = /(\d+)\s*$/m.exec(r.out.replace(/SENT_.*/s, "")); return m ? Number(m[1]) : null; }

async function processMemory(browser) {
  try {
    const cdp = await browser.newBrowserCDPSession();
    const { processInfo } = await cdp.send("SystemInfo.getProcessInfo"); await cdp.detach();
    const ids = processInfo.map((p) => p.id);
    const out = execFileSync("pwsh", ["-NoProfile", "-Command", `$ids=@(${ids.join(",")}); Get-Process | Where-Object { $ids -contains $_.Id } | Select-Object Id,WorkingSet64,PrivateMemorySize64 | ConvertTo-Json -Compress`], { encoding: "utf8" });
    const procsj = JSON.parse(out || "[]"); const list = (Array.isArray(procsj) ? procsj : [procsj]);
    return { total_private_MiB: Math.round(list.reduce((s, p) => s + p.PrivateMemorySize64, 0) / 1048576), total_ws_MiB: Math.round(list.reduce((s, p) => s + p.WorkingSet64, 0) / 1048576), n_proc: list.length };
  } catch (e) { return { error: String(e) }; }
}

// ---------- phases ----------
async function bootMainVM(context, result) {
  const page = await context.newPage();
  page.on("pageerror", (e) => log("pageerror", e.message));
  const t0 = Date.now();
  await page.goto(pageUrl({ relay: RELAY, netid: 0 }));
  await waitPrompt(page);
  result.boot = { page_to_prompt_s: Math.round((Date.now() - t0) / 100) / 10 };
  result.inbrowser_check = await page.evaluate(() => ({ adapter: window.E6.adapterName(), has: window.E6.hasNetworkAdapter() }));
  return page;
}

async function phaseSetup(page, result) {
  await relayReset();
  const probe = await run(page, "sh /root/probe.sh", { timeout: 3 * MIN });
  result.probe = probe.out.split("\n").filter((l) => l.startsWith("RES "));
  const up = await run(page, "sh /root/bottle-net.sh up", { timeout: 4 * MIN });
  result.bottle_up = up.out.split("\n").filter((l) => l.startsWith("RES ") || l.startsWith("MARK "));
}

async function phaseDecisions(page, result) {
  await relayReset();
  // b1 : HTTP port 80 autorise -> doit reussir a travers le relais
  const b1 = await run(page, `/root/bottle-net.sh run 1 'curl -s -m 20 -o /dev/null -w "HTTP_%{http_code}" http://web.test/'`, { timeout: 3 * MIN });
  // b2 : refus total -> nft coupe cote invite (echec de connexion)
  const b2 = await run(page, `/root/bottle-net.sh run 2 'curl -s -m 12 -o /dev/null -w "HTTP_%{http_code}" http://web.test/ 2>&1; echo " exit=$?"'`, { timeout: 3 * MIN });
  await sleep(400);
  const rs = await relayStats();
  const ts = await targetStats();
  result.decisions = {
    b1_curl: (/HTTP_\d+/.exec(b1.out) || [null])[0], b1_rc: b1.rc,
    b2_curl: (/HTTP_\d+|exit=\d+/g.exec(b2.out) || [null])[0], b2_rc: b2.rc,
    relay_opens: rs.stats.streams.filter((s) => s.kind === "open").map((s) => ({ id: s.identity, label: s.label, dport: s.dport })),
    relay_refused: rs.stats.refused.map((s) => ({ id: s.identity, label: s.label, dport: s.dport, reason: s.reason })),
    target_requests: ts.requests, target_byPort: ts.byPort,
  };
}

async function phaseLimits(page, result) {
  await relayReset();
  // (1) 5 flux paralleles depuis b1 (plafond 4) : le 5e doit etre refuse par le relais.
  const five = await run(page, `/root/bottle-net.sh run 1 'for i in 1 2 3 4 5; do (sleep 8 | nc web.test 80 >/dev/null 2>&1 &); done; sleep 2; echo NC_LANCES'`, { timeout: 2 * MIN });
  await sleep(1800);
  const rsCap = await relayStats();
  // (2) ports : on ouvre nft pour b1, puis 443 (refuse par la decision b1=HTTP) et 8443 (hors 80/443).
  await run(page, `/root/bottle-net.sh nft open`, { timeout: MIN });
  await relayReset();
  const p443 = await run(page, `/root/bottle-net.sh run 1 'curl -sk -m 8 -o /dev/null -w P443_%{http_code} https://web.test/ 2>&1; echo " e443=$?"'`, { timeout: 2 * MIN });
  const p8443 = await run(page, `/root/bottle-net.sh run 1 'curl -s -m 8 -o /dev/null -w P8443_%{http_code} http://web.test:8443/ 2>&1; echo " e8443=$?"'`, { timeout: 2 * MIN });
  const rsPorts = await relayStats();
  await run(page, `/root/bottle-net.sh nft on`, { timeout: MIN });
  // (3) coupure de duree : relais a MAX_SEC=5 s (ajuste a chaud), b1 ouvre un flux oisif (nc).
  await relayReset(); await relayMaxsec(5);
  const nc = await run(page, `/root/bottle-net.sh run 1 'T0=$(cut -d" " -f1 /proc/uptime); sleep 60 | nc web.test 80 </dev/null; T1=$(cut -d" " -f1 /proc/uptime); echo "NC_CUT dt=$(awk "BEGIN{print $T1-$T0}")"'`, { timeout: 100_000 }).catch(() => ({ out: "nc-timeout" }));
  const rsCut = await relayStats();
  await relayMaxsec(300);
  result.limits = {
    nc_five: (/NC_LANCES/.exec(five.out) || [null])[0],
    cap_opens: rsCap.stats.streams.filter((s) => s.kind === "open").length,
    cap_refused_reasons: rsCap.stats.refused.map((s) => s.reason),
    p443: (p443.out.match(/P443_\d+|e443=\d+/g) || []), p8443: (p8443.out.match(/P8443_\d+|e8443=\d+/g) || []),
    ports_refused: rsPorts.stats.refused.map((s) => ({ dport: s.dport, reason: s.reason })),
    nc_cut: (/NC_CUT dt=[\d.]+/.exec(nc.out) || [null])[0],
    cut_log: rsCut.log.filter((e) => e.kind === "timeout" || e.why === "max-sec").slice(0, 4),
  };
}

async function phaseConfinement(page, result) {
  const c = await run(page, `/root/bottle-net.sh run 2 'sh /tmp/verify-confinement.sh'`, { timeout: 3 * MIN });
  result.confinement = c.out.split("\n").filter((l) => l.startsWith("RES check "));
  // tentative supplementaire : un programme "hors bouteille" (usager non root, espace racine) a-t-il du reseau ?
  const u0 = await run(page, `/root/bottle-net.sh user0 'curl -s -m 8 -o /dev/null -w OUT0_%{http_code} http://web.test/ 2>&1; echo " e=$?"'`, { timeout: 2 * MIN });
  result.confinement_user0 = (/OUT0_\d+|e=\d+/g.exec(u0.out) || [null])[0];
}

async function phaseThroughput(page, result) {
  // le chemin "root-direct" sort par eth0 (source 192.168.86.100) : on l'autorise au relais.
  await relayDecision("192.168.86.100", "80,443", "root-direct");
  const runs = [];
  for (const mode of ["netns-b1", "root-direct", "netns-b1", "root-direct"]) {
    await relayReset();
    const cmd = mode === "netns-b1"
      ? `/root/bottle-net.sh run 1 'T0=$(date +%s%N); curl -s -m 120 -o /dev/null -w "DL_%{size_download}_%{speed_download}" http://web.test/blob?mb=20; echo'`
      : `T0=$(date +%s%N); curl -s -m 120 -o /dev/null -w "DL_%{size_download}_%{speed_download}" http://web.test/blob?mb=20; echo`;
    const r = await run(page, cmd, { timeout: 4 * MIN });
    const m = /DL_(\d+)_([\d.]+)/.exec(r.out);
    runs.push({ mode, size: m ? Number(m[1]) : null, speed_Bps: m ? Number(m[2]) : null, mBps: m ? Math.round(Number(m[2]) / 1048576 * 100) / 100 : null });
    log("debit", mode, runs[runs.length - 1].mBps, "MiB/s");
  }
  result.throughput = runs;
}

async function phaseResources(context, browser, result, page) {
  // 1 VM (2 bouteilles) : memoire deja en marche
  const guestKb = await guestMemUsedKb(page);
  const oneVM = { guest_used_MiB: guestKb ? Math.round(guestKb / 1024) : null, browser: await processMemory(browser), boot_s: result.boot.page_to_prompt_s };
  // 2 VM separees : deuxieme page, propre v86 (net id 1)
  await relayReset();
  const page2 = await context.newPage();
  const t0 = Date.now();
  await page2.goto(pageUrl({ relay: RELAY, netid: 1 }));
  await waitPrompt(page2);
  const twoVM = { second_boot_s: Math.round((Date.now() - t0) / 100) / 10, browser_both: await processMemory(browser) };
  await page2.close();
  result.resources = {
    one_vm_two_bottles: oneVM,
    two_separate_vms: twoVM,
    note: "two_separate_vms.browser_both = memoire des processus du navigateur avec 2 pages v86 actives",
  };
}

// ---------- boucle ----------
const browser = await chromium.launch({ channel: "chrome", headless: HEADLESS });
log("navigateur", browser.version(), HEADLESS ? "headless" : "headed", "mem", MEM);
for (let n = 1; n <= RUNS; n++) {
  const result = { run: n, date: new Date().toISOString(), mem_MiB: MEM, v86: "0.5.473 (npm)", relay: { max_streams: 4, max_sec: 300, ports: [80, 443] } };
  let ctx;
  try {
    ctx = await browser.newContext();
    const page = await bootMainVM(ctx, result);
    await phaseSetup(page, result);
    await phaseDecisions(page, result);
    await phaseLimits(page, result);
    await phaseConfinement(page, result);
    await phaseThroughput(page, result);
    await phaseResources(ctx, browser, result, page);
    await page.close(); await ctx.close();
  } catch (e) {
    result.error = String(e.stack || e); log("ERREUR run", n, e.message);
    try { const pages = ctx.pages(); if (pages[0]) { const tail = await pages[0].evaluate(() => window.E6.serial.slice(-2500)); result.serial_tail = tail; log("SERIAL TAIL:\n" + tail); } } catch {}
  }
  fs.writeFileSync(path.join(ROOT, `results-${n}.json`), JSON.stringify(result, null, 2));
  log(`results-${n}.json ecrit`);
}
await browser.close();
procs.forEach((p) => { try { p.kill(); } catch {} });
process.exit(0);
