# Essai E1 — maquette Wine dans v86 (prototype jetable, question 101).
# Invité Alpine Linux 3.24 x86 (32 bits) + Wine 11.0 + Xorg/Xvfb, exporté en
# arborescence 9p pour v86. Recette dérivée de v86/tools/docker/alpine/Dockerfile.
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

# Xorg sans udev : config explicite (fbdev si /dev/fb0, sinon vesa), clavier/souris evdev.
COPY guest/xorg.conf /etc/X11/xorg.conf
# Wine 11 (winex11) attend qu'un gestionnaire de fenêtres accuse ses changements d'état ;
# sans lui la fenêtre reste 1x1. twm (149 Kio) suffit ; RandomPlacement évite le placement à la souris.
COPY guest/twmrc /root/.twmrc
COPY guest/bench.sh /root/bench.sh
COPY guest/startx.sh /root/startx.sh
RUN chmod +x /root/bench.sh /root/startx.sh

# initramfs avec virtio + 9p (racine en 9p sur virtio).
RUN mkinitfs -F "base virtio 9p" $(cat /usr/share/kernel/$KERNEL/kernel.release)
