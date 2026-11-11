# Essai E4 — image invité : la même base que E1 (essais/e1-wine-v86/Dockerfile, couches Docker
# réutilisées à l'identique) + outils X11 (xprop, xwd, xclock, xeyes, xmessage) + l'agent e4agent
# compilé dans une étape à part (gcc n'entre pas dans l'image finale).
FROM --platform=linux/386 alpine:3.24 AS build
RUN apk add --no-cache gcc musl-dev libx11-dev libxcomposite-dev libxdamage-dev libxfixes-dev libxext-dev
COPY guest/e4agent.c /src/e4agent.c
RUN gcc -O2 -Wall -o /src/e4agent /src/e4agent.c -lX11 -lXcomposite -lXdamage -lXfixes

FROM --platform=linux/386 alpine:3.24

ENV KERNEL=virt

RUN apk add --no-cache \
    openrc alpine-base agetty alpine-conf linux-$KERNEL linux-firmware-none \
    wine \
    xorg-server xf86-video-fbdev xf86-video-vesa xf86-input-evdev \
    xvfb xinit xterm xdotool xwininfo xdpyinfo xrandr xset font-dejavu twm \
    procps-ng

# Autologin root sur tty1 et sur la console série ttyS0 (v86 -> serial0).
RUN sed -i 's/getty 38400 tty1/agetty --autologin root tty1 linux/' /etc/inittab \
 && echo 'ttyS0::respawn:/sbin/agetty --autologin root -s ttyS0 115200 vt100' >> /etc/inittab \
 && echo "root:" | chpasswd \
 && setup-hostname localhost

# Services OpenRC comme dans la recette v86 (chroot-like).
RUN for i in devfs dmesg mdev hwdrivers; do rc-update add $i sysinit; done \
 && for i in hwclock modules sysctl hostname syslog bootmisc; do rc-update add $i boot; done \
 && rc-update add local default \
 && rc-update add killprocs shutdown

# Marqueur "boot-ready" imprimé sur la console série par le service "local"
# (runlevel default), avec l'uptime invité pour recouper avec performance.now().
RUN printf '%s\n' '#!/bin/sh' \
    'echo "MARK boot-ready $(cut -d" " -f1 /proc/uptime)" > /dev/console' \
    'echo "MARK boot-ready $(cut -d" " -f1 /proc/uptime)" > /dev/ttyS0' \
    > /etc/local.d/00-mark.start && chmod +x /etc/local.d/00-mark.start

# Environnement Wine : pas de Mono ni Gecko pour la maquette.
RUN printf '%s\n' \
    'export WINEDLLOVERRIDES="mscoree,mshtml="' \
    'export WINEDEBUG=-all' \
    'export WINEPREFIX=/root/.wine' \
    'export DISPLAY=:0' \
    > /etc/profile.d/wine.sh

# --- E4 : outils X11 natifs + bibliothèques de l'agent ---
RUN apk add --no-cache xprop xwd xclock xeyes xmessage xrefresh xsetroot libxcomposite libxdamage libxfixes
COPY --from=build /src/e4agent /usr/local/bin/e4agent
COPY guest/xorg.conf /etc/X11/xorg.conf
COPY guest/twmrc /root/.twmrc
COPY guest/startx.sh /root/startx.sh
COPY guest/demo.sh /root/demo.sh
RUN chmod +x /root/startx.sh /root/demo.sh

# initramfs avec virtio + 9p (racine en 9p sur virtio).
RUN mkinitfs -F "base virtio 9p" $(cat /usr/share/kernel/$KERNEL/kernel.release)
