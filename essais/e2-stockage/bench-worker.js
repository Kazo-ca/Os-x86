// Essai E2 — stockage des disques. Code jetable de mesure, exécuté dans un Web Worker.
// Mesure OPFS (createSyncAccessHandle) et IndexedDB (morceaux de 256 Kio) sur une image de 1 Gio.

const KiB = 1024;
const MiB = 1024 * KiB;
const OPFS_FILE = 'e2-bench.img';
const IDB_NAME = 'e2-bench';
const IDB_STORE = 'chunks';
const IDB_CHUNK = 256 * KiB;
const IDB_FILL_BATCH = 32; // put par transaction pendant le remplissage
const IDB_PUT_BATCH = 16; // put par transaction pour la mesure « groupée »
const WRITE_FLUSH_OPS = 200; // écriture 4 Kio + flush, à chaque fois

function post(type, data) { self.postMessage({ type, ...data }); }
function log(msg) { post('log', { msg }); }

// Motif pseudo-aléatoire (xorshift32) pour ne pas écrire que des zéros.
function fillPattern(buffer, seed) {
  const u32 = new Uint32Array(buffer);
  let x = (seed >>> 0) || 1;
  for (let i = 0; i < u32.length; i++) {
    x ^= x << 13; x >>>= 0; x ^= x >>> 17; x ^= x << 5; x >>>= 0;
    u32[i] = x;
  }
}

function randomOffsets(count, sizeBytes, blockSize) {
  const blocks = Math.floor(sizeBytes / blockSize);
  const out = new Array(count);
  for (let i = 0; i < count; i++) out[i] = Math.floor(Math.random() * blocks) * blockSize;
  return out;
}

function randomIndices(count, max) {
  const out = new Array(count);
  for (let i = 0; i < count; i++) out[i] = Math.floor(Math.random() * max);
  return out;
}

// Statistiques d'une série de latences (ms) : médiane, p99, moyenne depuis le temps total.
function stats(latenciesMs, totalMs) {
  const sorted = Float64Array.from(latenciesMs).sort();
  const q = (p) => sorted[Math.min(sorted.length - 1, Math.floor(p * (sorted.length - 1)))];
  return {
    n: sorted.length,
    median_us: q(0.5) * 1000,
    p99_us: q(0.99) * 1000,
    mean_us: (totalMs / sorted.length) * 1000,
    min_us: sorted[0] * 1000,
    max_us: sorted[sorted.length - 1] * 1000,
    ops_per_s: sorted.length / (totalMs / 1000),
    total_ms: totalMs,
  };
}

// Mesure une boucle d'opérations synchrones : latence par opération + temps total.
function timedLoop(count, fn) {
  const lat = new Array(count);
  const t0 = performance.now();
  for (let i = 0; i < count; i++) {
    const a = performance.now();
    fn(i);
    lat[i] = performance.now() - a;
  }
  return stats(lat, performance.now() - t0);
}

async function timedLoopAsync(count, fn) {
  const lat = new Array(count);
  const t0 = performance.now();
  for (let i = 0; i < count; i++) {
    const a = performance.now();
    await fn(i);
    lat[i] = performance.now() - a;
  }
  return stats(lat, performance.now() - t0);
}

// Résolution effective de performance.now() (plus petit écart non nul observé).
function clockResolutionUs() {
  let min = Infinity;
  for (let i = 0; i < 20000; i++) {
    const a = performance.now();
    let b = performance.now();
    while (b === a) b = performance.now();
    min = Math.min(min, b - a);
  }
  return min * 1000;
}

async function estimate() {
  try { return await navigator.storage.estimate(); } catch (e) { return { error: e.name + ': ' + e.message }; }
}

// ---------------------------------------------------------------- OPFS

async function benchOpfs(sizeBytes, ops) {
  const r = { available: false };
  if (!navigator.storage || !navigator.storage.getDirectory) {
    r.reason = 'navigator.storage.getDirectory absent dans le worker';
    return r;
  }
  if (typeof FileSystemFileHandle === 'undefined' || !('createSyncAccessHandle' in FileSystemFileHandle.prototype)) {
    r.reason = 'FileSystemFileHandle.createSyncAccessHandle absent dans le worker';
    return r;
  }
  const root = await navigator.storage.getDirectory();
  await root.removeEntry(OPFS_FILE).catch(() => {});
  const fh = await root.getFileHandle(OPFS_FILE, { create: true });
  let ah = await fh.createSyncAccessHandle();
  r.available = true;

  // 1. Écriture séquentielle par morceaux de 1 Mio.
  const chunk = new Uint8Array(MiB);
  fillPattern(chunk.buffer, 42);
  const nChunks = sizeBytes / MiB;
  log(`OPFS : écriture séquentielle de ${nChunks} Mio…`);
  let t0 = performance.now();
  for (let i = 0; i < nChunks; i++) {
    chunk[0] = i & 255;
    const n = ah.write(chunk, { at: i * MiB });
    if (n !== MiB) throw new Error(`OPFS write court : ${n}`);
  }
  let t1 = performance.now();
  r.seqWrite_1MiB = { bytes: sizeBytes, total_ms: t1 - t0, MiB_per_s: nChunks / ((t1 - t0) / 1000) };
  t0 = performance.now(); ah.flush(); t1 = performance.now();
  r.flushAfterSeqWrite_ms = t1 - t0;
  r.sizeAfterWrite = ah.getSize();
  r.estimateAfterWrite = await estimate();

  // 2. Lectures et écritures aléatoires de 4 Kio et 64 Kio.
  for (const bs of [4 * KiB, 64 * KiB]) {
    const label = `${bs / KiB}K`;
    const buf = new Uint8Array(bs);
    fillPattern(buf.buffer, bs);
    const offsets = randomOffsets(ops, sizeBytes, bs);
    log(`OPFS : ${ops} lectures aléatoires de ${label}…`);
    r[`randRead_${label}`] = timedLoop(ops, (i) => {
      const n = ah.read(buf, { at: offsets[i] });
      if (n !== bs) throw new Error(`OPFS read court : ${n}`);
    });
    log(`OPFS : ${ops} écritures aléatoires de ${label}…`);
    r[`randWrite_${label}`] = timedLoop(ops, (i) => {
      const n = ah.write(buf, { at: offsets[i] });
      if (n !== bs) throw new Error(`OPFS write court : ${n}`);
    });
    t0 = performance.now(); ah.flush(); t1 = performance.now();
    r[`flushAfterRandWrite_${label}_ms`] = t1 - t0;
  }

  // 3. Coût de flush() après chaque écriture de 4 Kio (cas « journal »).
  {
    const buf = new Uint8Array(4 * KiB);
    fillPattern(buf.buffer, 99);
    const offsets = randomOffsets(WRITE_FLUSH_OPS, sizeBytes, 4 * KiB);
    log(`OPFS : ${WRITE_FLUSH_OPS} écritures 4 Kio suivies chacune d'un flush…`);
    r.writeAndFlush_4K = timedLoop(WRITE_FLUSH_OPS, (i) => {
      ah.write(buf, { at: offsets[i] });
      ah.flush();
    });
  }
  ah.close();

  // 4. Réouverture puis lecture séquentielle de 64 Mio (lecture « tiède » : même session).
  const fh2 = await root.getFileHandle(OPFS_FILE);
  ah = await fh2.createSyncAccessHandle();
  const rbuf = new Uint8Array(MiB);
  const n64 = 64;
  log('OPFS : réouverture et lecture séquentielle de 64 Mio…');
  t0 = performance.now();
  for (let i = 0; i < n64; i++) {
    const n = ah.read(rbuf, { at: i * MiB });
    if (n !== MiB) throw new Error(`OPFS read court : ${n}`);
  }
  t1 = performance.now();
  r.reopenSeqRead_64MiB = { total_ms: t1 - t0, MiB_per_s: n64 / ((t1 - t0) / 1000), lastChunkMarkerOk: rbuf[0] === ((n64 - 1) & 255) };
  r.sizeAfterReopen = ah.getSize();
  ah.close();

  // 5. Nettoyage et vérification.
  await root.removeEntry(OPFS_FILE);
  let stillThere = true;
  try { await root.getFileHandle(OPFS_FILE); } catch (e) { stillThere = false; r.cleanupCheck = e.name; }
  r.cleanedUp = !stillThere;
  return r;
}

// ---------------------------------------------------------------- IndexedDB

function idbReq(req) {
  return new Promise((res, rej) => { req.onsuccess = () => res(req.result); req.onerror = () => rej(req.error); });
}
function txDone(tx) {
  return new Promise((res, rej) => {
    tx.oncomplete = () => res();
    tx.onerror = () => rej(tx.error);
    tx.onabort = () => rej(tx.error || new Error('transaction abandonnée'));
  });
}
function idbOpen() {
  return new Promise((res, rej) => {
    const q = indexedDB.open(IDB_NAME, 1);
    q.onupgradeneeded = () => { q.result.createObjectStore(IDB_STORE); };
    q.onsuccess = () => res(q.result);
    q.onerror = () => rej(q.error);
    q.onblocked = () => rej(new Error('ouverture IndexedDB bloquée'));
  });
}

async function benchIdb(sizeBytes, ops) {
  const r = { available: false };
  if (typeof indexedDB === 'undefined') { r.reason = 'indexedDB absent dans le worker'; return r; }
  await idbReq(indexedDB.deleteDatabase(IDB_NAME)).catch((e) => log('suppression préalable IDB : ' + e));
  const db = await idbOpen();
  r.available = true;
  const nChunks = sizeBytes / IDB_CHUNK;
  const chunk = new Uint8Array(IDB_CHUNK);
  fillPattern(chunk.buffer, 7);

  // 1. Remplissage séquentiel (put groupés par transaction).
  log(`IDB : remplissage de ${nChunks} morceaux de 256 Kio (${IDB_FILL_BATCH} put par transaction)…`);
  let t0 = performance.now();
  for (let i = 0; i < nChunks; i += IDB_FILL_BATCH) {
    const tx = db.transaction(IDB_STORE, 'readwrite');
    const st = tx.objectStore(IDB_STORE);
    for (let j = i; j < Math.min(i + IDB_FILL_BATCH, nChunks); j++) {
      chunk[0] = j & 255;
      st.put(chunk.buffer, j);
    }
    await txDone(tx);
    if (i % 1024 === 0 && i > 0) log(`IDB : ${i}/${nChunks} morceaux`);
  }
  let t1 = performance.now();
  r.seqFill_256K = { bytes: sizeBytes, total_ms: t1 - t0, MiB_per_s: (sizeBytes / MiB) / ((t1 - t0) / 1000), putsPerTransaction: IDB_FILL_BATCH };
  r.estimateAfterFill = await estimate();

  const idx = randomIndices(ops, nChunks);

  // 2. get aléatoire d'un morceau, une transaction par opération.
  log(`IDB : ${ops} get aléatoires…`);
  r.randGet_256K = await timedLoopAsync(ops, async (i) => {
    const tx = db.transaction(IDB_STORE, 'readonly');
    const v = await idbReq(tx.objectStore(IDB_STORE).get(idx[i]));
    if (!(v instanceof ArrayBuffer) || v.byteLength !== IDB_CHUNK) throw new Error('IDB get : valeur inattendue');
  });

  // 3. put aléatoire d'un morceau, une transaction par opération.
  log(`IDB : ${ops} put aléatoires (une transaction chacun)…`);
  r.randPut_256K_oneTx = await timedLoopAsync(ops, async (i) => {
    const tx = db.transaction(IDB_STORE, 'readwrite');
    tx.objectStore(IDB_STORE).put(chunk.buffer, idx[i]);
    await txDone(tx);
  });

  // 4. put aléatoires groupés par 16 dans une transaction.
  log(`IDB : ${ops} put aléatoires groupés par ${IDB_PUT_BATCH}…`);
  {
    const nTx = Math.ceil(ops / IDB_PUT_BATCH);
    const lat = [];
    t0 = performance.now();
    for (let t = 0; t < nTx; t++) {
      const a = performance.now();
      const tx = db.transaction(IDB_STORE, 'readwrite');
      const st = tx.objectStore(IDB_STORE);
      for (let j = t * IDB_PUT_BATCH; j < Math.min((t + 1) * IDB_PUT_BATCH, ops); j++) st.put(chunk.buffer, idx[j]);
      await txDone(tx);
      lat.push(performance.now() - a);
    }
    t1 = performance.now();
    const perTx = stats(lat, t1 - t0);
    r.randPut_256K_batch16 = {
      transactions: nTx, putsPerTransaction: IDB_PUT_BATCH,
      tx_median_us: perTx.median_us, tx_p99_us: perTx.p99_us,
      put_mean_us: ((t1 - t0) / ops) * 1000, puts_per_s: ops / ((t1 - t0) / 1000), total_ms: t1 - t0,
    };
  }

  // 5. Écriture d'un bloc de 4 Kio = lecture-modification-écriture d'un morceau de 256 Kio.
  log(`IDB : ${ops} écritures de 4 Kio par lecture-modification-écriture…`);
  {
    const blk = new Uint8Array(4 * KiB);
    fillPattern(blk.buffer, 13);
    r.rmw_4K_in_256K = await timedLoopAsync(ops, async (i) => {
      const tx = db.transaction(IDB_STORE, 'readwrite');
      const st = tx.objectStore(IDB_STORE);
      const v = await idbReq(st.get(idx[i]));
      new Uint8Array(v).set(blk, Math.floor(Math.random() * (IDB_CHUNK / blk.length)) * blk.length);
      st.put(v, idx[i]);
      await txDone(tx);
    });
  }

  // 6. Nettoyage et vérification.
  db.close();
  await idbReq(indexedDB.deleteDatabase(IDB_NAME));
  if (typeof indexedDB.databases === 'function') {
    const list = await indexedDB.databases();
    r.cleanedUp = !list.some((d) => d.name === IDB_NAME);
  } else {
    r.cleanedUp = 'non vérifiable (indexedDB.databases absent)';
  }
  return r;
}

// ---------------------------------------------------------------- pilotage

self.onmessage = async (ev) => {
  const { sizeMiB, ops } = ev.data;
  const sizeBytes = sizeMiB * MiB;
  const results = {
    worker: {
      crossOriginIsolated: self.crossOriginIsolated,
      clockResolution_us: clockResolutionUs(),
      syncAccessHandle: typeof FileSystemFileHandle !== 'undefined' && 'createSyncAccessHandle' in FileSystemFileHandle.prototype,
      estimateStart: await estimate(),
    },
  };
  try {
    results.opfs = await benchOpfs(sizeBytes, ops);
  } catch (e) {
    results.opfs = { available: true, error: e.name + ': ' + e.message };
    try { const root = await navigator.storage.getDirectory(); await root.removeEntry(OPFS_FILE); } catch {}
  }
  try {
    results.idb = await benchIdb(sizeBytes, ops);
  } catch (e) {
    results.idb = { available: true, error: e.name + ': ' + e.message };
    try { await idbReq(indexedDB.deleteDatabase(IDB_NAME)); } catch {}
  }
  results.worker.estimateEnd = await estimate();
  post('done', { results });
};
