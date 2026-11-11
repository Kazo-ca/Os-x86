// Petit serveur statique local pour l'essai E2 (code jetable).
// Sert les fichiers du dossier courant sur 127.0.0.1, port libre choisi par le système.
// Envoie COOP/COEP pour obtenir l'isolation d'origine croisée (performance.now() plus fin).

import http from 'node:http';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.json': 'application/json' };

export function startServer() {
  const server = http.createServer(async (req, res) => {
    const urlPath = decodeURIComponent(new URL(req.url, 'http://127.0.0.1').pathname);
    const file = path.normalize(path.join(ROOT, urlPath === '/' ? 'bench.html' : urlPath));
    if (!file.startsWith(ROOT)) { res.writeHead(403); res.end(); return; }
    if (urlPath === '/favicon.ico') { res.writeHead(204); res.end(); return; }
    try {
      const body = await readFile(file);
      res.writeHead(200, {
        'Content-Type': TYPES[path.extname(file)] || 'application/octet-stream',
        'Cross-Origin-Opener-Policy': 'same-origin',
        'Cross-Origin-Embedder-Policy': 'require-corp',
        'Cache-Control': 'no-store',
      });
      res.end(body);
    } catch {
      res.writeHead(404); res.end('introuvable');
    }
  });
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => resolve({ server, port: server.address().port }));
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { port } = await startServer();
  process.stdout.write(`http://127.0.0.1:${port}/bench.html\n`);
}
