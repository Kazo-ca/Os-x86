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
#   DEBIAN_TOOLS_IMAGE     debian:trixie-slim + kmod (modprobe) + procps (sysctl, ps) + iproute2
#                          and nftables (the private network of work item 12)
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
ARG LINUX_VIRT_VERSION=6.18.64-r0
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

# --- Stage 2: the probes of the R134.5 checks and of the private network (tiny static programs, no compiler in the image)
FROM ${DEBIAN_COMPILER_IMAGE} AS probes
COPY probes/ /src/
RUN gcc -Os -static -o /osx86-rawsock /src/osx86-rawsock.c && gcc -Os -static -o /osx86-ptrace /src/osx86-ptrace.c \
    && gcc -Os -static -Wall -Wextra -Werror -o /osx86-netcheck /src/osx86-netcheck.c && strip /osx86-rawsock /osx86-ptrace /osx86-netcheck

# --- The guest agent (images/agent): only its two binaries are taken from this image ---------------
FROM ${AGENT_IMAGE} AS agent

# --- Stage 3: Debian i386, the init, the hardening -----------------------------------------------
FROM ${DEBIAN_TOOLS_IMAGE} AS base
ARG IMAGE_VERSION=0.1.0
ENV DEBIAN_FRONTEND=noninteractive

COPY --from=kernel /boot/vmlinuz-virt /boot/initramfs-virt /boot/
COPY --from=kernel /out/modules /lib/modules
COPY --from=kernel /provenance /usr/lib/osx86/provenance
COPY --from=probes /osx86-rawsock /osx86-ptrace /osx86-netcheck /usr/lib/osx86/
# The agent (root, started by the init on hvc0, user consoles on hvc1) and the program a guest
# runs to hand a URL to the host.
COPY --from=agent /out/agent /usr/lib/osx86/agent
COPY --from=agent /out/osx86-open-url /usr/bin/osx86-open-url
COPY init.sh /sbin/init
COPY verify-console.sh /usr/lib/osx86/verify-console.sh
# Work item 10: the guest side of the prefixes, run as root by the agent only.
COPY prefix-admin.sh /usr/lib/osx86/prefix-admin
# Work item 12: the guest side of the private network (eth0, a namespace per prefix, nftables in
# defence), run as root by the agent only.
COPY net-admin.sh /usr/lib/osx86/net-admin
COPY net/vm.nft net/prefix.nft /usr/lib/osx86/net/
# (/etc/hosts and /etc/hostname are read-only mounts during a build: copied, not written.)
COPY etc/hostname etc/hosts /etc/
RUN set -eux; \
    release="$(cat /usr/lib/osx86/provenance/kernel.release)"; \
    depmod -a "$release"; \
    chmod 755 /sbin/init /usr/lib/osx86/verify-console.sh /usr/lib/osx86/prefix-admin /usr/lib/osx86/net-admin /usr/lib/osx86/osx86-rawsock /usr/lib/osx86/osx86-ptrace /usr/lib/osx86/osx86-netcheck; \
    chmod 755 /usr/lib/osx86/net; chmod 644 /usr/lib/osx86/net/vm.nft /usr/lib/osx86/net/prefix.nft; \
    chown 0:0 /usr/lib/osx86/agent /usr/bin/osx86-open-url /usr/lib/osx86/prefix-admin /usr/lib/osx86/net-admin; \
    chown -R 0:0 /usr/lib/osx86/net; \
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
# slim.sh is shared with images/wine (it runs the same step after its Wine packages); it stays
# outside the image (bind mount, not COPY).
RUN --mount=type=bind,source=slim.sh,target=/tmp/osx86-slim.sh sh /tmp/osx86-slim.sh
