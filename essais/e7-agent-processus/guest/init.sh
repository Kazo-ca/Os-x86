#!/bin/sh
# Essai E7 — init minimal de l'invité Debian i386 dans v86 (PID 1, dérivé de E17) : montages,
# modules, durcissement (user namespaces, nosuid sur /home et /tmp), cgroup v2, agent sur hvc1,
# MARK boot-ready, shell root sur ttyS0 (pilotage de l'essai, pas le canal de l'agent).
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
mount -t proc proc /proc 2>/dev/null
mount -t sysfs sys /sys 2>/dev/null
mountpoint -q /dev || mount -t devtmpfs dev /dev
mkdir -p /dev/pts /dev/shm /run /tmp /sys/fs/cgroup
mount -t devpts -o gid=5,mode=620 devpts /dev/pts 2>/dev/null
mount -t tmpfs -o nosuid,nodev tmpfs /dev/shm 2>/dev/null
mount -t tmpfs -o nosuid,nodev,mode=0755 tmpfs /run 2>/dev/null  # défaut tmpfs = 1777 (trouvé par E7)
mount -t tmpfs -o nosuid,nodev,mode=1777 tmpfs /tmp 2>/dev/null
mount -t cgroup2 none /sys/fs/cgroup 2>/dev/null
# /home vit sur la racine 9p (préfixes Wine préfabriqués par usager) : remonté nosuid,nodev (garantie 2).
mount --bind /home /home && mount -o remount,bind,nosuid,nodev /home
chmod 755 / 2>/dev/null  # la racine du tar docker export est 0777 (trouvé par E7 : un usager de bouteille pouvait créer un fichier à /)
hostname -F /etc/hostname 2>/dev/null
sysctl -q -w user.max_user_namespaces=0 kernel.yama.ptrace_scope=1 2>/dev/null
chmod 600 /dev/hvc0 /dev/hvc1 /dev/hvc2 /dev/hvc3 /dev/ttyS0 /dev/fb0 /dev/input/* 2>/dev/null
chmod 666 /dev/null /dev/zero /dev/urandom /dev/tty /dev/ptmx 2>/dev/null
modprobe bochs 2>/dev/null || modprobe bochs-drm 2>/dev/null
for m in atkbd psmouse evdev ext4 sd_mod; do modprobe $m 2>/dev/null; done
mkdir -p /var/log
/usr/local/bin/e7-agent -p /dev/hvc1 -c /dev/hvc2 -l /var/log/e7-agent.log >/var/log/e7-agent.out 2>&1 &
echo "MARK boot-ready $(cut -d' ' -f1 /proc/uptime)" > /dev/ttyS0
while :; do
  setsid sh -c 'exec env ENV=/root/.shrc HOME=/root TERM=vt100 sh -i </dev/ttyS0 >/dev/ttyS0 2>&1'
  sleep 1
done
