# Essai E6 — réseau et permissions par bouteille (prototype jetable).
# Invité Alpine Linux 3.24 x86 (32 bits) sans Wine (les programmes de test sont curl,
# nc et httpd de busybox, à la place des programmes Windows). Recette dérivée de E1.
FROM --platform=linux/386 alpine:3.24

ENV KERNEL=virt

RUN apk add --no-cache \
    openrc alpine-base agetty alpine-conf linux-$KERNEL linux-firmware-none \
    iproute2 nftables iptables curl busybox-extras shadow util-linux-misc procps-ng

# Autologin root sur tty1 et sur la console série ttyS0 (v86 -> serial0).
RUN sed -i 's/getty 38400 tty1/agetty --autologin root tty1 linux/' /etc/inittab \
 && echo 'ttyS0::respawn:/sbin/agetty --autologin root -s ttyS0 115200 vt100' >> /etc/inittab \
 && sed -i 's/^root:[^:]*:/root::/' /etc/shadow \
 && setup-hostname localhost

RUN for i in devfs dmesg mdev hwdrivers; do rc-update add $i sysinit; done \
 && for i in hwclock modules sysctl hostname syslog bootmisc; do rc-update add $i boot; done \
 && rc-update add local default \
 && rc-update add killprocs shutdown

RUN printf '%s\n' '#!/bin/sh' \
    'echo "MARK boot-ready $(cut -d" " -f1 /proc/uptime)" > /dev/console' \
    'echo "MARK boot-ready $(cut -d" " -f1 /proc/uptime)" > /dev/ttyS0' \
    > /etc/local.d/00-mark.start && chmod +x /etc/local.d/00-mark.start

# Pas de sudo/doas ; on retire aussi tout binaire setuid de l'image (bbsuid, su de shadow...).
RUN apk del --no-cache sudo doas 2>/dev/null; \
    find / -xdev -type f \( -perm -4000 -o -perm -2000 \) -exec sh -c 'echo "SETUID avant: $1"; chmod u-s,g-s "$1"' _ {} \; ; \
    find / -xdev -type f \( -perm -4000 -o -perm -2000 \) -print

# Config du noyau pour le rapport (CONFIG_NET_NS, USER_NS, VETH, NF_TABLES, VIRTIO_NET).
RUN ls /boot && cp /boot/config-*-$KERNEL /root/kernel-config 2>/dev/null || true

COPY guest/bottle-net.sh /root/bottle-net.sh
COPY guest/verify-confinement.sh /root/verify-confinement.sh
COPY guest/probe.sh /root/probe.sh
RUN chmod +x /root/*.sh

RUN mkinitfs -F "base virtio 9p" $(cat /usr/share/kernel/$KERNEL/kernel.release)
