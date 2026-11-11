# Essai E14 — invité Debian trixie i386 + WineHQ stable (recette E17, copiée) + outils console :
# winegcc/mingw (compile guest/consoletest.c en PE), socat, kbd (openvt/chvt), nano, less, vim-tiny.
# Prototype jetable. Construire : pwsh -File build.ps1   (image Docker e14-wine-console)

# --- Étape 1 : noyau et initramfs d'Alpine (même recette que E1/E17 : linux-virt, mkinitfs base+virtio+9p).
FROM --platform=linux/386 alpine:3.24 AS kernel
RUN apk add --no-cache linux-virt linux-firmware-none \
 && mkinitfs -F "base virtio 9p" "$(cat /usr/share/kernel/virt/kernel.release)"

# --- Étape 2 : Debian i386 + WineHQ + Xorg (identique à E17 pour profiter du cache Docker).
FROM --platform=linux/386 debian:trixie-slim
ARG WINE_BRANCH=stable
ARG WINE_VERSION=11.0.0.0~trixie-1
ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates wget \
 && mkdir -p /etc/apt/keyrings \
 && wget -q -O /etc/apt/keyrings/winehq-archive.key https://dl.winehq.org/wine-builds/winehq.key \
 && wget -q -O /etc/apt/sources.list.d/winehq-trixie.sources https://dl.winehq.org/wine-builds/debian/dists/trixie/winehq-trixie.sources \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      "winehq-${WINE_BRANCH}=${WINE_VERSION}" "wine-${WINE_BRANCH}=${WINE_VERSION}" "wine-${WINE_BRANCH}-i386=${WINE_VERSION}" \
      libfontconfig1 libfreetype6 libxcomposite1 libxcursor1 libxfixes3 libxi6 libxinerama1 \
      libxrandr2 libxrender1 libxxf86vm1 libgnutls30t64 \
      xserver-xorg-core xserver-xorg-video-fbdev xserver-xorg-video-dummy xserver-xorg-input-evdev \
      x11-utils x11-xserver-utils xdotool twm fonts-dejavu-core kmod procps \
 && apt-get clean && rm -rf /var/lib/apt/lists/*

# --- E14 : outils console. wine-stable-dev donne winegcc ; gcc-mingw-w64-i686 lui permet de produire un PE.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      "wine-${WINE_BRANCH}-dev=${WINE_VERSION}" gcc-mingw-w64-i686 gcc libc6-dev \
      socat kbd nano less vim-tiny ncurses-term bash-completion \
 && apt-get clean && rm -rf /var/lib/apt/lists/*

COPY --from=kernel /boot /boot
COPY --from=kernel /lib/modules /lib/modules
RUN find /lib/modules -name '*.ko.gz' -exec gunzip {} + && depmod -a "$(ls /lib/modules)"

COPY guest/init.sh /sbin/init
COPY guest/shrc /root/.shrc
COPY guest/vga-run.sh /root/vga-run.sh
COPY guest/pty-run.sh /root/pty-run.sh
COPY guest/pty-child.sh /root/pty-child.sh
COPY guest/shell-probe.sh /root/shell-probe.sh
COPY guest/consoletest.c /root/consoletest.c
RUN chmod +x /sbin/init /root/*.sh && echo debian > /etc/hostname \
 && cd /root && (winegcc -b i686-w64-mingw32 -mconsole -o consoletest.exe consoletest.c \
     || i686-w64-mingw32-gcc -o consoletest.exe consoletest.c) \
 && ls -la /root/consoletest.exe* && head -c 2 /root/consoletest.exe && echo

# Bouteille préfabriquée (comme E17), sans serveur X.
ENV WINEPREFIX=/root/.wine WINEDLLOVERRIDES="mscoree,mshtml=" WINEDEBUG=-all
RUN wineboot -u && wineserver -w && rm -rf /tmp/.wine-0 /root/.cache \
 && du -sm /root/.wine && wine --version
