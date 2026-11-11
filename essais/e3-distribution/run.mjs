// Essai E3 — pilote Playwright (code jetable). Lance le serveur local (port 8098), puis chaque scénario dans
// un profil de navigateur tout neuf, et écrit resultats/results-<navigateur>-<scenario>-<n>.json + summary.json.
// Usage : node run.mjs [--browsers chrome,msedge,firefox] [--runs 3] [--only chunk,resume,throttle,whole,range,coi,cache,cors,hash,dotnet]
//                     [--size 800] [--port 8098] [--headed]

import { chromium, firefox } from 'playwright';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { startServer } from './server.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const OUT = path.join(HERE, 'resultats');
const MiB = 1024 * 1024;
const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : d; };
const RUNS = Number(arg('runs', 3));
const SIZE = Number(arg('size', 800));
const PORT = Number(arg('port', 8098));
const HEADED = process.argv.includes('--headed');
const BROWSERS = arg('browsers', 'chrome,msedge,firefox').split(',');
const ONLY = arg('only', 'chunk,resume,throttle,whole,range,coi,cache,cors,hash,dotnet').split(',');
const TIMEOUT = 30 * 60 * 1000;

const TYPES = {
  chrome: { type: chromium, opts: { channel: 'chrome' } },
  msedge: { type: chromium, opts: { channel: 'msedge' } },
  chromium: { type: chromium, opts: {} },
  firefox: { type: firefox, opts: {} },
};

const { server, port, state } = await startServer({ port: PORT, coi: true });
const base = `http://127.0.0.1:${port}`;
const IMG = `${base}/images/data-${SIZE}.bin`, MAN = `${base}/images/data-${SIZE}.manifest.json`;
const IMG200 = `${base}/images/data-200.bin`;
const ctl = async (q) => (await fetch(`${base}/ctl?${q}`)).json();
const stats = async () => (await fetch(`${base}/stats`)).json();

async function launchFresh(name) {
  const { type, opts } = TYPES[name];
  const profileDir = await mkdtemp(path.join(os.tmpdir(), `e3-${name}-`));
  const context = await type.launchPersistentContext(profileDir, { ...opts, headless: !HEADED, viewport: { width: 1000, height: 700 } });
  const page = context.pages()[0] ?? (await context.newPage());
  page.setDefaultTimeout(TIMEOUT);
  const consoleErrors = [];
  page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  page.on('pageerror', (e) => consoleErrors.push('pageerror: ' + e.message));
  let cdp = null;
  if (type === chromium) { try { cdp = await context.newCDPSession(page); await cdp.send('Performance.enable'); } catch {} }
  return { context, page, profileDir, consoleErrors, cdp, version: context.browser()?.version?.() ?? null };
}

// Mémoire privée du processus de rendu via CDP (Chromium seulement) ; null si indisponible.
async function rendererMemory(cdp) {
  if (!cdp) return null;
  try {
    const { metrics } = await cdp.send('Performance.getMetrics');
    const get = (n) => metrics.find((m) => m.name === n)?.value;
    return { jsHeapUsed_MiB: get('JSHeapUsedSize') / MiB, jsHeapTotal_MiB: get('JSHeapTotalSize') / MiB };
  } catch { return null; }
}

async function closeFresh(b) {
  await b.context.close().catch(() => {});
  await rm(b.profileDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 }).catch(() => {});
  return !existsSync(b.profileDir);
}

async function gotoDl(page) { await page.goto(`${base}/dl.html`); }
async function run(page, params) { return page.evaluate((p) => window.__run(p), params); }

// Mémoire des processus du navigateur lancé (Windows, via PowerShell) : processus principal = celui dont la ligne de
// commande contient le dossier de profil ; on additionne ses descendants et on isole les processus de rendu
// (--type=renderer pour Chromium, -contentproc pour Firefox). Jeu de travail (WorkingSet) et pages privées.
let psProc = null, psQueue = Promise.resolve();
function psQuery(script) {
  // Un seul PowerShell persistant, une requête à la fois, réponse délimitée par un marqueur.
  psQueue = psQueue.then(async () => {
    if (!psProc) {
      const cp = await import('node:child_process');
      psProc = cp.spawn('powershell', ['-NoProfile', '-NonInteractive', '-Command', '-'], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] });
      psProc.stdout.setEncoding('utf8');
      psProc.on('exit', () => { psProc = null; });
    }
    return new Promise((resolve) => {
      let buf = '';
      const onData = (d) => { buf += d; const i = buf.indexOf('<<FIN>>'); if (i >= 0) { psProc.stdout.off('data', onData); resolve(buf.slice(0, i)); } };
      psProc.stdout.on('data', onData);
      psProc.stdin.write(script + "; Write-Output '<<FIN>>'\n");
    });
  });
  return psQueue;
}
async function processMemory(profileDir) {
  try {
    const out = await psQuery(`Get-CimInstance Win32_Process -Filter "Name='chrome.exe' OR Name='msedge.exe' OR Name='firefox.exe'" | Select-Object ProcessId,ParentProcessId,WorkingSetSize,PrivatePageCount,CommandLine | ConvertTo-Json -Compress`);
    const list = JSON.parse(out.trim() || '[]');
    const procs = Array.isArray(list) ? list : [list];
    const needle = profileDir.toLowerCase();
    const main = procs.find((p) => (p.CommandLine || '').toLowerCase().includes(needle));
    if (!main) return null;
    const mine = new Set([main.ProcessId]);
    for (let changed = true; changed;) { changed = false; for (const p of procs) if (!mine.has(p.ProcessId) && mine.has(p.ParentProcessId)) { mine.add(p.ProcessId); changed = true; } }
    const list2 = procs.filter((p) => mine.has(p.ProcessId));
    const renderers = list2.filter((p) => /--type=renderer|-contentproc/.test(p.CommandLine || ''));
    const sum = (arr, k) => arr.reduce((a, p) => a + Number(p[k] || 0), 0) / MiB;
    return { processes: list2.length, renderers: renderers.length, rendererMaxWorkingSet_MiB: Math.max(0, ...renderers.map((p) => Number(p.WorkingSetSize || 0) / MiB)),
      rendererMaxPrivate_MiB: Math.max(0, ...renderers.map((p) => Number(p.PrivatePageCount || 0) / MiB)), totalWorkingSet_MiB: sum(list2, 'WorkingSetSize'), totalPrivate_MiB: sum(list2, 'PrivatePageCount'), samples: 1 };
  } catch { return null; }
}

// Sonde la mémoire pendant une tâche du worker : CDP (tas JS de la page) toutes les 500 ms, processus toutes les secondes.
async function runWithMemory(b, params) {
  let peak = null, procPeak = null, stop = false;
  const sampler = (async () => {
    while (!stop) {
      const m = await rendererMemory(b.cdp);
      if (m) peak = { jsHeapUsed_MiB: Math.max(peak?.jsHeapUsed_MiB ?? 0, m.jsHeapUsed_MiB), jsHeapTotal_MiB: Math.max(peak?.jsHeapTotal_MiB ?? 0, m.jsHeapTotal_MiB) };
      await new Promise((r) => setTimeout(r, 500));
    }
  })();
  const procSampler = (async () => {
    while (!stop) {
      const m = await processMemory(b.profileDir);
      if (m) procPeak = Object.fromEntries(Object.keys(m).map((k) => [k, k === 'samples' ? (procPeak?.samples ?? 0) + 1 : Math.max(procPeak?.[k] ?? 0, m[k])]));
      await new Promise((r) => setTimeout(r, 100));
    }
  })();
  const baseline = await processMemory(b.profileDir);
  const r = await run(b.page, params);
  stop = true; await sampler; await procSampler;
  r.cdpPeak = peak;
  r.processPeak = procPeak;
  r.processBaseline = baseline;
  return r;
}

const SCENARIOS = {
  // Taille de morceau : téléchargement complet + vérification sur place, puis nettoyage.
  async chunk(b, { chunkMiB, coi = true }) {
    await ctl(`reset=1&rate=0&coi=${coi ? 1 : 0}`);
    await gotoDl(b.page);
    const dl = await runWithMemory(b, { mode: 'chunked', url: IMG, manifestUrl: MAN, chunkSize: chunkMiB * MiB, name: 'img.bin', retry: true, verifyAfter: false });
    const verify = await runWithMemory(b, { mode: 'verify', manifestUrl: MAN, chunkSize: chunkMiB * MiB, name: 'img.bin' });
    const serverLog = (await stats()).log;
    const cleanup = await run(b.page, { mode: 'cleanup' });
    return { chunkMiB, coi, dl, verify, serverRequests: serverLog.length, serverBytes_MiB: serverLog.reduce((a, e) => a + e.sent, 0) / MiB, cleanup };
  },
  // Reprise : (a) coupures de connexion et 503, le worker réessaie ; (b) coupure, worker abandonne, page rechargée 3 fois.
  async resume(b, { chunkMiB = 32, coi = true }) {
    await ctl(`reset=1&rate=0&coi=${coi ? 1 : 0}&cut=20&cuts=3&skip=3&fail503=2`);
    await gotoDl(b.page);
    const a = await runWithMemory(b, { mode: 'chunked', url: IMG, manifestUrl: MAN, chunkSize: chunkMiB * MiB, name: 'img.bin', retry: true, verifyAfter: true });
    const aLog = (await stats()).log;
    await run(b.page, { mode: 'cleanup' });
    const attempts = [];
    for (let k = 0; k < 4; k++) {
      await ctl(`reset=1&cut=${k < 3 ? 20 : 0}&cuts=${k < 3 ? 1 : 0}&skip=5`);
      await gotoDl(b.page); // rechargement de la page = nouveau worker, même profil, même OPFS
      const r = await run(b.page, { mode: 'chunked', url: IMG, manifestUrl: MAN, chunkSize: chunkMiB * MiB, name: 'img.bin', retry: false, verifyAfter: k === 3 });
      const log = (await stats()).log;
      attempts.push({ attempt: k + 1, resumed: r.resumed, verifiedAtStart: r.verifiedAtStart, downloaded: r.downloaded, aborted: !!r.aborted, wastedBytes_MiB: r.wastedBytes / MiB, errors: r.errors, total_ms: r.total_ms, verify: r.verify, serverRequests: log.length, serverBytes_MiB: log.reduce((x, e) => x + e.sent, 0) / MiB });
    }
    const cleanup = await run(b.page, { mode: 'cleanup' });
    return { chunkMiB, coi, workerRetry: { ...a, serverRequests: aLog.length, serverBytes_MiB: aLog.reduce((x, e) => x + e.sent, 0) / MiB, cutResponses: aLog.filter((e) => e.cut).length, responses503: aLog.filter((e) => e.status === 503).length }, pageReload: attempts, cleanup };
  },
  // Débit limité à 100 Mbit/s côté serveur.
  async throttle(b, { chunkMiB = 32, rate = 100 }) {
    await ctl(`reset=1&rate=${rate}&coi=1`);
    await gotoDl(b.page);
    const dl = await runWithMemory(b, { mode: 'chunked', url: IMG, manifestUrl: MAN, chunkSize: chunkMiB * MiB, name: 'img.bin', retry: true, verifyAfter: true });
    await ctl('rate=0');
    const cleanup = await run(b.page, { mode: 'cleanup' });
    return { chunkMiB, rateMbps: rate, dl, cleanup };
  },
  // fetch d'un seul tenant avec integrity (tout le fichier en mémoire), puis sans integrity.
  async whole(b, { coi = true }) {
    await ctl(`reset=1&rate=0&coi=${coi ? 1 : 0}`);
    await gotoDl(b.page);
    const withIntegrity = await runWithMemory(b, { mode: 'whole', url: IMG, manifestUrl: MAN, name: 'img.bin', integrity: true });
    await run(b.page, { mode: 'cleanup' });
    await gotoDl(b.page);
    const withoutIntegrity = await runWithMemory(b, { mode: 'whole', url: IMG, manifestUrl: MAN, name: 'img.bin', integrity: false });
    const cleanup = await run(b.page, { mode: 'cleanup' });
    return { coi, withIntegrity, withoutIntegrity, cleanup };
  },
  // Lectures Range à la demande (comme v86), 64 Kio et 4 Kio, sans limite puis à 100 Mbit/s.
  async range(b) {
    await ctl('reset=1&rate=0&coi=1');
    await gotoDl(b.page);
    const r = {};
    r.k64 = await run(b.page, { mode: 'range-demand', url: IMG, size: SIZE * MiB, blockSize: 64 * 1024, count: 500 });
    r.k4 = await run(b.page, { mode: 'range-demand', url: IMG, size: SIZE * MiB, blockSize: 4 * 1024, count: 500 });
    await ctl('rate=100');
    r.k64_100Mbps = await run(b.page, { mode: 'range-demand', url: IMG, size: SIZE * MiB, blockSize: 64 * 1024, count: 200 });
    await ctl('rate=0');
    return r;
  },
  // Même passe « chunk » sans COOP/COEP (et un whole), pour voir ce qui change.
  async coi(b, { chunkMiB }) {
    return SCENARIOS.chunk(b, { chunkMiB, coi: false });
  },
  async cache(b, { sizes } = {}) {
    await ctl('reset=1&rate=0&coi=1');
    await gotoDl(b.page);
    const r = {};
    if (sizes) { // témoin : fichiers plus petits, même Cache-Control, pour trouver le seuil du cache HTTP
      for (const sz of String(sizes).split(',')) r[`m${sz}_maxage`] = await run(b.page, { mode: 'cache', url: `${base}/images/data-${sz}.bin`, cacheControl: 'public, max-age=3600' });
      await ctl('cc=' + encodeURIComponent('public, max-age=3600'));
      return r;
    }
    r.m200_maxage = await run(b.page, { mode: 'cache', url: IMG200, cacheControl: 'public, max-age=3600' });
    r.m200_nostore = await run(b.page, { mode: 'cache', url: IMG200 + '?v=2', cacheControl: 'no-store' });
    r.m800_maxage = await run(b.page, { mode: 'cache', url: IMG, cacheControl: 'public, max-age=3600' });
    await ctl('cc=' + encodeURIComponent('public, max-age=3600'));
    return r;
  },
  async cors(b) {
    await ctl('reset=1&rate=0&coi=1');
    await gotoDl(b.page);
    const hosts = [
      { label: 'GitHub Pages (microsoft.github.io)', url: 'https://microsoft.github.io/monaco-editor/node_modules/monaco-editor/min/vs/editor/editor.main.js' },
      { label: 'raw.githubusercontent.com', url: 'https://raw.githubusercontent.com/microsoft/monaco-editor/main/package-lock.json' },
      { label: 'jsDelivr (npm)', url: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.0/min/vs/editor/editor.main.js' },
      { label: 'GitHub Releases (redirection github.com)', url: 'https://github.com/cli/cli/releases/download/v2.63.0/gh_2.63.0_checksums.txt' },
      { label: 'GitHub Releases sans Range', url: 'https://github.com/cli/cli/releases/download/v2.63.0/gh_2.63.0_checksums.txt', range: false },
    ];
    const withCoi = await run(b.page, { mode: 'cors-range', hosts });
    await ctl('coi=0');
    await gotoDl(b.page);
    const withoutCoi = await run(b.page, { mode: 'cors-range', hosts });
    await ctl('coi=1');
    return { withCoi, withoutCoi };
  },
  async hash(b) {
    await ctl('reset=1&rate=0&coi=1');
    await gotoDl(b.page);
    return run(b.page, { mode: 'hash-bench', sizeMiB: 64, passes: 3 });
  },
  async dotnet(b, { variant = 'interp' }) {
    await ctl('reset=1&rate=0&coi=1');
    const t0 = Date.now();
    await b.page.goto(`${base}/dotnet/${variant}/?size=64&passes=3`);
    await b.page.waitForFunction(() => window.__done === true, null, { timeout: TIMEOUT, polling: 500 });
    const r = await b.page.evaluate(() => ({ results: window.__results, error: window.__error }));
    return { variant, wall_ms: Date.now() - t0, ...r };
  },
};

// Plan : scénario, paramètres, navigateurs, nombre de passes.
const PLAN = [
  ['chunk', { chunkMiB: 8 }, ['chrome'], RUNS],
  ['chunk', { chunkMiB: 32 }, ['chrome', 'msedge', 'firefox'], RUNS],
  ['chunk', { chunkMiB: 128 }, ['chrome'], RUNS],
  ['chunk', { chunkMiB: 512 }, ['chrome'], RUNS],
  ['resume', { chunkMiB: 32 }, ['chrome', 'msedge', 'firefox'], 1],
  ['resume', { chunkMiB: 32 }, ['chrome'], RUNS - 1],
  ['throttle', { chunkMiB: 32, rate: 100 }, ['chrome'], RUNS],
  ['whole', {}, ['chrome', 'msedge', 'firefox'], 1],
  ['whole', {}, ['chrome'], RUNS - 1],
  ['range', {}, ['chrome', 'firefox'], 1],
  ['coi', { chunkMiB: 8 }, ['chrome'], RUNS],
  ['coi', { chunkMiB: 32 }, ['chrome', 'firefox'], RUNS],
  ['coi', { chunkMiB: 128 }, ['chrome'], RUNS],
  ['coi', { chunkMiB: 512 }, ['chrome'], RUNS],
  ['cache', {}, ['chrome', 'msedge', 'firefox'], 1],
  ['cache', {}, ['chrome'], RUNS - 1],
  ['cache', { sizes: '8,32,64,128' }, ['chrome', 'msedge', 'firefox'], 1],
  ['cors', {}, ['chrome', 'firefox'], 1],
  ['hash', {}, ['chrome', 'msedge', 'firefox'], RUNS],
  ['dotnet', { variant: 'interp' }, ['chrome', 'msedge', 'firefox'], RUNS],
  ['dotnet', { variant: 'aot' }, ['chrome', 'firefox'], RUNS],
];

await mkdir(OUT, { recursive: true });
const all = [];
const counters = {};
for (const [scenario, params, browsers, runs] of PLAN) {
  if (!ONLY.includes(scenario)) continue;
  if (scenario === 'dotnet' && !existsSync(path.join(HERE, 'dotnet', 'publish', params.variant, 'wwwroot', 'index.html'))) { process.stdout.write(`[dotnet ${params.variant}] non publié, sauté\n`); continue; }
  for (const name of browsers) {
    if (!BROWSERS.includes(name)) continue;
    const key = `${name}-${scenario}-${Object.values(params).join('-') || 'x'}`;
    for (let i = 0; i < runs; i++) {
      counters[key] = (counters[key] ?? 0) + 1;
      const n = counters[key];
      const t0 = Date.now();
      let b;
      try { b = await launchFresh(name); } catch (e) { all.push({ browser: name, scenario, params, run: n, available: false, error: e.message.split('\n')[0] }); process.stdout.write(`[${key}] indisponible : ${e.message.split('\n')[0]}\n`); break; }
      let result;
      try { result = await SCENARIOS[scenario](b, params); } catch (e) { result = { error: e.message.split('\n')[0] }; }
      const profileDeleted = await closeFresh(b);
      const out = { browser: name, version: b.version, scenario, params, run: n, available: true, headless: !HEADED, wallClock_s: (Date.now() - t0) / 1000, consoleErrors: b.consoleErrors, profileDeleted, cpu: os.cpus()[0]?.model?.trim(), ramGiB: Math.round(os.totalmem() / 2 ** 30), os: `${os.type()} ${os.release()}`, result };
      all.push(out);
      await writeFile(path.join(OUT, `results-${key}-${n}.json`), JSON.stringify(out, null, 1));
      process.stdout.write(`[${key} passe ${n}] ${out.wallClock_s.toFixed(0)} s${result.error ? ' ERREUR ' + result.error : ''}\n`);
    }
  }
}
await writeFile(path.join(OUT, `summary-${Date.now()}.json`), JSON.stringify(all, null, 1));
server.close();
process.stdout.write('fini\n');
process.exit(0);
