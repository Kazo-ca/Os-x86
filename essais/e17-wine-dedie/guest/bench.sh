#!/bin/sh
# Essai E17 — mesure côté invité. Lancé depuis la console série.
# Usage : sh /root/bench.sh [xorg|noxinerama|dummy|xorg-norandr] [notepad|winecfg|winemine ...]
# Imprime "MARK <nom> <uptime>", "WIN <prog> <id> <w>x<h>+<x>+<y> <titre>" et "MEM ...".
[ -f /root/.shrc ] && . /root/.shrc
[ -f /etc/profile.d/wine.sh ] && . /etc/profile.d/wine.sh
XMODE="${1:-xorg}"; shift
PROGS="${*:-notepad winecfg winemine}"
mark() { echo "MARK $1 $(cut -d' ' -f1 /proc/uptime)"; }
meminfo() { echo "MEM $1 $(awk '/MemTotal/{t=$2}/MemAvailable/{a=$2}END{print "total_kb=" t " avail_kb=" a " used_kb=" t-a}' /proc/meminfo)"; }
geom() {  # geom <id> -> WxH+X+Y
  xwininfo -id "$1" 2>/dev/null | awk '/Width:/{w=$2}/Height:/{h=$2}/Absolute upper-left X:/{x=$4}/Absolute upper-left Y:/{y=$4}END{print w "x" h "+" x "+" y}'
}
# Fenêtre mappée dont le titre correspond, de largeur > 100 px.
findwin() {
  for w in $(xdotool search --onlyvisible --name "$1" 2>/dev/null); do
    width=$(xwininfo -id "$w" 2>/dev/null | awk '/Width:/{print $2}')
    [ "${width:-0}" -gt 100 ] && { echo "$w"; return 0; }
  done
  return 1
}
anywin() { xdotool search --onlyvisible --name "$1" 2>/dev/null | head -1; }

mark bench-start
meminfo boot
echo "WINEVER $(wine --version 2>/dev/null)"
if ! xdpyinfo >/dev/null 2>&1; then
  sh /root/startx.sh "$XMODE" || { mark bench-done; exit 1; }
fi
mark x-ready
if ! pgrep -x twm >/dev/null 2>&1; then twm >/root/twm.log 2>&1 & sleep 1; fi
mark wm-started

mark cmd-start
wine cmd /c echo hi 2>/dev/null
mark cmd-done
meminfo cmd

for prog in $PROGS; do
  case "$prog" in
    notepad) title='Notepad' ;;
    winecfg) title='Wine configuration' ;;
    winemine) title='WineMine' ;;
    *) title="$prog" ;;
  esac
  mark "$prog-start"
  wine "$prog" >"/root/$prog.log" 2>&1 &
  i=0; W=""
  while :; do
    W=$(findwin "$title") && break
    sleep 0.5; i=$((i+1))
    if [ $i -gt 360 ]; then echo "MARK $prog-failed $(cut -d' ' -f1 /proc/uptime)"; break; fi
  done
  if [ -n "$W" ]; then
    mark "$prog-visible"
    echo "WIN $prog $W $(geom "$W") $(xdotool getwindowname "$W" 2>/dev/null)"
  else
    A=$(anywin "$title")
    [ -n "$A" ] && echo "WIN $prog $A $(geom "$A") $(xdotool getwindowname "$A" 2>/dev/null) [1x1]" || echo "WIN $prog none"
  fi
  if [ "$prog" = notepad ] && [ -n "$W" ]; then
    xdotool windowactivate --sync "$W" 2>/dev/null; sleep 0.5
    xdotool type --delay 60 'hello from v86 e17' 2>/dev/null
    mark notepad-typed
  fi
done
xwininfo -root -tree 2>/dev/null | grep -E 'Notepad|Wine configuration|WineMine' | head -6
# Remettre le Bloc-notes devant pour la capture du canvas (le texte tapé doit y être lisible).
N=$(findwin Notepad) && xdotool windowactivate --sync "$N" windowraise "$N" 2>/dev/null; sleep 1
meminfo final
mark bench-done
