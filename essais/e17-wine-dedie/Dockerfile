# Essai E17 — « Wine dédié » (décision 170, jour 1) : invité Debian trixie i386 avec le Wine
# i386 de WineHQ (glibc), noyau linux-virt d'Alpine 3.24 (E1), exporté en racine 9p pour v86.
# Prototype jetable. Construire : pwsh -File build.ps1 -Branch stable|devel|staging
#   stable  -> wine-stable-i386  11.0.0.0~trixie-1
#   devel   -> wine-devel-i386   11.21~trixie-1
#   staging -> wine-staging-i386 11.21~trixie-1

# --- Étape 1 : noyau et initramfs d'Alpine (même recette que E1 : linux-virt, mkinitfs base+virtio+9p).
FROM --platform=linux/386 alpine:3.24 AS kernel
RUN apk add --no-cache linux-virt linux-firmware-none \
 && mkinitfs -F "base virtio 9p" "$(cat /usr/share/kernel/virt/kernel.release)"

# --- Étape 2 : Debian i386 + WineHQ + Xorg.
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

# Noyau, initramfs et modules d'Alpine. Le kmod de Debian trixie ne lit pas les .ko.gz d'Alpine
# (« Function not implemented ») : on les décompresse et on refait depmod.
COPY --from=kernel /boot /boot
COPY --from=kernel /lib/modules /lib/modules
RUN find /lib/modules -name '*.ko.gz' -exec gunzip {} + && depmod -a "$(ls /lib/modules)"

# Init minimal (pas de sysvinit) : montages, modules, shell sur ttyS0, MARK boot-ready.
COPY guest/init.sh /sbin/init
COPY guest/xorg.conf /etc/X11/xorg.conf
COPY guest/xorg-noxinerama.conf /etc/X11/xorg-noxinerama.conf
COPY guest/xorg-dummy.conf /etc/X11/xorg-dummy.conf
COPY guest/twmrc /root/.twmrc
COPY guest/bench.sh /root/bench.sh
COPY guest/startx.sh /root/startx.sh
COPY guest/shrc /root/.shrc
RUN chmod +x /sbin/init /root/bench.sh /root/startx.sh && echo debian > /etc/hostname

# Bouteille préfabriquée : wineboot tourne nativement ici (i386 sous Docker x86_64) au lieu de
# ~7 min dans v86 (E1). Pas de Mono ni Gecko. Sans serveur X : les clés d'affichage
# (Control\Video, Enum\DISPLAY) seront créées au premier wine sous Xorg, dans la VM.
ENV WINEPREFIX=/root/.wine WINEDLLOVERRIDES="mscoree,mshtml=" WINEDEBUG=-all
RUN wineboot -u && wineserver -w && rm -rf /tmp/.wine-0 /root/.cache \
 && du -sm /root/.wine && wine --version
