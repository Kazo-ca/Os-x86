// Essai E3 — résume resultats/results-*.json (médiane des passes par navigateur et scénario) en tableaux Markdown.
// Usage : node tableau.mjs
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const OUT = path.join(path.dirname(fileURLToPath(import.meta.url)), 'resultats');
const MiB = 1024 * 1024;
const files = (await readdir(OUT)).filter((f) => /^results-.*\.json$/.test(f));
const all = [];
for (const f of files) all.push(JSON.parse(await readFile(path.join(OUT, f), 'utf8')));

const median = (v) => { const s = [...v].sort((a, b) => a - b); const m = Math.floor(s.length / 2); return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2; };
function deepMedian(objs) {
  const first = objs.find((o) => o !== undefined && o !== null);
  if (typeof first === 'number') return median(objs.filter((v) => typeof v === 'number'));
  if (first && typeof first === 'object' && !Array.isArray(first)) {
    return Object.fromEntries(Object.keys(first).map((k) => [k, deepMedian(objs.map((o) => (o && typeof o === 'object' ? o[k] : undefined)))]));
  }
  return first;
}
const groups = {};
for (const r of all) {
  if (!r.available || r.result?.error) continue;
  const key = `${r.browser}|${r.scenario}|${JSON.stringify(r.params)}`;
  (groups[key] ??= []).push(r);
}
const med = {};
for (const [k, rs] of Object.entries(groups)) med[k] = { n: rs.length, version: rs[0].version, r: deepMedian(rs.map((x) => x.result)) };
const g = (browser, scenario, params) => med[`${browser}|${scenario}|${JSON.stringify(params)}`];
const f0 = (x) => (x === undefined || x === null || Number.isNaN(x) ? '—' : Math.round(x).toLocaleString('fr-FR'));
const f1 = (x) => (x === undefined || x === null || Number.isNaN(x) ? '—' : x.toLocaleString('fr-FR', { maximumFractionDigits: 1, minimumFractionDigits: 1 }));
const BROWSERS = ['chrome', 'msedge', 'firefox'];
const row = (cells) => '| ' + cells.join(' | ') + ' |';
const out = [];

out.push('## Taille de morceau (Chrome, 800 Mio, localhost sans limite, COOP/COEP actifs)', '');
out.push(row(['Mesure', '8 Mio', '32 Mio', '128 Mio', '512 Mio']), row(['---', '---:', '---:', '---:', '---:']));
const sizes = [8, 32, 128, 512];
const chunkRows = [
  ['Passes', (x) => x && String(x.n), true],
  ['Temps total du téléchargement (s)', (x) => f1(x?.r.dl.total_ms / 1000)],
  ['Débit (Mio/s)', (x) => f0(x?.r.dl.MiBps)],
  ['Réseau (s)', (x) => f1(x?.r.dl.net_ms / 1000)],
  ['SHA-256 des morceaux pendant le téléchargement (s)', (x) => f1(x?.r.dl.hash_ms / 1000)],
  ['Écriture OPFS (s)', (x) => f1(x?.r.dl.write_ms / 1000)],
  ['État (fichier JSON après chaque morceau) (s)', (x) => f1(x?.r.dl.state_ms / 1000)],
  ['`flush()` final (ms)', (x) => f0(x?.r.dl.flush_ms)],
  ['Mémoire privée du processus de rendu, pic (Mio)', (x) => f0(x?.r.dl.processPeak?.rendererMaxPrivate_MiB)],
  ['Mémoire privée du processus de rendu, au repos (Mio)', (x) => f0(x?.r.dl.processBaseline?.rendererMaxPrivate_MiB)],
  ['Requêtes vues par le serveur', (x) => f0(x?.r.serverRequests)],
  ['Vérification sur place (lecture OPFS + SHA-256) (s)', (x) => f1(x?.r.verify.total_ms / 1000)],
  ['dont lecture OPFS (s) / SHA-256 (s)', (x) => x && `${f1(x.r.verify.read_ms / 1000)} / ${f1(x.r.verify.hash_ms / 1000)}`],
  ['Vérification (Mio/s)', (x) => f0(x?.r.verify.MiBps)],
];
for (const [label, fn] of chunkRows) out.push(row([label, ...sizes.map((s) => fn(g('chrome', 'chunk', { chunkMiB: s })) ?? '—')]));
out.push('');

out.push('## Morceaux de 32 Mio par navigateur, avec et sans COOP/COEP', '');
out.push(row(['Mesure', 'Chrome COI', 'Chrome sans COI', 'Edge COI', 'Firefox COI', 'Firefox sans COI']), row(['---', '---:', '---:', '---:', '---:', '---:']));
const cols = [['chrome', 'chunk'], ['chrome', 'coi'], ['msedge', 'chunk'], ['firefox', 'chunk'], ['firefox', 'coi']];
for (const [label, fn] of chunkRows) out.push(row([label, ...cols.map(([b, sc]) => fn(g(b, sc, { chunkMiB: 32 })) ?? '—')]));
out.push('');

out.push('## Sans COOP/COEP (Chrome) : toutes les tailles', '');
out.push(row(['Mesure', '8 Mio', '32 Mio', '128 Mio', '512 Mio']), row(['---', '---:', '---:', '---:', '---:']));
for (const [label, fn] of chunkRows) out.push(row([label, ...sizes.map((s) => fn(g('chrome', 'coi', { chunkMiB: s })) ?? '—')]));
out.push('');

out.push('## Reprise après coupure (morceaux de 32 Mio)', '');
out.push(row(['Mesure', 'Chrome', 'Edge', 'Firefox']), row(['---', '---:', '---:', '---:']));
const res = (b) => g(b, 'resume', { chunkMiB: 32 });
const resRows = [
  ['Passes', (x) => x && String(x.n)],
  ['(a) worker qui réessaie : réponses coupées / 503 servies', (x) => x && `${f0(x.r.workerRetry.cutResponses)} / ${f0(x.r.workerRetry.responses503)}`],
  ['(a) nouvelles tentatives', (x) => f0(x?.r.workerRetry.retries)],
  ['(a) octets reçus perdus (Mio)', (x) => f0(x?.r.workerRetry.wastedBytes / MiB)],
  ['(a) temps total (s)', (x) => f1(x?.r.workerRetry.total_ms / 1000)],
  ['(a) morceaux vérifiés à la fin', (x) => x && `${f0(x.r.workerRetry.verify?.ok)} / ${f0(x.r.workerRetry.nChunks)}`],
  ['(b) rechargement : morceaux déjà vérifiés au départ des tentatives 1 à 4', (x) => x && x.r.pageReload.map((a) => f0(a.verifiedAtStart)).join(' → ')],
  ['(b) morceaux téléchargés par tentative', (x) => x && x.r.pageReload.map((a) => f0(a.downloaded)).join(' / ')],
  ['(b) octets perdus par tentative (Mio)', (x) => x && x.r.pageReload.map((a) => f0(a.wastedBytes_MiB)).join(' / ')],
  ['(b) temps par tentative (s)', (x) => x && x.r.pageReload.map((a) => f1(a.total_ms / 1000)).join(' / ')],
  ['(b) vérification finale : morceaux bons', (x) => f0(x?.r.pageReload[3]?.verify?.ok)],
];
for (const [label, fn] of resRows) out.push(row([label, ...BROWSERS.map((b) => fn(res(b)) ?? '—')]));
out.push('');

out.push('## Débit limité à 100 Mbit/s (Chrome, morceaux de 32 Mio)', '');
const th = g('chrome', 'throttle', { chunkMiB: 32, rate: 100 });
out.push(row(['Mesure', 'Valeur']), row(['---', '---:']));
out.push(row(['Passes', th ? String(th.n) : '—']));
out.push(row(['Temps total (s)', f1(th?.r.dl.total_ms / 1000)]));
out.push(row(['Débit (Mio/s) / (Mbit/s)', th ? `${f1(th.r.dl.MiBps)} / ${f1(th.r.dl.MiBps * 8.388608)}` : '—']));
out.push(row(['SHA-256 pendant le téléchargement (s)', f1(th?.r.dl.hash_ms / 1000)]));
out.push(row(['Vérification sur place après (s)', f1(th?.r.dl.verify?.ms / 1000)]));
out.push(row(['Mémoire privée du processus de rendu, pic (Mio)', f0(th?.r.dl.processPeak?.rendererMaxPrivate_MiB)]));
out.push('');

out.push('## `fetch` d\'un seul tenant (800 Mio en mémoire)', '');
out.push(row(['Mesure', 'Chrome', 'Edge', 'Firefox']), row(['---', '---:', '---:', '---:']));
const wh = (b) => g(b, 'whole', {});
const whRows = [
  ['Passes', (x) => x && String(x.n)],
  ['Avec `integrity` : temps total (s)', (x) => f1(x?.r.withIntegrity.total_ms / 1000)],
  ['Avec `integrity` : corps reçu et vérifié (s)', (x) => f1(x?.r.withIntegrity.body_ms / 1000)],
  ['Avec `integrity` : écriture OPFS (s)', (x) => f1(x?.r.withIntegrity.write_ms / 1000)],
  ['Avec `integrity` : mémoire privée du rendu, pic (Mio)', (x) => f0(x?.r.withIntegrity.processPeak?.rendererMaxPrivate_MiB)],
  ['Sans `integrity` : temps total (s)', (x) => f1(x?.r.withoutIntegrity.total_ms / 1000)],
  ['Sans `integrity` : mémoire privée du rendu, pic (Mio)', (x) => f0(x?.r.withoutIntegrity.processPeak?.rendererMaxPrivate_MiB)],
  ['Erreur', (x) => x?.r.withIntegrity.error ?? x?.r.withoutIntegrity.error ?? 'aucune'],
];
for (const [label, fn] of whRows) out.push(row([label, ...BROWSERS.map((b) => fn(wh(b)) ?? '—')]));
out.push('');

out.push('## Lectures `Range` à la demande (comme v86), 500 lectures aléatoires', '');
out.push(row(['Mesure', 'Chrome', 'Firefox']), row(['---', '---:', '---:']));
const rg = (b) => g(b, 'range', {});
for (const [label, k, fn] of [
  ['64 Kio, sans limite : médiane / p99 (ms)', 'k64', (x) => `${f1(x.median_ms)} / ${f1(x.p99_ms)}`],
  ['64 Kio, sans limite : débit (Mio/s)', 'k64', (x) => f1(x.MiBps)],
  ['4 Kio, sans limite : médiane / p99 (ms)', 'k4', (x) => `${f1(x.median_ms)} / ${f1(x.p99_ms)}`],
  ['64 Kio à 100 Mbit/s : médiane / p99 (ms)', 'k64_100Mbps', (x) => `${f1(x.median_ms)} / ${f1(x.p99_ms)}`],
  ['64 Kio à 100 Mbit/s : débit (Mio/s)', 'k64_100Mbps', (x) => f1(x.MiBps)],
]) out.push(row([label, ...['chrome', 'firefox'].map((b) => (rg(b)?.r[k] ? fn(rg(b).r[k]) : '—'))]));
out.push('');

out.push('## Cache HTTP et Cache API', '');
for (const [key, title] of [['m200_maxage', '200 Mio, `Cache-Control: public, max-age=3600`'], ['m200_nostore', '200 Mio, `Cache-Control: no-store`'], ['m800_maxage', '800 Mio, `Cache-Control: public, max-age=3600`']]) {
  out.push(`### ${title}`, '', row(['Étape', 'Chrome : requêtes serveur (statuts), temps', 'Edge', 'Firefox']), row(['---', '---', '---', '---']));
  const c = (b) => g(b, 'cache', {})?.r[key];
  const steps = c('chrome')?.steps ?? [];
  steps.forEach((s, i) => out.push(row([s.label, ...BROWSERS.map((b) => { const st = c(b)?.steps[i]; if (!st) return '—'; return `${f0(st.serverRequests)}${st.statuses?.length ? ' (' + st.statuses.join(', ') + ')' : ''}, ${f1(st.ms / 1000)} s${st.error ? ', erreur : ' + st.error : ''}${st.found === false ? ', introuvable' : ''}`; })])));
  out.push(row(['Usage annoncé avec la Cache API (Mio)', ...BROWSERS.map((b) => f0(c(b)?.estimateWithCache?.usage / MiB))]));
  out.push('');
}

out.push('### Témoin : seuil du cache HTTP (même `Cache-Control: public, max-age=3600`, 1 passe)', '');
out.push(row(['Taille', 'Chrome : requêtes vues par le serveur aux fetch 1 / 2 / 3 (force-cache) / 4 (no-cache)', 'Edge', 'Firefox']), row(['---', '---', '---', '---']));
for (const sz of [8, 32, 64, 128]) out.push(row([`${sz} Mio`, ...BROWSERS.map((b) => { const c = g(b, 'cache', { sizes: '8,32,64,128' })?.r[`m${sz}_maxage`]; if (!c) return '—'; return c.steps.slice(0, 4).map((st) => `${st.serverRequests}${st.statuses.length ? ' (' + st.statuses.join(',') + ')' : ''}`).join(' / '); })]));
out.push('');
out.push('## Range inter-origine depuis le navigateur (1 Kio par hôte)', '');
out.push(row(['Hôte', 'Chrome COI', 'Chrome sans COI', 'Firefox COI', 'Firefox sans COI']), row(['---', '---', '---', '---', '---']));
const cr = (b, k) => g(b, 'cors', {})?.r[k]?.results ?? [];
const hosts = cr('chrome', 'withCoi').map((h) => h.label);
hosts.forEach((label, i) => out.push(row([label, ...[['chrome', 'withCoi'], ['chrome', 'withoutCoi'], ['firefox', 'withCoi'], ['firefox', 'withoutCoi']].map(([b, k]) => { const h = cr(b, k)[i]; if (!h) return '—'; return h.error ? 'échec : ' + h.error.slice(0, 60) : `${h.status}, ${h.bytes} o${h.contentRange ? ', Content-Range lu' : ', Content-Range absent'}`; })])));
out.push('');

out.push('## SHA-256 : .NET WebAssembly contre SubtleCrypto et JS pur (64 Mio)', '');
out.push(row(['Mesure', 'Chrome', 'Edge', 'Firefox']), row(['---', '---:', '---:', '---:']));
const hb = (b) => g(b, 'hash', {}), dn = (b, v) => g(b, 'dotnet', { variant: v });
out.push(row(['`SubtleCrypto.digest` (Mo/s, meilleure de 3)', ...BROWSERS.map((b) => f0(hb(b)?.r.subtle_MBps_best))]));
out.push(row(['SHA-256 en JS pur (Mo/s, meilleure de 3)', ...BROWSERS.map((b) => f0(hb(b)?.r.js_MBps_best))]));
out.push(row(['.NET 10 interprété : `SHA256.HashData` (Mo/s)', ...BROWSERS.map((b) => f0(dn(b, 'interp')?.r.results?.hashData_MBps_best))]));
out.push(row(['.NET 10 interprété : `IncrementalHash` par 1 Mio (Mo/s)', ...BROWSERS.map((b) => f0(dn(b, 'interp')?.r.results?.incremental_MBps_best))]));
out.push(row(['.NET 10 interprété : boucle C# de somme d\'octets (Mo/s)', ...BROWSERS.map((b) => f0(dn(b, 'interp')?.r.results?.byteSumLoop_MBps))]));
out.push(row(['.NET 10 interprété : temps de chargement + hachage, page entière (s)', ...BROWSERS.map((b) => f1(dn(b, 'interp')?.r.wall_ms / 1000))]));
out.push(row(['.NET 10 AOT : `SHA256.HashData` (Mo/s)', ...BROWSERS.map((b) => f0(dn(b, 'aot')?.r.results?.hashData_MBps_best))]));
out.push(row(['Temps pour 800 Mio en .NET interprété, extrapolé (s)', ...BROWSERS.map((b) => f0(838.86 / (dn(b, 'interp')?.r.results?.hashData_MBps_best ?? NaN)))]));
out.push('');

out.push('## Versions', '');
for (const b of BROWSERS) { const any = Object.entries(med).find(([k]) => k.startsWith(b + '|')); out.push(`- ${b} : ${any ? any[1].version : 'non mesuré'}`); }
process.stdout.write(out.join('\n') + '\n');
