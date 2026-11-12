#!/usr/bin/env python3
"""Builds the Alpine test image of Os-x86 (milestone 0, work item 2) without Docker.

The image is the `linux-virt` kernel and the initramfs of an official Alpine Linux release for
x86 (32-bit), taken from the `alpine-virt` ISO of that release, verified against the SHA-256
pinned below (the value published next to the ISO on dl-cdn.alpinelinux.org), plus a small
init script (init.sh, next to this file) that replaces the Alpine init of the initramfs: no
boot media, no network, no package manager; a busybox shell on the serial console (ttyS0) and
the virtio-console ports (hvc0 to hvc3, driver built into linux-virt).

Work item 5 adds the guest agent when it is built (images/build/agent, made by
images/agent/build-agent.py; the same static musl binary as in linux-base): /usr/lib/osx86/agent
started by the init on hvc0 with the consoles on hvc1, /usr/bin/osx86-open-url, and two guest
users (osx86-test and osx86-other, the prefixes "test" and "other") for the `guest` tests of the
agent. Without a built agent the image is the one of work item 2, with hvc0 to hvc3 free.

Output (git-ignored, never committed): images/build/alpine-test/
    vmlinuz-virt        the kernel, byte for byte from the ISO
    initramfs-osx86     the official initramfs with init.sh as /init (gzip, deterministic)
    manifest.json       build description: names, sizes, SHA-256 of both files, command line, sources
    pack/               the published form (work item 4): chunk-NNNNN.bin, manifest.json (format 1),
                        manifest.sha256, sources.json; made by images/tools/pack-image.py

Run from the repository root with an isolated interpreter (standard library only):

    python -I images/alpine-test/build-alpine-test.py [--download-dir DIR] [--output-dir DIR]
                                                      [--agent-dir DIR] [--require-agent]

The download goes to an empty directory of its own (default images/alpine-test/download,
git-ignored) and is treated as untrusted: it is hashed, parsed with the two small readers
below (ISO 9660 and cpio newc) and never executed on the host.
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import os
import shutil
import struct
import subprocess
import sys
import urllib.request
import zlib
from pathlib import Path

ALPINE_VERSION = "3.22.8"
ALPINE_BRANCH = "v3.22"
ARCHITECTURE = "x86"
ISO_NAME = f"alpine-virt-{ALPINE_VERSION}-{ARCHITECTURE}.iso"
ISO_URL = f"https://dl-cdn.alpinelinux.org/alpine/{ALPINE_BRANCH}/releases/{ARCHITECTURE}/{ISO_NAME}"
# Published in alpine-virt-3.22.8-x86.iso.sha256 next to the ISO.
ISO_SHA256 = "6f47c45a324f636c0d35ddbe1c8f494b93f146e08cf574c5eb78e492df0d3f17"
KERNEL_IN_ISO = "/boot/vmlinuz-virt"
INITRAMFS_IN_ISO = "/boot/initramfs-virt"
MAX_ISO_BYTES = 128 * 1024 * 1024
MAX_FILE_IN_ISO_BYTES = 64 * 1024 * 1024
MAX_INITRAMFS_BYTES = 256 * 1024 * 1024  # decompressed
MAX_NAME_BYTES = 4096
USER_AGENT = "os-x86-build-alpine-test"

# What the engine passes to the kernel: serial console, v86's stable TSC, no useless mitigations
# in an emulator, no splash. `quiet` keeps the boot messages short on the console the tests read.
KERNEL_CMDLINE = "console=ttyS0 tsc=reliable mitigations=off quiet"

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[1]
PACK_TOOL = REPO_ROOT / "images" / "tools" / "pack-image.py"
IMAGE_ID = "alpine-test"
IMAGE_VERSION = "0.1.0"
# Alpine aports commit used for the source references of the copyleft components (branch
# 3.22-stable at the time of the 3.22.8 release); the APKBUILD files there give the upstream
# tarballs and the patches.
APORTS_BRANCH = "3.22-stable"
SOURCES = {
    "components": [
        {"name": "linux-virt", "license": "GPL-2.0-only", "origin": "Alpine aports main/linux-lts",
         "apkbuild": f"https://gitlab.alpinelinux.org/alpine/aports/-/raw/{APORTS_BRANCH}/main/linux-lts/APKBUILD",
         "upstream": "https://cdn.kernel.org/pub/linux/kernel/v6.x/"},
        {"name": "busybox", "license": "GPL-2.0-only", "origin": "Alpine aports main/busybox",
         "apkbuild": f"https://gitlab.alpinelinux.org/alpine/aports/-/raw/{APORTS_BRANCH}/main/busybox/APKBUILD",
         "upstream": "https://busybox.net/downloads/"},
        {"name": "musl", "license": "MIT", "origin": "Alpine aports main/musl",
         "apkbuild": f"https://gitlab.alpinelinux.org/alpine/aports/-/raw/{APORTS_BRANCH}/main/musl/APKBUILD"},
    ],
    "note": "The initramfs is the official one of the alpine-virt ISO with /init replaced; its exact "
            "package versions are those of the release, listed in the ISO's apks/ directory.",
}
DEFAULT_DOWNLOAD = HERE / "download"
DEFAULT_AGENT = REPO_ROOT / "images" / "build" / "agent"
AGENT_TOOL = REPO_ROOT / "images" / "agent" / "build-agent.py"
AGENT_PATH = "usr/lib/osx86/agent"
OPEN_URL_PATH = "usr/bin/osx86-open-url"
# The guest users of the agent tests: user osx86-<prefix> for the prefixes "test" and "other".
GUEST_USERS = (("osx86-test", 1001), ("osx86-other", 1002))
DEFAULT_OUTPUT = REPO_ROOT / "images" / "build" / "alpine-test"
CHUNK = 1024 * 1024


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(CHUNK), b""):
            digest.update(chunk)
    return digest.hexdigest()


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


# --- ISO 9660 (Joliet, with a Rock Ridge fallback), read-only -------------------------------

SECTOR = 2048


class Iso9660:
    """Enough of ISO 9660 to find and read a file by path."""

    def __init__(self, path: Path) -> None:
        self.stream = path.open("rb")
        self.joliet = False
        root = None
        sector = 16
        while True:
            self.stream.seek(sector * SECTOR)
            descriptor = self.stream.read(SECTOR)
            if descriptor[1:6] != b"CD001":
                raise SystemExit(f"{path.name} is not an ISO 9660 image")
            kind = descriptor[0]
            if kind == 255:
                break
            if kind == 1 and root is None:
                root = descriptor[156:190]
            if kind == 2 and descriptor[88:91] in (b"%/@", b"%/C", b"%/E"):
                root = descriptor[156:190]
                self.joliet = True
            sector += 1
        if root is None:
            raise SystemExit(f"{path.name} has no volume descriptor")
        self.root_extent = struct.unpack_from("<I", root, 2)[0]
        self.root_size = struct.unpack_from("<I", root, 10)[0]

    def _read(self, extent: int, size: int) -> bytes:
        if size > MAX_FILE_IN_ISO_BYTES:
            raise SystemExit(f"an entry of {size} bytes is larger than this reader accepts")
        self.stream.seek(extent * SECTOR)
        data = self.stream.read(size)
        if len(data) != size:
            raise SystemExit("the ISO ends before the end of an entry")
        return data

    def _entries(self, data: bytes):
        position = 0
        while position < len(data):
            length = data[position]
            if length == 0:
                position = (position // SECTOR + 1) * SECTOR
                continue
            if length < 34 or position + length > len(data) or data[position + 32] > length - 33:
                raise SystemExit("malformed directory record in the ISO")
            extent = struct.unpack_from("<I", data, position + 2)[0]
            size = struct.unpack_from("<I", data, position + 10)[0]
            flags = data[position + 25]
            name_length = data[position + 32]
            raw = data[position + 33 : position + 33 + name_length]
            if raw not in (b"\x00", b"\x01"):
                if self.joliet:
                    name = raw.decode("utf-16-be")
                else:
                    name = raw.decode("ascii", "replace")
                    use = position + 33 + name_length + (1 - name_length % 2)
                    rock_ridge = b""
                    while use + 4 <= position + length and data[use + 2] >= 4:
                        if data[use : use + 2] == b"NM":
                            rock_ridge += data[use + 5 : use + data[use + 2]]
                        use += data[use + 2]
                    if rock_ridge:
                        name = rock_ridge.decode("utf-8", "replace")
                yield name.split(";")[0], bool(flags & 2), extent, size
            position += length

    def read_file(self, path: str) -> bytes:
        extent, size = self.root_extent, self.root_size
        parts = [part for part in path.split("/") if part]
        for index, part in enumerate(parts):
            for name, is_directory, child_extent, child_size in self._entries(self._read(extent, size)):
                if name == part:
                    if index == len(parts) - 1 and not is_directory:
                        return self._read(child_extent, child_size)
                    if is_directory:
                        extent, size = child_extent, child_size
                        break
            else:
                raise SystemExit(f"{path} not found in the ISO")
        raise SystemExit(f"{path} is a directory in the ISO")


# --- cpio "newc" (SVR4 without checksum), the format of a Linux initramfs ----------------------

CPIO_MAGIC = b"070701"
CPIO_FIELDS = ("ino", "mode", "uid", "gid", "nlink", "mtime", "filesize", "devmajor", "devminor",
               "rdevmajor", "rdevminor", "namesize", "check")


def _pad4(length: int) -> int:
    return (4 - length % 4) % 4


def cpio_read(data: bytes):
    """Yields (name, header, body) for every entry before the trailer."""
    position = 0
    while position + 110 <= len(data):
        if data[position : position + 6] != CPIO_MAGIC:
            raise SystemExit(f"bad cpio magic at offset {position}")
        values = [int(data[position + 6 + 8 * index : position + 14 + 8 * index], 16) for index in range(13)]
        header = dict(zip(CPIO_FIELDS, values))
        if not 1 <= header["namesize"] <= MAX_NAME_BYTES or position + 110 + header["namesize"] + header["filesize"] > len(data):
            raise SystemExit(f"malformed cpio entry at offset {position}")
        name_start = position + 110
        name = data[name_start : name_start + header["namesize"] - 1].decode("utf-8", "replace")
        body_start = name_start + header["namesize"] + _pad4(110 + header["namesize"])
        body = data[body_start : body_start + header["filesize"]]
        position = body_start + header["filesize"] + _pad4(header["filesize"])
        if name == "TRAILER!!!":
            return
        yield name, header, body


def cpio_entry(name: str, header: dict, body: bytes) -> bytes:
    name_bytes = name.encode("utf-8") + b"\0"
    fields = dict(header, filesize=len(body), namesize=len(name_bytes), check=0)
    encoded = CPIO_MAGIC + "".join(f"{fields[field]:08x}" for field in CPIO_FIELDS).encode("ascii")
    return encoded + name_bytes + b"\0" * _pad4(110 + len(name_bytes)) + body + b"\0" * _pad4(len(body))


def cpio_file_header(mode: int, ino: int) -> dict:
    return {"ino": ino, "mode": mode, "uid": 0, "gid": 0, "nlink": 1, "mtime": 0, "filesize": 0,
            "devmajor": 0, "devminor": 0, "rdevmajor": 0, "rdevminor": 0, "namesize": 0, "check": 0}


def gunzip_bounded(data: bytes, limit: int) -> bytes:
    """Decompresses a gzip stream, refusing more than limit bytes of output."""
    decompressor = zlib.decompressobj(16 + zlib.MAX_WBITS)
    output = decompressor.decompress(data, limit)
    if decompressor.unconsumed_tail or not decompressor.eof:
        raise SystemExit(f"the initramfs decompresses to more than {limit} bytes, or is truncated")
    return output


def load_agent(agent_dir: Path) -> dict[str, bytes] | None:
    """The agent binaries of a build that images/agent/build-agent.py --check accepts, or None
    when there is no build. A build that fails its check (stale, altered) is an error."""
    if not (agent_dir / "manifest.json").is_file():
        return None
    checked = subprocess.run([sys.executable, "-I", str(AGENT_TOOL), "--check", "--output-dir", str(agent_dir)])
    if checked.returncode != 0:
        raise SystemExit(f"the agent build in {agent_dir} failed its check: run `python -I images/agent/build-agent.py` again")
    return {AGENT_PATH: (agent_dir / "agent").read_bytes(), OPEN_URL_PATH: (agent_dir / "osx86-open-url").read_bytes()}


def rebuild_initramfs(official: bytes, init_script: bytes, agent: dict[str, bytes] | None = None) -> tuple[bytes, dict]:
    """Rewrites the official initramfs with init.sh as /init, two small files under /etc, the
    guest users of the agent tests and, when given, the agent binaries."""
    entries = list(cpio_read(gunzip_bounded(official, MAX_INITRAMFS_BYTES)))
    names = {name for name, _, _ in entries}
    if "bin/busybox" not in names:
        raise SystemExit("the official initramfs has no bin/busybox: not the expected Alpine initramfs")
    replaced = "init" in names
    next_ino = max(header["ino"] for _, header, _ in entries) + 1
    added: list[tuple[str, dict, bytes]] = []

    def add(name: str, mode: int, body: bytes = b"") -> None:
        nonlocal next_ino
        added.append((name, cpio_file_header(mode, next_ino), body))
        next_ino += 1

    def add_directories(path: str) -> None:
        parts = path.split("/")[:-1]
        for depth in range(1, len(parts) + 1):
            directory = "/".join(parts[:depth])
            if directory not in names and all(directory != name for name, _, _ in added):
                add(directory, 0o040755)

    replaced_files = {"init", "etc/passwd", "etc/group"}
    bodies = {name: body for name, _, body in entries if name in ("etc/passwd", "etc/group")}
    passwd = bodies.get("etc/passwd", b"root:x:0:0:root:/root:/bin/sh\n")
    group = bodies.get("etc/group", b"root:x:0:root\n")
    for user, uid in GUEST_USERS:
        passwd += f"{user}:x:{uid}:{uid}:Os-x86 test prefix:/home/{user}:/bin/sh\n".encode()
        group += f"{user}:x:{uid}:\n".encode()
    add_directories("etc/passwd")
    add("init", 0o100755, init_script)
    add("etc/osx86-alpine-release", 0o100644, f"{ALPINE_VERSION}\n".encode())
    add("etc/osx86-profile", 0o100644, b"PS1='osx86# '\n")
    add("etc/passwd", 0o100644, passwd)
    add("etc/group", 0o100644, group)
    for path, body in sorted((agent or {}).items()):
        add_directories(path)
        add(path, 0o100755, body)

    kept = [(name, header, body) for name, header, body in entries if name.strip("./") not in replaced_files]
    final = kept + added
    if sum(1 for name, _, _ in final if name == "init") != 1:
        raise SystemExit("the rebuilt initramfs must have exactly one /init")
    output = io.BytesIO()
    with gzip.GzipFile(fileobj=output, mode="wb", compresslevel=9, mtime=0) as compressed:
        for name, header, body in final:
            compressed.write(cpio_entry(name, header, body))
        compressed.write(cpio_entry("TRAILER!!!", cpio_file_header(0, 0), b""))
    return output.getvalue(), {"entries": len(kept), "init_replaced": replaced, "added": [name for name, _, _ in added]}


def kernel_version(image: bytes) -> str:
    """The version string of a bzImage (boot protocol: kernel_version offset in the real-mode header)."""
    offset = struct.unpack_from("<H", image, 0x20E)[0] + 0x200
    end = image.index(b"\0", offset)
    return image[offset:end].decode("ascii", "replace").split(" ")[0]


def build(download_dir: Path, output: Path, agent_dir: Path, require_agent: bool) -> dict:
    output.mkdir(parents=True, exist_ok=True)
    agent = load_agent(agent_dir)
    if agent is None and require_agent:
        raise SystemExit(f"no agent build in {agent_dir}: run `python -I images/agent/build-agent.py` first")

    print(f"Alpine {ALPINE_VERSION} {ARCHITECTURE} (official release)")
    iso_path = download_dir / ISO_NAME
    download(ISO_URL, iso_path, ISO_SHA256, MAX_ISO_BYTES)
    iso = Iso9660(iso_path)
    kernel = iso.read_file(KERNEL_IN_ISO)
    initramfs = iso.read_file(INITRAMFS_IN_ISO)
    print(f"  {KERNEL_IN_ISO}: {len(kernel)} bytes, {kernel_version(kernel)}")
    print(f"  {INITRAMFS_IN_ISO}: {len(initramfs)} bytes")

    init_script = (HERE / "init.sh").read_bytes().replace(b"\r\n", b"\n")
    rebuilt, details = rebuild_initramfs(initramfs, init_script, agent)
    print(f"  initramfs rebuilt: {details['entries']} entries kept, /init replaced, {len(rebuilt)} bytes, "
          + (f"agent {len(agent[AGENT_PATH])} bytes" if agent else "no agent (images/build/agent not built)"))

    (output / "vmlinuz-virt").write_bytes(kernel)
    (output / "initramfs-osx86").write_bytes(rebuilt)
    manifest = {
        "format": 1,
        "image": "alpine-test",
        "alpine": ALPINE_VERSION,
        "architecture": ARCHITECTURE,
        "kernel": {"file": "vmlinuz-virt", "size": len(kernel), "sha256": sha256_bytes(kernel),
                   "version": kernel_version(kernel)},
        "initrd": {"file": "initramfs-osx86", "size": len(rebuilt), "sha256": sha256_bytes(rebuilt)},
        "cmdline": KERNEL_CMDLINE,
        "readyMarker": "OSX86-READY",
        "prompt": "osx86# ",
        # The agent of work item 5, when the image carries it: where it is, its two ports and the
        # prefixes the image has a guest user for.
        "agent": None if agent is None else {
            "path": "/" + AGENT_PATH, "sha256": sha256_bytes(agent[AGENT_PATH]), "port": "virtio0", "consolePort": "virtio1",
            "prefixes": [user.removeprefix("osx86-") for user, _ in GUEST_USERS],
        },
        "sources": {
            "iso": {"url": ISO_URL, "sha256": ISO_SHA256},
            "kernel_in_iso": KERNEL_IN_ISO,
            "initramfs_in_iso": INITRAMFS_IN_ISO,
            "initramfs_sha256": sha256_bytes(initramfs),
            "init_sha256": sha256_bytes(init_script),
            "licenses": "Linux kernel and BusyBox: GPL-2.0; sources: https://gitlab.alpinelinux.org/alpine/aports (branch 3.22-stable, main/linux-lts and main/busybox)",
        },
    }
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")
    pack(output, manifest)
    return manifest


def pack(output: Path, build_manifest: dict) -> None:
    """Runs images/tools/pack-image.py: the published form (chunks, manifest format 1, sources)."""
    pack_dir = output / "pack"
    pack_dir.mkdir(exist_ok=True)
    boot = {
        "kind": "linux",
        "cmdline": KERNEL_CMDLINE,
        "console": "serial0",
        "readyMarker": build_manifest["readyMarker"],
        "prompt": build_manifest["prompt"],
        "memoryMiB": 128,
    }
    if build_manifest.get("agent"):
        boot["agent"] = {key: build_manifest["agent"][key] for key in ("path", "port", "consolePort")}
    build = {
        "recipe": "images/alpine-test/build-alpine-test.py",
        "alpine": ALPINE_VERSION,
        "architecture": ARCHITECTURE,
        "kernelVersion": build_manifest["kernel"]["version"],
        "iso": build_manifest["sources"]["iso"],
    }
    (pack_dir / "boot.json").write_text(json.dumps(boot, indent=2) + "\n", encoding="utf-8", newline="\n")
    (pack_dir / "build.json").write_text(json.dumps(build, indent=2) + "\n", encoding="utf-8", newline="\n")
    sources = dict(SOURCES, image=IMAGE_ID, version=IMAGE_VERSION,
                   iso=build_manifest["sources"]["iso"], initSha256=build_manifest["sources"]["init_sha256"])
    (pack_dir / "sources.json").write_text(json.dumps(sources, indent=2) + "\n", encoding="utf-8", newline="\n")
    command = [sys.executable, "-I", str(PACK_TOOL), "pack", "--image", IMAGE_ID, "--version", IMAGE_VERSION,
               "--entry", f"kernel={output / 'vmlinuz-virt'}", "--entry", f"initramfs={output / 'initramfs-osx86'}",
               "--boot-json", str(pack_dir / "boot.json"), "--build-json", str(pack_dir / "build.json"),
               "--output", str(pack_dir)]
    subprocess.run(command, check=True)
    subprocess.run([sys.executable, "-I", str(PACK_TOOL), "check", str(pack_dir)], check=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--download-dir", type=Path, default=DEFAULT_DOWNLOAD,
                        help="empty directory that receives the ISO (default: images/alpine-test/download, git-ignored)")
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT,
                        help="where the image goes (default: images/build/alpine-test, git-ignored)")
    parser.add_argument("--keep-download", action="store_true", help="keep the download directory afterwards")
    parser.add_argument("--agent-dir", type=Path, default=DEFAULT_AGENT,
                        help="the agent build to put in the image (default: images/build/agent; skipped when not built)")
    parser.add_argument("--require-agent", action="store_true", help="fail when the agent is not built instead of making an image without it")
    args = parser.parse_args()
    download_dir = args.download_dir.resolve()
    # The download directory is an empty directory of its own: checked before anything is written
    # there, and only then is it deleted at the end (a directory that was not empty is never touched).
    download_dir.mkdir(parents=True, exist_ok=True)
    if any(download_dir.iterdir()):
        raise SystemExit(f"{download_dir} is not empty: the download directory must be an empty directory of its own")
    try:
        manifest = build(download_dir, args.output_dir.resolve(), args.agent_dir.resolve(), args.require_agent)
        print(f"Built {manifest['image']} (Alpine {manifest['alpine']}, kernel {manifest['kernel']['version']}, "
              f"{'with' if manifest['agent'] else 'without'} the agent) in {args.output_dir}")
        return 0
    finally:
        if not args.keep_download and download_dir.exists():
            shutil.rmtree(download_dir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
