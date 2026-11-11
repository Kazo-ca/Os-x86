#!/bin/sh
# Essai E17 — démarre Xorg sur :0 selon le mode : xorg (fbdev, comme E1), noxinerama (fbdev,
# extension XINERAMA désactivée), dummy (pilote dummy), xorg-norandr (fbdev, -extension RANDR).
# Fonctionne sur l'invité Debian (E17) et sur l'invité Alpine de E1 (injecté par 9p).
MODE="${1:-xorg}"
shift
modprobe evdev 2>/dev/null; modprobe bochs 2>/dev/null || modprobe bochs-drm 2>/dev/null
[ -e /dev/fb0 ] && echo "X fbdev present" || echo "X no fb0"
case "$MODE" in
  noxinerama) CONF=/etc/X11/xorg-noxinerama.conf ;;
  dummy) CONF=/etc/X11/xorg-dummy.conf ;;
  *) CONF=/etc/X11/xorg.conf ;;
esac
EXTRA=""
[ "$MODE" = "xorg-norandr" ] && EXTRA="-extension RANDR"
[ "$MODE" = "noxinerama-cli" ] && EXTRA="-extension XINERAMA"
echo "X conf $CONF extra '$EXTRA $*'"
Xorg :0 vt7 -noreset -nolisten tcp -config "$CONF" $EXTRA "$@" >/root/xorg.log 2>&1 &
i=0
while ! xdpyinfo >/dev/null 2>&1; do
  sleep 0.5; i=$((i+1))
  if [ $i -gt 120 ]; then echo "MARK x-failed"; exit 1; fi
done
echo "X $(xdpyinfo 2>/dev/null | awk '/dimensions/{print $2}') ext: $(xdpyinfo 2>/dev/null | grep -c -E '^    (XINERAMA|RANDR)$') $(xdpyinfo 2>/dev/null | grep -E '^    (XINERAMA|RANDR)$' | tr -d ' ' | tr '\n' ' ')"
exit 0
