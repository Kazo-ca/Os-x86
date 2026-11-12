#!/usr/bin/env python3
"""Packs a guest image of Os-x86 into the published form (milestone 0, work item 4).

A guest image is a few large files produced by a recipe (a kernel, an initramfs, a root disk, or
a DOS disk). This tool serialises them into one blob, cuts the blob into chunks of 32 MiB named
chunk-00000.bin, chunk-00001.bin, ... (the last one shorter) and writes manifest.json, the file
the storage of Os-x86 (work item 3) reads to download, verify and keep the image in the OPFS,
plus manifest.sha256 (the SHA-256 of manifest.json, published next to it).

Each entry of the blob is a zstd *seekable* stream (the format of contrib/seekable_format of
zstd): independent frames of a fixed decompressed size (--block-size, 1 MiB by default) followed
by a seek table in a skippable frame. A plain `zstd -d` decompresses the whole entry; the engine
decompresses only the frames it needs, which is what a root disk read by sectors from the OPFS
requires (docs/fr/images.md explains the choice: raw ext4 disk rather than a 9p tree).

manifest.json (format 1; unknown keys are ignored by readers):

    {
      "format": 1,
      "image": "<identifier>",
      "version": "<semver>",
      "engine": {"v86": "0.5.473"},
      "size": <bytes of the whole blob>,
      "chunkSize": 33554432,
      "chunks": [{"sha256": "<hex, lowercase>"}, ...],
      "files": {
        "<name>": {"offset": <bytes>, "size": <bytes>, "rawSize": <bytes>, "blockSize": <bytes>,
                   "sha256": "<hex of the decompressed content>"},
        ...
      },
      "boot": {...},   // optional: what the engine passes to v86 (cmdline, readyMarker, prompt...)
      "build": {...}   // optional: provenance (sources, licenses, sizes)
    }

Chunk i is the bytes [i * chunkSize, min((i + 1) * chunkSize, size)) of the blob. Entry names:
`kernel`, `initramfs`, `rootfs` for a Linux image, `disk` for a DOS disk.

Usage (standard library only; Python 3.14 for compression.zstd, or the `zstandard` package):

    python -I images/tools/pack-image.py pack --image alpine-test --version 0.1.0 \\
        --entry kernel=build/vmlinuz-virt --entry initramfs=build/initramfs-osx86 \\
        --boot-json build/boot.json --output build/pack
    python -I images/tools/pack-image.py check build/pack        # recompute and compare everything
    python -I images/tools/pack-image.py extract build/pack --to build/unpacked   # decompress the entries
    python -I images/tools/pack-image.py info build/pack
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
import sys
from pathlib import Path

MANIFEST_FORMAT = 1
V86_VERSION = "0.5.473"  # tools/v86/vendor-v86.py: the engine this image is built for
DEFAULT_CHUNK_SIZE = 32 * 1024 * 1024
DEFAULT_BLOCK_SIZE = 1024 * 1024
DEFAULT_LEVEL = 19
MAX_MANIFEST_BYTES = 4 * 1024 * 1024
MAX_ENTRY_BYTES = 8 * 1024 * 1024 * 1024
MANIFEST_NAME = "manifest.json"
MANIFEST_SHA256_NAME = "manifest.sha256"
CHUNK_NAME = "chunk-{:05d}.bin"
CHUNK_NAME_PATTERN = re.compile(r"^chunk-(\d{5})\.bin$")
ENTRY_NAME_PATTERN = re.compile(r"^[a-z][a-z0-9-]{0,31}$")
IMAGE_ID_PATTERN = re.compile(r"^[a-z][a-z0-9-]{0,63}$")
SEMVER_PATTERN = re.compile(r"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")
HEX_SHA256 = re.compile(r"[0-9a-f]{64}")

# zstd seekable format (contrib/seekable_format/zstd_seekable_compression_format.md)
SKIPPABLE_MAGIC = 0x184D2A5E
SEEKABLE_MAGIC = 0x8F92EAB1
SEEK_TABLE_FOOTER = 9  # Number_Of_Frames u32, Seek_Table_Descriptor u8, Seekable_Magic_Number u32
MAX_FRAMES = 1 << 24

# --- zstd (Python 3.14 standard library, else the zstandard package) --------------------------


class Zstd:
    def __init__(self, level: int) -> None:
        self.level = level
        try:
            from compression import zstd  # Python 3.14+

            self._compress = lambda data: zstd.compress(data, level=level)
            self._decompress = zstd.decompress
            self.backend = "compression.zstd"
        except ImportError:
            try:
                import zstandard
            except ImportError:
                raise SystemExit("zstd is needed: Python 3.14 (compression.zstd) or the zstandard package") from None
            compressor = zstandard.ZstdCompressor(level=level)
            decompressor = zstandard.ZstdDecompressor()
            self._compress = compressor.compress
            self._decompress = decompressor.decompress
            self.backend = "zstandard"

    def compress(self, data: bytes) -> bytes:
        return self._compress(data)

    def decompress(self, frame: bytes) -> bytes:
        return self._decompress(frame)


# --- the blob, cut into chunks ---------------------------------------------------------------


class ChunkWriter:
    """Receives the blob as a stream and writes chunk-NNNNN.bin files, hashing each one."""

    def __init__(self, output: Path, chunk_size: int) -> None:
        self.output = output
        self.chunk_size = chunk_size
        self.position = 0
        self.hashes: list[str] = []
        self._stream = None
        self._digest = None
        self._in_chunk = 0

    def write(self, data: bytes) -> None:
        view = memoryview(data)
        while len(view) > 0:
            if self._stream is None:
                self._stream = (self.output / CHUNK_NAME.format(len(self.hashes))).open("wb")
                self._digest = hashlib.sha256()
                self._in_chunk = 0
            room = self.chunk_size - self._in_chunk
            piece = view[:room]
            self._stream.write(piece)
            self._digest.update(piece)
            self._in_chunk += len(piece)
            self.position += len(piece)
            view = view[len(piece):]
            if self._in_chunk == self.chunk_size:
                self._close_chunk()

    def _close_chunk(self) -> None:
        assert self._stream is not None and self._digest is not None
        self._stream.close()
        self.hashes.append(self._digest.hexdigest())
        self._stream = None
        self._digest = None

    def finish(self) -> None:
        if self._stream is not None:
            self._close_chunk()


def sha256_file(path: Path, chunk: int = 1024 * 1024) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for piece in iter(lambda: stream.read(chunk), b""):
            digest.update(piece)
    return digest.hexdigest()


def seek_table(frames: list[tuple[int, int]]) -> bytes:
    """The skippable frame that ends a seekable stream: one (compressed, decompressed) u32 pair
    per frame, no checksums, then the 9-byte footer."""
    body = b"".join(struct.pack("<II", compressed, decompressed) for compressed, decompressed in frames)
    footer = struct.pack("<IBI", len(frames), 0, SEEKABLE_MAGIC)
    return struct.pack("<II", SKIPPABLE_MAGIC, len(body) + SEEK_TABLE_FOOTER) + body + footer


def pack_entry(source: Path, writer: ChunkWriter, zstd: Zstd, block_size: int) -> dict:
    """Compresses source as a seekable stream straight into the chunk writer."""
    raw_size = source.stat().st_size
    if raw_size > MAX_ENTRY_BYTES:
        raise SystemExit(f"{source}: {raw_size} bytes is more than this tool packs in one entry")
    offset = writer.position
    digest = hashlib.sha256()
    frames: list[tuple[int, int]] = []
    with source.open("rb") as stream:
        for block in iter(lambda: stream.read(block_size), b""):
            digest.update(block)
            frame = zstd.compress(block)
            frames.append((len(frame), len(block)))
            writer.write(frame)
    if len(frames) > MAX_FRAMES:
        raise SystemExit(f"{source}: more than {MAX_FRAMES} frames; use a larger --block-size")
    writer.write(seek_table(frames))
    return {
        "offset": offset,
        "size": writer.position - offset,
        "rawSize": raw_size,
        "blockSize": block_size,
        "sha256": digest.hexdigest(),
    }


# --- reading back (check, extract) -----------------------------------------------------------


class BlobReader:
    """Random access into the blob made of the chunk files of a pack directory."""

    def __init__(self, directory: Path, manifest: dict) -> None:
        self.directory = directory
        self.chunk_size = manifest["chunkSize"]
        self.size = manifest["size"]

    def read(self, offset: int, length: int) -> bytes:
        if offset < 0 or length < 0 or offset + length > self.size:
            raise SystemExit(f"read of {length} bytes at {offset} is outside the blob of {self.size} bytes")
        pieces = []
        while length > 0:
            index, inside = divmod(offset, self.chunk_size)
            take = min(length, self.chunk_size - inside)
            with (self.directory / CHUNK_NAME.format(index)).open("rb") as stream:
                stream.seek(inside)
                piece = stream.read(take)
            if len(piece) != take:
                raise SystemExit(f"{CHUNK_NAME.format(index)} is shorter than the manifest says")
            pieces.append(piece)
            offset += take
            length -= take
        return b"".join(pieces)


def read_seek_table(blob: BlobReader, entry: dict) -> list[tuple[int, int]]:
    end = entry["offset"] + entry["size"]
    if entry["size"] < 8 + SEEK_TABLE_FOOTER:
        raise SystemExit("entry too short to hold a seek table")
    count, descriptor, magic = struct.unpack("<IBI", blob.read(end - SEEK_TABLE_FOOTER, SEEK_TABLE_FOOTER))
    if magic != SEEKABLE_MAGIC:
        raise SystemExit("entry does not end with the zstd seekable magic number")
    if descriptor & 0x80:
        raise SystemExit("seek tables with checksums are not produced by this tool")
    if count > MAX_FRAMES:
        raise SystemExit(f"seek table claims {count} frames")
    table_size = 8 * count + SEEK_TABLE_FOOTER
    table_start = end - table_size - 8
    if table_start < entry["offset"]:
        raise SystemExit("seek table larger than the entry")
    skippable_magic, frame_size = struct.unpack("<II", blob.read(table_start, 8))
    if skippable_magic != SKIPPABLE_MAGIC or frame_size != table_size:
        raise SystemExit("malformed seek table frame")
    body = blob.read(table_start + 8, 8 * count)
    return [struct.unpack_from("<II", body, 8 * index) for index in range(count)]


def iterate_entry(blob: BlobReader, entry: dict, zstd: Zstd):
    """Yields the decompressed blocks of an entry, checking every frame against the seek table."""
    frames = read_seek_table(blob, entry)
    position = entry["offset"]
    total = 0
    for index, (compressed, decompressed) in enumerate(frames):
        if decompressed > entry["blockSize"] or (index < len(frames) - 1 and decompressed != entry["blockSize"]):
            raise SystemExit(f"frame {index}: decompressed size {decompressed} does not match blockSize {entry['blockSize']}")
        block = zstd.decompress(blob.read(position, compressed))
        if len(block) != decompressed:
            raise SystemExit(f"frame {index}: decompressed to {len(block)} bytes, the seek table says {decompressed}")
        position += compressed
        total += len(block)
        yield block
    if total != entry["rawSize"]:
        raise SystemExit(f"entry decompresses to {total} bytes, the manifest says {entry['rawSize']}")


def load_manifest(directory: Path) -> dict:
    path = directory / MANIFEST_NAME
    if not path.is_file():
        raise SystemExit(f"{path} not found")
    if path.stat().st_size > MAX_MANIFEST_BYTES:
        raise SystemExit(f"{path} is larger than {MAX_MANIFEST_BYTES} bytes")
    try:
        manifest = json.loads(path.read_text(encoding="utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise SystemExit(f"{path}: not valid JSON ({error})") from None
    validate_manifest(manifest)
    return manifest


def validate_manifest(manifest: dict) -> None:
    def expect(condition: bool, message: str) -> None:
        if not condition:
            raise SystemExit(f"manifest: {message}")

    expect(isinstance(manifest, dict), "not an object")
    expect(manifest.get("format") == MANIFEST_FORMAT, f"format is not {MANIFEST_FORMAT}")
    expect(isinstance(manifest.get("image"), str) and IMAGE_ID_PATTERN.match(manifest["image"]) is not None, "bad image identifier")
    expect(isinstance(manifest.get("version"), str) and SEMVER_PATTERN.match(manifest["version"]) is not None, "version is not semver")
    expect(isinstance(manifest.get("engine"), dict) and isinstance(manifest["engine"].get("v86"), str), "engine.v86 missing")
    size, chunk_size, chunks = manifest.get("size"), manifest.get("chunkSize"), manifest.get("chunks")
    expect(isinstance(size, int) and size >= 0, "size is not a non-negative integer")
    expect(isinstance(chunk_size, int) and chunk_size > 0, "chunkSize is not a positive integer")
    expect(isinstance(chunks, list), "chunks is not a list")
    expected_count = (size + chunk_size - 1) // chunk_size
    expect(len(chunks) == expected_count, f"{len(chunks)} chunks for {size} bytes in chunks of {chunk_size}; expected {expected_count}")
    for index, chunk in enumerate(chunks):
        expect(isinstance(chunk, dict) and isinstance(chunk.get("sha256"), str) and HEX_SHA256.fullmatch(chunk["sha256"]) is not None,
               f"chunk {index} has no lowercase hex sha256")
    files = manifest.get("files")
    expect(isinstance(files, dict) and len(files) > 0, "files is not a non-empty object")
    previous_end = 0
    for name, entry in files.items():
        expect(ENTRY_NAME_PATTERN.match(name) is not None, f"bad entry name {name!r}")
        expect(isinstance(entry, dict), f"files.{name} is not an object")
        for key in ("offset", "size", "rawSize", "blockSize"):
            expect(isinstance(entry.get(key), int) and entry[key] >= 0, f"files.{name}.{key} is not a non-negative integer")
        expect(entry["blockSize"] > 0, f"files.{name}.blockSize is zero")
        expect(isinstance(entry.get("sha256"), str) and HEX_SHA256.fullmatch(entry["sha256"]) is not None, f"files.{name}.sha256 missing")
        expect(entry["offset"] == previous_end, f"files.{name} does not start where the previous entry ends")
        previous_end = entry["offset"] + entry["size"]
    expect(previous_end == size, "the entries do not cover the blob exactly")


def chunk_files(directory: Path) -> list[Path]:
    return sorted(path for path in directory.iterdir() if CHUNK_NAME_PATTERN.match(path.name))


# --- commands ----------------------------------------------------------------------------------


def parse_entries(specs: list[str]) -> list[tuple[str, Path]]:
    entries: list[tuple[str, Path]] = []
    for spec in specs:
        name, separator, path = spec.partition("=")
        if not separator or ENTRY_NAME_PATTERN.match(name) is None:
            raise SystemExit(f"--entry expects name=path with a lowercase name; got {spec!r}")
        if name in dict(entries):
            raise SystemExit(f"entry {name} given twice")
        source = Path(path)
        if not source.is_file():
            raise SystemExit(f"{source}: not a file")
        entries.append((name, source))
    if not entries:
        raise SystemExit("at least one --entry is required")
    return entries


def load_json_option(path: Path | None, what: str) -> dict | None:
    if path is None:
        return None
    if path.stat().st_size > MAX_MANIFEST_BYTES:
        raise SystemExit(f"{path} is larger than {MAX_MANIFEST_BYTES} bytes")
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise SystemExit(f"{path}: {what} must be a JSON object")
    return value


def write_manifest(directory: Path, manifest: dict) -> str:
    text = json.dumps(manifest, indent=2) + "\n"
    (directory / MANIFEST_NAME).write_text(text, encoding="utf-8", newline="\n")
    digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
    (directory / MANIFEST_SHA256_NAME).write_text(f"{digest}  {MANIFEST_NAME}\n", encoding="utf-8", newline="\n")
    return digest


def command_pack(args: argparse.Namespace) -> int:
    if IMAGE_ID_PATTERN.match(args.image) is None:
        raise SystemExit(f"bad image identifier {args.image!r} (lowercase letters, digits, dashes)")
    if SEMVER_PATTERN.match(args.version) is None:
        raise SystemExit(f"version {args.version!r} is not semver")
    if args.chunk_size <= 0 or args.block_size <= 0 or args.block_size > 1 << 30:
        raise SystemExit("chunk and block sizes must be positive (block size at most 1 GiB)")
    entries = parse_entries(args.entry)
    boot = load_json_option(args.boot_json, "boot")
    build = load_json_option(args.build_json, "build")
    output: Path = args.output
    output.mkdir(parents=True, exist_ok=True)
    for stale in chunk_files(output) + [output / MANIFEST_NAME, output / MANIFEST_SHA256_NAME]:
        if stale.exists():
            stale.unlink()

    zstd = Zstd(args.level)
    writer = ChunkWriter(output, args.chunk_size)
    files: dict[str, dict] = {}
    for name, source in entries:
        files[name] = pack_entry(source, writer, zstd, args.block_size)
        entry = files[name]
        print(f"  {name}: {entry['rawSize']} -> {entry['size']} bytes ({100 * entry['size'] / max(1, entry['rawSize']):.1f} %)")
    writer.finish()

    manifest = {
        "format": MANIFEST_FORMAT,
        "image": args.image,
        "version": args.version,
        "engine": {"v86": V86_VERSION},
        "size": writer.position,
        "chunkSize": args.chunk_size,
        "chunks": [{"sha256": digest} for digest in writer.hashes],
        "files": files,
    }
    if boot is not None:
        manifest["boot"] = boot
    if build is not None:
        manifest["build"] = build
    validate_manifest(manifest)
    digest = write_manifest(output, manifest)
    print(f"Packed {args.image} {args.version}: {writer.position} bytes in {len(writer.hashes)} chunks, "
          f"manifest {digest[:12]}... ({zstd.backend}, level {args.level}, blocks of {args.block_size})")
    return 0


def command_check(args: argparse.Namespace) -> int:
    directory: Path = args.directory
    manifest = load_manifest(directory)
    text = (directory / MANIFEST_NAME).read_text(encoding="utf-8")
    sha_file = directory / MANIFEST_SHA256_NAME
    expected = sha_file.read_text(encoding="utf-8").split()[0] if sha_file.is_file() else None
    actual = hashlib.sha256(text.encode("utf-8")).hexdigest()
    if expected != actual:
        raise SystemExit(f"{MANIFEST_SHA256_NAME}: {expected} does not match the manifest ({actual})")

    present = chunk_files(directory)
    if [path.name for path in present] != [CHUNK_NAME.format(index) for index in range(len(manifest["chunks"]))]:
        raise SystemExit(f"chunk files on disk ({len(present)}) do not match the manifest ({len(manifest['chunks'])})")
    for index, (path, chunk) in enumerate(zip(present, manifest["chunks"])):
        expected_size = min(manifest["chunkSize"], manifest["size"] - index * manifest["chunkSize"])
        if path.stat().st_size != expected_size:
            raise SystemExit(f"{path.name}: {path.stat().st_size} bytes, expected {expected_size}")
        if sha256_file(path) != chunk["sha256"]:
            raise SystemExit(f"{path.name}: SHA-256 differs from the manifest")

    zstd = Zstd(DEFAULT_LEVEL)
    blob = BlobReader(directory, manifest)
    for name, entry in manifest["files"].items():
        digest = hashlib.sha256()
        for block in iterate_entry(blob, entry, zstd):
            digest.update(block)
        if digest.hexdigest() != entry["sha256"]:
            raise SystemExit(f"files.{name}: decompressed content differs from its sha256")
    print(f"OK {manifest['image']} {manifest['version']}: {len(present)} chunks, {len(manifest['files'])} entries, manifest {actual[:12]}...")
    return 0


def command_extract(args: argparse.Namespace) -> int:
    directory: Path = args.directory
    manifest = load_manifest(directory)
    wanted = args.entry or list(manifest["files"])
    for name in wanted:
        if name not in manifest["files"]:
            raise SystemExit(f"no entry named {name} (have {', '.join(manifest['files'])})")
    args.to.mkdir(parents=True, exist_ok=True)
    zstd = Zstd(DEFAULT_LEVEL)
    blob = BlobReader(directory, manifest)
    for name in wanted:
        entry = manifest["files"][name]
        digest = hashlib.sha256()
        target = args.to / name
        with target.open("wb") as out:
            for block in iterate_entry(blob, entry, zstd):
                digest.update(block)
                out.write(block)
        if digest.hexdigest() != entry["sha256"]:
            target.unlink()
            raise SystemExit(f"files.{name}: decompressed content differs from its sha256")
        print(f"  {name}: {entry['rawSize']} bytes -> {target}")
    return 0


def command_info(args: argparse.Namespace) -> int:
    manifest = load_manifest(args.directory)
    print(f"{manifest['image']} {manifest['version']} for v86 {manifest['engine']['v86']}: "
          f"{manifest['size']} bytes, {len(manifest['chunks'])} chunks of {manifest['chunkSize']}")
    for name, entry in manifest["files"].items():
        print(f"  {name}: offset {entry['offset']}, {entry['size']} bytes packed, {entry['rawSize']} raw, blocks of {entry['blockSize']}")
    if "boot" in manifest:
        print(f"  boot: {json.dumps(manifest['boot'])}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", metavar="DIR", type=Path, help="same as the check command")
    commands = parser.add_subparsers(dest="command")

    pack = commands.add_parser("pack", help="pack entries into chunks and a manifest")
    pack.add_argument("--image", required=True, help="image identifier (lowercase, digits, dashes)")
    pack.add_argument("--version", required=True, help="image version (semver)")
    pack.add_argument("--entry", action="append", default=[], metavar="NAME=PATH", help="an entry (kernel, initramfs, rootfs, disk, ...)")
    pack.add_argument("--output", required=True, type=Path, help="pack directory (chunks and manifest)")
    pack.add_argument("--boot-json", type=Path, help="JSON object copied to manifest.boot")
    pack.add_argument("--build-json", type=Path, help="JSON object copied to manifest.build")
    pack.add_argument("--chunk-size", type=int, default=DEFAULT_CHUNK_SIZE)
    pack.add_argument("--block-size", type=int, default=DEFAULT_BLOCK_SIZE, help="decompressed bytes per zstd frame")
    pack.add_argument("--level", type=int, default=DEFAULT_LEVEL, help="zstd level")
    pack.set_defaults(run=command_pack)

    check = commands.add_parser("check", help="recompute and compare chunks, entries and manifest")
    check.add_argument("directory", type=Path)
    check.set_defaults(run=command_check)

    extract = commands.add_parser("extract", help="decompress the entries into files")
    extract.add_argument("directory", type=Path)
    extract.add_argument("--to", required=True, type=Path)
    extract.add_argument("--entry", action="append", help="only this entry (repeatable)")
    extract.set_defaults(run=command_extract)

    info = commands.add_parser("info", help="print the manifest summary")
    info.add_argument("directory", type=Path)
    info.set_defaults(run=command_info)

    args = parser.parse_args(argv)
    if args.check is not None:
        args.directory = args.check
        return command_check(args)
    if args.command is None:
        parser.print_help()
        return 2
    return args.run(args)


if __name__ == "__main__":
    sys.exit(main())
