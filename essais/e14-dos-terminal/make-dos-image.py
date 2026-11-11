#!/usr/bin/env python3
"""Essai E14 — fabrique images/dos.img à partir de FD14LITE.img (FreeDOS 1.4 LiteUSB, 32 Mio, FAT16) :
- FDAUTO.BAT remplacé (pas d'installateur : invite directe, PATH, option FDAPM) ; SETUP.BAT retiré ;
- C:\\E14\\ : CHOICE.EXE (du paquet BASE), DEBUG, FIND ; trois .COM assemblés à la main ici :
    KEYTEST.COM  affiche scan/ascii (hex) de chaque touche (int 16h), Échap quitte avec le code 7 ;
    GUESS.COM    jeu texte : deviner un chiffre (int 21h), + / - / OK, Échap quitte avec 1 ;
    GFX.COM      passe en mode graphique 13h, attend une touche, revient en mode 3 (signal pour 164).
Écriture par mtools dans le conteneur e14-mtools (docker). Usage : python -I make-dos-image.py
"""
import os, struct, subprocess, sys, zipfile, shutil, pathlib

ROOT = pathlib.Path(__file__).resolve().parent
DL = ROOT / "images" / "dl"
SRC = DL / "FD14-LiteUSB" / "FD14LITE.img"
OUT = ROOT / "images" / "dos.img"
STAGE = ROOT / "images" / "dos-stage"
PART_OFFSET = 63 * 512


def asm(code):
    """Mini assembleur : liste de (label | bytes | ('call'|'jmp'|'jcc', opcode, label)). Deux passes."""
    for _ in range(2):
        labels, out = {}, bytearray()
        pos = 0x100
        for item in code:
            if isinstance(item, str):
                labels[item] = pos
            elif isinstance(item, bytes):
                out += item; pos += len(item)
            else:
                kind, op, target = item
                size = 3 if kind == "call" else 2
                tgt = labels_prev.get(target, pos + size) if _ else pos + size
                rel = tgt - (pos + size)
                if kind == "call":
                    out += bytes([op]) + struct.pack("<h", rel)
                else:
                    assert -128 <= rel < 128, (target, rel)
                    out += bytes([op, rel & 0xFF])
                pos += size
        labels_prev = labels
    return bytes(out)


def msg(s):
    return s.encode("cp437") + b"$"


# print_hex_byte (AL) : deux quartets par int 21h/AH=2.
PHB = [
    "phb", b"\x50",                 # push ax
    b"\xC0\xE8\x04",               # shr al,4
    ("call", 0xE8, "nib"),
    b"\x58",                       # pop ax
    b"\x24\x0F",                   # and al,0Fh
    "nib", b"\x04\x30",            # add al,'0'
    b"\x3C\x39",                   # cmp al,'9'
    ("jcc", 0x76, "nibok"),        # jbe
    b"\x04\x07",                   # add al,7
    "nibok", b"\x8A\xD0",          # mov dl,al
    b"\xB4\x02\xCD\x21",           # mov ah,2 ; int 21h
    b"\xC3",                       # ret
]

def keytest():
    code = [
        "main", b"\xB4\x00\xCD\x16",      # mov ah,0 ; int 16h  -> AH scan, AL ascii
        b"\x50\x50",                      # push ax ; push ax
        b"\x8A\xC4", ("call", 0xE8, "phb"),   # mov al,ah ; call phb
        b"\xB2\x2F\xB4\x02\xCD\x21",      # '/'
        b"\x58", ("call", 0xE8, "phb"),   # pop ax ; call phb (AL = ascii)
        b"\xB2\x20\xB4\x02\xCD\x21",      # ' '
        b"\x58",                          # pop ax
        b"\x3C\x1B",                      # cmp al,1Bh
        ("jcc", 0x75, "main"),            # jne main
        b"\xB8\x07\x4C\xCD\x21",          # mov ax,4C07h ; int 21h
    ] + PHB
    return asm(code)


def guess():
    # secret = (ticks & 0xFF) % 10 ; boucle : lit un chiffre (int 21h AH=1 avec écho), compare.
    code = [
        b"\xB4\x00\xCD\x1A",              # mov ah,0 ; int 1Ah -> DX ticks
        b"\x8A\xC2\x32\xE4",              # mov al,dl ; xor ah,ah
        b"\xB3\x0A\xF6\xF3",              # mov bl,10 ; div bl -> AH reste
        b"\x8A\xDC",                      # mov bl,ah  (secret dans BL)
        "loop", b"\xBA", ("abs16", "mprompt"), b"\xB4\x09\xCD\x21",   # mov dx,prompt ; int 21h/9
        b"\xB4\x01\xCD\x21",              # mov ah,1 ; int 21h -> AL
        b"\x3C\x1B", ("jcc", 0x74, "quit"),   # cmp al,1Bh ; je quit
        b"\x2C\x30",                      # sub al,'0'
        b"\x3A\xC3",                      # cmp al,bl
        ("jcc", 0x74, "win"),             # je win
        ("jcc", 0x72, "lower"),           # jb lower
        b"\xBA", ("abs16", "mminus"), ("jmp", 0xEB, "say"),
        "lower", b"\xBA", ("abs16", "mplus"),
        "say", b"\xB4\x09\xCD\x21", ("jmp", 0xEB, "loop"),
        "win", b"\xBA", ("abs16", "mok"), b"\xB4\x09\xCD\x21", b"\xB8\x00\x4C\xCD\x21",
        "quit", b"\xB8\x01\x4C\xCD\x21",
        "mprompt", msg("\r\nChiffre (0-9, Echap quitte) ? "),
        "mminus", msg(" plus petit"),
        "mplus", msg(" plus grand"),
        "mok", msg(" OK, gagne !\r\n"),
    ]
    return asm_abs(code)


def gfx():
    code = [
        b"\xB8\x13\x00\xCD\x10",          # mov ax,13h ; int 10h (320x200x256)
        b"\xB4\x00\xCD\x16",              # mov ah,0 ; int 16h
        b"\xB8\x03\x00\xCD\x10",          # mov ax,3 ; int 10h (texte 80x25)
        b"\xB8\x00\x4C\xCD\x21",
    ]
    return asm(code)


def asm_abs(code):
    """Comme asm, mais gère ('abs16', label) = adresse absolue 16 bits (org 100h)."""
    labels = {}
    for _ in range(2):
        out = bytearray(); pos = 0x100; new = {}
        for item in code:
            if isinstance(item, str):
                new[item] = pos
            elif isinstance(item, bytes):
                out += item; pos += len(item)
            elif item[0] == "abs16":
                out += struct.pack("<H", labels.get(item[1], 0)); pos += 2
            else:
                kind, op, target = item
                size = 3 if kind == "call" else 2
                tgt = labels.get(target, pos + size)
                rel = tgt - (pos + size)
                if kind == "call":
                    out += bytes([op]) + struct.pack("<h", rel)
                else:
                    assert -128 <= rel < 128, (target, rel)
                    out += bytes([op, rel & 0xFF])
                pos += size
        labels = new
    return bytes(out)


FDAUTO = """@echo off
SET DOSDIR=\\FREEDOS
SET PATH=\\FREEDOS\\BIN;\\E14
SET DIRCMD=/OGN /Y
SET OS_NAME=FreeDOS
SET OS_VERSION=1.4
if not exist \\E14\\NOAPM.FLG FDAPM APMDOS
prompt $p$g
echo E14 READY
"""


def main():
    STAGE.mkdir(parents=True, exist_ok=True)
    (STAGE / "KEYTEST.COM").write_bytes(keytest())
    (STAGE / "GUESS.COM").write_bytes(guess())
    (STAGE / "GFX.COM").write_bytes(gfx())
    (STAGE / "FDAUTO.BAT").write_bytes(FDAUTO.replace("\n", "\r\n").encode("ascii"))
    # Fichier texte pour EDIT et un gros fichier pour DIR/TYPE.
    (STAGE / "LISEZMOI.TXT").write_bytes(("\r\n".join(f"Ligne {i:3d} : essai E14, terminal de Kazo, FreeDOS 1.4" for i in range(1, 121)) + "\r\n").encode("cp437"))
    # CHOICE, DEBUG, FIND depuis les paquets BASE de l'image source (zip FreeDOS : BIN/xxx.EXE).
    img = SRC.read_bytes()
    for pkg in ("CHOICE", "DEBUG", "FIND"):
        data = extract_from_image(img, f"PACKAGES/BASE/{pkg}.ZIP")
        tmp = STAGE / f"{pkg}.ZIP"; tmp.write_bytes(data)
        with zipfile.ZipFile(tmp) as z:
            for n in z.namelist():
                if n.upper().startswith("BIN/") and n.upper().endswith((".EXE", ".COM")):
                    (STAGE / os.path.basename(n).upper()).write_bytes(z.read(n))
        tmp.unlink()
    shutil.copyfile(SRC, OUT)
    print("stage:", sorted(p.name for p in STAGE.iterdir()))
    # mtools dans Docker : l'image dos.img est montée depuis images/ ; partition à l'offset 63*512.
    work = str(ROOT / "images").replace("\\", "/")
    cmds = ["set -e",
            "cd /work",
            "export MTOOLS_SKIP_CHECK=1",
            "mdel -i dos.img@@32256 ::SETUP.BAT || true",
            "mcopy -o -i dos.img@@32256 dos-stage/FDAUTO.BAT ::FDAUTO.BAT",
            "mmd -i dos.img@@32256 ::E14 || true",
            "for f in dos-stage/*; do case $f in *FDAUTO.BAT) ;; *) mcopy -o -i dos.img@@32256 $f ::E14/ ;; esac; done",
            "mdir -i dos.img@@32256 ::E14"]
    r = subprocess.run(["docker", "run", "--rm", "-v", f"{work}:/work", "e14-mtools", "sh", "-c", "\n".join(cmds)], capture_output=True, text=True)
    print(r.stdout); print(r.stderr, file=sys.stderr)
    if r.returncode: sys.exit(r.returncode)
    print("dos.img :", OUT.stat().st_size, "octets")


def extract_from_image(img, path):
    """Lit un fichier de la partition FAT16 de l'image source (chemin en majuscules, séparateur /)."""
    base = PART_OFFSET
    b = img[base:base + 512]
    bps, spc, rsv, nf, rootent, _, media, spf = struct.unpack("<HBHBHHBH", b[11:24])
    fat = base + rsv * bps; root = fat + nf * spf * bps; data = root + rootent * 32

    def entries(off, n):
        for i in range(n):
            e = img[off + 32 * i: off + 32 * i + 32]
            if e[0] == 0: return
            if e[0] == 0xE5 or e[11] & 0x08 or e[11] == 0x0F: continue
            name = e[:8].decode("latin1").strip()
            ext = e[8:11].decode("latin1").strip()
            yield (name + ("." + ext if ext else "")), e[11] & 0x10, struct.unpack("<H", e[26:28])[0], struct.unpack("<I", e[28:32])[0]

    def chain(clus, size):
        out = b""
        while clus < 0xFFF8 and len(out) < size:
            out += img[data + (clus - 2) * spc * bps: data + (clus - 1) * spc * bps]
            clus = struct.unpack("<H", img[fat + clus * 2: fat + clus * 2 + 2])[0]
        return out[:size]

    off, n = root, rootent
    parts = path.split("/")
    for i, p in enumerate(parts):
        for name, isdir, clus, size in entries(off, n):
            if name == p:
                if i == len(parts) - 1:
                    return chain(clus, size)
                off, n = data + (clus - 2) * spc * bps, 1 << 16
                break
        else:
            raise FileNotFoundError(path)


if __name__ == "__main__":
    main()
