"use strict";
// Essai E14 — simulateur du terminal de Kazo au-dessus de v86 (prototype jetable).
// Paramètres d'URL : ?guest=dos|wine  &poll=33  &route=vga|pty  &cols=80&rows=25  &mem=1024  &fs=wine-fs.json
// Deux voies d'affichage dans la même grille :
//   vga : sondage de emu.screen_adapter.get_text_screen() (adaptateur « ansi », sans canvas) ;
//   pty : octets de ttyS1 (serial1-output-byte) interprétés par un petit émulateur VT (xterm réduit).
// Entrée : jeu de touches réduit d'une commande Terminal de Kazo (lettres, chiffres, ponctuation,
// Entrée, Retour, Tab, flèches, Échap, Ctrl+lettre ; F1-F12 et Alt refusés ; Ctrl+C = fermer la commande).
// Tout l'état de mesure est dans window.E14 (drive.mjs / run.mjs).

const E14 = window.E14 = {
  t0: performance.now(),
  params: Object.fromEntries(new URLSearchParams(location.search)),
  marks: [], events: [], serial: "", serial1: "", serial1Bytes: 0,
  rejectedKeys: [], keySamples: [], guestWriteSamples: [], pollSamples: [], pollOverruns: 0,
  graphical: null, screenSizeEvents: [],
  emu: null, vt: null,
};
const $ = (id) => document.getElementById(id);
const guest = E14.params.guest || "dos";
const memMiB = Number(E14.params.mem || (guest === "dos" ? 64 : 1024));
let cols = Number(E14.params.cols || 80), rows = Number(E14.params.rows || 25);
$("cols").value = cols; $("rows").value = rows;
$("guest_name").textContent = guest;
if (E14.params.poll) $("poll").value = E14.params.poll;
if (E14.params.route) $("route").value = E14.params.route;

// ---------------------------------------------------------------- v86
const config = {
  wasm_path: "lib/v86.wasm",
  memory_size: memMiB * 1024 * 1024,
  vga_memory_size: 8 * 1024 * 1024,
  screen: { ansi: true },        // pas de conteneur DOM : seul get_text_screen() alimente le terminal
  bios: { url: "lib/seabios.bin" },
  vga_bios: { url: "lib/vgabios.bin" },
  autostart: true,
  disable_keyboard: true,        // le simulateur envoie lui-même les scancodes
  disable_mouse: true,
  disable_speaker: true,
  uart1: true,                   // ttyS1 (0x2F8) : v86 ne crée le 2e UART que sur demande (option non documentée)
};
if (guest === "dos") {
  config.hda = { url: "images/dos.img", async: false };
  config.boot_order = 0x132;
} else {
  const fsName = E14.params.fs || "wine-fs.json", flatDir = E14.params.flat || "rootfs-flat";
  config.filesystem = { baseurl: "images/" + flatDir + "/", basefs: "images/" + fsName };
  config.bzimage_initrd_from_filesystem = true;
  config.cmdline = "rw root=host9p rootfstype=9p rootflags=trans=virtio,cache=loose modules=virtio_pci console=ttyS0 tsc=reliable mitigations=off";
}
if (E14.params.state) config.initial_state = { url: "images/" + E14.params.state };
const emu = E14.emu = new V86(config);
// ttyS1 : lignes modem DCD/DSR/CTS levées, sinon un open() ordinaire (sans O_NONBLOCK ni clocal) bloque dans l'invité.
emu.add_listener("emulator-ready", () => { try { emu.serial_set_carrier_detect(1, true); emu.serial_set_data_set_ready(1, true); emu.serial_set_clear_to_send(1, true); } catch {} });
for (const ev of ["emulator-ready", "emulator-started", "emulator-stopped", "download-error"]) {
  emu.add_listener(ev, () => { E14.events.push({ name: ev, t: performance.now() }); setState(ev); });
}
emu.add_listener("screen-set-size", ([w, h, bpp]) => {
  const graphical = bpp !== 0;
  E14.screenSizeEvents.push({ w, h, bpp, graphical, t: performance.now() });
  if (graphical !== E14.graphical) {
    E14.graphical = graphical;
    setState(graphical ? `MODE GRAPHIQUE ${w}x${h}x${bpp} (signal 164 : à envoyer à BoxedWine)` : `mode texte ${w}x${h}`);
  }
  // Le BIOS passe par 80x256 puis 80x16 avant 80x25 : la grille suit la taille texte courante (voie VGA).
  if (!graphical && (w !== cols || h !== rows) && route() === "vga") { cols = w; rows = h; $("cols").value = w; $("rows").value = h; grid.resize(cols, rows); lastRawKey = null; lastRowsText = null; }
});
function setState(s) { $("state").textContent = "(" + s + ")"; }

// Console série 0 : shell de contrôle (marques, commandes de drive.mjs). Jamais affichée dans le terminal.
let line = "";
emu.add_listener("serial0-output-byte", (byte) => {
  const c = String.fromCharCode(byte);
  E14.serial += c;
  const el = $("serial"); el.textContent += c;
  if (el.textContent.length > 100000) el.textContent = el.textContent.slice(-50000);
  el.scrollTop = el.scrollHeight;
  if (c === "\n") {
    const l = line.trim(); line = "";
    const mm = /^MARK (\S+)(?: (\S+))?$/.exec(l);
    if (mm) E14.marks.push({ name: mm[1], uptime: Number(mm[2] || 0), t: performance.now() });
    const ex = /E14-EXIT (\d+)\s*$/.exec(l);   // la ligne peut être précédée du prompt du shell ttyS0
    if (ex) E14.events.push({ name: "exit", code: Number(ex[1]), t: performance.now() });
  } else if (c !== "\r") line += c;
});
$("serial_in").onkeydown = function (e) { if (e.key === "Enter") { emu.serial0_send(this.value + "\n"); this.value = ""; } e.stopPropagation(); };

// ---------------------------------------------------------------- grille (le « terminal »)
// Palette VGA (16 couleurs) : l'adaptateur ansi de v86 donne des SGR 24 bits issus de la palette VGA ;
// l'émulateur pty donne des indices 0-15. On affiche tout en RVB.
const VGA = ["#000000", "#0000aa", "#00aa00", "#00aaaa", "#aa0000", "#aa00aa", "#aa5500", "#aaaaaa",
  "#555555", "#5555ff", "#55ff55", "#55ffff", "#ff5555", "#ff55ff", "#ffff55", "#ffffff"];
class Grid {
  constructor(el) { this.el = el; this.rowsHtml = []; this.resize(cols, rows); }
  resize(c, r) {
    this.cols = c; this.rows = r; this.rowsHtml = new Array(r).fill(null);
    this.el.replaceChildren(...Array.from({ length: r }, () => document.createElement("div")));
    this.el.style.width = (c * 9.6 + 2) + "px";
  }
  /** rowsCells : tableau de lignes ; ligne = tableau de cellules {ch, fg, bg} (fg/bg : couleurs CSS). cursor : {row, col} ou null */
  render(rowsCells, cursor) {
    let changed = 0;
    for (let r = 0; r < this.rows; r++) {
      const cells = rowsCells[r] || [];
      let html = "", run = "", fg = null, bg = null;
      const flush = () => { if (run) html += `<span style="color:${fg};background:${bg}">${run}</span>`; run = ""; };
      for (let c = 0; c < this.cols; c++) {
        const cell = cells[c] || { ch: " ", fg: "#aaaaaa", bg: "#000000" };
        const isCur = cursor && cursor.row === r && cursor.col === c;
        if (cell.fg !== fg || cell.bg !== bg || isCur) { flush(); fg = cell.fg; bg = cell.bg; }
        const ch = cell.ch === "<" ? "&lt;" : cell.ch === "&" ? "&amp;" : cell.ch;
        if (isCur) { html += `<span class="cur" style="color:${fg};background:${bg}">${ch}</span>`; fg = null; }
        else run += ch;
      }
      flush();
      if (html !== this.rowsHtml[r]) { this.rowsHtml[r] = html; this.el.children[r].innerHTML = html; changed++; }
    }
    return changed;
  }
}
const grid = new Grid($("term"));

// ---------------------------------------------------------------- voie VGA : sondage de get_text_screen()
// L'adaptateur « ansi » de v86 rend chaque ligne avec des SGR 24 bits (\x1b[38;2;r;g;bm / 48;2) et finit par \x1b[0m.
function parseAnsiRow(s) {
  const cells = []; let fg = "#aaaaaa", bg = "#000000";
  const re = /\x1b\[([0-9;]*)m/g; let last = 0, m;
  const push = (text) => { for (const ch of text) cells.push({ ch, fg, bg }); };
  while ((m = re.exec(s))) {
    push(s.slice(last, m.index)); last = re.lastIndex;
    const p = m[1].split(";").map(Number);
    if (p[0] === 0) { fg = "#aaaaaa"; bg = "#000000"; }
    else if ((p[0] === 38 || p[0] === 48) && p[1] === 2) { const col = `rgb(${p[2]},${p[3]},${p[4]})`; if (p[0] === 38) fg = col; else bg = col; }
  }
  push(s.slice(last));
  return cells;
}
function vgaCursor() {
  // Pas d'API publique pour le curseur avec l'adaptateur ansi : on lit le périphérique VGA de v86 (interne).
  try {
    const vga = emu.v86.cpu.devices.vga;
    const w = Math.max(vga.max_cols, 2 * vga.offset_register);
    const a = vga.cursor_address - vga.start_address;
    if (a < 0) return null;
    return { row: (a / w) | 0, col: a % w, enabled: !(vga.cursor_scanline_start & 0x20) };
  } catch { return null; }
}
let lastRowsText = null;
// Lecture « brute » : mémoire texte du périphérique VGA de v86 (interne, non publié) : code CP437 + attribut
// (fg 4 bits, bg 3 ou 4 bits). Donne les vraies couleurs VGA et évite la table de glyphes de l'adaptateur ansi
// (qui rend certains caractères CP437 en demi-paires de substitution, ex. 0xBF « ┐ »).
const CP437 = "\u0000☺☻♥♦♣♠•◘○◙♂♀♪♫☼►◄↕‼¶§▬↨↑↓→←∟↔▲▼ !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~⌂ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";
E14.vgaRead = E14.params.vga || "raw";   // raw | ansi
function readVgaRaw() {
  const vga = emu.v86?.cpu?.devices?.vga;
  if (!vga || !vga.vga_memory) return null;
  const mem = vga.vga_memory, w = vga.max_cols, h = vga.max_rows;
  const stride = Math.max(w, 2 * vga.offset_register) * 2;
  const blink = vga.attribute_mode & 8;
  const rowsCells = [], text = [];
  let base = vga.start_address << 1;
  for (let r = 0; r < h; r++) {
    const cells = new Array(w); let s = "";
    for (let c = 0, a = base; c < w; c++, a += 2) {
      const ch = CP437[mem[a]] || " ", attr = mem[a | 1];
      cells[c] = { ch, fg: VGA[attr & 15], bg: VGA[blink ? (attr >> 4) & 7 : attr >> 4] };
      s += ch;
    }
    rowsCells.push(cells); text.push(s); base += stride;
  }
  return { rowsCells, text };
}
function pollVga() {
  const t = performance.now();
  if (!emu.screen_adapter) return 0;
  if (E14.vgaRead === "raw") return pollVgaRaw(t);
  const raw = emu.screen_adapter.get_text_screen();
  const tRead = performance.now();
  const joined = raw.join("\n");
  let changedRows = 0;
  if (joined !== lastRowsText) {
    lastRowsText = joined;
    const cells = raw.map(parseAnsiRow);
    const cur = vgaCursor();
    changedRows = grid.render(cells, cur && cur.enabled ? cur : null);
    E14.screenText = raw.map((r) => r.replace(/\x1b\[[0-9;]*m/g, ""));
  } else {
    const cur = vgaCursor();
    if (cur && (cur.row !== lastCursor?.row || cur.col !== lastCursor?.col)) { changedRows = grid.render(raw.map(parseAnsiRow), cur.enabled ? cur : null); }
    lastCursor = cur;
  }
  const tEnd = performance.now();
  recordPoll(tRead - t, tEnd - t, changedRows, t);
  return changedRows;
}
let lastCursor = null;
let lastRawKey = null;
function pollVgaRaw(t) {
  const r = readVgaRaw();
  const tRead = performance.now();
  if (!r) return 0;
  const cur = vgaCursor();
  // Clé de changement : texte + attributs (somme rapide) + curseur.
  let attrKey = 0;
  for (const row of r.rowsCells) for (const c of row) attrKey = (attrKey * 31 + c.fg.length + c.bg.charCodeAt(1) + c.bg.charCodeAt(3)) | 0;
  const key = r.text.join("\n") + "|" + attrKey + "|" + (cur ? cur.row + "," + cur.col + "," + cur.enabled : "");
  let changedRows = 0;
  if (key !== lastRawKey) {
    lastRawKey = key;
    changedRows = grid.render(r.rowsCells, cur && cur.enabled ? cur : null);
    E14.screenText = r.text;
  }
  recordPoll(tRead - t, performance.now() - t, changedRows, t);
  return changedRows;
}

// ---------------------------------------------------------------- voie pty : émulateur VT minimal sur ttyS1
class Vt {
  constructor(c, r) { this.resize(c, r); this.out = (s) => emu.serial_send_bytes(1, new TextEncoder().encode(s)); }
  resize(c, r) {
    this.cols = c; this.rows = r; this.screen = this.blank(); this.alt = null;
    this.x = 0; this.y = 0; this.top = 0; this.bot = r - 1; this.sgr = { fg: 7, bg: 0, bold: false, inv: false };
    this.state = "g"; this.buf = ""; this.saved = null; this.wrapPending = false; this.cursorVisible = true; this.changed = true;
  }
  blank() { return Array.from({ length: this.rows }, () => this.blankRow()); }
  blankRow() { return Array.from({ length: this.cols }, () => ({ ch: " ", fg: 7, bg: 0, bold: false, inv: false })); }
  cells() {
    return this.screen.map((row) => row.map((c) => {
      let fg = VGA[c.bold && c.fg < 8 ? c.fg + 8 : c.fg], bg = VGA[c.bg];
      if (c.inv) [fg, bg] = [bg, fg];
      return { ch: c.ch, fg, bg };
    }));
  }
  feed(bytes) { for (const b of bytes) this.byte(b); }
  byte(b) {
    const ch = String.fromCharCode(b);
    if (this.state === "g") {
      if (b === 0x1b) { this.state = "esc"; return; }
      if (b === 0x0d) { this.x = 0; this.wrapPending = false; return; }
      if (b === 0x0a || b === 0x0b || b === 0x0c) { this.lf(); return; }
      if (b === 0x08) { if (this.x > 0) this.x--; this.wrapPending = false; return; }
      if (b === 0x09) { this.x = Math.min(this.cols - 1, (this.x + 8) & ~7); return; }
      if (b === 0x07) return;
      if (b < 0x20) return;
      if (b >= 0x80) { this.utf = (this.utf || []).concat(b); const s = tryUtf8(this.utf); if (s === null) return; this.utf = null; if (s === "") return; this.put(s); return; }
      this.put(ch); return;
    }
    if (this.state === "esc") {
      if (ch === "[") { this.state = "csi"; this.buf = ""; return; }
      if (ch === "]") { this.state = "osc"; this.buf = ""; return; }
      if (ch === "(" || ch === ")") { this.state = "chs"; return; }
      this.state = "g";
      if (ch === "7") this.saved = { x: this.x, y: this.y, sgr: { ...this.sgr } };
      else if (ch === "8" && this.saved) { this.x = this.saved.x; this.y = this.saved.y; this.sgr = { ...this.saved.sgr }; }
      else if (ch === "M") { if (this.y === this.top) this.scroll(-1); else this.y = Math.max(0, this.y - 1); }
      else if (ch === "D") this.lf();
      else if (ch === "E") { this.x = 0; this.lf(); }
      else if (ch === "c") this.resize(this.cols, this.rows);
      else if (ch === "=" || ch === ">") {}
      else E14.events.push({ name: "vt-unknown-esc", seq: ch, t: performance.now() });
      return;
    }
    if (this.state === "chs") { this.state = "g"; return; }
    if (this.state === "osc") { if (b === 0x07) this.state = "g"; else if (b === 0x1b) this.state = "osc-esc"; return; }
    if (this.state === "osc-esc") { this.state = "g"; return; }
    if (this.state === "csi") {
      if (b >= 0x40 && b <= 0x7e) { this.csi(this.buf, ch); this.state = "g"; } else this.buf += ch;
    }
  }
  put(s) {
    if (this.wrapPending) { this.x = 0; this.lf(); this.wrapPending = false; }
    const row = this.screen[this.y];
    row[this.x] = { ch: s, fg: this.sgr.fg, bg: this.sgr.bg, bold: this.sgr.bold, inv: this.sgr.inv };
    this.lastChar = s;
    this.changed = true;
    if (this.x === this.cols - 1) this.wrapPending = true; else this.x++;
  }
  lf() { if (this.y === this.bot) this.scroll(1); else if (this.y < this.rows - 1) this.y++; this.changed = true; }
  scroll(n) {
    const region = this.screen.slice(this.top, this.bot + 1);
    if (n > 0) { region.shift(); region.push(this.blankRow()); } else { region.pop(); region.unshift(this.blankRow()); }
    for (let i = 0; i < region.length; i++) this.screen[this.top + i] = region[i];
    this.changed = true;
  }
  csi(params, final) {
    const priv = params.startsWith("?"); if (priv) params = params.slice(1);
    const p = params.split(";").map((x) => (x === "" ? 0 : Number(x)));
    const n = Math.max(1, p[0] || 0);
    this.changed = true;
    switch (final) {
      case "A": this.y = Math.max(this.top, this.y - n); this.wrapPending = false; break;
      case "B": this.y = Math.min(this.bot, this.y + n); this.wrapPending = false; break;
      case "C": this.x = Math.min(this.cols - 1, this.x + n); this.wrapPending = false; break;
      case "D": this.x = Math.max(0, this.x - n); this.wrapPending = false; break;
      case "G": this.x = Math.min(this.cols - 1, n - 1); this.wrapPending = false; break;
      case "d": this.y = Math.min(this.rows - 1, n - 1); break;
      case "H": case "f": this.y = Math.min(this.rows - 1, Math.max(1, p[0] || 1) - 1); this.x = Math.min(this.cols - 1, Math.max(1, p[1] || 1) - 1); this.wrapPending = false; break;
      case "J": {
        const m = p[0] || 0;
        if (m === 2 || m === 3) this.screen = this.blank();
        else if (m === 0) { this.eraseLine(this.y, this.x, this.cols); for (let r = this.y + 1; r < this.rows; r++) this.screen[r] = this.blankRow(); }
        else if (m === 1) { this.eraseLine(this.y, 0, this.x + 1); for (let r = 0; r < this.y; r++) this.screen[r] = this.blankRow(); }
        break;
      }
      case "K": { const m = p[0] || 0; if (m === 0) this.eraseLine(this.y, this.x, this.cols); else if (m === 1) this.eraseLine(this.y, 0, this.x + 1); else this.eraseLine(this.y, 0, this.cols); break; }
      case "L": for (let i = 0; i < n; i++) { this.screen.splice(this.bot, 1); this.screen.splice(this.y, 0, this.blankRow()); } break;
      case "M": for (let i = 0; i < n; i++) { this.screen.splice(this.y, 1); this.screen.splice(this.bot, 0, this.blankRow()); } break;
      case "P": { const row = this.screen[this.y]; row.splice(this.x, n); while (row.length < this.cols) row.push({ ch: " ", fg: 7, bg: 0, bold: false, inv: false }); break; }
      case "@": { const row = this.screen[this.y]; for (let i = 0; i < n; i++) row.splice(this.x, 0, { ch: " ", fg: 7, bg: 0, bold: false, inv: false }); row.length = this.cols; break; }
      case "X": this.eraseLine(this.y, this.x, Math.min(this.cols, this.x + n)); break;
      case "b": for (let i = 0; i < n && this.lastChar; i++) this.put(this.lastChar); break;   // REP (ncurses avec TERM=xterm)
      case "S": for (let i = 0; i < n; i++) this.scroll(1); break;
      case "T": for (let i = 0; i < n; i++) this.scroll(-1); break;
      case "r": this.top = Math.max(0, (p[0] || 1) - 1); this.bot = Math.min(this.rows - 1, (p[1] || this.rows) - 1); this.x = 0; this.y = this.top; break;
      case "m": this.sgrSet(p); break;
      case "h": case "l": {
        const on = final === "h";
        for (const q of p) {
          if (priv && q === 25) this.cursorVisible = on;
          else if (priv && (q === 1049 || q === 47 || q === 1047)) { if (on && !this.alt) { this.alt = { screen: this.screen, x: this.x, y: this.y }; this.screen = this.blank(); } else if (!on && this.alt) { this.screen = this.alt.screen; this.x = this.alt.x; this.y = this.alt.y; this.alt = null; } }
        }
        break;
      }
      case "n": if (p[0] === 6) this.out(`\x1b[${this.y + 1};${this.x + 1}R`); break;
      case "c": this.out("\x1b[?1;2c"); break;
      case "s": this.saved = { x: this.x, y: this.y, sgr: { ...this.sgr } }; break;
      case "u": if (this.saved) { this.x = this.saved.x; this.y = this.saved.y; } break;
      case "t": break;
      default: E14.events.push({ name: "vt-unknown-csi", seq: (priv ? "?" : "") + params + final, t: performance.now() });
    }
  }
  eraseLine(y, from, to) { const row = this.screen[y]; for (let c = from; c < to; c++) row[c] = { ch: " ", fg: 7, bg: this.sgr.bg, bold: false, inv: false }; }
  sgrSet(p) {
    if (p.length === 0) p = [0];
    for (let i = 0; i < p.length; i++) {
      const v = p[i];
      if (v === 0) this.sgr = { fg: 7, bg: 0, bold: false, inv: false };
      else if (v === 1) this.sgr.bold = true;
      else if (v === 22) this.sgr.bold = false;
      else if (v === 7) this.sgr.inv = true;
      else if (v === 27) this.sgr.inv = false;
      else if (v >= 30 && v <= 37) this.sgr.fg = v - 30;
      else if (v >= 90 && v <= 97) this.sgr.fg = v - 90 + 8;
      else if (v === 39) this.sgr.fg = 7;
      else if (v >= 40 && v <= 47) this.sgr.bg = v - 40;
      else if (v >= 100 && v <= 107) this.sgr.bg = v - 100 + 8;
      else if (v === 49) this.sgr.bg = 0;
      else if ((v === 38 || v === 48) && p[i + 1] === 5) { const idx = p[i + 2] & 15; if (v === 38) this.sgr.fg = idx; else this.sgr.bg = idx; i += 2; }
      else if ((v === 38 || v === 48) && p[i + 1] === 2) { i += 4; }
    }
  }
}
function tryUtf8(bytes) {
  const b0 = bytes[0]; const need = b0 >= 0xf0 ? 4 : b0 >= 0xe0 ? 3 : b0 >= 0xc0 ? 2 : 1;
  if (bytes.length < need) return null;
  try { return new TextDecoder("utf-8", { fatal: true }).decode(new Uint8Array(bytes)); } catch { return "?"; }
}
const vt = E14.vt = new Vt(cols, rows);
let ptyPending = [];
let ptyExitLine = "";
emu.add_listener("serial1-output-byte", (byte) => {
  E14.serial1Bytes++;
  const t = performance.now();
  if (E14.keyPending && E14.keyPending.route === "pty" && E14.keyPending.tEcho == null) E14.keyPending.tEcho = t;
  ptyPending.push(byte);
  // Détection du code de sortie écrit par pty-run.sh (« E14-EXIT n » en fin de ligne).
  const c = String.fromCharCode(byte);
  if (c === "\n") { const m = /E14-EXIT (\d+)/.exec(ptyExitLine); if (m) E14.events.push({ name: "exit", code: Number(m[1]), route: "pty", t }); ptyExitLine = ""; } else ptyExitLine += c;
  E14.serial1 += c; if (E14.serial1.length > 200000) E14.serial1 = E14.serial1.slice(-100000);
});
function pollPty() {
  const t = performance.now();
  if (ptyPending.length) { vt.feed(ptyPending); ptyPending = []; }
  let changedRows = 0;
  if (vt.changed) {
    vt.changed = false;
    changedRows = grid.render(vt.cells(), vt.cursorVisible ? { row: vt.y, col: vt.x } : null);
    E14.screenText = vt.screen.map((r) => r.map((c) => c.ch).join(""));
  }
  recordPoll(0, performance.now() - t, changedRows, t);
  return changedRows;
}

// ---------------------------------------------------------------- sondage, mesures
function route() { return $("route").value; }
let pollTimer = null, lastPollAt = 0;
function recordPoll(readMs, totalMs, changedRows, at) {
  const s = E14.pollStats || (E14.pollStats = { n: 0, readMs: 0, totalMs: 0, maxMs: 0, changed: 0, renders: 0 });
  s.n++; s.readMs += readMs; s.totalMs += totalMs; s.maxMs = Math.max(s.maxMs, totalMs); s.changed += changedRows; if (changedRows) s.renders++;
  if (lastPollAt && at - lastPollAt > pollMs() * 1.5) E14.pollOverruns++;
  lastPollAt = at;
  if (changedRows && E14.keyPending && E14.keyPending.tRender == null) {
    E14.keyPending.tRender = performance.now();
    finishKeySample();
  }
}
function pollMs() { return Number($("poll").value); }
function tick() {
  if (!E14.graphical || route() === "pty") (route() === "vga" ? pollVga : pollPty)();
}
function startPolling() { if (pollTimer) clearInterval(pollTimer); pollTimer = setInterval(tick, pollMs()); E14.pollStats = null; lastPollAt = 0; }
$("poll").onchange = startPolling;
$("route").onchange = () => { lastRowsText = null; vt.changed = true; startPolling(); };
$("btn_resize").onclick = () => E14.resize(Number($("cols").value), Number($("rows").value));
E14.resize = (c, r) => { cols = c; rows = r; $("cols").value = c; $("rows").value = r; grid.resize(c, r); vt.resize(c, r); lastRowsText = null; };
E14.setPoll = (ms) => { $("poll").value = String(ms); startPolling(); };
E14.setRoute = (r) => { $("route").value = r; $("route").onchange(); };
E14.resetStats = () => { E14.pollStats = null; E14.pollOverruns = 0; E14.keySamples = []; E14.rejectedKeys = []; lastPollAt = 0; };
startPolling();

// Latence « touche → écran » : à l'envoi d'une touche, un guetteur rapide (setTimeout 0, hors sondage)
// note l'instant où get_text_screen()/ttyS1 change (tChange) ; le sondage note l'instant du rendu DOM (tRender).
function screenJoined() { return (emu.screen_adapter ? emu.screen_adapter.get_text_screen() : []).join("\n"); }
function watchChange() {
  const p = E14.keyPending; if (!p) return;
  const now = performance.now();
  if (p.tChange == null) {
    if (p.route === "vga") { const s = screenJoined(); if (s !== p.screenBefore) p.tChange = now; }
    else if (p.tEcho != null) p.tChange = p.tEcho;
  }
  if (p.tChange != null && p.tRender != null) return finishKeySample();
  if (now - p.t0 > 3000) { p.timeout = true; return finishKeySample(); }
  setTimeout(watchChange, 0);
}
function finishKeySample() {
  const p = E14.keyPending; if (!p) return;
  if (p.tChange == null || p.tRender == null) { if (!p.timeout) return; }
  E14.keyPending = null;
  E14.keySamples.push({ key: p.key, route: p.route, poll: pollMs(), change_ms: p.tChange == null ? null : Math.round((p.tChange - p.t0) * 100) / 100, render_ms: p.tRender == null ? null : Math.round((p.tRender - p.t0) * 100) / 100, timeout: !!p.timeout });
  updateStatus();
}
function beginKeySample(key) {
  if (E14.keyPending) finishKeySampleNow();
  E14.keyPending = { key, route: route(), t0: performance.now(), screenBefore: route() === "vga" ? screenJoined() : null };
  setTimeout(watchChange, 0);
}
function finishKeySampleNow() { const p = E14.keyPending; if (p) { p.timeout = p.tChange == null || p.tRender == null; finishKeySample(); } }

// ---------------------------------------------------------------- clavier : jeu de touches de Kazo
// Scancodes (set 1) : lettres/chiffres/ponctuation US, Entrée, Retour, Tab, Échap, flèches (E0).
const SC = { a: 0x1e, b: 0x30, c: 0x2e, d: 0x20, e: 0x12, f: 0x21, g: 0x22, h: 0x23, i: 0x17, j: 0x24, k: 0x25, l: 0x26, m: 0x32, n: 0x31, o: 0x18, p: 0x19, q: 0x10, r: 0x13, s: 0x1f, t: 0x14, u: 0x16, v: 0x2f, w: 0x11, x: 0x2d, y: 0x15, z: 0x2c,
  "1": 0x02, "2": 0x03, "3": 0x04, "4": 0x05, "5": 0x06, "6": 0x07, "7": 0x08, "8": 0x09, "9": 0x0a, "0": 0x0b, "-": 0x0c, "=": 0x0d, "[": 0x1a, "]": 0x1b, ";": 0x27, "'": 0x28, "`": 0x29, "\\": 0x2b, ",": 0x33, ".": 0x34, "/": 0x35, " ": 0x39,
  Enter: 0x1c, Backspace: 0x0e, Tab: 0x0f, Escape: 0x01 };
const SHIFTED = { "!": "1", "@": "2", "#": "3", "$": "4", "%": "5", "^": "6", "&": "7", "*": "8", "(": "9", ")": "0", _: "-", "+": "=", "{": "[", "}": "]", ":": ";", '"': "'", "~": "`", "|": "\\", "<": ",", ">": ".", "?": "/" };
const EXT = { ArrowUp: 0x48, ArrowDown: 0x50, ArrowLeft: 0x4b, ArrowRight: 0x4d, Home: 0x47, End: 0x4f, PageUp: 0x49, PageDown: 0x51, Insert: 0x52, Delete: 0x53 };
const KAZO_EXTRA = new Set(["Home", "End", "PageUp", "PageDown", "Insert", "Delete"]);   // hors du jeu strict
const PTY_KEYS = { Enter: "\r", Backspace: "\x7f", Tab: "\t", Escape: "\x1b", ArrowUp: "\x1b[A", ArrowDown: "\x1b[B", ArrowRight: "\x1b[C", ArrowLeft: "\x1b[D", Home: "\x1b[H", End: "\x1b[F", PageUp: "\x1b[5~", PageDown: "\x1b[6~", Insert: "\x1b[2~", Delete: "\x1b[3~" };

/** Filtre « commande Terminal de Kazo » : renvoie {key, ctrl, shift} ou null si la touche est refusée. */
function kazoFilter(e) {
  const strict = $("strict").checked;
  if (e.altKey || e.metaKey) return { reject: "Alt/Meta" };
  if (/^F\d{1,2}$/.test(e.key)) return { reject: e.key };
  if (["Shift", "Control", "Alt", "Meta", "CapsLock", "NumLock", "ScrollLock", "Dead"].includes(e.key)) return null;
  if (strict && KAZO_EXTRA.has(e.key)) return { reject: e.key };
  if (e.ctrlKey) {
    if (/^[a-z]$/i.test(e.key)) {
      if (e.key.toLowerCase() === "c" && $("ctrlc").value === "kazo") return { reject: "Ctrl+C (Kazo ferme la commande)", kazoClose: true };
      return { key: e.key.toLowerCase(), ctrl: true };
    }
    return { reject: "Ctrl+" + e.key };
  }
  if (e.key.length === 1) return { key: e.key };
  if (e.key in SC || e.key in EXT) return { key: e.key };
  return { reject: e.key };
}
// Touches hors du jeu de Kazo, atteignables seulement par un « accord » (Échap puis touche) que le
// Terminal traduirait lui-même : F1-F12 et Alt+lettre (menus d'EDIT, de QBASIC…). Option « chord ».
const FKEY_SC = { F1: 0x3b, F2: 0x3c, F3: 0x3d, F4: 0x3e, F5: 0x3f, F6: 0x40, F7: 0x41, F8: 0x42, F9: 0x43, F10: 0x44, F11: 0x57, F12: 0x58 };
const FKEY_PTY = { F1: "\x1bOP", F2: "\x1bOQ", F3: "\x1bOR", F4: "\x1bOS", F5: "\x1b[15~", F6: "\x1b[17~", F7: "\x1b[18~", F8: "\x1b[19~", F9: "\x1b[20~", F10: "\x1b[21~", F11: "\x1b[23~", F12: "\x1b[24~" };
const CHORD_DIGIT = { "1": "F1", "2": "F2", "3": "F3", "4": "F4", "5": "F5", "6": "F6", "7": "F7", "8": "F8", "9": "F9", "0": "F10", "-": "F11", "=": "F12" };
function sendKeyVga(k) {
  const codes = [];
  let key = k.key, shift = false;
  if (key.length === 1) {
    if (key in SHIFTED) { key = SHIFTED[key]; shift = true; }
    else if (/^[A-Z]$/.test(key)) { key = key.toLowerCase(); shift = true; }
    if (!(key in SC)) { E14.rejectedKeys.push({ key: k.key, why: "pas de scancode (clavier US)" }); return false; }
  }
  const sc = key in FKEY_SC ? FKEY_SC[key] : SC[key], ext = EXT[key];
  if (sc === undefined && ext === undefined) { E14.rejectedKeys.push({ key: k.key, why: "touche inconnue" }); return false; }
  if (k.ctrl) codes.push(0x1d);
  if (shift) codes.push(0x2a);
  if (ext !== undefined) codes.push(0xe0, ext, 0xe0, ext | 0x80);
  else codes.push(sc, sc | 0x80);
  if (shift) codes.push(0xaa);
  if (k.ctrl) codes.push(0x9d);
  if (k.alt) {
    // Alt doit être « tenu » : EDIT (document ouvert) ignore Alt+F envoyé d'une seule rafale ; avec ~40 ms
    // entre l'appui d'Alt, la touche et le relâchement, le menu s'ouvre.
    emu.keyboard_send_scancodes([0x38]);
    setTimeout(() => emu.keyboard_send_scancodes(codes), 40);
    setTimeout(() => emu.keyboard_send_scancodes([0xb8]), 80);
    return true;
  }
  emu.keyboard_send_scancodes(codes);
  return true;
}
function sendKeyPty(k) {
  let s;
  if (k.ctrl) s = String.fromCharCode(k.key.toUpperCase().charCodeAt(0) & 0x1f);
  else if (k.alt) s = "\x1b" + k.key;
  else if (k.key in FKEY_PTY) s = FKEY_PTY[k.key];
  else if (k.key.length === 1) s = k.key;
  else s = PTY_KEYS[k.key];
  if (s == null) { E14.rejectedKeys.push({ key: k.key, why: "pas de séquence pty" }); return false; }
  vt.out(s);
  return true;
}
E14.sendKey = (k, measure = true) => {
  const label = (k.ctrl ? "Ctrl+" : "") + (k.alt ? "Alt+" : "") + k.key;
  if (measure) beginKeySample(label);
  const ok = route() === "vga" ? sendKeyVga(k) : sendKeyPty(k);
  if (!ok && measure) E14.keyPending = null;
  return ok;
};
E14.type = (text) => { for (const ch of text) E14.sendKey(ch === "\n" ? { key: "Enter" } : { key: ch }, false); };
E14.closeCommand = () => { E14.events.push({ name: "kazo-close", t: performance.now() }); setState("Ctrl+C : Kazo fermerait la commande ici"); };
// Accord : Échap puis lettre -> Alt+lettre ; Échap puis chiffre (1-9, 0, -, =) -> F1-F12 ; Échap seul après 400 ms.
let escPending = null;
E14.chord = E14.params.chord === "1";
function chordKey(k) {
  if (!E14.chord) return k;
  if (escPending) {
    clearTimeout(escPending.timer); escPending = null;
    if (/^[a-z]$/i.test(k.key) && !k.ctrl) return { key: k.key.toLowerCase(), alt: true, chord: true };
    if (k.key in CHORD_DIGIT) return { key: CHORD_DIGIT[k.key], chord: true };
    E14.sendKey({ key: "Escape" }, false);
    return k;
  }
  if (k.key === "Escape" && !k.ctrl) {
    escPending = { timer: setTimeout(() => { escPending = null; E14.sendKey({ key: "Escape" }); }, 400) };
    return null;
  }
  return k;
}
E14.sendChord = (k) => { const c = chordKey(k); if (c) E14.sendKey(c); };
$("term").addEventListener("keydown", (e) => {
  const k = kazoFilter(e);
  e.preventDefault();
  if (!k) return;
  if (k.reject) { E14.rejectedKeys.push({ key: k.reject, t: performance.now() }); if (k.kazoClose) E14.closeCommand(); updateStatus(); return; }
  E14.sendChord(k);
});
$("term").addEventListener("mousedown", () => $("term").focus());

// ---------------------------------------------------------------- utilitaires pour drive.mjs
E14.send = (text) => emu.serial0_send(text);
E14.screen = () => (E14.screenText || []).map((r) => r.replace(/\s+$/, ""));
E14.readText = async (p) => new TextDecoder().decode(await emu.read_file(p.replace(/^\/+/, "")));
E14.writeFile = (p, text) => emu.create_file(p.replace(/^\/+/, ""), new TextEncoder().encode(text));
E14.measureMemory = async () => { if (!performance.measureUserAgentSpecificMemory) return { unsupported: true }; try { return await performance.measureUserAgentSpecificMemory(); } catch (e) { return { error: String(e) }; } };
E14.saveState = async () => { const t = performance.now(); const s = await emu.save_state(); E14.state = s; return { ms: performance.now() - t, bytes: s.byteLength }; };
E14.uploadState = async (name) => {
  const CHUNK = 32 * 1048576; let r;
  for (let off = 0; off < E14.state.byteLength; off += CHUNK) r = await fetch(`/__state/${name}?offset=${off}`, { method: "PUT", body: E14.state.slice(off, Math.min(off + CHUNK, E14.state.byteLength)) });
  E14.state = null; return r.text();
};
// Latence « écriture dans l'invité → écran » : commande par ttyS0 qui écrit sur tty1 (Linux seulement) ;
// t0 = envoi série, tChange = changement de get_text_screen (guetteur rapide), tRender = rendu DOM.
E14.guestWrite = (text, cmd) => {
  const marker = text || ("W" + Math.random().toString(36).slice(2, 8));
  beginKeySample("guest-write");
  emu.serial0_send((cmd || `echo ${marker} >/dev/tty1`) + "\n");
  return marker;
};
function updateStatus() {
  const s = E14.pollStats || { n: 0, readMs: 0, totalMs: 0, maxMs: 0, changed: 0, renders: 0 };
  const ks = E14.keySamples.slice(-20);
  const avg = (a, f) => (a.length ? (a.reduce((x, y) => x + (f(y) || 0), 0) / a.length).toFixed(1) : "-");
  $("status").textContent = `sondage ${pollMs()} ms : ${s.n} tours, lecture ${(s.readMs / Math.max(1, s.n)).toFixed(2)} ms, total ${(s.totalMs / Math.max(1, s.n)).toFixed(2)} ms (max ${s.maxMs.toFixed(1)}), ${s.renders} rendus, ${E14.pollOverruns} retards` +
    `\nlatence touche→changement ${avg(ks, (k) => k.change_ms)} ms, touche→rendu ${avg(ks, (k) => k.render_ms)} ms (${ks.length} derniers) ; touches refusées : ${E14.rejectedKeys.length} ${E14.rejectedKeys.slice(-5).map((r) => r.key).join(", ")}`;
}
setInterval(updateStatus, 1000);
