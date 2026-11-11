// Essai E6 — relais WebSocket local (prototype jetable de mesure).
// v86 (adaptateur "relay" simple) envoie des TRAMES ETHERNET brutes sur la WebSocket.
// Le relais termine lui-meme Ethernet/ARP/IPv4/UDP(DHCP,DNS)/ICMP/TCP, puis ouvre un vrai
// socket Node vers la cible LOCALE (127.0.0.1:8080 pour le port 80, :8443 pour le 443).
// C'est ici qu'on applique, AVANT d'ouvrir le flux reel, la decision par programme :
//   identite = adresse IP source de la trame (10.0.1.2 = bouteille b1, 10.0.2.2 = b2),
//   posee par l'espace de noms de la bouteille cote invite (routage sans NAT).
// Limites facon Kazo : ports 80/443 seulement, 4 flux simultanes par identite, 300 s par flux.
// Jamais d'Internet reel : toute cible est redirigee vers 127.0.0.1. Aucune donnee ne sort.
// Usage : node relay.mjs [port=9099] [--maxstreams 4] [--maxsec 300]
import http from "node:http";
import net from "node:net";
import { WebSocketServer } from "ws";

const argv = process.argv.slice(2);
const PORT = Number(argv.find((a) => /^\d+$/.test(a)) || 9099);
const opt = (name, def) => { const i = argv.indexOf("--" + name); return i >= 0 ? Number(argv[i + 1]) : def; };
const MAX_STREAMS = opt("maxstreams", 4);
let MAX_SEC = opt("maxsec", 300); // ajustable a chaud via /__relay/maxsec?s=N (pour demontrer la coupure sans attendre 300 s)
const ALLOWED_PORTS = new Set([80, 443]);
const PORT_MAP = { 80: 8080, 443: 8443 };

// Decisions par identite (adresse source de la bouteille). Modifiables par /__relay/decision.
const decisions = {
  "10.0.1.2": { label: "b1", allowPorts: new Set([80]) },      // b1 : HTTP seulement
  "10.0.2.2": { label: "b2", allowPorts: new Set() },          // b2 : refus total
};
const identityOf = (ip) => decisions[ip] || { label: ip, allowPorts: new Set() };

const ROUTER_IP = [192, 168, 86, 1];
const ROUTER_MAC = [0x52, 0x54, 0x00, 0x01, 0x02, 0x03];
const VM_IP = [192, 168, 86, 100];
const STATIC_DNS_A = [203, 0, 113, 9]; // toute resolution pointe vers cette IP "Internet" (terminee par le relais)

const log = [];
const stats = { frames_in: 0, frames_out: 0, streams: [], refused: [], byIdentity: {} };
function record(kind, o) {
  const e = { t: Date.now(), kind, ...o };
  log.push(e);
  if (kind === "open" || kind === "close") stats.streams.push(e);
  if (kind === "refuse") stats.refused.push(e);
  const id = o.identity;
  if (id) { stats.byIdentity[id] = stats.byIdentity[id] || { open: 0, refused: 0, bytes_to_guest: 0 }; }
  console.log("RELAY", kind, JSON.stringify(o));
}

// ---------- utilitaires binaires ----------
const seqGt = (a, b) => { const d = (a - b) >>> 0; return d !== 0 && d < 0x80000000; }; // comparaison serie 32 bits
const b2s = (b, o, n) => Array.from(b.subarray(o, o + n)).join(".");
const mac2s = (b, o) => Array.from(b.subarray(o, o + 6)).map((x) => x.toString(16).padStart(2, "0")).join(":");
function checksum(buf, off, len, initial = 0) {
  let sum = initial;
  for (let i = 0; i < len - 1; i += 2) sum += (buf[off + i] << 8) | buf[off + i + 1];
  if (len & 1) sum += buf[off + len - 1] << 8;
  while (sum >> 16) sum = (sum & 0xffff) + (sum >> 16);
  return (~sum) & 0xffff;
}
function ipChecksum(buf, off) { buf[off + 10] = 0; buf[off + 11] = 0; const c = checksum(buf, off, 20); buf[off + 10] = c >> 8; buf[off + 11] = c & 0xff; }
function l4Checksum(ip, l4Off, l4Len, proto) {
  // pseudo-entete : src(4) dst(4) 0 proto len(2)
  let sum = 0;
  for (let i = 12; i < 20; i += 2) sum += (ip[i] << 8) | ip[i + 1];
  sum += proto; sum += l4Len;
  // met le champ checksum a 0 selon le proto
  const csumOff = proto === 6 ? l4Off + 16 : l4Off + 6;
  ip[csumOff] = 0; ip[csumOff + 1] = 0;
  const c = checksum(ip, l4Off, l4Len, sum);
  ip[csumOff] = c >> 8; ip[csumOff + 1] = c & 0xff;
}

// ---------- une session WebSocket = une VM (un invite v86) ----------
class Session {
  constructor(ws) {
    this.ws = ws;
    this.vmMac = null;             // appris des trames entrantes
    this.conns = new Map();        // cle "sip:sport>dip:dport" -> conn
    this.perIdentity = new Map();  // ip -> nb de flux actifs
  }
  send(frame) { if (this.ws.readyState === 1) { this.ws.send(Buffer.from(frame)); stats.frames_out++; } }

  onFrame(buf) {
    stats.frames_in++;
    if (buf.length < 14) return;
    const ethertype = (buf[12] << 8) | buf[13];
    if (!this.vmMac) this.vmMac = Array.from(buf.subarray(6, 12));
    if (ethertype === 0x0806) return this.onArp(buf);
    if (ethertype === 0x0800) return this.onIp(buf);
    // 0x86dd IPv6 et autres : ignores (pas de pile IPv6 -> l'invite ne sort pas en IPv6)
  }

  frameHead(dstMac, ethertype, ipLen) {
    const f = Buffer.alloc(14 + ipLen);
    for (let i = 0; i < 6; i++) f[i] = dstMac[i];
    for (let i = 0; i < 6; i++) f[6 + i] = ROUTER_MAC[i];
    f[12] = ethertype >> 8; f[13] = ethertype & 0xff;
    return f;
  }

  onArp(buf) {
    const oper = (buf[20] << 8) | buf[21];
    if (oper !== 1) return;
    const tpa = buf.subarray(38, 42);
    // on repond pour la passerelle et pour toute IP hors sous-reseau (masquerade) : L3 par le routeur
    const f = Buffer.alloc(14 + 28);
    const sha = buf.subarray(22, 28), spa = buf.subarray(28, 32);
    for (let i = 0; i < 6; i++) { f[i] = sha[i]; f[6 + i] = ROUTER_MAC[i]; }
    f[12] = 0x08; f[13] = 0x06;
    f.set([0, 1, 0x08, 0, 6, 4, 0, 2], 14);           // htype ptype hlen plen oper=2
    f.set(ROUTER_MAC, 22); f.set(tpa, 28);            // sha=routeur spa=IP demandee
    f.set(sha, 32); f.set(spa, 38);                   // tha spa de la VM
    this.send(f);
  }

  sendIp(dstMac, ipBuf) {
    ipChecksum(ipBuf, 0);
    const f = Buffer.alloc(14 + ipBuf.length);
    for (let i = 0; i < 6; i++) { f[i] = dstMac[i]; f[6 + i] = ROUTER_MAC[i]; }
    f[12] = 0x08; f[13] = 0x00;
    ipBuf.copy(f, 14);
    this.send(f);
  }
  makeIp(proto, src, dst, payloadLen) {
    const ip = Buffer.alloc(20 + payloadLen);
    ip[0] = 0x45; ip[8] = 64; ip[9] = proto;
    ip.writeUInt16BE(20 + payloadLen, 2);
    ip.set(src, 12); ip.set(dst, 16);
    return ip;
  }

  onIp(buf) {
    const ip = buf.subarray(14);
    const ihl = (ip[0] & 0x0f) * 4;
    const proto = ip[9];
    const src = ip.subarray(12, 16), dst = ip.subarray(16, 20);
    const srcMac = Array.from(buf.subarray(6, 12));
    if (proto === 17) return this.onUdp(srcMac, ip, ihl, src, dst);
    if (proto === 1) return this.onIcmp(srcMac, ip, ihl, src, dst);
    if (proto === 6) return this.onTcp(srcMac, ip, ihl, src, dst);
  }

  onIcmp(srcMac, ip, ihl, src, dst) {
    const icmp = ip.subarray(ihl);
    if (icmp[0] !== 8) return; // echo request seulement
    const payload = icmp.subarray(4);
    const out = this.makeIp(1, dst, src, 4 + payload.length);
    out[20] = 0; out[21] = icmp[1];
    out.set(icmp.subarray(4), 24);
    l4Checksum(out, 20, 4 + payload.length, 0 /*icmp: pas de pseudo*/);
    // ICMP checksum = sur tout le message ICMP, sans pseudo-entete
    out[22] = 0; out[23] = 0; const c = checksum(out, 20, 4 + payload.length); out[22] = c >> 8; out[23] = c & 0xff;
    this.sendIp(srcMac, out);
  }

  onUdp(srcMac, ip, ihl, src, dst) {
    const udp = ip.subarray(ihl);
    const sport = (udp[0] << 8) | udp[1], dport = (udp[2] << 8) | udp[3];
    const data = udp.subarray(8);
    if (dport === 67) return this.onDhcp(srcMac, data);
    if (dport === 53) return this.onDns(srcMac, src, sport, data);
    // tout autre UDP (y compris NTP 123) : pas de reponse -> l'invite ne sort pas en UDP par le relais
  }

  udpReply(dstMac, src, dst, sport, dport, payload) {
    const out = this.makeIp(17, src, dst, 8 + payload.length);
    out.writeUInt16BE(sport, 20); out.writeUInt16BE(dport, 22);
    out.writeUInt16BE(8 + payload.length, 24);
    payload.copy(out, 28);
    l4Checksum(out, 20, 8 + payload.length, 17);
    this.sendIp(dstMac, out);
  }

  onDhcp(srcMac, d) {
    // d: op htype hlen hops xid(4) ... chaddr(16) at 28 ... magic at 236 ... options 240+
    const xid = d.subarray(4, 8);
    const chaddr = d.subarray(28, 34);
    let msgType = 0;
    for (let i = 240; i < d.length - 2;) {
      const t = d[i], l = d[i + 1];
      if (t === 255) break; if (t === 0) { i++; continue; }
      if (t === 53) msgType = d[i + 2];
      i += 2 + l;
    }
    if (msgType !== 1 && msgType !== 3) return; // DISCOVER ou REQUEST
    const reply = Buffer.alloc(240 + 64);
    reply[0] = 2; reply[1] = 1; reply[2] = 6;
    xid.copy(reply, 4);
    reply.set(VM_IP, 16);        // yiaddr
    reply.set(ROUTER_IP, 20);    // siaddr
    chaddr.copy(reply, 28);
    reply.set([99, 130, 83, 99], 236); // magic
    const opts = [
      53, 1, (msgType === 1 ? 2 : 5),           // OFFER / ACK
      1, 4, 255, 255, 255, 0,                   // netmask
      3, 4, ...ROUTER_IP,                       // routeur
      6, 4, ...ROUTER_IP,                       // DNS = routeur
      51, 4, 0, 0, 0x0e, 0x10,                  // bail 3600 s
      54, 4, ...ROUTER_IP,                      // identifiant serveur
      255,
    ];
    reply.set(opts, 240);
    this.udpReply(srcMac, ROUTER_IP, VM_IP, 67, 68, reply);
  }

  onDns(srcMac, src, sport, data) {
    // reponse statique : toute question A -> STATIC_DNS_A (le relais terminera la connexion)
    const id = data.subarray(0, 2);
    const q = data.subarray(12);
    let i = 0; while (q[i] !== 0 && i < q.length) i += q[i] + 1; const qend = i + 1 + 4; // nom + 0 + qtype + qclass
    const header = Buffer.from([id[0], id[1], 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0]);
    const question = Buffer.from(q.subarray(0, qend));
    const answer = Buffer.from([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, ...STATIC_DNS_A]);
    this.udpReply(srcMac, ROUTER_IP, src, 53, sport, Buffer.concat([header, question, answer]));
  }

  // ---------- TCP ----------
  tcpSend(conn, flags, payload = null) {
    const plen = payload ? payload.length : 0;
    const tcpLen = 20 + plen;
    const out = this.makeIp(6, conn.dip, conn.sip, tcpLen);
    const t = 20;
    out.writeUInt16BE(conn.dport, t);       // relais = cote distant : sport=dport demande
    out.writeUInt16BE(conn.sport, t + 2);
    out.writeUInt32BE(conn.seq >>> 0, t + 4);
    out.writeUInt32BE(conn.ack >>> 0, t + 8);
    out[t + 12] = 5 << 4;                   // data offset 5 (pas d'options)
    out[t + 13] = flags;
    out.writeUInt16BE(0xffff, t + 14);      // fenetre annoncee par le relais
    if (payload) payload.copy(out, t + 20);
    l4Checksum(out, 20, tcpLen, 6);
    this.sendIp(conn.srcMac, out);
  }

  onTcp(srcMac, ip, ihl, src, dst) {
    const tcp = ip.subarray(ihl);
    const sport = (tcp[0] << 8) | tcp[1], dport = (tcp[2] << 8) | tcp[3];
    const seq = tcp.readUInt32BE(4), ackn = tcp.readUInt32BE(8);
    const doff = (tcp[12] >> 4) * 4;
    const flags = tcp[13];
    const payload = tcp.subarray(doff, (ip[2] << 8 | ip[3]) - ihl);
    const sip = Array.from(src), dip = Array.from(dst);
    const key = `${sip.join(".")}:${sport}>${dip.join(".")}:${dport}`;
    const FIN = flags & 1, SYN = flags & 2, RST = flags & 4, ACK = flags & 16;
    let conn = this.conns.get(key);

    if (SYN && !ACK && !conn) {
      const idIp = sip.join(".");
      const identity = identityOf(idIp);
      const active = this.perIdentity.get(idIp) || 0;
      // --- admission : decision par programme AVANT d'ouvrir le flux reel ---
      let refuse = null;
      if (!ALLOWED_PORTS.has(dport)) refuse = `port ${dport} interdit (seuls 80/443)`;
      else if (!identity.allowPorts.has(dport)) refuse = `decision ${identity.label} : port ${dport} refuse`;
      else if (active >= MAX_STREAMS) refuse = `plafond ${MAX_STREAMS} flux atteint`;
      if (refuse) {
        record("refuse", { identity: idIp, label: identity.label, dport, key, reason: refuse });
        const r = { srcMac, sip, dip, sport, dport, seq: ackn >>> 0, ack: (seq + 1) >>> 0 };
        this.tcpSend(r, 0x14); // RST+ACK
        return;
      }
      conn = this.openConn(srcMac, sip, dip, sport, dport, seq, idIp, identity.label);
      return;
    }
    if (!conn) { if (!RST) { const r = { srcMac, sip, dip, sport, dport, seq: ackn >>> 0, ack: (seq + 1) >>> 0 }; this.tcpSend(r, 0x04); } return; }
    if (RST) { this.closeConn(conn, "rst-invite"); return; }
    const win = tcp.readUInt16BE(14);
    if (payload.length) {
      conn.ack = (conn.ack + payload.length) >>> 0;
      if (conn.socket && !conn.socket.destroyed) conn.socket.write(Buffer.from(payload));
      this.tcpSend(conn, 0x10); // ACK du sens invite -> relais
    }
    if (ACK) { conn.guestWin = win; this.onGuestAck(conn, ackn >>> 0); }
    if (FIN) {
      conn.ack = (conn.ack + 1) >>> 0;
      conn.guestFin = true;
      if (conn.socket && !conn.socket.destroyed) conn.socket.end();
      this.tcpSend(conn, 0x10);
      if (conn.serverFin && conn.sndUna === conn.seq) this.closeConn(conn, "fin-both");
    }
  }

  // ACK recu de l'invite : avance la fenetre, purge le tampon de retransmission, relance l'envoi.
  onGuestAck(conn, ackn) {
    if (seqGt(ackn, conn.sndUna) || ackn === conn.sndUna) conn.sndUna = ackn;
    conn.inflight = conn.inflight.filter((s) => seqGt((s.seq + s.len) >>> 0, conn.sndUna)); // garde ce qui n'est pas entierement acquitte
    if (conn.socket && conn.socket.isPaused() && this.inflightBytes(conn) < 32768) conn.socket.resume();
    this.pump(conn);
    if (conn.serverFin && conn.sndUna === conn.seq && conn.guestFin) this.closeConn(conn, "fin-acked");
  }
  inflightBytes(conn) { return (conn.seq - conn.sndUna) >>> 0; }

  // envoie autant de segments que la fenetre de l'invite le permet
  pump(conn) {
    const MSS = 1400;
    const win = Math.max(MSS, Math.min(conn.guestWin || 64240, 65535));
    while (conn.outBuf.length && this.inflightBytes(conn) + MSS <= win) {
      const seg = conn.outBuf.subarray(0, MSS);
      conn.outBuf = conn.outBuf.subarray(seg.length);
      const s = { seq: conn.seq, len: seg.length, data: Buffer.from(seg), sentAt: Date.now() };
      conn.inflight.push(s);
      this.tcpSend(conn, 0x18, s.data); // PSH+ACK
      conn.seq = (conn.seq + seg.length) >>> 0;
      conn.bytesToGuest += seg.length;
    }
    if (conn.socket && !conn.socket.isPaused() && (conn.outBuf.length > 262144)) conn.socket.pause();
    // FIN differe jusqu'a ce que tout soit envoye ET acquitte
    if (conn.serverPending && conn.outBuf.length === 0 && this.inflightBytes(conn) === 0 && !conn.serverFin) {
      conn.serverFin = true; this.tcpSend(conn, 0x11); conn.seq = (conn.seq + 1) >>> 0;
    }
  }

  retransmit(conn) {
    if (!conn.inflight.length) return;
    const oldest = conn.inflight[0];
    if (Date.now() - oldest.sentAt > 400) {
      // reconstruit le segment a partir de sndUna (cumulatif) et le renvoie
      const r = { srcMac: conn.srcMac, sip: conn.sip, dip: conn.dip, sport: conn.sport, dport: conn.dport, seq: oldest.seq, ack: conn.ack };
      this.tcpSend(r, 0x18, oldest.data);
      oldest.sentAt = Date.now();
    }
  }

  openConn(srcMac, sip, dip, sport, dport, clientSeq, idIp, label) {
    const conn = {
      srcMac, sip, dip, sport, dport, identity: idIp, label, key: `${sip.join(".")}:${sport}>${dip.join(".")}:${dport}`,
      seq: 1000000, sndUna: 1000000, ack: (clientSeq + 1) >>> 0, guestWin: 64240,
      outBuf: Buffer.alloc(0), inflight: [], guestFin: false, serverFin: false, serverPending: false,
      t0: Date.now(), bytesToGuest: 0, socket: null,
    };
    this.conns.set(conn.key, conn);
    this.perIdentity.set(idIp, (this.perIdentity.get(idIp) || 0) + 1);
    const target = net.connect(PORT_MAP[dport], "127.0.0.1");
    conn.socket = target;
    conn.timer = setTimeout(() => { record("timeout", { key: conn.key, identity: idIp, sec: MAX_SEC }); this.tcpSend(conn, 0x14); this.closeConn(conn, "max-sec"); }, MAX_SEC * 1000);
    conn.rtx = setInterval(() => this.retransmit(conn), 150);
    target.on("connect", () => {
      this.tcpSend(conn, 0x12); conn.seq = (conn.seq + 1) >>> 0; // SYN-ACK, consomme 1 seq
      record("open", { key: conn.key, identity: idIp, label, dport });
    });
    target.on("data", (d) => { conn.outBuf = conn.outBuf.length ? Buffer.concat([conn.outBuf, d]) : Buffer.from(d); this.pump(conn); });
    target.on("end", () => { conn.serverPending = true; this.pump(conn); });
    target.on("close", () => { if (conn.guestFin && conn.serverFin) this.closeConn(conn, "socket-close"); });
    target.on("error", (e) => { this.tcpSend(conn, 0x14); this.closeConn(conn, "socket-error:" + (e.code || e.message)); });
    return conn;
  }

  closeConn(conn, why) {
    if (conn.closed) return;
    conn.closed = true;
    clearTimeout(conn.timer); clearInterval(conn.rtx);
    if (conn.socket && !conn.socket.destroyed) conn.socket.destroy();
    this.conns.delete(conn.key);
    this.perIdentity.set(conn.identity, Math.max(0, (this.perIdentity.get(conn.identity) || 1) - 1));
    record("close", { key: conn.key, identity: conn.identity, why, bytes_to_guest: conn.bytesToGuest, dur_s: Math.round((Date.now() - conn.t0) / 100) / 10 });
  }

  destroyAll() { for (const c of [...this.conns.values()]) this.closeConn(c, "ws-close"); }
}

// ---------- serveur HTTP (stats) + WebSocket (relais) ----------
const httpServer = http.createServer((req, res) => {
  if (req.url === "/__relay") { res.writeHead(200, { "Content-Type": "application/json" }); return res.end(JSON.stringify({ stats, log, config: { MAX_STREAMS, MAX_SEC, ports: [...ALLOWED_PORTS] } })); }
  if (req.url === "/__reset") { log.length = 0; stats.frames_in = stats.frames_out = 0; stats.streams = []; stats.refused = []; stats.byIdentity = {}; res.writeHead(200); return res.end("ok"); }
  if (req.url.startsWith("/__relay/maxsec")) { const s = Number(new URL(req.url, "http://x").searchParams.get("s")); if (s > 0) MAX_SEC = s; res.writeHead(200); return res.end(String(MAX_SEC)); }
  if (req.url.startsWith("/__relay/decision")) { const u = new URL(req.url, "http://x"); const ip = u.searchParams.get("ip"); const ports = (u.searchParams.get("ports") || "").split(",").filter(Boolean).map(Number); if (ip) decisions[ip] = { label: u.searchParams.get("label") || ip, allowPorts: new Set(ports) }; res.writeHead(200); return res.end(JSON.stringify(decisions[ip]?.label)); }
  res.writeHead(404); res.end("relay");
});
const wss = new WebSocketServer({ server: httpServer });
wss.on("connection", (ws) => {
  const sess = new Session(ws);
  record("ws-open", {});
  ws.on("message", (data) => { try { sess.onFrame(Buffer.isBuffer(data) ? data : Buffer.from(data)); } catch (e) { console.error("RELAY err", e.message); } });
  ws.on("close", () => { sess.destroyAll(); record("ws-close", {}); });
  ws.on("error", (e) => console.error("RELAY ws", e.message));
});
httpServer.listen(PORT, "127.0.0.1", () => console.log(`E6 relay: ws://localhost:${PORT}/  (max ${MAX_STREAMS} flux/identite, ${MAX_SEC}s, ports ${[...ALLOWED_PORTS]})`));
