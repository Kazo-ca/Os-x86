# Os-x86 linux-base (milestone 0, work item 4; decisions 201 A, 210 A): the Linux guest every
# Linux image of Os-x86 starts from. Debian trixie i386 (glibc, the WineHQ packages of work item 9
# install on it), the `linux-virt` kernel of Alpine fetched with `apk fetch`, a minimal init, four
# virtio-console ports and the serial console, the guest agent of work item 5, and the
# hardening of 210 A. Built by build-linux-base.py (docker build --platform linux/386, then
# docker export); nothing here is run outside Docker. No Wine here (work item 9 builds on the
# `base` stage).
#
# The platform (linux/386) is given by the build command (build-linux-base.py passes
# --platform linux/386 to every stage); a constant in FROM is refused by BuildKit's linter.
#
# No apt-get runs in a build step: the seccomp profile BuildKit applies to its RUN sandboxes
# refuses socket(2) to a 32-bit container on the GitHub runners (measured with strace), so
# build-linux-base.py installs the packages with `docker run` (the daemon's profile, repaired
# by the workflow) and commits two prepared images that the two Debian stages start from:
#   DEBIAN_TOOLS_IMAGE     debian:trixie-slim + kmod (modprobe) + procps (sysctl, ps)
#   DEBIAN_COMPILER_IMAGE  debian:trixie-slim + gcc + libc6-dev (the static probes only)
#
# Stages: kernel (Alpine: kernel, initramfs, pruned modules, provenance) -> probes (two static
# programs of the R134.5 checks) -> base (Debian with the packages, the init, the hardening and
# the package inventory) -> slim (the published root).

ARG ALPINE_VERSION=3.24
ARG DEBIAN_TOOLS_IMAGE=osx86-debian-tools:0.1.0
ARG DEBIAN_COMPILER_IMAGE=osx86-debian-compiler:0.1.0
# The guest agent of work item 5, built by images/agent/build-agent.py (a static musl i386 binary,
# the same one the Alpine test image carries): build-linux-base.py builds it first.
ARG AGENT_IMAGE=osx86-agent-build:0.1.0

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
FROM ${DEBIAN_COMPILER_IMAGE} AS probes
COPY probes/ /src/
RUN gcc -Os -static -o /osx86-rawsock /src/osx86-rawsock.c && gcc -Os -static -o /osx86-ptrace /src/osx86-ptrace.c && strip /osx86-rawsock /osx86-ptrace

# --- The guest agent (images/agent): only its two binaries are taken from this image ---------------
FROM ${AGENT_IMAGE} AS agent

# --- Stage 3: Debian i386, the init, the hardening -----------------------------------------------
FROM ${DEBIAN_TOOLS_IMAGE} AS base
ARG IMAGE_VERSION=0.1.0
ENV DEBIAN_FRONTEND=noninteractive

COPY --from=kernel /boot/vmlinuz-virt /boot/initramfs-virt /boot/
COPY --from=kernel /out/modules /lib/modules
COPY --from=kernel /provenance /usr/lib/osx86/provenance
COPY --from=probes /osx86-rawsock /osx86-ptrace /usr/lib/osx86/
# The agent (root, started by the init on hvc0, user consoles on hvc1) and the program a guest
# runs to hand a URL to the host.
COPY --from=agent /out/agent /usr/lib/osx86/agent
COPY --from=agent /out/osx86-open-url /usr/bin/osx86-open-url
COPY init.sh /sbin/init
COPY verify-console.sh /usr/lib/osx86/verify-console.sh
# (/etc/hosts and /etc/hostname are read-only mounts during a build: copied, not written.)
COPY etc/hostname etc/hosts /etc/
RUN set -eux; \
    release="$(cat /usr/lib/osx86/provenance/kernel.release)"; \
    depmod -a "$release"; \
    chmod 755 /sbin/init /usr/lib/osx86/verify-console.sh /usr/lib/osx86/osx86-rawsock /usr/lib/osx86/osx86-ptrace; \
    chown 0:0 /usr/lib/osx86/agent /usr/bin/osx86-open-url; \
    chmod 755 /usr/lib/osx86/agent /usr/bin/osx86-open-url; \
    echo "${IMAGE_VERSION}" > /etc/osx86-release; \
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
\
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
