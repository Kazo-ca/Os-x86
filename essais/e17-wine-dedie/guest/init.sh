#!/bin/sh
# Essai E17 — init minimal de l'invité Debian i386 dans v86 (PID 1, lancé par l'initramfs Alpine
# après le montage de la racine 9p). Montages, modules, MARK boot-ready, shell sur ttyS0.
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
mount -t proc proc /proc 2>/dev/null
mount -t sysfs sys /sys 2>/dev/null
mountpoint -q /dev || mount -t devtmpfs dev /dev
mkdir -p /dev/pts /dev/shm /run /tmp
mount -t devpts devpts /dev/pts 2>/dev/null
mount -t tmpfs tmpfs /dev/shm 2>/dev/null
mount -t tmpfs tmpfs /run 2>/dev/null
mount -t tmpfs tmpfs /tmp 2>/dev/null
hostname -F /etc/hostname 2>/dev/null
# Pilotes : framebuffer du VGA Bochs (/dev/fb0 pour Xorg fbdev), evdev (clavier/souris pour Xorg).
modprobe bochs 2>/dev/null || modprobe bochs-drm 2>/dev/null
# i8042 est intégré au noyau ; clavier AT et souris PS/2 (v86) sont des modules, evdev donne /dev/input/event*.
for m in atkbd psmouse evdev; do modprobe $m 2>/dev/null; done
echo "MARK boot-ready $(cut -d' ' -f1 /proc/uptime)" > /dev/ttyS0
# Shell root sur la console série, relancé s'il se termine (pas de login, pas de PAM).
while :; do
  setsid sh -c 'exec env ENV=/root/.shrc HOME=/root TERM=vt100 sh -i </dev/ttyS0 >/dev/ttyS0 2>&1'
  sleep 1
done
