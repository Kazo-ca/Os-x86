#!/bin/sh
# Essai E7 — Xorg fbdev :0 + twm, comme E17 ; puis ouverture de l'affichage aux usagers locaux des
# bouteilles (Xorg lancé par root sans -auth : seul root passe par défaut). Imprime MARK x-ready|x-failed.
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin DISPLAY=:0
modprobe evdev 2>/dev/null; modprobe bochs 2>/dev/null || modprobe bochs-drm 2>/dev/null
if ! xdpyinfo >/dev/null 2>&1; then
  Xorg :0 vt7 -noreset -nolisten tcp -config /etc/X11/xorg.conf >/root/xorg.log 2>&1 &
  i=0
  while ! xdpyinfo >/dev/null 2>&1; do
    sleep 0.5; i=$((i+1)); [ $i -gt 120 ] && { echo "MARK x-failed $(cut -d' ' -f1 /proc/uptime)"; exit 1; }
  done
fi
pgrep -x twm >/dev/null 2>&1 || { twm >/root/twm.log 2>&1 & sleep 1; }
for u in wine-b1 wine-b2; do xhost "+si:localuser:$u" >/dev/null 2>&1; done
chmod 600 /dev/fb0 /dev/input/event* 2>/dev/null  # affichage et entrées : réservés à root (Xorg), pas aux bouteilles
echo "X $(xdpyinfo 2>/dev/null | awk '/dimensions/{print $2}') xhost: $(xhost 2>/dev/null | tr '\n' ' ')"
echo "MARK x-ready $(cut -d' ' -f1 /proc/uptime)"
