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
| `images/alpine-test/` | the Alpine test image of work item 2, now packed |
| `images/freedos/` | the FreeDOS recipe: `build-freedos.py`, `FDAUTO.BAT` |
| `images/build/<image>/` | the output (git-ignored); `pack/` is the published form |
| `tests/OsX86.Tests/Images/` | `unit` tests of the tools and the `guest` test of the Alpine pack |
| `.github/workflows/images.yml` | CI build, artifacts, Pages and R2 publication |

```bash
python -I images/alpine-test/build-alpine-test.py     # official ISO, no Docker
python -I images/freedos/build-freedos.py             # official archive, mtools or osx86-tools
python -I images/linux-base/build-linux-base.py       # Docker (linux/386), 5 GiB free required
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
(`/run`, `/tmp`, `/var/tmp`, `/var/log`, `/home`); the prefix disk comes with work items 9 and 10
(211 A). Cost: the decompressed root is one ext4 file (138 MiB for linux-base) rather than files
deduplicated by hash; the block compression below makes up for it, since the OPFS keeps the
compressed bytes.

**Booting linux-base for real is not possible yet**: the worker of work item 2 reads only the
kernel and the initramfs from the OPFS; `hda` support (a v86 buffer backed by a
`SyncAccessHandle`, decompressing frames on demand) is work item 6. The Alpine test pack, on the
other hand, boots in the `guest` test of this work item.

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
            "agent": {"path": "/usr/lib/osx86/agent", "port": "virtio0"}},
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

Four Docker stages, all `linux/386`:

1. **kernel**: `alpine:3.24`, `apk fetch linux-virt busybox musl` (pinned file names, SHA-256
   kept in `/usr/lib/osx86/provenance`), the `linux-virt` 6.18.63-r0 kernel unpacked from the
   package, the official Alpine initramfs rebuilt with `mkinitfs -F "base osx86"` (the
   `osx86.modules` feature adds `sd_mod`, the ATA drivers and ext4: `ata_piix` is built into the
   kernel, `sd_mod` and `ext4` are modules), and the modules of `modules.keep` with their
   dependencies (56 files out of 868, decompressed because the `kmod` of Debian does not read
   `.ko.gz`, trial E17).
2. **probes**: two static C programs of the R134.5 checks (`osx86-rawsock`, `osx86-ptrace`),
   since the image has no compiler.
3. **base**: `debian:trixie-slim` i386 plus `kmod` and `procps`, the kernel, the initramfs and
   the modules, `init.sh` as `/sbin/init`, `verify-console.sh`, the place of the agent
   (`/usr/lib/osx86/agent`, started by the init when present), the hardening. Work item 9 (Wine)
   builds on this stage.
4. **slim**: `apt`, `perl-base` and their libraries purged, manual pages, locales, `gconv`
   converters (except Unicode, CP437, CP1252, Latin-1), time zones (except UTC) and the dpkg
   database removed; the `copyright` files stay. The package inventory (`packages.tsv`,
   `licenses.tsv`) is written at this stage, after the purge.

`build-linux-base.py` exports the container (`docker export`), runs in `osx86-tools` the
extraction with owners and modes, the image test, then `mkfs.ext4 -d` (no journal, fixed UUID and
seed, `SOURCE_DATE_EPOCH`), writes `sources.json`, packs and measures.

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

### alpine-test

Unchanged in its construction (official ISO 3.22.8, home-made ISO 9660 and cpio readers,
`init.sh` as `/init`); `build-alpine-test.py` now calls `pack-image.py` and writes
`images/build/alpine-test/pack/` (entries `kernel` and `initramfs`, `boot.json`, `sources.json`).
The `manifest.json` of `images/build/alpine-test/` stays the build description the test page of
work item 2 reads.

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
  `hidepid`, agent started as root, cgroup2 mounted `nosuid,nodev,noexec` and never `chown`ed.

The `unit` tests of `ImageCheckTests` build a synthetic tar with the real `init.sh` and break it
five ways (setuid, writable `/run/lock`, an init without sysctls or mount options, an open
`/root`) to verify that every leak is seen.

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
- **alpine-test**: the `3.22-stable` aports (`main/linux-lts`, `main/busybox`, `main/musl`) and
  the ISO; **freedos**: the official archive, the per-package licenses of the `PACKAGES` directory
  of the disk and the sources of the 1.4 distribution on ibiblio.

## Publication (202 A)

`images.yml` runs on every change of `images/**`, of the image tests or of the workflow: four
jobs build and check alpine-test, freedos, linux-base (Docker on the runner, native i386) and the
tool tests, and keep every pack as an artifact. On a push to `j0/base` or `main`:

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
| alpine-test | kernel 7.70 MB + initramfs 7.58 MB | **15.14 MB** | 1 | the bzImage is already compressed |
| freedos | disk 33.55 MB | **17.25 MB** | 1 | the disk holds 17 MiB of zip packages |
| linux-base | kernel 8.11 MB + initramfs 5.06 MB + ext4 138.41 MB (export 102.35 MB) | **73.05 MB** | 3 | no Wine; 56 kernel modules; slimming: see `measurements.json` |

The 250 MiB objective of 201 A is about the Wine image (work item 9), which starts from the
`base` stage; E17 had measured 380 MiB with a full Debian, 130 MiB of modules and `apt`. The
measures are in `images/build/<image>/measurements.json` and in `manifest.build.sizes`.

## Tests

- `dotnet test --filter "Category=unit"`: `PackImageTests` (chunks, schema, `check` that detects
  a changed byte and a modified manifest, `extract`, refusal of bad arguments), `ImageCheckTests`
  (synthetic tar, 22 checks, five breakages). They run Python with `-I`; without Python 3.14 or
  `zstandard` they are skipped locally and fail in the CI.
- `dotnet test --filter "Category=guest"`: `PackedAlpineGuestTests` checks the Alpine pack,
  extracts it and boots it in the worker up to the serial prompt (9 s in Chrome).
- The CI (`ci.yml`, `guest-tests.yml`) installs Python 3.14; `images.yml` builds the images.
