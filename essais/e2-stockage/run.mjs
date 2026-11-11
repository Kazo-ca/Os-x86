// Essai E2 — lance chaque navigateur avec Playwright, exécute bench.html, enregistre les résultats.
// Usage : node run.mjs [--runs 3] [--size 1024] [--ops 2000] [--browsers chrome,msedge,chromium,firefox] [--headed]
// Résultats : resultats/results-<navigateur>-<n>.json et resultats/summary.json (médiane des passes).

import { chromium, firefox } from 'playwright';
import { mkdir, mkdtemp, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { startServer } from './server.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const OUT = path.join(HERE, 'resultats');
const PAGE_TIMEOUT_MS = 45 * 60 * 1000;

function arg(name, def) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : def;
}
const RUNS = Number(arg('runs', 3));
const SIZE = Number(arg('size', 1024));
const OPS = Number(arg('ops', 2000));
const HEADED = process.argv.includes('--headed');
const BROWSERS = arg('browsers', 'chrome,msedge,chromium,firefox').split(',');

// Un contexte « newContext() » de Playwright est éphémère (type navigation privée) : dans Chrome son
// stockage peut rester en mémoire. On lance donc un profil persistant tout neuf, sur disque, par passe.
const TYPES = {
  chrome: { type: chromium, opts: { channel: 'chrome' } },
  msedge: { type: chromium, opts: { channel: 'msedge' } },
  chromium: { type: chromium, opts: {} },
  firefox: { type: firefox, opts: {} },
};

async function launchFresh(name) {
  const { type, opts } = TYPES[name];
  const probe = await type.launch({ ...opts, headless: true });
  const version = probe.version();
  await probe.close();
  const profileDir = await mkdtemp(path.join(os.tmpdir(), `e2-${name}-`));
  const context = await type.launchPersistentContext(profileDir, { ...opts, headless: !HEADED });
  return { context, version, profileDir };
}

async function dirSize(dir) {
  let total = 0;
  for (const e of await readdir(dir, { withFileTypes: true, recursive: true })) {
    if (e.isFile()) total += (await stat(path.join(e.parentPath ?? e.path, e.name))).size;
  }
  return total;
}

function median(values) {
  const s = [...values].sort((a, b) => a - b);
  const m = Math.floor(s.length / 2);
  return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2;
}

// Médiane feuille par feuille de plusieurs objets de même forme ; les non-nombres viennent du premier.
function deepMedian(objs) {
  const first = objs[0];
  if (typeof first === 'number') return median(objs.filter((v) => typeof v === 'number'));
  if (first && typeof first === 'object' && !Array.isArray(first)) {
    return Object.fromEntries(Object.keys(first).map((k) => [k, deepMedian(objs.map((o) => (o && typeof o === 'object' ? o[k] : undefined)))]));
  }
  return first;
}

async function runOnce(name, n, url) {
  const t0 = Date.now();
  let launched;
  try {
    launched = await launchFresh(name);
  } catch (e) {
    return { browser: name, run: n, available: false, error: e.message.split('\n')[0] };
  }
  const { context, version, profileDir } = launched;
  const page = context.pages()[0] ?? (await context.newPage());
  const consoleErrors = [];
  page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
  page.on('pageerror', (e) => consoleErrors.push('pageerror: ' + e.message));
  process.stdout.write(`[${name} ${version} passe ${n}] démarrage\n`);
  let results;
  try {
    await page.goto(`${url}?size=${SIZE}&ops=${OPS}`);
    await page.waitForFunction(() => window.__done === true, null, { timeout: PAGE_TIMEOUT_MS, polling: 1000 });
    results = await page.evaluate(() => ({ results: window.__results, error: window.__error }));
  } catch (e) {
    results = { error: e.message.split('\n')[0] };
  }
  await context.close();
  // Taille du profil après l'essai (doit être petite : le fichier et la base ont été supprimés), puis suppression.
  let profileSizeAfter_MiB = null;
  try { profileSizeAfter_MiB = (await dirSize(profileDir)) / 2 ** 20; } catch {}
  await rm(profileDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 }).catch(() => {});
  const profileDeleted = !existsSync(profileDir);
  const out = {
    browser: name, run: n, available: true, version, headless: !HEADED, freshProfile: true, profileSizeAfter_MiB, profileDeleted,
    os: `${os.type()} ${os.release()} (${os.arch()})`, cpu: os.cpus()[0]?.model?.trim(), ramGiB: Math.round(os.totalmem() / 2 ** 30),
    wallClock_s: (Date.now() - t0) / 1000, consoleErrors, error: results.error, ...results.results,
  };
  process.stdout.write(`[${name} ${version} passe ${n}] fini en ${out.wallClock_s.toFixed(0)} s${out.error ? ' ERREUR ' + out.error : ''}\n`);
  return out;
}

const { server, port } = await startServer();
const url = `http://127.0.0.1:${port}/bench.html`;
await mkdir(OUT, { recursive: true });
const summary = {};
for (const name of BROWSERS) {
  const runs = [];
  for (let n = 1; n <= RUNS; n++) {
    const r = await runOnce(name, n, url);
    runs.push(r);
    await writeFile(path.join(OUT, `results-${name}${HEADED ? '-headed' : ''}-${n}.json`), JSON.stringify(r, null, 2));
    if (!r.available) break;
  }
  const good = runs.filter((r) => r.available && !r.error);
  summary[name] = good.length ? { runsUsed: good.length, ...deepMedian(good) } : runs[0];
}
await writeFile(path.join(OUT, `summary${HEADED ? '-headed' : ''}.json`), JSON.stringify(summary, null, 2));
server.close();
process.stdout.write(`Résumé écrit dans ${path.relative(HERE, OUT)}\n`);
