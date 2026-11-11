#!/bin/sh
# Essai E14 — init minimal de l'invité Debian i386 dans v86 (PID 1), dérivé d'E17.
# Différence : AUCUN pilote DRM (bochs) : la console tty1 reste en mode texte VGA (vgacon),
# lisible par get_text_screen(). ttyS0 = shell de contrôle ; ttyS1 = voie « pty » ; tty1 = voie « VGA ».
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
# Clavier AT (i8042 intégré ; atkbd en module) pour que tty1 reçoive les touches.
for m in atkbd; do modprobe $m 2>/dev/null; done
# ttyS1 : ligne brute, pas d'écho : c'est le pty créé par `script` qui fera la discipline de ligne.
stty -F /dev/ttyS1 raw -echo -echoe -echok 115200 2>/dev/null
echo "MARK boot-ready $(cut -d' ' -f1 /proc/uptime)" > /dev/ttyS0
while :; do
  setsid sh -c 'exec env ENV=/root/.shrc HOME=/root TERM=vt100 sh -i </dev/ttyS0 >/dev/ttyS0 2>&1'
  sleep 1
done
