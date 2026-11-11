#!/bin/sh
# Démarre un serveur X sur :0. "xorg" tente Xorg sur le VGA v86 (fbdev/vesa),
# "xvfb" démarre Xvfb (repli, pas d'affichage sur le canvas).
MODE="${1:-xvfb}"
if [ "$MODE" = "xorg" ]; then
  modprobe evdev 2>/dev/null; modprobe bochs 2>/dev/null || true
  sleep 1
  ls /dev/input 2>/dev/null | tr '\n' ' '; echo
  if [ -e /dev/fb0 ]; then echo "X fbdev present"; else echo "X no fb0, vesa"; sed -i 's/"fbdev"/"vesa"/' /etc/X11/xorg.conf; fi
  Xorg :0 vt7 -noreset -nolisten tcp >/root/xorg.log 2>&1 &
else
  Xvfb :0 -screen 0 800x600x16 -nolisten tcp >/root/xvfb.log 2>&1 &
fi
