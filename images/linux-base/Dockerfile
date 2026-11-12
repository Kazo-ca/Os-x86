# Os-x86 linux-base (milestone 0, work item 4; decisions 201 A, 210 A): the Linux guest every
# Linux image of Os-x86 starts from. Debian trixie i386 (glibc, the WineHQ packages of work item 9
# install on it), the `linux-virt` kernel of Alpine fetched with `apk fetch`, a minimal init, four
# virtio-console ports and the serial console, a place for the guest agent of work item 5, and the
# hardening of 210 A. Built by build-linux-base.py (docker build --platform linux/386, then
# docker export); nothing here is run outside Docker. No Wine here (work item 9 builds on the
# `base` stage).
#
# The platform (linux/386) is given by the build command (build-linux-base.py passes
# --platform linux/386 to every stage); a constant in FROM is refused by BuildKit's linter.
#
# Stages: kernel (Alpine: kernel, initramfs, pruned modules, provenance) -> probes (two static
# programs of the R134.5 checks) -> base (Debian with the packages, the init, the hardening and
# the package inventory) -> slim (the published root).

ARG ALPINE_VERSION=3.24
ARG DEBIAN_IMAGE=debian:trixie-slim

# --- Stage 1: the kernel of Alpine --------------------------------------------------------------
FROM alpine:${ALPINE_VERSION} AS kernel
ARG LINUX_VIRT_VERSION=6.18.63-r0
ARG BUSYBOX_VERSION=1.37.0-r31
ARG MUSL_VERSION=1.2.6-r3
COPY osx86.modules /etc/mkinitfs/features.d/osx86.modules
COPY modules.keep /tmp/modules.keep
RUN set -eux; \
    apk update; \
    apk add --no-cache mkinitfs kmod; \
    mkdir -p /provenance; \
    # `apk fetch` downloads the packages without installing them: the kernel package is then
    # unpacked by hand, and the three .apk files (kernel, busybox and musl of the initramfs) stay
    # as provenance with their SHA-256.
    # (apk-tools 3 takes no version constraint on fetch: the pinned file names are checked.)
    apk fetch --no-cache -o /provenance linux-virt busybox musl; \
    test -f "/provenance/linux-virt-${LINUX_VIRT_VERSION}.apk" -a -f "/provenance/busybox-${BUSYBOX_VERSION}.apk" -a -f "/provenance/musl-${MUSL_VERSION}.apk" \
      || { echo "fetched packages differ from the pinned versions:"; ls /provenance; exit 1; }; \
    (cd /provenance && sha256sum *.apk > apk.sha256); \
    tar -xzf "/provenance/linux-virt-${LINUX_VIRT_VERSION}.apk" -C / boot lib/modules usr/share/kernel; \
    release="$(cat /usr/share/kernel/virt/kernel.release)"; \
    echo "$release" > /provenance/kernel.release; \
    depmod -a "$release"; \
    # The initramfs: the official Alpine init with the modules that mount an ext4 root on the
    # IDE disk of v86 (ata_piix is built in; sd_mod and ext4 are modules) and the base feature.
    mkinitfs -F "base osx86" -o /boot/initramfs-virt "$release"; \
    # Modules kept in the root (modules.keep plus their dependencies), decompressed because the
    # kmod of Debian trixie does not read .ko.gz (trial E17).
    mkdir -p /out/modules/"$release"; \
    for module in $(grep -v '^#' /tmp/modules.keep); do \
      modprobe -S "$release" -D "$module" 2>/dev/null | awk '/^insmod/ {print $2}'; \
    done | sort -u > /tmp/modules.closure; \
    while read -r path; do \
      target="/out/modules/$release/${path#/lib/modules/$release/}"; \
      mkdir -p "$(dirname "$target")"; \
      case "$path" in *.gz) gunzip -c "$path" > "${target%.gz}";; *) cp "$path" "$target";; esac; \
    done < /tmp/modules.closure; \
    cp /lib/modules/"$release"/modules.order /lib/modules/"$release"/modules.builtin /lib/modules/"$release"/modules.builtin.modinfo /out/modules/"$release"/ 2>/dev/null || true; \
    cp /boot/config-* /provenance/kernel.config; \
    wc -l /tmp/modules.closure

# --- Stage 2: the probes of the R134.5 checks (two tiny static programs, no compiler in the image)
FROM ${DEBIAN_IMAGE} AS probes
# apt's own seccomp sandbox for its download methods, stacked on the seccomp profile of the
# Docker daemon of the GitHub runners, makes socket(2) fail with ENOSYS in an i386 container
# (measured: getent resolves, apt-get update cannot connect; amd64 and seccomp=unconfined work).
# Off during the build only; the file is removed from the published root.
COPY etc/apt-build.conf /etc/apt/apt.conf.d/90osx86-build
RUN apt-get -o Acquire::Retries=5 update && apt-get -o Acquire::Retries=5 install -y --no-install-recommends gcc libc6-dev && rm -rf /var/lib/apt/lists/*
COPY probes/ /src/
RUN gcc -Os -static -o /osx86-rawsock /src/osx86-rawsock.c && gcc -Os -static -o /osx86-ptrace /src/osx86-ptrace.c && strip /osx86-rawsock /osx86-ptrace

# --- Stage 3: Debian i386, the init, the hardening -----------------------------------------------
FROM ${DEBIAN_IMAGE} AS base
ARG IMAGE_VERSION=0.1.0
ENV DEBIAN_FRONTEND=noninteractive
COPY etc/apt-build.conf /etc/apt/apt.conf.d/90osx86-build
RUN set -eux; \
    apt-get -o Acquire::Retries=5 update; \
    # kmod: modprobe for the agent and the display of later work items; procps: sysctl, ps.
    apt-get -o Acquire::Retries=5 install -y --no-install-recommends kmod procps; \
    apt-get clean; rm -rf /var/lib/apt/lists/*

COPY --from=kernel /boot/vmlinuz-virt /boot/initramfs-virt /boot/
COPY --from=kernel /out/modules /lib/modules
COPY --from=kernel /provenance /usr/lib/osx86/provenance
COPY --from=probes /osx86-rawsock /osx86-ptrace /usr/lib/osx86/
COPY init.sh /sbin/init
COPY verify-console.sh /usr/lib/osx86/verify-console.sh
# (/etc/hosts and /etc/hostname are read-only mounts during a build: copied, not written.)
COPY etc/hostname etc/hosts /etc/
RUN set -eux; \
    release="$(cat /usr/lib/osx86/provenance/kernel.release)"; \
    depmod -a "$release"; \
    chmod 755 /sbin/init /usr/lib/osx86/verify-console.sh /usr/lib/osx86/osx86-rawsock /usr/lib/osx86/osx86-ptrace; \
    echo "${IMAGE_VERSION}" > /etc/osx86-release; \
    # The agent of work item 5 goes to /usr/lib/osx86/agent; the init starts it when present.
    mkdir -p /usr/lib/osx86 /home; \
    # Hardening (210 A and trial E7): / is 0755, no setuid or setgid program (the VM is driven by
    # the agent as root; a guest user never needs su, mount or passwd), home directories 0700.
    chmod 755 /; \
    find / -xdev -type f \( -perm -4000 -o -perm -2000 \) -exec chmod u-s,g-s {} +; \
    chmod 1777 /tmp /var/tmp; \
    rm -rf /run/lock /run/*; \
    sed -i 's/^#\?DIR_MODE=.*/DIR_MODE=0700/' /etc/adduser.conf 2>/dev/null || echo 'DIR_MODE=0700' > /etc/adduser.conf; \
    grep -q '^HOME_MODE' /etc/login.defs && sed -i 's/^HOME_MODE.*/HOME_MODE\t0700/' /etc/login.defs || echo 'HOME_MODE 0700' >> /etc/login.defs; \
    chmod 700 /root

# --- Stage 4: the published root, slimmed ----------------------------------------------------------
FROM base AS slim
RUN set -eux; \
    # Packages that are only useful to install more packages, or that nothing in the VM runs.
    dpkg --purge --force-depends --force-remove-essential apt libapt-pkg7.0 debian-archive-keyring sqv perl-base 2>/dev/null || true; \
    # Package inventory and licenses of what is really in the image (plan section 12), written
    # after the purge and before the dpkg database is trimmed.
    dpkg-query -W -f '${binary:Package}\t${Version}\t${source:Package}\t${source:Version}\n' | sort > /usr/lib/osx86/packages.tsv; \
    for copyright in /usr/share/doc/*/copyright; do \
      package="$(basename "$(dirname "$copyright")")"; \
      grep -q "^$package" /usr/lib/osx86/packages.tsv || continue; \
      licenses="$(grep -E '^License:' "$copyright" | sed 's/^License: *//' | sort -u | tr '\n' ';' | sed 's/;$//')"; \
      printf '%s\t%s\n' "$package" "${licenses:-see copyright file}"; \
    done > /usr/lib/osx86/licenses.tsv; \
    for doc in /usr/share/doc/*; do grep -q "^$(basename "$doc")" /usr/lib/osx86/packages.tsv || rm -rf "$doc"; done; \
    rm -f /etc/apt/apt.conf.d/90osx86-build; \
    rm -rf /var/lib/apt /var/cache/apt /var/cache/debconf/*-old /var/log/* /var/lib/dpkg/info /var/lib/dpkg/*-old; \
    rm -rf /usr/share/man /usr/share/info /usr/share/locale /usr/share/lintian /usr/share/bug /usr/share/perl /usr/share/perl5 /usr/lib/i386-linux-gnu/perl* /usr/lib/i386-linux-gnu/perl-base /usr/bin/perl*; \
    # /usr/share/doc: the copyright files stay (they are the license texts of the image), the rest goes.
    find /usr/share/doc -mindepth 2 ! -name copyright -type f -delete; \
    find /usr/share/doc -mindepth 2 -type d -empty -delete; \
    # Character set converters other than the Unicode ones; time zones other than UTC.
    find /usr/lib/i386-linux-gnu/gconv -type f ! -name 'gconv-modules*' ! -name 'UTF-*' ! -name 'UNICODE*' ! -name 'IBM437*' ! -name 'CP1252*' ! -name 'ISO8859-1*' -delete; \
    find /usr/share/zoneinfo -mindepth 1 ! -name UTC ! -name UCT ! -name Zulu ! -name 'Etc' ! -path '*/Etc/*' ! -name 'zone*.tab' ! -name 'tzdata.zi' ! -name 'leap*' ! -name 'iso3166.tab' -delete 2>/dev/null || true; \
    rm -f /etc/machine-id /var/lib/dbus/machine-id; \
    du -sm /usr /lib /var /etc 2>/dev/null | cat
