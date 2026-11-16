#!/usr/bin/env python3
"""Builds the wine image of Os-x86 (milestone 0, work item 9; decisions 201 A, 152 A, 204 A, 210 A).

The wine image is linux-base with Wine: the `base` stage of images/linux-base (Debian trixie i386,
the linux-virt kernel of Alpine, the init, the agent, the hardening), the WineHQ packages of Wine
11.0 (pinned), Mono and Gecko as shared installs, a template prefix made at build time, the list of
the hot files of a first launch and the init hook that preloads them. Every step that runs 32-bit
code goes through `docker run` + `docker commit` (the seccomp profile of BuildKit's RUN steps
refuses socket(2) to 32-bit containers on the GitHub runners; images/linux-base explains it):

  0. images/linux-base/build-linux-base.py (imported, not copied) builds the agent and the
     linux-base Docker images; this script starts from osx86-linux-base-full (the `base` stage);
  1. the WineHQ key, Mono and Gecko are downloaded into build/wine/download/ and checked against
     the SHA-256 pinned below (Mono and Gecko are the versions Wine 11.0 asks for: its
     dlls/appwiz.cpl/addons.c pins the SHA-256 of their .msi, recorded below for reference);
  2. install-wine.sh (osx86-wine-install -> osx86-wine-installed): the WineHQ repository, the
     pinned packages, Mono and Gecko unpacked under /usr/share/wine;
  3. osx86-hello.c is cross-compiled with the MinGW-w64 of Debian (osx86-mingw, amd64);
  4. make-prefix.sh (osx86-wine-prefix): wineboot makes the template prefix, duplicates of Wine
     files become links;
  5. trace-hot.sh (throwaway container): strace of a first `wine cmd /c echo` and of the console
     program by a guest user with a copy of the template -> hot-files.list;
  6. slim-wine.sh (osx86-wine-slim -> osx86-wine): purge, Wine development tools out, hot list and
     preload hook in, then images/linux-base/slim.sh;
  7. export, image test of 210 A and ext4 (the tools container of linux-base), sources.json, pack.

Run from the repository root with an isolated interpreter (standard library only; Docker running;
at least 5 GiB free on the build drive; about 2.5 GiB of Docker images are made):

    python -I images/wine/build-wine.py [--output-dir DIR] [--skip-docker-build | --from STEP]
                                         [--fetch-sources]

--fetch-sources also downloads the sources of the copyleft components (Wine, Mono, Gecko and the
Alpine components of linux-base; Mono's alone is 357 MiB) into build/wine/sources/: for the R2
publication, never for GitHub Pages.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import shutil
import subprocess
import sys
import tarfile
import urllib.request
from pathlib import Path

IMAGE_ID = "wine"
IMAGE_VERSION = "0.1.0"
WINE_VERSION = "11.0.0.0~trixie-1"
WINE_UPSTREAM = "11.0"
MONO_VERSION = "10.4.1"
GECKO_VERSION = "2.47.4"
DOCKER_IMAGE = f"osx86-wine:{IMAGE_VERSION}"
INSTALLED_IMAGE = f"osx86-wine-installed:{IMAGE_VERSION}"
PREFIX_IMAGE = f"osx86-wine-prefix:{IMAGE_VERSION}"
MINGW_IMAGE = f"osx86-mingw:{IMAGE_VERSION}"
EXPORT_CONTAINER = "osx86-wine-export"
MEMORY_MIB = 512
TEMPLATE = "usr/lib/osx86/wine/prefix-template"
HOT_LIST = "usr/lib/osx86/wine/hot-files.list"
WINDOWS_USER = "osx86-template"
USER_AGENT = "os-x86-build-wine"
MAX_DOWNLOAD_BYTES = 1024 ** 3

# Everything downloaded is pinned. The key is the WineHQ archive key (fingerprint
# D43F 6401 4536 9C51 D786 DDEA 76F1 A20F F987 672F) that signs the Release file apt checks.
DOWNLOADS = {
    "winehq.key": {
        "url": "https://dl.winehq.org/wine-builds/winehq.key",
        "sha256": "d965d646defe94b3dfba6d5b4406900ac6c81065428bf9d9303ad7a72ee8d1b8",
    },
    f"wine-mono-{MONO_VERSION}-x86.tar.xz": {
        "url": f"https://dl.winehq.org/wine/wine-mono/{MONO_VERSION}/wine-mono-{MONO_VERSION}-x86.tar.xz",
        "sha256": "a16606ef0724202e6a6848ece6e0cbba64d11e2f11aefe744af1d93c6d9f99bb",
    },
    f"wine-gecko-{GECKO_VERSION}-x86.tar.xz": {
        "url": f"https://dl.winehq.org/wine/wine-gecko/{GECKO_VERSION}/wine-gecko-{GECKO_VERSION}-x86.tar.xz",
        "sha256": "2cfc8d5c948602e21eff8a78613e1826f2d033df9672cace87fed56e8310afb6",
    },
}
# The SHA-256 Wine 11.0 itself pins for the .msi of the same releases (dlls/appwiz.cpl/addons.c):
# the .msi published next to the archives were checked against them when the archives were pinned.
WINE_PINNED_MSI = {
    "mono": f"wine-mono-{MONO_VERSION}-x86.msi 071f4b2887e1c97a11d791ff3d65be9429eed6dec4c2708888bfd546ba358e23",
    "gecko": f"wine-gecko-{GECKO_VERSION}-x86.msi 26cecc47706b091908f7f814bddb074c61beb8063318e9efc5a7f789857793d6",
}
WINEHQ_POOL = "https://dl.winehq.org/wine-builds/debian/pool/main/w/wine"
SOURCES_EXTRA = {
    "wine": {
        "license": "LGPL-2.1-or-later",
        "copyleft": True,
        "packages": ["winehq-stable", "wine-stable", "wine-stable-i386"],
        "version": WINE_VERSION,
        "sources": [
            {"name": f"wine_{WINE_VERSION}.dsc", "url": f"{WINEHQ_POOL}/wine_{WINE_VERSION}.dsc"},
            {"name": f"wine_{WINE_VERSION.split('-')[0]}.orig.tar.gz", "url": f"{WINEHQ_POOL}/wine_{WINE_VERSION.split('-')[0]}.orig.tar.gz"},
            {"name": f"wine_{WINE_VERSION}.debian.tar.xz", "url": f"{WINEHQ_POOL}/wine_{WINE_VERSION}.debian.tar.xz"},
            {"name": f"wine-{WINE_UPSTREAM}.tar.xz", "url": f"https://dl.winehq.org/wine/source/{WINE_UPSTREAM}/wine-{WINE_UPSTREAM}.tar.xz"},
        ],
        "note": "The WineHQ source package (.dsc, orig, debian) the binary packages were built from, and the upstream release.",
    },
    "mono": {
        "license": "Mixed: MIT X11 (most), LGPL-2.0 and GPL with exceptions (parts of Mono, the VB compiler, "
                   "SharpZipLib), MS-PL and MIT (FNA), zlib (FAudio, FNA3D, MojoShader, SDL), BSD (jay); "
                   "see COPYING of wine-mono",
        "copyleft": True,
        "version": MONO_VERSION,
        "installedAt": f"/usr/share/wine/mono/wine-mono-{MONO_VERSION}",
        "binary": DOWNLOADS[f"wine-mono-{MONO_VERSION}-x86.tar.xz"],
        "licenseFile": f"https://gitlab.winehq.org/wine-mono/wine-mono/-/raw/wine-mono-{MONO_VERSION}/COPYING",
        "sources": [{"name": f"wine-mono-{MONO_VERSION}-src.tar.xz",
                     "url": f"https://dl.winehq.org/wine/wine-mono/{MONO_VERSION}/wine-mono-{MONO_VERSION}-src.tar.xz"}],
    },
    "gecko": {
        "license": "MPL-2.0 (Mozilla Gecko) with the other licenses listed in toolkit/content/license.html "
                   "of its source; no right to the Mozilla trademarks",
        "copyleft": True,
        "version": GECKO_VERSION,
        "installedAt": f"/usr/share/wine/gecko/wine-gecko-{GECKO_VERSION}-x86",
        "binary": DOWNLOADS[f"wine-gecko-{GECKO_VERSION}-x86.tar.xz"],
        "licenseFile": f"https://gitlab.winehq.org/wine/wine-gecko/-/raw/wine-gecko-{GECKO_VERSION}/LICENSE",
        "sources": [{"name": f"wine-gecko-{GECKO_VERSION}-src.tar.xz",
                     "url": f"https://dl.winehq.org/wine/wine-gecko/{GECKO_VERSION}/wine-gecko-{GECKO_VERSION}-src.tar.xz"}],
    },
}

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[1]
LINUX_BASE_DIR = REPO_ROOT / "images" / "linux-base"
DEFAULT_OUTPUT = REPO_ROOT / "images" / "build" / IMAGE_ID


def load_linux_base():
    """images/linux-base/build-linux-base.py as a module: its agent build, Docker build, export
    helpers, image test, source listing and pack are those of this image too."""
    sys.dont_write_bytecode = True  # no __pycache__ next to the recipe of linux-base
    spec = importlib.util.spec_from_file_location("osx86_build_linux_base", LINUX_BASE_DIR / "build-linux-base.py")
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


lb = load_linux_base()
run = lb.run
volume = lb.docker_volume


def remove_container(name: str) -> None:
    subprocess.run(["docker", "rm", "-f", name], capture_output=True)


def run_and_commit(name: str, image: str, tag: str, mounts: list[tuple[Path, str]], command: list[str], platform: str = "linux/386") -> None:
    """docker run of a named container, then docker commit of it into tag; the container goes."""
    remove_container(name)
    arguments = ["docker", "run", "--platform", platform, "--name", name]
    for source, target in mounts:
        arguments += ["-v", f"{volume(source)}:{target}"]
    try:
        run(arguments + [image] + command)
        run(["docker", "commit", name, tag], capture_output=True)
    finally:
        remove_container(name)


# --- downloads -------------------------------------------------------------------------------------


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def download(url: str, target: Path, expected: str | None, limit: int = MAX_DOWNLOAD_BYTES) -> dict:
    print(f"  download {url}")
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    digest = hashlib.sha256()
    received = 0
    partial = target.with_name(target.name + ".part")
    with urllib.request.urlopen(request, timeout=600) as response, partial.open("wb") as out:
        for block in iter(lambda: response.read(1024 * 1024), b""):
            received += len(block)
            if received > limit:
                raise SystemExit(f"{url}: more than {limit} bytes")
            digest.update(block)
            out.write(block)
    if expected is not None and digest.hexdigest() != expected:
        partial.unlink()
        raise SystemExit(f"{url}: SHA-256 {digest.hexdigest()} differs from the pinned {expected}")
    partial.replace(target)
    return {"file": target.name, "bytes": received, "sha256": digest.hexdigest(), "from": url}


def fetch_downloads(directory: Path) -> None:
    """The pinned downloads, in a directory of their own; a file already there is checked again."""
    directory.mkdir(parents=True, exist_ok=True)
    for partial in directory.glob("*.part"):
        partial.unlink()
    for name in sorted(entry.name for entry in directory.iterdir()):
        if name not in DOWNLOADS:
            raise SystemExit(f"{directory / name}: unexpected file in the download directory")
    for name, pin in DOWNLOADS.items():
        target = directory / name
        if target.exists() and sha256_of(target) == pin["sha256"]:
            continue
        download(pin["url"], target, pin["sha256"])


# --- the Docker steps ------------------------------------------------------------------------------


def build_mingw_program(output: Path) -> None:
    """osx86-hello.exe from its source, with the MinGW-w64 cross compiler of Debian (amd64)."""
    run_and_commit("osx86-mingw-prepare", lb.DEBIAN_IMAGE, MINGW_IMAGE, [], [
        "sh", "-c", "set -eu; apt-get -qq -o Acquire::Retries=5 update; DEBIAN_FRONTEND=noninteractive apt-get -qq -o Acquire::Retries=5 "
                    "install -y --no-install-recommends gcc-mingw-w64-i686-win32 > /dev/null; apt-get clean"], platform="linux/amd64")
    remove_container("osx86-mingw-build")
    run(["docker", "run", "--rm", "--platform", "linux/amd64", "--name", "osx86-mingw-build",
         "-v", f"{volume(HERE)}:/recipe:ro", "-v", f"{volume(output)}:/out", MINGW_IMAGE,
         "i686-w64-mingw32-gcc", "-Os", "-s", "-o", "/out/osx86-hello.exe", "/recipe/osx86-hello.c"])


def docker_build(output: Path, agent: dict, start: str) -> dict:
    """Steps 0 and 2 to 6, from `start` on (install: everything; prefix: the osx86-wine-installed
    image of an earlier run is reused; slim: the osx86-wine-prefix image and the hot list too);
    returns what the trace of step 5 measured."""
    if start != "slim":
        build_until_trace(output, agent, start)
    run_and_commit("osx86-wine-slim", PREFIX_IMAGE, DOCKER_IMAGE,
                   [(HERE, "/recipe:ro"), (LINUX_BASE_DIR, "/linux-base:ro"), (output, "/out:ro")],
                   ["sh", "/recipe/slim-wine.sh", IMAGE_VERSION])
    return read_trace(output)


def build_until_trace(output: Path, agent: dict, start: str) -> None:
    if start == "install":
        lb.docker_build(output, agent)
        downloads = output / "download"
        run_and_commit("osx86-wine-install", lb.DOCKER_IMAGE_FULL, INSTALLED_IMAGE,
                       [(HERE, "/recipe:ro"), (downloads, "/download:ro")],
                       ["sh", "/recipe/install-wine.sh", WINE_VERSION, MONO_VERSION, GECKO_VERSION])
        build_mingw_program(output)
    run_and_commit("osx86-wine-prefix", INSTALLED_IMAGE, PREFIX_IMAGE, [(HERE, "/recipe:ro"), (output, "/out:ro")], [
        "sh", "-c", "set -eu; sh /recipe/make-prefix.sh; "
                    "install -m 755 /recipe/prefix-copy.sh /usr/lib/osx86/wine/prefix-copy; "
                    "install -m 644 /out/osx86-hello.exe /usr/lib/osx86/wine/osx86-hello.exe"])
    remove_container("osx86-wine-trace")
    run(["docker", "run", "--rm", "--platform", "linux/386", "--name", "osx86-wine-trace",
         "-v", f"{volume(HERE)}:/recipe:ro", "-v", f"{volume(output)}:/out", PREFIX_IMAGE, "sh", "/recipe/trace-hot.sh"])


def read_trace(output: Path) -> dict:
    values = {}
    for line in (output / "hot-trace.txt").read_text(encoding="utf-8").splitlines():
        key, _, value = line.partition(" ")
        values[key] = int(value)
    return values


def docker_export(output: Path) -> Path:
    tar_path = output / "rootfs.tar"
    remove_container(EXPORT_CONTAINER)
    run(["docker", "create", "--platform", "linux/386", "--name", EXPORT_CONTAINER, DOCKER_IMAGE], capture_output=True)
    try:
        run(["docker", "export", "-o", str(tar_path), EXPORT_CONTAINER])
    finally:
        remove_container(EXPORT_CONTAINER)
    return tar_path


# --- checks on the export --------------------------------------------------------------------------


def inspect_export(tar_path: Path) -> dict:
    """What the export must hold for Wine to start without a wineboot in the VM, and the sizes of
    the parts of the image (bytes of regular files)."""
    parts = {"opt/wine-stable/": 0, "usr/share/wine/mono/": 0, "usr/share/wine/gecko/": 0, TEMPLATE + "/": 0}
    total = 0
    regular: dict[str, int] = {}
    links: dict[str, str] = {}
    timestamp = None
    inf_mtime = None
    hot_list = None
    with tarfile.open(tar_path, "r:") as archive:
        for member in archive:
            name = member.name[2:] if member.name.startswith("./") else member.name
            if member.issym():
                links[name] = member.linkname
            if not member.isfile():
                continue
            regular[name] = member.size
            total += member.size
            for prefix in parts:
                if name.startswith(prefix):
                    parts[prefix] += member.size
            if name == TEMPLATE + "/.update-timestamp":
                timestamp = int(archive.extractfile(member).read().decode().strip())  # type: ignore[union-attr]
            elif name == "opt/wine-stable/share/wine/wine.inf":
                inf_mtime = int(member.mtime)
            elif name == HOT_LIST:
                hot_list = archive.extractfile(member).read().decode().split()  # type: ignore[union-attr]
    if timestamp is None or timestamp != inf_mtime:
        raise SystemExit(f"{TEMPLATE}/.update-timestamp ({timestamp}) differs from the mtime of wine.inf ({inf_mtime}): "
                         "Wine would run wineboot -u at the first start in the VM")
    if not hot_list:
        raise SystemExit(f"/{HOT_LIST} is missing or empty")
    missing = [path for path in hot_list if path.lstrip("/") not in regular]
    if missing:
        raise SystemExit(f"{len(missing)} hot files are not regular files of the image, e.g. {missing[:3]}")
    # The links that replace duplicates must reach a file of the image (dosdevices/ holds Wine's
    # drive links, c: -> ../drive_c and z: -> /, which are directories).
    dangling = [name for name, target in links.items()
                if name.startswith(TEMPLATE + "/") and not name.startswith(TEMPLATE + "/dosdevices/")
                and target.lstrip("/") not in regular]
    if dangling:
        raise SystemExit(f"{len(dangling)} links of the template point nowhere, e.g. {dangling[:3]}")
    return {
        "regularBytes": total,
        "wineBytes": parts["opt/wine-stable/"],
        "monoBytes": parts["usr/share/wine/mono/"],
        "geckoBytes": parts["usr/share/wine/gecko/"],
        "templateBytes": parts[TEMPLATE + "/"],
        "templateLinks": sum(1 for name in links if name.startswith(TEMPLATE + "/")),
        "hotFiles": len(hot_list),
        "hotBytes": sum(regular[path.lstrip("/")] for path in hot_list),
    }


# --- sources (plan section 12) -----------------------------------------------------------------------


def wine_sources(output: Path, agent: dict) -> dict:
    debian = lb.debian_sources(output)
    for package in debian:
        if package["source"] == "wine" and package["package"].split(":")[0] in SOURCES_EXTRA["wine"]["packages"]:
            package["snapshot"] = None
            package["winehq"] = f"{WINEHQ_POOL}/"
    return {
        "image": IMAGE_ID,
        "version": IMAGE_VERSION,
        "debian": {"suite": "trixie", "architecture": "i386", "packages": debian,
                   "note": "Sources of the Debian packages: the snapshot.debian.org address of the exact source version; "
                           "the WineHQ packages: see `wine`. The copyright files are kept in the image under "
                           "/usr/share/doc/<package>/copyright."},
        "alpine": {"branch": lb.ALPINE_BRANCH, "components": lb.alpine_sources(output)},
        "wine": SOURCES_EXTRA["wine"],
        "mono": {**SOURCES_EXTRA["mono"], "msiPinnedByWine": WINE_PINNED_MSI["mono"]},
        "gecko": {**SOURCES_EXTRA["gecko"], "msiPinnedByWine": WINE_PINNED_MSI["gecko"]},
        "osx86Hello": {"source": "images/wine/osx86-hello.c of the Os-x86 repository (the repository's license)",
                       "compiler": "gcc-mingw-w64-i686-win32 of Debian trixie (amd64)"},
        "agent": {"version": agent["agent"], "source": "images/agent/src of the Os-x86 repository (the repository's license)",
                  "linkedStatically": "musl (MIT, Alpine aports main/musl)", "toolchain": agent["toolchain"],
                  "sourcesSha256": agent["sources"]},
    }


def fetch_sources(sources: dict, directory: Path) -> list[dict]:
    """The source archives of the copyleft components: Alpine (linux-base's function), then Wine,
    Mono and Gecko (no pinned digest exists for them: their SHA-256 is recorded)."""
    fetched = lb.fetch_source_archives(sources["alpine"]["components"], directory)
    for component in ("wine", "mono", "gecko"):
        for source in sources[component]["sources"]:
            fetched.append(download(source["url"], directory / Path(source["name"]).name, None))
    return fetched


# --- pack ----------------------------------------------------------------------------------------------


def pack(output: Path, sizes: dict, sources: dict, agent: dict) -> dict:
    pack_dir = output / "pack"
    pack_dir.mkdir(exist_ok=True)
    boot = {
        "kind": "linux",
        "cmdline": lb.KERNEL_CMDLINE,
        "console": "serial0",
        "disks": {"hda": "rootfs"},
        "readyMarker": "OSX86-READY",
        "prompt": "osx86# ",
        "memoryMiB": MEMORY_MIB,
        "agent": {"path": "/" + lb.AGENT_IN_IMAGE, "port": "virtio0", "consolePort": "virtio1", "protocol": agent["protocol"]},
        "wine": {"version": WINE_UPSTREAM, "prefixTemplate": "/" + TEMPLATE, "prefixCopy": "/usr/lib/osx86/wine/prefix-copy",
                 "templateWindowsUser": WINDOWS_USER, "hotFiles": "/" + HOT_LIST, "preloadOff": "osx86.preload=off"},
    }
    build = {
        "recipe": "images/wine/build-wine.py",
        "debian": "trixie-slim i386",
        "wine": WINE_VERSION,
        "mono": MONO_VERSION,
        "gecko": GECKO_VERSION,
        "kernelRelease": (output / "kernel.release").read_text(encoding="utf-8").strip(),
        "agent": {"version": agent["agent"], "protocol": agent["protocol"], "sha256": agent["files"]["agent"]["sha256"]},
        "sizes": sizes,
    }
    for name, value in (("boot.json", boot), ("build.json", build), ("sources.json", sources)):
        (pack_dir / name).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8", newline="\n")
    for name in ("packages.tsv", "licenses.tsv", "image-check.json", "hot-files.list"):
        shutil.copyfile(output / name, pack_dir / name)
    run([sys.executable, "-I", str(lb.PACK_TOOL), "pack", "--image", IMAGE_ID, "--version", IMAGE_VERSION,
         "--entry", f"kernel={output / 'vmlinuz-virt'}", "--entry", f"initramfs={output / 'initramfs-virt'}",
         "--entry", f"rootfs={output / 'rootfs.ext4'}",
         "--boot-json", str(pack_dir / "boot.json"), "--build-json", str(pack_dir / "build.json"), "--output", str(pack_dir)])
    run([sys.executable, "-I", str(lb.PACK_TOOL), "check", str(pack_dir)])
    return json.loads((pack_dir / "manifest.json").read_text(encoding="utf-8"))


def image_size(tag: str) -> int | None:
    result = subprocess.run(["docker", "image", "inspect", "--format", "{{.Size}}", tag], capture_output=True, text=True)
    return int(result.stdout.strip()) if result.returncode == 0 and result.stdout.strip().isdigit() else None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT, help="build directory (default images/build/wine, git-ignored)")
    parser.add_argument("--skip-docker-build", action="store_true", help="reuse the osx86-wine image already built")
    parser.add_argument("--from", dest="start", choices=("install", "prefix", "slim"), default="install",
                        help="first Docker step to run (prefix and slim reuse the images of an earlier run)")
    parser.add_argument("--fetch-sources", action="store_true", help="also download the sources of the copyleft components")
    args = parser.parse_args()
    output: Path = args.output_dir.resolve()
    lb.check_free_space(output)

    print(f"wine {IMAGE_VERSION} (Wine {WINE_VERSION}, Mono {MONO_VERSION}, Gecko {GECKO_VERSION})")
    agent = lb.build_agent(rebuild=not args.skip_docker_build)
    fetch_downloads(output / "download")
    if args.skip_docker_build:
        trace = read_trace(output)
    else:
        trace = docker_build(output, agent, args.start)
    tar_path = docker_export(output)
    print(f"  export: {tar_path.stat().st_size} bytes")
    lb.check_agent_in_export(tar_path, agent)
    lb.extract_from_tar(tar_path, output)
    contents = inspect_export(tar_path)
    with tarfile.open(tar_path, "r:") as archive:
        member = archive.getmember("./" + HOT_LIST) if "./" + HOT_LIST in archive.getnames() else archive.getmember(HOT_LIST)
        (output / "hot-files.list").write_bytes(archive.extractfile(member).read())  # type: ignore[union-attr]
    lb.run_tools(output)
    sizes = {
        "dockerWine": image_size(DOCKER_IMAGE),
        "exportTar": tar_path.stat().st_size,
        "rootfsExt4": (output / "rootfs.ext4").stat().st_size,
        "kernel": (output / "vmlinuz-virt").stat().st_size,
        "initramfs": (output / "initramfs-virt").stat().st_size,
        **contents,
        "trace": trace,
    }
    sources = wine_sources(output, agent)
    if args.fetch_sources:
        sources["fetched"] = fetch_sources(sources, output / "sources")
    manifest = pack(output, sizes, sources, agent)
    sizes["pack"] = manifest["size"]
    sizes["packEntries"] = {name: entry["size"] for name, entry in manifest["files"].items()}
    (output / "measurements.json").write_text(json.dumps(sizes, indent=2) + "\n", encoding="utf-8", newline="\n")
    tar_path.unlink()
    print(f"Built {IMAGE_ID} {IMAGE_VERSION}: pack {manifest['size']} bytes in {len(manifest['chunks'])} chunks "
          f"(ext4 {sizes['rootfsExt4']} bytes; Wine {contents['wineBytes']}, Mono {contents['monoBytes']}, "
          f"Gecko {contents['geckoBytes']}, template {contents['templateBytes']} bytes; "
          f"{contents['hotFiles']} hot files, {contents['hotBytes']} bytes) in {output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
