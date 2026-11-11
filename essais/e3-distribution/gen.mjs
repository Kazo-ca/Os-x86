// Essai E3 — génère images/data-<N>.bin (motif pseudo-aléatoire, N Mio) et son manifeste
// images/data-<N>.manifest.json : SHA-256 du fichier entier et SHA-256 par morceau pour 8, 32, 128, 512 Mio.
// Usage : node gen.mjs [--size 800] [--name data-800]
import { createHash } from 'node:crypto';
import { open, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const MiB = 1024 * 1024;
const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i >= 0 ? process.argv[i + 1] : d; };
const sizeMiB = Number(arg('size', 800));
const name = arg('name', `data-${sizeMiB}`);
const CHUNK_SIZES = [8, 32, 128, 512].map((m) => m * MiB).filter((c) => c <= sizeMiB * MiB);

await mkdir(path.join(HERE, 'images'), { recursive: true });
const file = path.join(HERE, 'images', `${name}.bin`);
const fh = await open(file, 'w');
const block = new Uint8Array(4 * MiB);
const u32 = new Uint32Array(block.buffer);
let x = 0x9e3779b9;
const whole = createHash('sha256');
const perChunk = Object.fromEntries(CHUNK_SIZES.map((c) => [c, { hashes: [], cur: createHash('sha256'), filled: 0 }]));
const t0 = Date.now();
for (let written = 0; written < sizeMiB * MiB; written += block.length) {
  for (let i = 0; i < u32.length; i++) { x ^= x << 13; x >>>= 0; x ^= x >>> 17; x ^= x << 5; x >>>= 0; u32[i] = x; }
  await fh.write(block);
  whole.update(block);
  for (const c of CHUNK_SIZES) {
    const s = perChunk[c];
    s.cur.update(block); s.filled += block.length;
    if (s.filled === c) { s.hashes.push(s.cur.digest('hex')); s.cur = createHash('sha256'); s.filled = 0; }
  }
}
await fh.close();
for (const c of CHUNK_SIZES) if (perChunk[c].filled > 0) perChunk[c].hashes.push(perChunk[c].cur.digest('hex'));
const manifest = {
  file: `${name}.bin`, size: sizeMiB * MiB, sha256: whole.digest('hex'),
  chunks: Object.fromEntries(CHUNK_SIZES.map((c) => [c, perChunk[c].hashes])),
};
await writeFile(path.join(HERE, 'images', `${name}.manifest.json`), JSON.stringify(manifest, null, 1));
console.log(`${name}.bin : ${sizeMiB} Mio en ${((Date.now() - t0) / 1000).toFixed(1)} s, sha256 ${manifest.sha256}`);
