// Imprime en Markdown les tableaux de resultats/summary.json (médiane des passes).
// Usage : node tableau.mjs [resultats/summary.json]

import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const file = process.argv[2] ?? path.join(HERE, 'resultats', 'summary.json');
const summary = JSON.parse(await readFile(file, 'utf8'));
const names = Object.keys(summary);
const label = { chrome: 'Chrome', msedge: 'Edge', chromium: 'Chromium (Playwright)', firefox: 'Firefox (Playwright)' };

const fmt = (v, d = 0) => (typeof v === 'number' && Number.isFinite(v) ? v.toLocaleString('fr-CA', { maximumFractionDigits: d, minimumFractionDigits: d }).replace(/ /g, ' ') : v == null ? '—' : String(v));
const get = (o, p) => p.split('.').reduce((a, k) => (a == null ? undefined : a[k]), o);
const scaled = (v, scale) => (typeof v === 'number' ? v * scale : v);
const row = (title, p, d = 0, scale = 1) => `| ${title} | ${names.map((n) => fmt(typeof p === 'function' ? p(summary[n]) : scaled(get(summary[n], p), scale), d)).join(' | ')} |`;
const head = (col) => `| ${col} | ${names.map((n) => label[n] ?? n).join(' | ')} |\n|---|${names.map(() => '---:').join('|')}|`;

const out = [];
out.push('### Navigateurs', '', head('Élément'));
out.push(row('Version', (s) => s.version));
out.push(row('Mode', (s) => (s.headless ? 'sans tête' : 'avec fenêtre')));
out.push(row('Passes utilisées', 'runsUsed'));
out.push(row('`createSyncAccessHandle` (worker)', 'worker.worker.syncAccessHandle'));
out.push(row('Résolution de `performance.now()` (µs)', 'worker.worker.clockResolution_us', 0));
out.push(row('Quota annoncé (Gio)', 'estimateBefore.quota', 1, 1 / 2 ** 30));
out.push(row('`persist()`', (s) => String(s.persist)));
out.push(row('Usage mesuré après écriture OPFS (Mio)', 'worker.opfs.estimateAfterWrite.usage', 0, 1 / 2 ** 20));
out.push(row('Usage mesuré après remplissage IDB (Mio)', 'worker.idb.estimateAfterFill.usage', 0, 1 / 2 ** 20));
out.push(row('Usage après nettoyage (Mio)', 'estimateAfter.usage', 1, 1 / 2 ** 20));
out.push(row('Profil restant après nettoyage (Mio)', 'profileSizeAfter_MiB', 1));
out.push(row('Durée d\'une passe (s)', 'wallClock_s', 0));
out.push('', '### OPFS (`createSyncAccessHandle`, fichier de 1 Gio)', '', head('Mesure'));
out.push(row('Écriture séquentielle 1 Mio (Mio/s)', 'worker.opfs.seqWrite_1MiB.MiB_per_s'));
out.push(row('`flush()` après l\'écriture de 1 Gio (ms)', 'worker.opfs.flushAfterSeqWrite_ms', 1));
for (const bs of ['4K', '64K']) {
  const lbl = bs === '4K' ? '4 Kio' : '64 Kio';
  for (const [k, t] of [['randRead', 'Lecture aléatoire'], ['randWrite', 'Écriture aléatoire']]) {
    out.push(row(`${t} ${lbl} — médiane (µs)`, `worker.opfs.${k}_${bs}.median_us`));
    out.push(row(`${t} ${lbl} — p99 (µs)`, `worker.opfs.${k}_${bs}.p99_us`));
    out.push(row(`${t} ${lbl} — op/s`, `worker.opfs.${k}_${bs}.ops_per_s`));
  }
  out.push(row(`\`flush()\` après 2000 écritures ${lbl} (ms)`, `worker.opfs.flushAfterRandWrite_${bs}_ms`, 1));
}
out.push(row('Écriture 4 Kio + `flush()` à chaque fois — médiane (µs)', 'worker.opfs.writeAndFlush_4K.median_us'));
out.push(row('Écriture 4 Kio + `flush()` à chaque fois — p99 (µs)', 'worker.opfs.writeAndFlush_4K.p99_us'));
out.push(row('Réouverture + lecture séquentielle 64 Mio (Mio/s)', 'worker.opfs.reopenSeqRead_64MiB.MiB_per_s'));
out.push(row('Fichier supprimé et absent ensuite', (s) => String(get(s, 'worker.opfs.cleanedUp'))));
out.push('', '### IndexedDB (4096 enregistrements de 256 Kio, clé = index)', '', head('Mesure'));
out.push(row('Remplissage séquentiel, 32 `put` par transaction (Mio/s)', 'worker.idb.seqFill_256K.MiB_per_s'));
out.push(row('`get` aléatoire d\'un morceau — médiane (µs)', 'worker.idb.randGet_256K.median_us'));
out.push(row('`get` aléatoire d\'un morceau — p99 (µs)', 'worker.idb.randGet_256K.p99_us'));
out.push(row('`get` aléatoire — op/s', 'worker.idb.randGet_256K.ops_per_s'));
out.push(row('`put` aléatoire, 1 transaction par `put` — médiane (µs)', 'worker.idb.randPut_256K_oneTx.median_us'));
out.push(row('`put` aléatoire, 1 transaction par `put` — p99 (µs)', 'worker.idb.randPut_256K_oneTx.p99_us'));
out.push(row('`put` aléatoire, 1 transaction par `put` — op/s', 'worker.idb.randPut_256K_oneTx.ops_per_s'));
out.push(row('`put` aléatoire, 16 par transaction — coût moyen par `put` (µs)', 'worker.idb.randPut_256K_batch16.put_mean_us'));
out.push(row('`put` aléatoire, 16 par transaction — `put`/s', 'worker.idb.randPut_256K_batch16.puts_per_s'));
out.push(row('Écriture d\'un bloc de 4 Kio (lecture-modification-écriture) — médiane (µs)', 'worker.idb.rmw_4K_in_256K.median_us'));
out.push(row('Écriture d\'un bloc de 4 Kio (lecture-modification-écriture) — p99 (µs)', 'worker.idb.rmw_4K_in_256K.p99_us'));
out.push(row('Écriture d\'un bloc de 4 Kio — op/s', 'worker.idb.rmw_4K_in_256K.ops_per_s'));
out.push(row('Base supprimée et absente ensuite', (s) => String(get(s, 'worker.idb.cleanedUp'))));
process.stdout.write(out.join('\n') + '\n');
