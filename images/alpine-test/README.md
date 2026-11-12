# Alpine test image (milestone 0, work item 2)

The smallest guest that boots in the v86 engine and talks on the serial console: the `linux-virt`
kernel and the initramfs of an official Alpine Linux release for x86 (32-bit), plus `init.sh` as
`/init`. No disk, no network, no package manager. Used by the `guest` tests and by hand.

```bash
python -I images/alpine-test/build-alpine-test.py
```

- Downloads `alpine-virt-<version>-x86.iso` from `dl-cdn.alpinelinux.org` into
  `images/alpine-test/download/` (an empty directory of its own, git-ignored, deleted afterwards
  unless `--keep-download`), checks its SHA-256 against the value pinned in the script (the one
  published next to the ISO), then reads `boot/vmlinuz-virt` and `boot/initramfs-virt` from the
  ISO with a small ISO 9660 reader. Nothing downloaded is executed on the host.
- Rebuilds the initramfs (cpio newc, gzip, deterministic) with `init.sh` as `/init` and two files
  under `/etc` (`osx86-alpine-release`, `osx86-profile`).
- Writes `images/build/alpine-test/` (git-ignored): `vmlinuz-virt`, `initramfs-osx86`,
  `manifest.json` (sizes, SHA-256, kernel command line, ready marker, prompt, sources, licenses).

Boot: the engine loads both files from the OPFS and passes `console=ttyS0 tsc=reliable
mitigations=off quiet`. The guest prints `OSX86-GUEST ...` then `OSX86-READY` on the serial
console, then runs a busybox shell on it with the prompt `osx86# `. The virtio-console ports
`/dev/hvc0` to `/dev/hvc3` exist (the driver is built into `linux-virt`) when the engine declares
the virtio-console device.

Licenses: the kernel and BusyBox are GPL-2.0; their sources are the Alpine aports (branch
`3.22-stable`, `main/linux-lts` and `main/busybox`). This image is a test fixture and is never
published.
