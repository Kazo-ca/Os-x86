> [Version française](../fr/images.md)

# The guest images

Milestone 0, work item 4 (`j0/4-images`). An image is what the virtual machine boots: a kernel,
an initramfs and a root for Linux, a disk for DOS. This work item ships the recipes that build
them, the tool that puts them in the published form (32 MiB chunks and a manifest, read by the
storage of work item 3), the image test of decision 210 A, the collection of sources and the
publication by the CI. No image or archive is committed (`images/.gitignore`): everything is
rebuilt from official sources pinned by their SHA-256.

## Where things are

| Path | Content |
| --- | --- |
| `images/tools/pack-image.py` | the packing tool: `pack`, `check` (or `--check`), `extract`, `info` |
| `images/tools/image-check.py` | the image test of 210 A: writable inventory and the 22 R134.5 checks |
| `images/tools/publish-layout.py` | the publication layout (`images/<image>/<version>/`, `index.json`, refusal above 100 MiB for Pages) |
| `images/tools/Dockerfile` | the `osx86-tools` container (e2fsprogs, mtools, python3) the recipes use |
| `images/linux-base/` | the base Linux recipe (201 A): `Dockerfile`, `init.sh`, `verify-console.sh`, `modules.keep`, `osx86.modules`, `probes/`, `build-linux-base.py` |
| `images/linux-base/slim.sh` | the slim step shared by linux-base and the wine image (package and license inventory included) |
| `images/wine/` | the recipe of the wine image (work item 9): `build-wine.py`, `install-wine.sh`, `winehq-trixie.sources`, `make-prefix.sh`, `prefix-copy.sh`, `trace-hot.sh`, `preload.sh`, `slim-wine.sh`, `osx86-hello.c` |
| `images/alpine-test/` | the Alpine test image of work item 2, now packed; it carries the agent of work item 5 |
| `images/agent/` | the guest agent (work item 5): C sources, `Dockerfile`, `build-agent.py`; see [`agent.md`](agent.md) |
| `images/freedos/` | the FreeDOS recipe: `build-freedos.py`, `FDAUTO.BAT` |
| `images/build/<image>/` | the output (git-ignored); `pack/` is the published form |
| `tests/OsX86.Tests/Images/` | `unit` tests of the tools and the `guest` test of the Alpine pack |
| `.github/workflows/images.yml` | CI build, artifacts, Pages and R2 publication |

```bash
python -I images/agent/build-agent.py                 # the guest agent (Docker linux/386), see agent.md
python -I images/alpine-test/build-alpine-test.py     # official ISO; carries the agent when it is built
python -I images/freedos/build-freedos.py             # official archive, mtools or osx86-tools
python -I images/linux-base/build-linux-base.py       # Docker (linux/386), 5 GiB free required
python -I images/wine/build-wine.py                   # starts from linux-base; about 2.5 GB of Docker images
python -I images/tools/pack-image.py check images/build/linux-base/pack
```

Python 3.14 (`compression.zstd` module) or the `zstandard` package; Docker Desktop or dockerd
for linux-base. Every Docker image and container created is named `osx86-*`; nothing else is
touched.

## The format: a raw ext4 disk, not a 9p tree

The plan spoke of a 9p root (Docker → `fs.json` + `.zst` files, the recipe of trials E17 and E7).
This work item chooses **a raw ext4 disk attached as `hda`** (the IDE disk of v86), for three
measured reasons:

| Fact | Measure | Source |
| --- | --- | --- |
| v86 has one virtio-9p device: a 9p root forbids any second mount | "no channels available" on the second mount | E8 |
| Re-reading an installed program costs 3.5 to 9 times more on 9p than on ext4 | 3,000 files re-read: 15.7 s on 9p, 1.7 s on ext4; `stat` of the 866 Wine files: 9.5 to 12.8 s | E8 |
| The 9p transport is capped and pays per message | 8 to 11 MB/s, ≈ 4 KiB per `Tread`; the 9p root is ≈ 14 s of a 49 s boot | E8, E17 |

The Kazo shared folder (D22, E8) therefore has the 9p device to itself; the root is mounted
read-only (`root=/dev/sda rootfstype=ext4 ro`), the writable places are tmpfs mounted by the init
(`/run`, `/tmp`, `/var/tmp`, `/var/log`); since work item 10, `/home` (the prefixes) is on the VM's
disk, the only writable place of the root ([`programs.md`](programs.md#where-prefixes-live-in-the-guest-choice)). Cost: the decompressed root is one ext4 file (138 MiB for linux-base) rather than files
deduplicated by hash; the block compression below makes up for it, since the OPFS keeps the
compressed bytes.

**linux-base boots from the OPFS since work item 6a**: the worker reads the kernel, the initramfs
and the `hda` root from `image.bin` and decompresses frames on demand (see
[`engine.md`](engine.md), "Disks and the boot described by the manifest"); `OSX86-READY` in 19 to
27 s, the agent answers, and `verify-console.sh` holds its 22 checks in the VM.

### Two defects found by booting the image (work item 6a)

- **Line endings.** A Git checkout on Windows wrote `osx86.modules` and `modules.keep` in CRLF;
  `mkinitfs` then read `sd_mod.ko*\r` and found nothing: the initramfs had neither `sd_mod` nor
  `ext4`, and the root did not mount. The CI builds on Linux and was not affected; the 70.67 MB
  pack measured in work item 5 was that broken build. `.gitattributes` now forces LF on all of
  `images/**`.
- **Modes of `/dev`.** The `mdev` of the Alpine initramfs leaves every node of `/dev` in 0660 and
  linux-base has no udev: no user could open `/dev/null`, which also silently broke the commands
  of `verify-console.sh` written with `2>/dev/null`. `init.sh` sets `/dev/null`, `zero`, `full`,
  `random`, `urandom`, `tty` and `ptmx` back to 0666. `verify-console.sh` counts a process hidden
  by `hidepid` as protected and takes the agent's pid as its third argument (a user cannot see it
  under `hidepid=invisible`).

## The published form and the manifest

`pack-image.py` serialises the entries (kernel, initramfs, root or disk) into one blob, cuts it
into 32 MiB chunks `chunk-00000.bin`, `chunk-00001.bin`, … (five digits, the last one shorter)
and writes `manifest.json` and `manifest.sha256` (the SHA-256 of the manifest, published next to
it).

Each entry is a **zstd "seekable" stream** (the `contrib/seekable_format` of zstd): independent
frames of 1 MiB decompressed (`blockSize`), followed by a seek table in a skippable frame (per
frame: compressed and decompressed size, u32; a 9-byte footer with the frame count and the magic
number `0x8F92EAB1`). A plain `zstd -d` decompresses the whole entry; the engine reads the table
from the end of the entry and decompresses only the frames a sector read touches (the wasm zstd
of v86 does that; a cache of decompressed frames is enough). The OPFS keeps the chunks as they
are: the downloader of work item 3 needs no zstd.

```json
{
  "format": 1,
  "image": "linux-base",
  "version": "0.1.0",
  "engine": {"v86": "0.5.473"},
  "size": 73045750,
  "chunkSize": 33554432,
  "chunks": [{"sha256": "…"}, {"sha256": "…"}, {"sha256": "…"}],
  "files": {
    "kernel":    {"offset": 0,        "size": 7978719,  "rawSize": 8106496,   "blockSize": 1048576, "sha256": "…"},
    "initramfs": {"offset": 7978719,  "size": 5052944,  "rawSize": 5062322,   "blockSize": 1048576, "sha256": "…"},
    "rootfs":    {"offset": 13031663, "size": 60014087, "rawSize": 138412032, "blockSize": 1048576, "sha256": "…"}
  },
  "boot":  {"kind": "linux", "cmdline": "root=/dev/sda rootfstype=ext4 ro console=ttyS0 …", "console": "serial0",
            "disks": {"hda": "rootfs"}, "readyMarker": "OSX86-READY", "prompt": "osx86# ", "memoryMiB": 256,
            "agent": {"path": "/usr/lib/osx86/agent", "port": "virtio0", "consolePort": "virtio1", "protocol": 1}},
  "build": {"recipe": "…", "sizes": {…}}
}
```

- Chunk *i* is the range `[i × chunkSize, min((i + 1) × chunkSize, size))` of the blob; `size` is
  the sum of the chunks; the entries follow each other without a gap and cover the blob.
- `files` carries the names `kernel`, `initramfs`, `rootfs` (Linux) or `disk` (DOS). `offset` and
  `size` are in the stored blob; `rawSize` is the decompressed size; `sha256` that of the
  decompressed content.
- `boot` and `build` are optional, for the engine and for provenance; a reader ignores the keys
  it does not know. The schema is authoritative for work item 3 and does not change without
  agreement.
- `check` recomputes everything: size and SHA-256 of every chunk, the table of every entry,
  decompression and SHA-256 of the content, `manifest.sha256`.

## The recipes

### linux-base (201 A)

Four Docker stages, all `linux/386`, plus the image of the agent (`images/agent`, built first by
`build-linux-base.py`) from which the `base` stage copies the two binaries:

1. **kernel**: `alpine:3.24`, `apk fetch linux-virt busybox musl` (pinned file names, SHA-256
   kept in `/usr/lib/osx86/provenance`), the `linux-virt` 6.18.63-r0 kernel unpacked from the
   package, the official Alpine initramfs rebuilt with `mkinitfs -F "base osx86"` (the
   `osx86.modules` feature adds `sd_mod`, the ATA drivers and ext4: `ata_piix` is built into the
   kernel, `sd_mod` and `ext4` are modules), and the modules of `modules.keep` with their
   dependencies (56 files out of 868, decompressed because the `kmod` of Debian does not read
   `.ko.gz`, trial E17).
2. **probes**: two static C programs of the R134.5 checks (`osx86-rawsock`, `osx86-ptrace`),
   since the image has no compiler.
3. **base**: `debian:trixie-slim` i386 plus `kmod` and `procps` (installed with `docker run` then
   `docker commit` by the script, like `gcc` for the `probes` stage: no `apt-get` in a build step,
   see "Publication"), the kernel, the initramfs and
   the modules, `init.sh` as `/sbin/init`, `verify-console.sh`, the agent of work item 5
   (`/usr/lib/osx86/agent`, root, 0755, started by the init; `/usr/bin/osx86-open-url`), the
   hardening. Work item 9 (Wine) builds on this stage.
4. **slim**: `apt`, `perl-base` and their libraries purged, manual pages, locales, `gconv`
   converters (except Unicode, CP437, CP1252, Latin-1), time zones (except UTC) and the dpkg
   database removed; the `copyright` files stay. The package inventory (`packages.tsv`,
   `licenses.tsv`) is written at this stage, after the purge.

`build-linux-base.py` builds and checks the agent, exports the container (`docker export`),
checks that the export carries exactly that binary (SHA-256, root, 0755), runs in `osx86-tools` the
extraction with owners and modes, the image test, then `mkfs.ext4 -d` (with an 8 MiB journal and
256 MiB of free room for the prefixes since work item 10, fixed UUID and seed, `SOURCE_DATE_EPOCH`), writes `sources.json`, packs and measures.

**Init** (`init.sh`, PID 1 after the initramfs): mounts (`/proc` with `hidepid`, devtmpfs `/dev`,
`/dev/pts`, `/dev/shm`, `/run` as `mode=0755`, `/tmp` and `/var/tmp` as 1777 `nosuid`,
`/var/log`, `/home` `nosuid,nodev`, cgroup v2 for 206 A), `chmod 755 /`, ports `ttyS0` and
`hvc0` to `hvc3` as 0600, `/dev/fb0` and `/dev/input/*` as 0600, sysctls
(`user.max_user_namespaces=0`, `kernel.yama.ptrace_scope=1`, `dmesg_restrict`, `kptr_restrict`,
protected links), the `OSX86-GUEST …` then `OSX86-READY` markers on the serial console, the
agent when present, and a root shell on `ttyS0` (prompt `osx86# `, the fallback of 212 A).

**Hardening in the recipe (210 A)**: `/` as 0755, no setuid or setgid program (bits removed from
`su`, `mount`, `passwd`…: the VM is driven by the agent as root), `/root` as 0700, `HOME_MODE` and
`DIR_MODE` at 0700 for the prefix users, `/run/lock` removed (the image test had found it 1777).

### wine (work item 9; 201 A, 152 A, 204 A)

The wine image is linux-base plus Wine: it starts from the `base` stage of linux-base (Docker image
`osx86-linux-base-full`, built by `build-linux-base.py`, which `build-wine.py` imports rather than
copies) and ends with the same slim step (`images/linux-base/slim.sh`, shared). Every step that
runs 32-bit code goes through `docker run` then `docker commit` (the seccomp profile of BuildKit's
`RUN` steps refuses `socket(2)` to 32-bit containers on the GitHub runners; `wineserver` needs a
socket too); images and containers are named `osx86-wine-*` and `osx86-mingw`.

1. **Pinned downloads** into `images/build/wine/download/` (a directory of their own): the key of
   the WineHQ repository (`winehq.key`, SHA-256 pinned; fingerprint `D43F 6401 4536 9C51 D786
   DDEA 76F1 A20F F987 672F`), `wine-mono-10.4.1-x86.tar.xz` and `wine-gecko-2.47.4-x86.tar.xz`
   (SHA-256 pinned). Mono 10.4.1 and Gecko 2.47.4 are the versions Wine 11.0 asks for: its
   `dlls/appwiz.cpl/addons.c` pins the SHA-256 of their `.msi`, and the `.msi` published next to
   the archives were checked against those values when the archives were pinned.
2. **`install-wine.sh`** (`osx86-wine-installed`): `ca-certificates`, the key and
   `winehq-trixie.sources` (repository `https://dl.winehq.org/wine-builds/debian`, suite `trixie`,
   i386 only, `Signed-By` the key), then `winehq-stable`, `wine-stable` and `wine-stable-i386`
   **= 11.0.0.0~trixie-1** (checked afterwards), plus `libfreetype6`, `libfontconfig1` and
   `libgnutls30t64` (gdi32 fonts, bcrypt and TLS); Mono and Gecko are **unpacked once** under
   `/usr/share/wine/mono/wine-mono-10.4.1` and `/usr/share/wine/gecko/wine-gecko-2.47.4-x86`,
   where Wine looks before offering a download: a shared install, never copied into a prefix. The
   WineHQ `.deb` files are not pinned one by one: apt checks them against the `Release` file signed
   by the pinned key, and the version is forced (like linux-base's Debian packages).
3. **`osx86-hello.c`** compiled by Debian's MinGW-w64 (`gcc-mingw-w64-i686-win32`, amd64 container
   `osx86-mingw`): the console program of the test (it prints its arguments, the Windows directory
   and the user, and exits with code 7); no third-party binary.
4. **`make-prefix.sh`** (`osx86-wine-prefix`): the **template prefix** (152 A), see below.
5. **`trace-hot.sh`** (throwaway container): the list of the **hot files** (204 A), see below.
6. **`slim-wine.sh`** (`osx86-wine`): slimming, the pieces of the image, then linux-base's
   `slim.sh` (package and license inventory, `apt` and `perl-base` purged, man pages, locales…).
7. Export, image test of 210 A and ext4 in `osx86-tools` (linux-base's functions), Wine-specific
   checks of the export, `sources.json`, pack.

`--from prefix` or `--from slim` starts again at step 4 or 6 on the Docker images of an earlier
build (tuning); `--skip-docker-build` reuses `osx86-wine` as it is.

**Slimming.** `wine-stable-i386` hard-depends on GStreamer, PulseAudio, SANE, libgphoto2, OpenCL,
libpcap, Wayland and ALSA-plugins: 201 packages and 1 GiB installed by apt. Those only serve Wine
modules a console program never opens (`winegstreamer`, `winepulse`, `sane`, `gphoto2`, `opencl`,
`wpcap`, `winewayland`): they are purged, with everything no longer in the dependency closure of
what stays (computed by `apt-cache depends --recurse`: linux-base's base, Wine, its other hard
dependencies and the three chosen libraries). Those modules then fail to load, as Wine does on a
system without those libraries. Wine's development tools (`winegcc`, `winebuild`, `widl`, `wrc`,
`wmc`, `winedump`, `winemaker`…) and its `.a` import libraries go too, as do `ca-certificates`,
`xz-utils`, the apt key and source. The setuid and setgid bits of the added packages are cleared
(210 A). What stays: the X libraries (`libx11-6`, `libxext6`) for the graphical milestones,
`libasound2`, `libudev1` and `libusb` (small).

**The template prefix (152 A).** `wineboot -u` runs in the container, natively, as root, with
`WINEPREFIX=/usr/lib/osx86/wine/prefix-template`, `HOME=/root` (no XDG folder: the profile
folders are real directories) and the Windows user **`osx86-template`**; never at the user's.
Then:

- every file of the template whose bytes are those of a file of Wine, Mono or Gecko (the builtin
  DLLs `wineboot` copies into `system32`, the NLS files, the fonts…) becomes a **symbolic link**
  to that file: the template keeps only what `wineboot` really wrote (registry, a few stubs); Wine
  reads only the header of a builtin DLL of `system32` before loading its own, and a program
  that would overwrite one gets `EACCES` (it belongs to the image);
- `.update-timestamp` must equal the modification time of `/opt/wine-stable/share/wine/wine.inf`
  (Wine compares them at every start and runs a full `wineboot -u` when they differ; `wineboot`
  writes the number followed by CR LF): checked in the container, in the export, **in the ext4**
  (`debugfs`, added to the `osx86-tools` container) and in the VM by the `guest` test. First try in
  the VM: `mkfs.ext4` with `SOURCE_DATE_EPOCH=1` (linux-base's setting for a reproducible image)
  brought every date back to 1970, Wine saw a "changed" `wine.inf` and remade the prefix at the
  first launch (`rundll32 setupapi,InstallHinfSection DefaultInstall … wine.inf`, minutes). The
  wine image therefore gives `mkfs.ext4` the date of `wine.inf` as `SOURCE_DATE_EPOCH`: later dates
  are brought back to it, that of `wine.inf` and earlier ones stay; the value changes with every
  Wine version, which keeps the detection of a Wine update;
- Mono is registered as a shared install (support package only); Gecko is found on demand in
  `/usr/share/wine/gecko`.
- `winemenubuilder` is disabled in the registry of the template (`DllOverrides`): the trace of the
  first launch showed it writing `.desktop` files, MIME types and icons into `~/.local` at every
  session start, for a desktop the VM does not have (BoxedWine reads shortcuts itself, BwLib); its `RunServices` value is removed (otherwise `wineboot` tries to
  start it at every session).

**Per-prefix copy.** `/usr/lib/osx86/wine/prefix-copy <user> [<destination>]` (root) copies the
template for the guest user `osx86-<prefix>` (by default into `$HOME/.wine`, which must not
exist): `cp -a` (links stay links: a few MiB written), renaming of `drive_c/users/osx86-template`
and of the user in `system.reg`, `user.reg` and `userdef.reg` (Wine takes the Windows user name
from `$USER`; without the renaming, `%TEMP%` and the profile folders would point to
`osx86-template`), then `chown` to the user (Wine refuses a prefix that is not the user's). Since
work item 10, `/usr/lib/osx86/prefix.d/50-wine` calls it when each prefix is made (`prefix-admin`,
[`programs.md`](programs.md)), and `/usr/lib/osx86/wine/prefix-session` starts the Wine session of
a prefix (`wineserver` and services in a launch of their own). After a change of Wine version in the image, a prefix
copied from the old template keeps links to files of the new version: to handle with the
migration of prefixes.

**Hot files and preloading (204 A).** `trace-hot.sh` gives a copy of the template to a user
`osx86-trace`, then traces with `strace` (installed in that throwaway container only) what a first
session does: `wine cmd /c echo`, then `osx86-hello.exe`. Every regular file opened or executed
with success is kept, in the order of its first use, those of the copy mapped back to the
template and links resolved; the list is filtered after the slimming and stored as
`/usr/lib/osx86/wine/hot-files.list` (also published with the pack). The hook
`/usr/lib/osx86/init.d/50-preload-wine`, run by the init before `OSX86-READY`, reads those files
in the background, at `nice 10`, and prints `OSX86-PRELOAD done files=… start=… end=…` on the
serial console: the guest keeps them in its page cache, and the first `wine` reads memory instead
of frames decompressed one at a time. `osx86.preload=off` on the kernel command line turns it off
(to measure without). linux-base's init now runs, in order, the executables of
`/usr/lib/osx86/init.d/` (with the kernel command line as arguments) and reads the image name from
`/etc/osx86-image`.

**Manifest.** `boot.memoryMiB` is 512 (E17: the guest uses 142 MiB with three Wine windows) and
`boot.wine` gives `prefixTemplate`, `prefixCopy`, `templateWindowsUser`, `hotFiles` and
`preloadOff`; `build` carries the Wine, Mono and Gecko versions and the measurements.

### alpine-test

Official ISO 3.22.8, home-made ISO 9660 and cpio readers, `init.sh` as `/init`;
`build-alpine-test.py` calls `pack-image.py` and writes
`images/build/alpine-test/pack/` (entries `kernel` and `initramfs`, `boot.json`, `sources.json`).
The `manifest.json` of `images/build/alpine-test/` stays the build description the test page of
work item 2 reads.

Since work item 5, the recipe adds to the initramfs the agent of `images/build/agent/` when it
is built (the same static binary as in linux-base; `--require-agent` fails when it is missing,
otherwise the image is made without it), `/usr/bin/osx86-open-url` and two prefix users
(`osx86-test`, `osx86-other`). The init mounts cgroup v2 and `/tmp`, makes `/` 0755 (the root of
the initramfs is 0700), closes `ttyS0` and `hvc0` to `hvc3` (0600), creates the homes as 0700
and starts the agent on `hvc0`; `hvc2` and `hvc3` stay free for the engine tests. This is the
image of the agent's `guest` test of work item 5; linux-base has its own since work item 6a.

### freedos (214 A, E14)

`FD14-LiteUSB.zip` of FreeDOS 1.4 (pinned SHA-256, the one of `verify.txt`), `FD14LITE.img`
(32 MiB, an MBR and one FAT16 partition) modified with mtools, never booted: `FDAUTO.BAT`
replaced (text prompt instead of the installer, `FDAPM APMDOS` for idling, `OSX86-READY` on
screen), `SETUP.BAT` removed. Entry `disk`, `boot.kind` = `dos`, VGA text display read every
33 ms (214 A). Native mtools when on the PATH, else the `osx86-tools` container.

## The image test (210 A)

`image-check.py` reads the exported tree (a directory extracted as root, or a `docker export`
tar) and the init, and prints a report (`image-check.json`, published with the pack); it fails
when a check breaks. It applies:

- the 210 A inventory: world-writable directories (only `/tmp` and `/var/tmp`, sticky), `/` as
  0755, `/run` mounted `mode=0755`, `/dev/fb0` and `/dev/input/*` as 0600 in the init;
- the **22 R134.5 checks of E7**, in their static counterpart: the 22 were measured from a user
  console inside the VM (`verify-console.sh`, kept in the image under `/usr/lib/osx86/` with its
  two probes for the `guest` tests of the next work items); here each check looks at what
  guarantees it in the tree and the init: namespaces (`user.max_user_namespaces=0`, no setuid
  `unshare`), another prefix (`/root` and `/home/*` as 0700, `HOME_MODE`, sticky `/tmp`), ports
  (`ttyS0`, `hvc0` to `hvc3` as 0600), raw socket, `chown`, `kill`, `nsenter` (no setuid, no
  file capability), `nosuid` on `/home` and `/tmp`, `ptrace` (`yama` at 1), `/proc` with
  `hidepid`, agent started as root, cgroup2 mounted `nosuid,nodev,noexec` and never `chown`ed;
- `agent_files_root_only` (work item 5): when the image carries the agent, `/usr/lib/osx86/agent`
  and `/usr/bin/osx86-open-url` are executable files of root that nobody else can write; an image
  without the agent breaks nothing (212 A). The inventory lists them (`inventory.agent`).

The `unit` tests of `ImageCheckTests` build a synthetic tar with the real `init.sh` and break it
seven ways (setuid, writable `/run/lock`, an init without sysctls or mount options, an open
`/root`, a world-writable agent, an agent that is not executable) to verify that every leak is
seen.

## Sources and licenses (plan, section 12)

Every pack publishes `sources.json` next to the image:

- **Debian**: for each binary package of linux-base, the source package, its exact version, the
  licenses read from its `copyright` file (kept in the image under
  `/usr/share/doc/<package>/copyright`), the copyleft flag (GPL, LGPL, MPL…) and **the exact
  address on `snapshot.debian.org`** (`https://snapshot.debian.org/package/<source>/<version>/`,
  where the `.dsc`, the `.orig.tar` and the `.debian.tar` are). Chosen over `apt-get source`:
  the sources of the base (glibc, coreutils, bash, util-linux…) weigh several hundred MiB per
  image and would fit neither in a light artifact nor on Pages; the `snapshot` addresses are
  stable and name exactly what is installed. A download option can be added at the time of the
  R2 publication if Kazo asks for it.
- **Alpine**: for `linux-virt` (kernel, GPL-2.0), `busybox` (GPL-2.0, in the initramfs) and
  `musl` (MIT): the installed version and the SHA-256 of the `.apk`, the aports APKBUILD (branch
  `3.24-stable`, read from the GitHub mirror because `gitlab.alpinelinux.org` answers 418 to a
  client without a browser) and the upstream archives it names with their SHA-512;
  `--fetch-sources` downloads them into `images/build/linux-base/sources/` (kernel ≈ 150 MiB:
  for R2, never for Pages).
- **wine**: all of the above for the base, plus:
  - **Wine** (LGPL-2.1 or later; the `copyright` file of the WineHQ packages is kept in the
    image): the WineHQ source package the binaries come from (`wine_11.0.0.0~trixie-1.dsc`,
    `.orig.tar.gz`, `.debian.tar.xz` of `dl.winehq.org/wine-builds/debian/pool/main/w/wine/`) and
    the upstream `wine-11.0.tar.xz`; the WineHQ packages are not on `snapshot.debian.org` (their
    `debian.packages` lines point to the WineHQ repository);
  - **Mono 10.4.1**: mixed licenses, read in the `COPYING` of wine-mono (tag `wine-mono-10.4.1`):
    MIT X11 for most of it; parts of Mono under LGPL or GPL with exceptions (SharpZipLib among
    them, GPL with an exception; the VB compiler under LGPL v2); FNA under MS-PL and MIT; FAudio,
    FNA3D, MojoShader and SDL under zlib; `jay` under BSD; winforms, wpf, System.Speech under MIT.
    **Copyleft (LGPL, GPL): sources to publish**, `wine-mono-10.4.1-src.tar.xz` (357 MiB);
  - **Gecko 2.47.4**: Mozilla's Gecko under the **MPL 2.0** with the other licenses listed in
    `toolkit/content/license.html` of its sources (the `LICENSE` of wine-gecko points there); no
    right to the Mozilla trademarks. **MPL: sources to publish**, `wine-gecko-2.47.4-src.tar.xz`
    (197 MiB);
  - `osx86-hello.exe`: source in the repository (`images/wine/osx86-hello.c`), compiler recorded.

  The binary archives of Mono and Gecko carry no license text: publishing the sources of Wine,
  Mono and Gecko and those texts next to the image is a prerequisite of the first publication
  (plan, section 12). `build-wine.py --fetch-sources` downloads the sources of Wine, Mono, Gecko and
  the Alpine components into `images/build/wine/sources/` and records their SHA-256 (no upstream
  digest exists for them): for R2, never for Pages.
- **alpine-test**: the `3.22-stable` aports (`main/linux-lts`, `main/busybox`, `main/musl`) and
  the ISO; **freedos**: the official archive, the per-package licenses of the `PACKAGES` directory
  of the disk and the sources of the 1.4 distribution on ibiblio.

## Publication (202 A)

`images.yml` runs on every change of `images/**`, of the image tests or of the workflow (the
linux-base job first repairs the `socket` rule of the seccomp profile of the runner's Docker daemon,
which refuses `socket(2)` to any 32-bit container, measured with `strace`: pinned official profile,
rule made unconditional, nothing else changes; the `RUN` steps of BuildKit keep their own profile,
hence the apt installs through `docker run`; the repair is the shared script
`.github/scripts/repair-i386-seccomp.sh`, run by every job that builds or runs 32-bit containers:
agent, alpine-test, linux-base): five
jobs build and check the agent (compilation, `--check`, smoke test), alpine-test, freedos,
linux-base (Docker on the runner, native i386) and the tool tests, and keep every pack as an artifact. On a push to `j0/base` or `main`:

- **GitHub Pages** receives the small test images (alpine-test, freedos) under
  `images/<image>/<version>/` with `images/index.json`, through `publish-layout.py`, which refuses
  any file above 100 MiB and a site above 900 MiB. The job runs only when the repository variable
  `OSX86_PUBLISH_PAGES` is `true`; **Pages is not enabled on the repository** (a maintainer
  setting: source "GitHub Actions"); this work item enabled nothing.
- **R2** behind a Kazo domain receives all the images (`rclone copy`, versioned paths,
  `Cache-Control: public, max-age=31536000, immutable`) when the variable `OSX86_R2_BUCKET` and
  the secrets `OSX86_R2_ACCOUNT_ID`, `OSX86_R2_ACCESS_KEY_ID`, `OSX86_R2_SECRET_ACCESS_KEY` exist.
  None is defined: the R2 origin has not been obtained from Kazo yet, the job has never run. The
  bucket's CORS policy will have to expose `Content-Range`, `ETag` and `Content-Length` (E3).

## Measured sizes (zstd level 19, 1 MiB frames)

| Image | Decompressed | zstd pack | Chunks | Note |
| --- | --- | --- | --- | --- |
| alpine-test | kernel 7.70 MB + initramfs 7.66 MB | **15.21 MB** | 1 | the bzImage is already compressed; with the agent (+73 kB) |
| freedos | disk 33.55 MB | **17.25 MB** | 1 | the disk holds 17 MiB of zip packages |
| linux-base | kernel 8.11 MB + initramfs 5.06 MB + ext4 138.41 MB (export 102.49 MB) | **73.07 MB** | 3 | no Wine; 56 kernel modules; with the agent; measured again in work item 6a (the 70.67 MB of work item 5 came from a CRLF build, an initramfs without `sd_mod` nor `ext4`); slimming: see `measurements.json` |
| wine | kernel 8.11 MB + initramfs 5.06 MB + ext4 1,304.43 MB (1,244 MiB; 1.13 GB of files: Wine 663.2 MB, Mono 236.2 MB, Gecko 109.3 MB, template 2.1 MB) | **365.84 MB (348.9 MiB)** | 11 | work item 9; objective 250 MiB: gap of about 99 MiB, see below |

**Wine image and the 250 MiB objective (201 A).** The pack is 348.9 MiB, about 99 MiB above the
objective (E17: 380 MiB with a full Debian, `apt`, 130 MiB of modules, without Mono or Gecko). The
slimming removed 176 packages and Wine's development tools; what remains is mostly Wine itself
(builtin PE DLLs), Mono and Gecko, which 201 A and the plan (section 1) put in the image. The
compressed breakdown is below.

| Part (zstd 19, one stream per part) | Raw | Compressed |
| --- | --- | --- |
| Wine's PE DLLs (`/opt/wine-stable/lib/wine/i386-windows`) | 645.8 MB | 143.0 MB |
| Mono (`/usr/share/wine/mono`) | 238.4 MB | 63.7 MB |
| Gecko (`/usr/share/wine/gecko`) | 109.3 MB | 44.8 MB |
| Debian i386 libraries (`/usr/lib/i386-linux-gnu`) | 41.5 MB | 14.0 MB |
| rest of Wine (`i386-unix`, `share`) | 17.4 MB | 3.6 MB |
| kernel modules | 4.8 MB | 1.4 MB |
| kernel + initramfs (pack entries) | 13.2 MB | 13.0 MB |

The sum is about 284 MB; the pack is 365.8: the rest is the Debian base, the prefix and above all
the cost of independent 1 MiB frames (each frame starts again without a dictionary). Ways to the
objective, not taken here because they change what 201 A puts in the image or the format of work
item 4: Gecko and Mono as separate packs downloaded on demand (about 109 MB), larger frames for the
root (less loss, but more bytes read per access), or dropping DLLs a console never opens
(Direct3D, `d3dcompiler`…, to measure with a wider trace). The measures are in
`images/build/<image>/measurements.json` and in `manifest.build.sizes`.

### Measurements of the Wine image (work item 9, 204 A)

`guest` test `WineGuestTests`, locally (Windows 11, Ryzen 7 3700X, headless Chrome through
Playwright, pack copied into the OPFS, `hda` root decompressed on demand, 512 MiB of memory,
discard layer). Times counted from the serial prompt; each line is a cold boot; the template copy
includes adding the user in memory from the serial console:

| Boot | Prompt | Template copied | `wine cmd /c echo` (itself) | `osx86-hello.exe` (itself) | **First program** |
| --- | --- | --- | --- | --- | --- |
| no preloading | 15.2 s | 4.3 s | 12.4 s (8.0 s) | 14.6 s (2.2 s) | **14.6 s** |
| preloading, program at once | 15.2 s | 5.5 s | 15.1 s (9.6 s) | 17.1 s (2.0 s) | **17.1 s** |
| preloading, program after it | 15.3 s | 4.2 s | 11.1 s (6.8 s) | 13.3 s (2.2 s) | **13.3 s** |

An earlier pass (template copied in place, without the staging directory under `/run`) gave
13.3, 13.9 and 12.2 s; the spread from one pass to the next is about 1 to 3 s.

- **Criterion of 204 A met**: first console program 12 to 17 s after the prompt (objective: under
  60 s), without a template state; 152 A does not need to be asked again.
- **Preloading gains little**: about 1.2 s on `wine cmd` when it finished first (224 files,
  108 MB read in 1.8 s of guest uptime when nothing else runs), and costs 1 to 2 s when it runs at
  the same time as the first program (11 to 14 s of competing reads). E17's 62 to 79 s came from the 9p root;
  with the ext4 root read from the OPFS (work item 6a), the first `wine` is already at 8.6 s. The
  preloading stays in the image (measured gain, no visible cost on the prompt);
  `osx86.preload=off` turns it off.
- What remains in `wine cmd`: starting `wineserver`, `wineboot --init` (services, `plugplay`,
  `winedevice`), then `cmd`. The template is never remade in the VM (checked: `.update-timestamp`
  equals the date of `wine.inf` in the guest).
- **The prefix's `wineserver` must live on its own.** A `wineserver` started by an
  `exec-in-prefix` command belongs to that command's cgroup, which the agent ends 2 s after it
  exits (SIGHUP then `cgroup.kill`, 208 A): a second command connecting to it in that window hangs
  (480 s in a first try). The test starts `wineserver -f -p` as a launch of its own before the
  commands; work item 10 decides how a prefix keeps its own.
- Natively (Docker), `wine cmd` takes 1.2 to 1.5 s and `osx86-hello.exe` 1 s: the VM costs a
  factor of 6 to 8.
- Not measured: Firefox, Safari, a slower machine, Mono and Gecko in use (no .NET or HTML program
  run). The CI prints the same lines in the `guest-wine` job.

## Tests

- `dotnet test --filter "Category=unit"`: `PackImageTests` (chunks, schema, `check` that detects
  a changed byte and a modified manifest, `extract`, refusal of bad arguments), `ImageCheckTests`
  (synthetic tar, 22 checks, seven breakages). They run Python with `-I`; without Python 3.14 or
  `zstandard` they are skipped locally and fail in the CI.
- `WineGuestTests` (`guest`) boots the Wine pack three times and runs `wine cmd /c echo` and
  `osx86-hello.exe` through `exec-in-prefix` (see "Measurements of the Wine image"); skipped when
  the pack is not built, fails in the `guest-wine` job (`OSX86_REQUIRE_PACKS`).
- `dotnet test --filter "Category=guest"`: `PackedAlpineGuestTests` checks the Alpine pack,
  extracts it and boots it in the worker up to the serial prompt (9 s in Chrome).
- The CI (`ci.yml`, `guest-tests.yml`) installs Python 3.14; `images.yml` builds the images.
