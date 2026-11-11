#!/bin/sh
# Essai E1 — script de mesure côté invité. Lancé depuis la console série par run.mjs.
# Imprime des lignes "MARK <nom> <uptime-secondes>" que boot.html horodate avec performance.now().
# Usage : sh /root/bench.sh [xorg|xvfb]
. /etc/profile.d/wine.sh
XMODE="${1:-xvfb}"

mark() { echo "MARK $1 $(cut -d' ' -f1 /proc/uptime)"; }
meminfo() {
  echo "MEM $1 $(awk '/MemTotal/{t=$2}/MemAvailable/{a=$2}END{print "total_kb=" t " avail_kb=" a " used_kb=" t-a}' /proc/meminfo)"
}

mark bench-start
meminfo boot

# --- Serveur X ---
sh /root/startx.sh "$XMODE"
i=0
while ! xdpyinfo >/dev/null 2>&1; do
  sleep 0.5; i=$((i+1))
  if [ $i -gt 240 ]; then echo "MARK x-failed"; break; fi
done
mark x-ready
echo "XMODE $XMODE $(xdpyinfo 2>/dev/null | awk '/dimensions/{print $2}')"
# Gestionnaire de fenêtres (voir Dockerfile) : sans lui Wine 11 laisse ses fenêtres à 1x1.
twm >/root/twm.log 2>&1 &
sleep 2
mark wm-started

# --- wineboot -u : premier préfixe ---
mark wineboot-start
wineboot -u >/root/wineboot.log 2>&1
mark wineboot-done
meminfo wineboot
du -sk "$WINEPREFIX" | awk '{print "PREFIX_KB " $1}'

# --- Programme console ---
mark cmd-start
wine cmd /c echo hi 2>/dev/null
mark cmd-done

# --- Notepad sous X ---
mark notepad-start
wine notepad >/root/notepad.log 2>&1 &
i=0
# Fenêtre mappée dont le titre contient "Notepad" ET de taille réelle (> 50 px) :
# sans gestionnaire de fenêtres, Wine 11 laisse la fenêtre X mappée à 1x1.
notepad_win() {
  for w in $(xdotool search --onlyvisible --name 'Notepad' 2>/dev/null); do
    width=$(xwininfo -id "$w" 2>/dev/null | awk '/Width:/{print $2}')
    [ "${width:-0}" -gt 50 ] && { echo "$w"; return 0; }
  done
  return 1
}
while ! notepad_win >/dev/null; do
  sleep 0.2; i=$((i+1))
  if [ $i -gt 1500 ]; then echo "MARK notepad-failed"; break; fi
done
mark notepad-visible
echo "WINDOWS $(xdotool search --name 'Notepad' | tr '\n' ' ')"
xwininfo -root -tree 2>/dev/null | grep -i notepad | head -3
meminfo notepad

# Taper un peu de texte dans Notepad (preuve que la fenêtre réagit).
W=$(notepad_win)
[ -n "$W" ] && xdotool windowactivate --sync "$W" type --delay 50 'hello from v86' 2>/dev/null
mark notepad-typed

sync; echo 3 > /proc/sys/vm/drop_caches
meminfo final
mark bench-done
