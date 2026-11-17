// Os-x86 engine: the VGA text screen of v86 with its colour attributes and its cursor (milestone 0,
// work item 11; decisions 214 A and 218 A, trial E14). Loaded by v86-worker.js with importScripts.
//
// This file is the ONLY place that reads fields of v86 that are not its public API: the VGA device
// (`emulator.v86.cpu.devices.vga`) and, in it, `vga_memory`, `start_address`, `offset_register`,
// `line_compare`, `max_cols`, `max_rows`, `cursor_address`, `cursor_scanline_start`,
// `cursor_scanline_end`, `max_scan_line`, `attribute_mode`, `graphical_mode` and
// `scan_line_to_screen_row()` (v86 0.5.473, src/vga.js). Public v86 only gives the characters
// (`screen-put-char`), not their attributes nor the cursor (E14). Decision 218 A: no fork while
// these fields exist; an API for them is proposed upstream. When they are missing (another v86),
// read() falls back to the characters the worker keeps from `screen-put-char`, in light grey on
// black and without a cursor, and says so (`source: "events"`).
"use strict";

(function (root) {
  // Code page 437 (the VGA font of DOS) to Unicode; 0 is shown as a space.
  const CP437 = "\u0000☺☻♥♦♣♠•◘○◙♂♀♪♫☼►◄↕‼¶§▬↨↑↓→←∟↔▲▼ !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~⌂ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";
  const DEFAULT_ATTRIBUTE = 0x07; // light grey on black
  const CURSOR_DISABLED = 0x20; // bit 5 of the cursor start register
  const MAX_COLUMNS = 256;
  const MAX_ROWS = 128;

  function glyph(code) {
    return code === 0 ? " " : CP437[code];
  }

  /** The VGA device of a running emulator, or null when v86 does not expose the expected fields. */
  function vgaOf(emulator) {
    let vga;
    try {
      vga = emulator && emulator.v86 && emulator.v86.cpu && emulator.v86.cpu.devices && emulator.v86.cpu.devices.vga;
    } catch (error) {
      return null;
    }
    if (!vga || !(vga.vga_memory instanceof Uint8Array)) {
      return null;
    }
    for (const field of ["start_address", "offset_register", "max_cols", "max_rows", "cursor_address", "cursor_scanline_start", "cursor_scanline_end", "attribute_mode"]) {
      if (typeof vga[field] !== "number") {
        return null;
      }
    }
    return vga;
  }

  function toBase64(bytes) {
    let binary = "";
    for (let index = 0; index < bytes.length; index += 0x8000) {
      binary += String.fromCharCode.apply(null, bytes.subarray(index, index + 0x8000));
    }
    return btoa(binary);
  }

  /** The row where the split screen of line_compare starts (Infinity when there is none). */
  function splitRow(vga) {
    if (typeof vga.scan_line_to_screen_row !== "function" || typeof vga.line_compare !== "number") {
      return Infinity;
    }
    try {
      return vga.scan_line_to_screen_row(vga.line_compare);
    } catch (error) {
      return Infinity;
    }
  }

  /** The cursor as vga.js update_cursor() and update_cursor_scanline() place it. */
  function cursorOf(vga, cols, rows, split) {
    const stride = Math.max(cols, 2 * vga.offset_register);
    let row;
    let col;
    if (vga.cursor_address >= vga.start_address) {
      row = Math.floor((vga.cursor_address - vga.start_address) / stride);
      col = (vga.cursor_address - vga.start_address) % stride;
    } else {
      row = Math.floor(vga.cursor_address / stride) + split;
      col = vga.cursor_address % stride;
    }
    const maxScan = typeof vga.max_scan_line === "number" ? vga.max_scan_line & 31 : 31;
    const start = Math.min(maxScan, vga.cursor_scanline_start & 31);
    const end = Math.min(maxScan, vga.cursor_scanline_end & 31);
    const visible = (vga.cursor_scanline_start & CURSOR_DISABLED) === 0 && start < end && row >= 0 && row < rows && col >= 0 && col < cols;
    return { row: Math.max(0, Math.min(rows - 1, row)), col: Math.max(0, Math.min(cols - 1, col)), visible };
  }

  /** Reads the text screen from the VGA memory, the way vga.js text_mode_redraw() walks it. */
  function readVga(vga) {
    const cols = Math.min(MAX_COLUMNS, Math.max(1, vga.max_cols | 0));
    const rows = Math.min(MAX_ROWS, Math.max(1, vga.max_rows | 0));
    const memory = vga.vga_memory;
    const split = splitRow(vga);
    const skip = Math.max(0, 2 * (2 * vga.offset_register - cols));
    const attributes = new Uint8Array(cols * rows);
    const lines = [];
    let address = vga.start_address << 1;
    for (let row = 0; row < rows; row++) {
      if (row === split) {
        address = 0;
      }
      let line = "";
      for (let col = 0; col < cols; col++) {
        const inside = address + 1 < memory.length;
        line += glyph(inside ? memory[address] : 0);
        attributes[row * cols + col] = inside ? memory[address | 1] : DEFAULT_ATTRIBUTE;
        address += 2;
      }
      address += skip;
      lines.push(line);
    }
    return {
      cols,
      rows,
      lines,
      attributes: toBase64(attributes),
      cursor: cursorOf(vga, cols, rows, split),
      blink: (vga.attribute_mode & 8) !== 0,
      graphical: vga.graphical_mode === true,
      source: "vga",
    };
  }

  /** Reads the characters kept from screen-put-char ({cols, rows, cells: Uint16Array}). */
  function readEvents(textScreen) {
    const lines = [];
    for (let row = 0; row < textScreen.rows; row++) {
      let line = "";
      for (let col = 0; col < textScreen.cols; col++) {
        line += glyph(textScreen.cells[row * textScreen.cols + col] & 0xff);
      }
      lines.push(line);
    }
    return {
      cols: textScreen.cols,
      rows: textScreen.rows,
      lines,
      attributes: toBase64(new Uint8Array(textScreen.cols * textScreen.rows).fill(DEFAULT_ATTRIBUTE)),
      cursor: null,
      blink: false,
      graphical: false,
      source: "events",
    };
  }

  /**
   * The text screen of the guest: from the VGA device when the emulator exposes it, otherwise from
   * the characters of screen-put-char. Never throws: a read that fails falls back to the events.
   */
  function read(emulator, textScreen) {
    const vga = vgaOf(emulator);
    if (vga) {
      try {
        return readVga(vga);
      } catch (error) {
        // An internal field changed meaning: the public characters still work.
      }
    }
    return readEvents(textScreen);
  }

  root.OsX86VgaText = { CP437, glyph, read, vgaOf, MAX_COLUMNS, MAX_ROWS };
})(typeof self !== "undefined" ? self : globalThis);
