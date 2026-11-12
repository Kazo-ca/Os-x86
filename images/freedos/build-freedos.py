#!/usr/bin/env python3
"""Builds the FreeDOS image of Os-x86 (milestone 0, work item 4; trial E14, decision 214 A).

The image is the official FreeDOS 1.4 "LiteUSB" disk (FD14LITE.img: 32 MiB, an MBR and one
FAT16 partition, the plain DOS system without the applications), taken from FD14-LiteUSB.zip
of the FreeDOS project and verified against the SHA-256 pinned below (the value published in
verify.txt next to the archive on freedos.org), with two changes made with mtools (never by
booting anything): FDAUTO.BAT (next to this script) replaces the one of the archive so that the
disk boots to a text prompt instead of the installer and idles with FDAPM APMDOS, and
SETUP.BAT is removed. A text console on the VGA screen is the way of 214 A for DOS.

Output (git-ignored, never committed): images/build/freedos/
    freedos.img     the modified disk, byte for byte otherwise
    pack/           the published form: chunk-NNNNN.bin, manifest.json (entry `disk`),
                    manifest.sha256, sources.json; made by images/tools/pack-image.py

Run from the repository root with an isolated interpreter (standard library only; mtools on
the PATH, or Docker for the osx86-tools container):

    python -I images/freedos/build-freedos.py [--download-dir DIR] [--output-dir DIR]

The download goes to an empty directory of its own (default images/freedos/download,
git-ignored) and is treated as untrusted: hashed, read with zipfile, written into with mtools;
nothing downloaded is executed on the host.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path

IMAGE_ID = "freedos"
IMAGE_VERSION = "0.1.0"
FREEDOS_VERSION = "1.4"
ARCHIVE_NAME = "FD14-LiteUSB.zip"
ARCHIVE_URL = f"https://download.freedos.org/{FREEDOS_VERSION}/{ARCHIVE_NAME}"
# Published in verify.txt of the FreeDOS 1.4 release (ibiblio.org/pub/micro/pc-stuff/freedos/files/distributions/1.4/official/).
ARCHIVE_SHA256 = "857dcd2ebf9d3d094320154db5fb5b830acba6fb98f981a95a0ca7ab3350338b"
DISK_IN_ARCHIVE = "FD14LITE.img"
MAX_ARCHIVE_BYTES = 64 * 1024 * 1024
MAX_DISK_BYTES = 64 * 1024 * 1024
USER_AGENT = "os-x86-build-freedos"
TOOLS_IMAGE = "osx86-tools"
SOURCES = {
    "components": [
        {"name": "FreeDOS 1.4 LiteUSB", "license": "mixed (GPL-2.0 kernel and FreeCOM; per-package licenses in the "
                                                   "PACKAGES directory of the disk and on freedos.org)",
         "archive": ARCHIVE_URL, "sha256": ARCHIVE_SHA256,
         "sources": "https://www.ibiblio.org/pub/micro/pc-stuff/freedos/files/distributions/1.4/ (the BonusCD and the "
                    "per-package source archives of the 1.4 release; the kernel: https://github.com/FDOS/kernel)"},
    ],
}

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[1]
PACK_TOOL = REPO_ROOT / "images" / "tools" / "pack-image.py"
DEFAULT_DOWNLOAD = HERE / "download"
DEFAULT_OUTPUT = REPO_ROOT / "images" / "build" / IMAGE_ID
CHUNK = 1024 * 1024


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def download(url: str, destination: Path, expected_sha256: str, max_bytes: int) -> None:
    """Downloads url to destination, hashing while writing; refuses more than max_bytes and a
    SHA-256 that differs from the pinned one. The file only gets its final name once verified."""
    print(f"  download {url}")
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    partial = destination.with_name(destination.name + ".part")
    digest = hashlib.sha256()
    received = 0
    with urllib.request.urlopen(request, timeout=300) as response, partial.open("wb") as out:
        for chunk in iter(lambda: response.read(CHUNK), b""):
            received += len(chunk)
            if received > max_bytes:
                partial.unlink()
                raise SystemExit(f"{destination.name}: more than {max_bytes} bytes received, download refused")
            digest.update(chunk)
            out.write(chunk)
    actual = digest.hexdigest()
    if actual != expected_sha256:
        partial.unlink()
        raise SystemExit(f"SHA-256 mismatch for {destination.name}: expected {expected_sha256}, got {actual}")
    os.replace(partial, destination)


def read_disk(archive: Path) -> bytes:
    with zipfile.ZipFile(archive) as zipped:
        try:
            info = zipped.getinfo(DISK_IN_ARCHIVE)
        except KeyError:
            raise SystemExit(f"{DISK_IN_ARCHIVE} not found in {archive.name}") from None
        if info.file_size > MAX_DISK_BYTES:
            raise SystemExit(f"{DISK_IN_ARCHIVE} is {info.file_size} bytes, more than this script accepts")
        return zipped.read(info)


def partition_offset(disk: bytes) -> int:
    """Byte offset of the first partition of an MBR disk, after a sanity check of the FAT16 header."""
    if len(disk) < 1024 * 1024 or disk[510:512] != b"\x55\xaa":
        raise SystemExit("not an MBR disk image")
    lba = struct.unpack_from("<I", disk, 446 + 8)[0]
    offset = lba * 512
    if offset + 512 > len(disk):
        raise SystemExit("the first partition starts outside the image")
    boot = disk[offset : offset + 512]
    if boot[54:62].strip() != b"FAT16" or boot[510:512] != b"\x55\xaa":
        raise SystemExit("the first partition is not a FAT16 file system")
    return offset


def mtools_command(image: Path, offset: int, work: Path) -> list[str]:
    """The mtools invocation prefix: native when mtools is installed, else in the osx86-tools
    container with the work directory mounted."""
    if shutil.which("mcopy"):
        return []
    if not shutil.which("docker"):
        raise SystemExit("neither mtools (mcopy) nor docker is available")
    subprocess.run(["docker", "build", "-q", "-t", TOOLS_IMAGE, str(REPO_ROOT / "images" / "tools")], check=True, capture_output=True)
    return ["docker", "run", "--rm", "-e", "MTOOLS_SKIP_CHECK=1", "-v", f"{str(work.resolve()).replace(chr(92), '/')}:/work", "-w", "/work", TOOLS_IMAGE]


def customise(disk: bytes, output: Path) -> Path:
    """Writes freedos.img: FD14LITE.img with our FDAUTO.BAT and without SETUP.BAT (mtools)."""
    offset = partition_offset(disk)
    image = output / "freedos.img"
    image.write_bytes(disk)
    with tempfile.TemporaryDirectory(dir=output) as temporary:
        work = Path(temporary)
        batch = work / "FDAUTO.BAT"
        text = (HERE / "FDAUTO.BAT").read_text(encoding="ascii").replace("\r\n", "\n").replace("\n", "\r\n")
        batch.write_bytes(text.encode("ascii"))
        shutil.copyfile(image, work / "disk.img")
        prefix = mtools_command(image, offset, work)
        inside = prefix != []
        disk_arg = f"disk.img@@{offset}" if inside else f"{work / 'disk.img'}@@{offset}"
        batch_arg = "FDAUTO.BAT" if inside else str(batch)
        environment = dict(os.environ, MTOOLS_SKIP_CHECK="1")
        subprocess.run(prefix + ["mdel", "-i", disk_arg, "::SETUP.BAT"], env=environment, capture_output=True)
        subprocess.run(prefix + ["mcopy", "-o", "-i", disk_arg, batch_arg, "::FDAUTO.BAT"], env=environment, check=True)
        listing = subprocess.run(prefix + ["mdir", "-i", disk_arg, "::"], env=environment, check=True, capture_output=True, text=True).stdout
        if "FDAUTO" not in listing.upper() or "SETUP" in listing.upper().replace("FDSETUP", ""):
            raise SystemExit(f"unexpected root directory after the changes:\n{listing}")
        shutil.copyfile(work / "disk.img", image)
    return image


def pack(output: Path, image: Path, disk_sha256: str) -> dict:
    pack_dir = output / "pack"
    pack_dir.mkdir(exist_ok=True)
    boot = {
        "kind": "dos",
        "disks": {"hda": "disk"},
        "display": "vga-text",
        "readyMarker": "OSX86-READY",
        "memoryMiB": 64,
    }
    build = {"recipe": "images/freedos/build-freedos.py", "freedos": FREEDOS_VERSION,
             "archive": {"url": ARCHIVE_URL, "sha256": ARCHIVE_SHA256}, "originalDiskSha256": disk_sha256}
    sources = dict(SOURCES, image=IMAGE_ID, version=IMAGE_VERSION)
    for name, value in (("boot.json", boot), ("build.json", build), ("sources.json", sources)):
        (pack_dir / name).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8", newline="\n")
    subprocess.run([sys.executable, "-I", str(PACK_TOOL), "pack", "--image", IMAGE_ID, "--version", IMAGE_VERSION,
                    "--entry", f"disk={image}", "--boot-json", str(pack_dir / "boot.json"),
                    "--build-json", str(pack_dir / "build.json"), "--output", str(pack_dir)], check=True)
    subprocess.run([sys.executable, "-I", str(PACK_TOOL), "check", str(pack_dir)], check=True)
    return json.loads((pack_dir / "manifest.json").read_text(encoding="utf-8"))


def build(download_dir: Path, output: Path) -> dict:
    output.mkdir(parents=True, exist_ok=True)
    print(f"FreeDOS {FREEDOS_VERSION} LiteUSB (official release)")
    archive = download_dir / ARCHIVE_NAME
    download(ARCHIVE_URL, archive, ARCHIVE_SHA256, MAX_ARCHIVE_BYTES)
    disk = read_disk(archive)
    print(f"  {DISK_IN_ARCHIVE}: {len(disk)} bytes, sha256 {sha256_bytes(disk)[:12]}...")
    image = customise(disk, output)
    print(f"  freedos.img: {image.stat().st_size} bytes")
    return pack(output, image, sha256_bytes(disk))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--download-dir", type=Path, default=DEFAULT_DOWNLOAD,
                        help="empty directory that receives the archive (default: images/freedos/download, git-ignored)")
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT,
                        help="where the image goes (default: images/build/freedos, git-ignored)")
    parser.add_argument("--keep-download", action="store_true", help="keep the download directory afterwards")
    args = parser.parse_args()
    download_dir = args.download_dir.resolve()
    # The download directory is an empty directory of its own: checked before anything is written
    # there, and only then is it deleted at the end (a directory that was not empty is never touched).
    download_dir.mkdir(parents=True, exist_ok=True)
    if any(download_dir.iterdir()):
        raise SystemExit(f"{download_dir} is not empty: the download directory must be an empty directory of its own")
    try:
        manifest = build(download_dir, args.output_dir.resolve())
        print(f"Built {manifest['image']} {manifest['version']}: pack {manifest['size']} bytes in {len(manifest['chunks'])} chunks")
        return 0
    finally:
        if not args.keep_download and download_dir.exists():
            shutil.rmtree(download_dir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
