#!/usr/bin/env python3
"""Vendors v86 into src/OsX86/wwwroot/v86 (plan section 12: BSD-2-Clause, notice kept, local patches).

Downloads the published npm package of v86 and the SeaBIOS binaries of the matching v86 commit
into an empty download directory, checks every file against the SHA-256 pinned below, copies
what the engine needs into the output directory, applies the patches of tools/v86/patches/ in
order and writes VENDOR.json (provenance). Everything downloaded is treated as untrusted: it is
never executed here, only hashed, copied and patched.

Run from the repository root with an isolated interpreter:

    python -I tools/v86/vendor-v86.py            # refresh src/OsX86/wwwroot/v86
    python -I tools/v86/vendor-v86.py --check    # rebuild in a temporary directory and compare

Standard library only; no network access other than the pinned URLs.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import tarfile
import tempfile
import urllib.request
from pathlib import Path

V86_VERSION = "0.5.473"
V86_COMMIT = "315b2b0b758703ef0e6cc7b4088d6068f83f3c20"  # package.json: 0.5.473+g315b2b0
SEABIOS_RELEASE = "rel-1.16.2"  # bios/fetch-and-build-seabios.sh of that commit

NPM_TARBALL = {
    "name": f"v86-{V86_VERSION}.tgz",
    "url": f"https://registry.npmjs.org/v86/-/v86-{V86_VERSION}.tgz",
    "sha256": "364ab67a68c1d83646e2ddf7caf73c24574694a132b2b43689ad0f72e7242636",
}

# Files of the v86 repository that the npm package does not ship (the BIOS binaries are built by
# the v86 maintainers from SeaBIOS; LGPL-3.0, COPYING.LESSER kept next to them).
RAW_FILES = [
    {"name": "seabios.bin", "path": "bios/seabios.bin",
     "sha256": "73e3f359102e3a9982c35fce98eb7cd08f18303ac7f1ba6ebfbe6cdc1c244d98"},
    {"name": "vgabios.bin", "path": "bios/vgabios.bin",
     "sha256": "a4bc0d80cc3ca028c73dafa8fee396b8d054ce87ebd8abfbd31b06b437607880"},
    {"name": "COPYING.LESSER", "path": "bios/COPYING.LESSER",
     "sha256": "a853c2ffec17057872340eee242ae4d96cbf2b520ae27d903e1b2fef1a5f9d1c"},
]
RAW_BASE = f"https://raw.githubusercontent.com/copy/v86/{V86_COMMIT}/"
MAX_TARBALL_BYTES = 64 * 1024 * 1024
MAX_RAW_FILE_BYTES = 4 * 1024 * 1024
MAX_MEMBER_BYTES = 16 * 1024 * 1024
USER_AGENT = "os-x86-vendor-v86"

# Members of the npm tarball copied to the output directory.
PACKAGE_FILES = {
    "package/LICENSE": "LICENSE",
    "package/build/libv86.js": "libv86.js",
    "package/build/v86.wasm": "v86.wasm",
}

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUTPUT = REPO_ROOT / "src" / "OsX86" / "wwwroot" / "v86"
DEFAULT_DOWNLOAD = REPO_ROOT / "tools" / "v86" / "download"
PATCH_DIR = Path(__file__).resolve().parent / "patches"
CHUNK = 1024 * 1024


def sha256_of(path: Path) -> str:
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


def extract_members(tarball: Path, members: dict[str, str], output: Path) -> None:
    """Copies the listed tarball members to output; anything else in the archive is ignored."""
    with tarfile.open(tarball, "r:gz") as archive:
        for member_name, target_name in members.items():
            try:
                member = archive.getmember(member_name)
            except KeyError:
                raise SystemExit(f"{member_name} is missing from {tarball.name}") from None
            if not member.isfile() or member.size > MAX_MEMBER_BYTES:
                raise SystemExit(f"{member_name} is not a regular file of at most {MAX_MEMBER_BYTES} bytes in {tarball.name}")
            source = archive.extractfile(member)
            assert source is not None
            with (output / target_name).open("wb") as out:
                shutil.copyfileobj(source, out)
            print(f"  extracted {member_name} -> {target_name}")


Hunk = tuple[int, list[str], list[str]]


def parse_unified_diff(text: str) -> list[tuple[str, list[Hunk]]]:
    """Returns [(target file, [(old start line, old lines, new lines), ...]), ...] (strict, no fuzz)."""
    files: list[tuple[str, list[Hunk]]] = []
    hunks: list[Hunk] = []
    lines = text.split("\n")
    index = 0
    while index < len(lines):
        line = lines[index]
        if line.startswith("+++ "):
            target = line[4:].strip()
            target = target[2:] if target.startswith("b/") else target
            hunks = []
            files.append((target, hunks))
        elif line.startswith("@@"):
            header = line.split("@@")[1].strip()
            old_start = int(header.split(" ")[0][1:].split(",")[0])
            old_lines: list[str] = []
            new_lines: list[str] = []
            index += 1
            while index < len(lines) and lines[index] and lines[index][0] in " +-\\":
                body = lines[index]
                if body.startswith("\\"):
                    pass  # "No newline at end of file": the split on "\n" already handles it
                elif body.startswith(" "):
                    old_lines.append(body[1:])
                    new_lines.append(body[1:])
                elif body.startswith("-"):
                    old_lines.append(body[1:])
                else:
                    new_lines.append(body[1:])
                index += 1
            if not old_lines:
                raise SystemExit("a hunk without context or removed lines cannot be placed")
            hunks.append((old_start, old_lines, new_lines))
            continue
        index += 1
    return files


def apply_patch(patch: Path, output: Path) -> None:
    """Applies a unified diff produced by git to files of output; every hunk must match exactly."""
    print(f"  patch {patch.name}")
    for target, hunks in parse_unified_diff(patch.read_text(encoding="utf-8")):
        path = (output / target).resolve()
        if not path.is_relative_to(output.resolve()) or not path.is_file():
            raise SystemExit(f"{patch.name}: '{target}' is not a vendored file")
        content = path.read_text(encoding="utf-8").split("\n")
        offset = 0
        for old_start, old_lines, new_lines in hunks:
            start = old_start - 1 + offset
            if content[start : start + len(old_lines)] != old_lines:
                raise SystemExit(f"{patch.name}: hunk at line {old_start} of {target} does not match")
            content[start : start + len(old_lines)] = new_lines
            offset += len(new_lines) - len(old_lines)
        path.write_text("\n".join(content), encoding="utf-8", newline="\n")


def build(download_dir: Path, output: Path) -> dict:
    download_dir.mkdir(parents=True, exist_ok=True)
    if any(download_dir.iterdir()):
        raise SystemExit(f"{download_dir} is not empty: the download directory must be an empty directory of its own")
    output.mkdir(parents=True, exist_ok=True)
    for stale in output.iterdir():
        if stale.is_file():
            stale.unlink()

    print("v86 npm package")
    tarball = download_dir / NPM_TARBALL["name"]
    download(NPM_TARBALL["url"], tarball, NPM_TARBALL["sha256"], MAX_TARBALL_BYTES)
    extract_members(tarball, PACKAGE_FILES, output)

    print("SeaBIOS binaries and license from the v86 repository")
    for raw in RAW_FILES:
        download(RAW_BASE + raw["path"], download_dir / raw["name"], raw["sha256"], MAX_RAW_FILE_BYTES)
        shutil.copyfile(download_dir / raw["name"], output / raw["name"])

    unpatched = {name: sha256_of(output / name) for name in sorted(PACKAGE_FILES.values())}
    patches = sorted(PATCH_DIR.glob("*.patch"))
    print("Local patches")
    for patch in patches:
        apply_patch(patch, output)

    provenance = {
        "name": "v86",
        "version": V86_VERSION,
        "commit": V86_COMMIT,
        "license": "BSD-2-Clause (LICENSE); SeaBIOS binaries LGPL-3.0 (COPYING.LESSER)",
        "sources": {
            "npm": {"url": NPM_TARBALL["url"], "sha256": NPM_TARBALL["sha256"]},
            "repository": f"https://github.com/copy/v86/tree/{V86_COMMIT}",
            "seabios": {"release": SEABIOS_RELEASE, "url": "https://git.seabios.org/seabios.git",
                        "built_by": "bios/fetch-and-build-seabios.sh of the v86 repository"},
        },
        "unpatched_sha256": unpatched,
        "patches": [patch.name for patch in patches],
        "files": {path.name: sha256_of(path) for path in sorted(output.iterdir()) if path.name != "VENDOR.json"},
    }
    (output / "VENDOR.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8", newline="\n")
    return provenance


def check(download_dir: Path, committed: Path) -> int:
    with tempfile.TemporaryDirectory(prefix="os-x86-v86-check-") as temp:
        fresh = Path(temp) / "v86"
        build(download_dir, fresh)
        differences = []
        for path in sorted(fresh.iterdir()):
            other = committed / path.name
            if not other.exists() or sha256_of(other) != sha256_of(path):
                differences.append(path.name)
        for path in sorted(committed.iterdir()):
            if path.is_file() and not (fresh / path.name).exists():
                differences.append(path.name + " (extra)")
    if differences:
        print("The vendored files differ from a fresh build: " + ", ".join(differences))
        return 1
    print(f"OK: {committed} matches a fresh build of v86 {V86_VERSION}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--download-dir", type=Path, default=DEFAULT_DOWNLOAD,
                        help="empty directory that receives the downloads (default: tools/v86/download, git-ignored)")
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT,
                        help="vendored directory to (re)write (default: src/OsX86/wwwroot/v86)")
    parser.add_argument("--check", action="store_true",
                        help="build into a temporary directory and compare with --output-dir instead of writing it")
    parser.add_argument("--keep-download", action="store_true", help="keep the download directory afterwards")
    args = parser.parse_args()

    download_dir = args.download_dir.resolve()
    try:
        if args.check:
            return check(download_dir, args.output_dir.resolve())
        provenance = build(download_dir, args.output_dir.resolve())
        print(f"Vendored v86 {provenance['version']} into {args.output_dir}")
        return 0
    finally:
        if not args.keep_download and download_dir.exists():
            shutil.rmtree(download_dir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
