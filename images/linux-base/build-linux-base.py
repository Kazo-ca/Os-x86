#!/usr/bin/env python3
"""Builds the linux-base image of Os-x86 (milestone 0, work item 4; decisions 201 A, 210 A).

Steps, all through Docker (images and containers named osx86-*; nothing else is touched):
  1. docker build of the Dockerfile next to this script (stages `base` and `slim`, linux/386);
  2. docker export of the slim stage into build/linux-base/rootfs.tar;
  3. in the osx86-tools container: the tree is extracted (as root, exact modes), the image test
     of 210 A runs on it (images/tools/image-check.py: writable inventory and the 22 R134.5
     checks; the build fails if a check breaks), then mkfs.ext4 -d makes rootfs.ext4 from it;
  4. the kernel, the initramfs, the package inventory, the license list and the provenance of
     the kernel are taken out of the tar; sources.json lists the sources of every component
     (Debian: the exact snapshot.debian.org address of each source package; Alpine: the APKBUILD
     and the upstream archives of linux-lts, busybox and musl; see docs/fr/images.md for why the
     addresses rather than the archives are published by default);
  5. images/tools/pack-image.py packs kernel, initramfs and rootfs into build/linux-base/pack/
     and checks the result; measurements.json records the sizes.

Run from the repository root with an isolated interpreter (standard library only; Docker
Desktop or dockerd running; at least 5 GiB free on the build drive):

    python -I images/linux-base/build-linux-base.py [--output-dir DIR] [--skip-docker-build]
                                                    [--fetch-sources]

--fetch-sources also downloads the upstream archives of the copyleft components into
build/linux-base/sources/ (the kernel alone is about 150 MiB): for the R2 publication, never
for GitHub Pages.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import urllib.parse
import urllib.request
from pathlib import Path

IMAGE_ID = "linux-base"
IMAGE_VERSION = "0.1.0"
DOCKER_IMAGE = f"osx86-linux-base:{IMAGE_VERSION}"
DOCKER_IMAGE_FULL = f"osx86-linux-base-full:{IMAGE_VERSION}"
TOOLS_IMAGE = "osx86-tools"
EXPORT_CONTAINER = "osx86-linux-base-export"
ALPINE_BRANCH = "3.24-stable"
ALPINE_SOURCE_PACKAGES = {"linux-virt": "main/linux-lts", "busybox": "main/busybox", "musl": "main/musl"}
COPYLEFT = re.compile(r"\b(GPL|LGPL|AGPL|MPL|CDDL|EPL|CeCILL)\b", re.I)
MIN_FREE_BYTES = 5 * 1024 ** 3
MAX_APKBUILD_BYTES = 1024 * 1024
MAX_SOURCE_ARCHIVE_BYTES = 512 * 1024 ** 2
USER_AGENT = "os-x86-build-linux-base"
KERNEL_CMDLINE = "root=/dev/sda rootfstype=ext4 ro console=ttyS0 tsc=reliable mitigations=off quiet"

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[1]
TOOLS_DIR = REPO_ROOT / "images" / "tools"
PACK_TOOL = TOOLS_DIR / "pack-image.py"
DEFAULT_OUTPUT = REPO_ROOT / "images" / "build" / IMAGE_ID

# What the tools container runs on the exported tree: extraction with owners and modes, the
# image test, then the ext4 root (no journal: the root is mounted read-only; fixed UUID and
# hash seed and SOURCE_DATE_EPOCH for a reproducible image).
TOOLS_SCRIPT = r"""
set -eu
cd /work
rm -rf rootfs
mkdir rootfs
tar -xpf rootfs.tar -C rootfs --exclude=.dockerenv --numeric-owner
chmod 755 rootfs
python3 -I /tools/image-check.py --tree /work/rootfs --report /work/image-check.json
used=$(du -sk rootfs | cut -f1)
inodes=$(find rootfs | wc -l)
size_kib=$(( used + used / 8 + inodes + 16384 ))
size_mib=$(( (size_kib + 4095) / 4096 * 4 ))
rm -f rootfs.ext4
export E2FSPROGS_FAKE_TIME=1 SOURCE_DATE_EPOCH=1
mkfs.ext4 -q -F -d rootfs -L osx86-root -U 0b6f2a3c-4d5e-4f60-8172-839495a6b7c8 -m 0 -O ^has_journal \
  -E hash_seed=5d8c2e3a-6b7f-4c90-a1b2-c3d4e5f60718,lazy_itable_init=0,root_owner=0:0 rootfs.ext4 "${size_mib}M"
e2fsck -fn rootfs.ext4 > /dev/null
rm -rf rootfs
echo "ext4: ${size_mib} MiB for ${used} KiB used and ${inodes} entries"
"""


def run(command: list[str], **kwargs) -> subprocess.CompletedProcess:
    print("  $ " + " ".join(command if len(" ".join(command)) < 200 else command[:6] + ["..."]))
    return subprocess.run(command, check=True, **kwargs)


def docker_volume(path: Path) -> str:
    """A bind mount source Docker Desktop and dockerd both accept (forward slashes, absolute)."""
    return str(path.resolve()).replace("\\", "/")


def check_free_space(directory: Path) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    free = shutil.disk_usage(directory).free
    if free < MIN_FREE_BYTES:
        raise SystemExit(f"{free // 1024 ** 2} MiB free under {directory}: at least 5 GiB are required")


def docker_build(output: Path) -> None:
    # OSX86_DOCKER_BUILD_ARGS: extra arguments for the two image builds (the CI passes
    # --network=host: the DNS of the build sandbox on the GitHub runners did not resolve
    # deb.debian.org).
    extra = os.environ.get("OSX86_DOCKER_BUILD_ARGS", "").split()
    run(["docker", "build", "-t", TOOLS_IMAGE, str(TOOLS_DIR)])
    for target, tag in (("base", DOCKER_IMAGE_FULL), ("slim", DOCKER_IMAGE)):
        run(["docker", "build", "--platform", "linux/386", *extra, "--target", target, "-t", tag,
             "--build-arg", f"IMAGE_VERSION={IMAGE_VERSION}", str(HERE)])


def docker_export(output: Path) -> Path:
    tar_path = output / "rootfs.tar"
    subprocess.run(["docker", "rm", "-f", EXPORT_CONTAINER], capture_output=True)
    run(["docker", "create", "--platform", "linux/386", "--name", EXPORT_CONTAINER, DOCKER_IMAGE], capture_output=True)
    try:
        run(["docker", "export", "-o", str(tar_path), EXPORT_CONTAINER])
    finally:
        subprocess.run(["docker", "rm", "-f", EXPORT_CONTAINER], capture_output=True)
    return tar_path


def image_sizes() -> dict:
    sizes = {}
    for key, tag in (("dockerFull", DOCKER_IMAGE_FULL), ("dockerSlim", DOCKER_IMAGE)):
        result = subprocess.run(["docker", "image", "inspect", "--format", "{{.Size}}", tag], capture_output=True, text=True)
        sizes[key] = int(result.stdout.strip()) if result.returncode == 0 and result.stdout.strip().isdigit() else None
    return sizes


def extract_from_tar(tar_path: Path, output: Path) -> dict:
    """Takes the kernel, the initramfs, the inventories and the provenance out of the export."""
    wanted = {
        "boot/vmlinuz-virt": "vmlinuz-virt",
        "boot/initramfs-virt": "initramfs-virt",
        "usr/lib/osx86/packages.tsv": "packages.tsv",
        "usr/lib/osx86/licenses.tsv": "licenses.tsv",
        "usr/lib/osx86/provenance/apk.sha256": "alpine-apk.sha256",
        "usr/lib/osx86/provenance/kernel.release": "kernel.release",
        "usr/lib/osx86/provenance/kernel.config": "kernel.config",
    }
    found = {}
    with tarfile.open(tar_path, "r:") as archive:
        for member in archive:
            name = member.name[2:] if member.name.startswith("./") else member.name
            if name in wanted and member.isfile():
                stream = archive.extractfile(member)
                assert stream is not None
                (output / wanted[name]).write_bytes(stream.read())
                found[name] = member.size
    missing = [name for name in wanted if name not in found]
    if missing:
        raise SystemExit(f"missing from the export: {', '.join(missing)}")
    return found


def run_tools(output: Path) -> None:
    run(["docker", "run", "--rm", "-v", f"{docker_volume(output)}:/work", "-v", f"{docker_volume(TOOLS_DIR)}:/tools:ro",
         TOOLS_IMAGE, "sh", "-c", TOOLS_SCRIPT])
    report = json.loads((output / "image-check.json").read_text(encoding="utf-8"))
    if not report.get("ok"):
        raise SystemExit("the image test failed (see image-check.json)")


# --- sources (plan section 12) ------------------------------------------------------------------


def fetch_text(url: str, limit: int) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=120) as response:
        data = response.read(limit + 1)
    if len(data) > limit:
        raise SystemExit(f"{url}: more than {limit} bytes")
    return data.decode("utf-8", "replace")


def parse_apkbuild(text: str) -> dict:
    """pkgver, pkgrel, license and the expanded source URLs of an APKBUILD (shell syntax read
    well enough for the aports of the kernel, busybox and musl: simple assignments, $var,
    ${var} and ${var%%.*})."""
    variables: dict[str, str] = {}
    for match in re.finditer(r"^([A-Za-z_][A-Za-z0-9_]*)=(\"[^\"]*\"|'[^']*'|[^\s#]+)", text, re.M):
        value = match.group(2)
        if value[:1] in "\"'":
            value = value[1:-1]
        variables[match.group(1)] = value

    pattern = re.compile(r"\$(?:\{([A-Za-z_][A-Za-z0-9_]*)(%%?\.\*)?\}|([A-Za-z_][A-Za-z0-9_]*))")

    def expand(value: str, depth: int = 0) -> str:
        if depth > 5:
            return value

        def replace(match: re.Match) -> str:
            name = match.group(1) or match.group(3)
            inner = variables.get(name, "")
            if match.group(2) == "%%.*":
                inner = inner.split(".")[0]
            elif match.group(2) == "%.*":
                inner = inner.rsplit(".", 1)[0]
            return expand(inner, depth + 1)

        return pattern.sub(replace, value)

    sources = []
    for item in variables.get("source", "").split():
        name, _, url = item.rpartition("::")
        url = expand(url)
        if url.startswith("http"):
            sources.append({"name": expand(name) if name else url.rsplit("/", 1)[-1], "url": url})
    checksums = {}
    block = re.search(r"^sha512sums=\"([^\"]*)\"", text, re.M)
    if block:
        for line in block.group(1).splitlines():
            parts = line.split()
            if len(parts) == 2:
                checksums[parts[1]] = parts[0]
    for source in sources:
        source["sha512"] = checksums.get(source["name"])
    return {"pkgver": expand(variables.get("pkgver", "")), "pkgrel": variables.get("pkgrel", ""),
            "license": variables.get("license", ""), "sources": sources}


def debian_sources(output: Path) -> list[dict]:
    packages = []
    licenses = {}
    for line in (output / "licenses.tsv").read_text(encoding="utf-8").splitlines():
        parts = line.split("\t")
        if len(parts) == 2:
            licenses[parts[0]] = parts[1]
    for line in (output / "packages.tsv").read_text(encoding="utf-8").splitlines():
        parts = line.split("\t")
        if len(parts) != 4:
            continue
        binary, version, source, source_version = parts
        binary_name = binary.split(":")[0]
        license_text = licenses.get(binary_name, licenses.get(source, "see copyright file"))
        packages.append({
            "package": binary, "version": version, "source": source, "sourceVersion": source_version,
            "license": license_text, "copyleft": bool(COPYLEFT.search(license_text)),
            "snapshot": f"https://snapshot.debian.org/package/{urllib.parse.quote(source)}/{urllib.parse.quote(source_version)}/",
            "copyrightFile": f"/usr/share/doc/{binary_name}/copyright",
        })
    return packages


def alpine_sources(output: Path) -> list[dict]:
    apk_versions = {}
    for line in (output / "alpine-apk.sha256").read_text(encoding="utf-8").splitlines():
        digest, _, filename = line.partition("  ")
        match = re.match(r"^(.*)-(\d[^-]*-r\d+)\.apk$", filename.strip())
        if match:
            apk_versions[match.group(1)] = {"version": match.group(2), "apkSha256": digest}
    components = []
    for package, aports_path in ALPINE_SOURCE_PACKAGES.items():
        # The GitHub mirror of aports: gitlab.alpinelinux.org answers 418 to a plain HTTP client.
        url = f"https://raw.githubusercontent.com/alpinelinux/aports/{ALPINE_BRANCH}/{aports_path}/APKBUILD"
        print(f"  APKBUILD {url}")
        parsed = parse_apkbuild(fetch_text(url, MAX_APKBUILD_BYTES))
        installed = apk_versions.get(package, {})
        expected = f"{parsed['pkgver']}-r{parsed['pkgrel']}"
        components.append({
            "package": package, "version": installed.get("version"), "apkSha256": installed.get("apkSha256"),
            "aports": f"https://gitlab.alpinelinux.org/alpine/aports/-/tree/{ALPINE_BRANCH}/{aports_path}",
            "apkbuild": url, "apkbuildVersion": expected, "versionMatches": installed.get("version") == expected,
            "license": parsed["license"], "copyleft": bool(COPYLEFT.search(parsed["license"])),
            "sources": parsed["sources"],
        })
    return components


def fetch_source_archives(components: list[dict], directory: Path) -> list[dict]:
    """Downloads the upstream archives of the copyleft Alpine components (R2 publication)."""
    directory.mkdir(parents=True, exist_ok=True)
    if any(directory.iterdir()):
        raise SystemExit(f"{directory} is not empty: the sources directory must be an empty directory of its own")
    fetched = []
    for component in components:
        if not component["copyleft"]:
            continue
        for source in component["sources"]:
            name = Path(source["name"]).name
            if not name or name in (".", "..") or not source.get("sha512"):
                raise SystemExit(f"{source['url']}: no usable file name or no SHA-512 in the APKBUILD; refused")
            target = directory / name
            print(f"  download {source['url']}")
            request = urllib.request.Request(source["url"], headers={"User-Agent": USER_AGENT})
            digest = hashlib.sha512()
            received = 0
            with urllib.request.urlopen(request, timeout=600) as response, target.open("wb") as out:
                for chunk in iter(lambda: response.read(1024 * 1024), b""):
                    received += len(chunk)
                    if received > MAX_SOURCE_ARCHIVE_BYTES:
                        raise SystemExit(f"{source['url']}: more than {MAX_SOURCE_ARCHIVE_BYTES} bytes")
                    digest.update(chunk)
                    out.write(chunk)
            if digest.hexdigest() != source["sha512"]:
                target.unlink()
                raise SystemExit(f"{source['url']}: SHA-512 differs from the APKBUILD")
            fetched.append({"file": target.name, "bytes": received, "sha512": digest.hexdigest(), "from": source["url"]})
    return fetched


# --- pack --------------------------------------------------------------------------------------


def pack(output: Path, sizes: dict, sources: dict) -> dict:
    pack_dir = output / "pack"
    pack_dir.mkdir(exist_ok=True)
    boot = {
        "kind": "linux",
        "cmdline": KERNEL_CMDLINE,
        "console": "serial0",
        "disks": {"hda": "rootfs"},
        "readyMarker": "OSX86-READY",
        "prompt": "osx86# ",
        "memoryMiB": 256,
        "agent": {"path": "/usr/lib/osx86/agent", "port": "virtio0"},
    }
    build = {
        "recipe": "images/linux-base/build-linux-base.py",
        "debian": "trixie-slim i386",
        "kernelRelease": (output / "kernel.release").read_text(encoding="utf-8").strip(),
        "sizes": sizes,
    }
    for name, value in (("boot.json", boot), ("build.json", build), ("sources.json", sources)):
        (pack_dir / name).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8", newline="\n")
    for name in ("packages.tsv", "licenses.tsv", "image-check.json"):
        shutil.copyfile(output / name, pack_dir / name)
    run([sys.executable, "-I", str(PACK_TOOL), "pack", "--image", IMAGE_ID, "--version", IMAGE_VERSION,
         "--entry", f"kernel={output / 'vmlinuz-virt'}", "--entry", f"initramfs={output / 'initramfs-virt'}",
         "--entry", f"rootfs={output / 'rootfs.ext4'}",
         "--boot-json", str(pack_dir / "boot.json"), "--build-json", str(pack_dir / "build.json"), "--output", str(pack_dir)])
    run([sys.executable, "-I", str(PACK_TOOL), "check", str(pack_dir)])
    return json.loads((pack_dir / "manifest.json").read_text(encoding="utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT, help="build directory (default images/build/linux-base, git-ignored)")
    parser.add_argument("--skip-docker-build", action="store_true", help="reuse the osx86-linux-base image already built")
    parser.add_argument("--fetch-sources", action="store_true", help="also download the upstream archives of the copyleft components")
    args = parser.parse_args()
    output: Path = args.output_dir.resolve()
    check_free_space(output)

    print(f"linux-base {IMAGE_VERSION}")
    if not args.skip_docker_build:
        docker_build(output)
    tar_path = docker_export(output)
    print(f"  export: {tar_path.stat().st_size} bytes")
    extract_from_tar(tar_path, output)
    run_tools(output)
    ext4 = output / "rootfs.ext4"
    sizes = {
        **image_sizes(),
        "exportTar": tar_path.stat().st_size,
        "rootfsExt4": ext4.stat().st_size,
        "kernel": (output / "vmlinuz-virt").stat().st_size,
        "initramfs": (output / "initramfs-virt").stat().st_size,
    }
    sources = {
        "image": IMAGE_ID,
        "version": IMAGE_VERSION,
        "debian": {"suite": "trixie", "architecture": "i386", "packages": debian_sources(output),
                   "note": "Sources of the Debian packages: the snapshot.debian.org address of the exact source version "
                           "(the .dsc, .orig.tar and .debian.tar are there); the copyright files are kept in the image "
                           "under /usr/share/doc/<package>/copyright."},
        "alpine": {"branch": ALPINE_BRANCH, "components": alpine_sources(output)},
    }
    if args.fetch_sources:
        sources["fetched"] = fetch_source_archives(sources["alpine"]["components"], output / "sources")
    manifest = pack(output, sizes, sources)
    sizes["pack"] = manifest["size"]
    sizes["packEntries"] = {name: entry["size"] for name, entry in manifest["files"].items()}
    (output / "measurements.json").write_text(json.dumps(sizes, indent=2) + "\n", encoding="utf-8", newline="\n")
    tar_path.unlink()
    print(f"Built {IMAGE_ID} {IMAGE_VERSION}: pack {manifest['size']} bytes in {len(manifest['chunks'])} chunks "
          f"(ext4 {sizes['rootfsExt4']} bytes, export {sizes['exportTar']} bytes) in {output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
