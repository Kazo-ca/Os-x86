// Essai E7 — lecteur ext4 minimal, en lecture seule, jetable : liste récursivement un chemin d'une image ext4
// depuis Node (superbloc, descripteurs de groupe, table d'inodes, extents, entrées de répertoire ; pas de
// journal, pas de htree, pas de chiffrement, pas de fichiers > 4 Gio). Sert à lire le disque interne d'une VM
// à l'arrêt (plan 4.2 ligne E7 : « disque interne lu à l'arrêt »).
// Usage : node ext4-ls.mjs <image> [chemin=/] [--json]   ou   import { openExt4 } from "./ext4-ls.mjs"
import fs from "node:fs";

export function openExt4(file) {
  const fd = fs.openSync(file, "r");
  const readAt = (off, len) => { const b = Buffer.alloc(len); fs.readSync(fd, b, 0, len, Number(off)); return b; };
  const sb = readAt(1024, 1024);
  if (sb.readUInt16LE(0x38) !== 0xEF53) throw new Error("pas un ext2/3/4 (magic)");
  const blockSize = 1024 << sb.readUInt32LE(0x18);
  const inodesPerGroup = sb.readUInt32LE(0x28), inodeSize = sb.readUInt16LE(0x58);
  const featIncompat = sb.readUInt32LE(0x60);
  const is64 = !!(featIncompat & 0x80), descSize = is64 ? sb.readUInt16LE(0xFE) || 64 : 32;
  const gdtStart = blockSize === 1024 ? 2048 : blockSize;
  const groupDesc = (g) => { const d = readAt(gdtStart + g * descSize, descSize); let tbl = BigInt(d.readUInt32LE(8)); if (is64 && descSize >= 64) tbl |= BigInt(d.readUInt32LE(40)) << 32n; return { inodeTable: tbl }; };
  const readInode = (ino) => {
    const g = Math.floor((ino - 1) / inodesPerGroup), idx = (ino - 1) % inodesPerGroup;
    const off = groupDesc(g).inodeTable * BigInt(blockSize) + BigInt(idx * inodeSize);
    const b = readAt(off, inodeSize);
    const flags = b.readUInt32LE(0x20);
    const size = Number(BigInt(b.readUInt32LE(0x4)) | (BigInt(b.readUInt32LE(0x6C)) << 32n));
    return { mode: b.readUInt16LE(0), uid: b.readUInt16LE(2) | (b.readUInt16LE(0x78) << 16), size, mtime: b.readUInt32LE(0x10), ctime: b.readUInt32LE(0xC), links: b.readUInt16LE(0x1A), flags, iblock: b.subarray(0x28, 0x28 + 60) };
  };
  // Extents : retourne la liste des (logicalBlock, physicalBlock, len) ; arbres de profondeur > 0 suivis.
  const extents = (node, out = []) => {
    const magic = node.readUInt16LE(0); if (magic !== 0xF30A) throw new Error("extent magic");
    const entries = node.readUInt16LE(2), depth = node.readUInt16LE(6);
    for (let i = 0; i < entries; i++) {
      const e = node.subarray(12 + i * 12, 24 + i * 12);
      if (depth === 0) { const len = e.readUInt16LE(4); out.push({ lblk: e.readUInt32LE(0), pblk: Number(BigInt(e.readUInt32LE(8)) | (BigInt(e.readUInt16LE(6)) << 32n)), len: len > 32768 ? len - 32768 : len }); }
      else { const child = Number(BigInt(e.readUInt32LE(4)) | (BigInt(e.readUInt16LE(8)) << 32n)); extents(readAt(BigInt(child) * BigInt(blockSize), blockSize), out); }
    }
    return out;
  };
  const readData = (inode) => {
    if (inode.flags & 0x10000000) return inode.iblock.subarray(0, inode.size); // inline data (petits fichiers/répertoires)
    if (!(inode.flags & 0x80000)) throw new Error("inode sans extents (ancien mappage indirect non géré)");
    const chunks = [];
    for (const x of extents(inode.iblock)) chunks.push({ lblk: x.lblk, data: readAt(BigInt(x.pblk) * BigInt(blockSize), x.len * blockSize) });
    chunks.sort((a, b) => a.lblk - b.lblk);
    const out = Buffer.alloc(inode.size);
    for (const c of chunks) c.data.copy(out, c.lblk * blockSize, 0, Math.min(c.data.length, inode.size - c.lblk * blockSize));
    return out;
  };
  const listDir = (ino) => {
    const inode = readInode(ino); if ((inode.mode & 0xF000) !== 0x4000) throw new Error("pas un répertoire");
    const data = readData(inode), entries = [];
    for (let off = 0; off + 8 <= data.length;) {
      const eino = data.readUInt32LE(off), recLen = data.readUInt16LE(off + 4), nameLen = data[off + 6], type = data[off + 7];
      if (recLen < 8) break;
      if (eino) { const name = data.toString("utf8", off + 8, off + 8 + nameLen); if (name !== "." && name !== "..") entries.push({ name, ino: eino, type }); }
      off += recLen;
    }
    return entries;
  };
  const lookup = (p) => { let ino = 2; for (const part of p.split("/").filter(Boolean)) { const e = listDir(ino).find((x) => x.name === part); if (!e) throw new Error("introuvable: " + part); ino = e.ino; } return ino; };
  const walk = (p, depth = 50) => {
    const ino = lookup(p), out = [];
    const rec = (dirIno, prefix, d) => {
      for (const e of listDir(dirIno)) {
        const inode = readInode(e.ino); const isDir = (inode.mode & 0xF000) === 0x4000;
        out.push({ path: prefix + e.name, ino: e.ino, dir: isDir, size: inode.size, uid: inode.uid, mode: (inode.mode & 0o7777).toString(8), mtime: inode.mtime });
        if (isDir && d > 0) rec(e.ino, prefix + e.name + "/", d - 1);
      }
    };
    rec(ino, p.replace(/\/?$/, "/"), depth);
    return out;
  };
  const readFile = (p) => readData(readInode(lookup(p)));
  return { blockSize, inodeSize, inodesPerGroup, is64, listDir, readInode, walk, readFile, lookup, close: () => fs.closeSync(fd) };
}

if (process.argv[1] && import.meta.url.endsWith(process.argv[1].replace(/\\/g, "/").split("/").pop())) {
  const [img, p0 = "/"] = process.argv.slice(2).filter((a) => !a.startsWith("--")); const p = p0.replace(/^[A-Za-z]:/, "") || "/";  // Git Bash transforme « / » en « C:/ »
  const t0 = performance.now();
  const e = openExt4(img);
  const list = e.walk(p);
  const ms = performance.now() - t0;
  if (process.argv.includes("--json")) console.log(JSON.stringify({ ms, count: list.length, blockSize: e.blockSize, entries: list }));
  else { for (const x of list) console.log(`${x.dir ? "d" : "-"} ${x.mode.padStart(4)} ${String(x.uid).padStart(5)} ${String(x.size).padStart(9)} ${new Date(x.mtime * 1000).toISOString()} ${x.path}`); console.log(`${list.length} entrées en ${ms.toFixed(1)} ms`); }
  e.close();
}
