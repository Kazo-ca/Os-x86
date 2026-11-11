// Essai E6 — cible HTTP locale que l'invite joint A TRAVERS le relais (jamais en direct).
// Ecoute sur 127.0.0.1 : 8080 tient lieu du port 80, 8443 tient lieu du port 443.
// Compte les requetes et sert un fichier de N octets sur /blob?mb=20 pour le debit.
// Usage : node target.mjs [port1=8080] [port2=8443]
import http from "node:http";

const PORTS = process.argv.slice(2).map(Number);
if (!PORTS.length) PORTS.push(8080, 8443);
const stats = { requests: 0, byPort: {}, byPath: {} };

function handler(port) {
  return (req, res) => {
    stats.requests++;
    stats.byPort[port] = (stats.byPort[port] || 0) + 1;
    const u = new URL(req.url, `http://127.0.0.1:${port}`);
    stats.byPath[u.pathname] = (stats.byPath[u.pathname] || 0) + 1;
    if (u.pathname === "/__stats") {
      res.writeHead(200, { "Content-Type": "application/json" });
      return res.end(JSON.stringify(stats));
    }
    if (u.pathname === "/blob") {
      const mb = Math.min(64, Number(u.searchParams.get("mb") || 1));
      const chunk = Buffer.alloc(1024 * 1024, 0x61);
      res.writeHead(200, { "Content-Type": "application/octet-stream", "Content-Length": mb * chunk.length });
      let i = 0;
      const pump = () => {
        while (i < mb) { if (!res.write(chunk)) { i++; res.once("drain", pump); return; } i++; }
        res.end();
      };
      return pump();
    }
    res.writeHead(200, { "Content-Type": "text/plain" });
    res.end(`E6 target on :${port} path=${u.pathname} ok\n`);
  };
}

for (const p of PORTS) {
  http.createServer(handler(p)).listen(p, "127.0.0.1", () => console.log(`E6 target: http://127.0.0.1:${p}`));
}
