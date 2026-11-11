#!/bin/sh
# Essai E4 — lance le serveur X (sans gestionnaire de fenêtres), les clients X11 natifs
# de la mesure, puis l'agent. Imprime des MARK sur la console série.
# Usage : sh /root/demo.sh [composite|geometry] [hz]
. /etc/profile.d/wine.sh
MODE="${1:-composite}"
HZ="${2:-20}"
AGENT="${AGENT:-e4agent}"   # run.mjs injecte la version corrigée de l'agent en /root/e4agent (image non reconstruite : disque plein)
mark() { echo "MARK $1 $(cut -d' ' -f1 /proc/uptime)"; }

mark demo-start
if ! xdpyinfo >/dev/null 2>&1; then
  sh /root/startx.sh xorg
  i=0
  while ! xdpyinfo >/dev/null 2>&1; do sleep 0.5; i=$((i+1)); [ $i -gt 240 ] && { echo "MARK x-failed"; exit 1; }; done
fi
mark x-ready
xdpyinfo | awk '/dimensions/{print "X " $2}'
xdpyinfo | grep -c -E 'Composite|DAMAGE|XTEST|RANDR' | sed 's/^/X ext count /'
xsetroot -solid '#3a4050' 2>/dev/null

# Trois fenêtres de premier niveau : un xterm (texte qui change à la demande), une horloge
# numérique (change chaque seconde), xeyes (change avec la souris) ; plus un xmessage
# déclaré transient de l'xterm (WM_TRANSIENT_FOR posé par xprop, comme le ferait une boîte de dialogue).
if ! xdotool search --class xterm >/dev/null 2>&1; then
  xterm -geometry 60x14+40+40 -fa 'DejaVu Sans Mono' -fs 11 -bg black -fg '#8f8' -title 'Terminal invité' -e sh -c 'echo "pty: $(tty)"; echo "$(tty)" > /root/e4-pty; exec sh' &
  xclock -digital -update 1 -geometry 220x60+560+60 -fg white -bg '#204' -title 'Horloge' &
  xeyes -geometry 200x150+560+200 &
  xmessage -geometry 260x90+120+330 -title 'Dialogue' -buttons OK 'Boite de dialogue (transient de xterm)' &
  # xterm met ~90 s à s'ouvrir la première fois (cache fontconfig + Xft) : attendre sa vraie taille.
  i=0
  while :; do
    XT=$(xdotool search --class xterm 2>/dev/null | head -1)
    W=$([ -n "$XT" ] && xwininfo -id "$XT" 2>/dev/null | awk '/Width:/{print $2}')
    [ "${W:-0}" -gt 50 ] && break
    sleep 1; i=$((i+1)); [ $i -gt 240 ] && { echo "MARK xterm-failed"; break; }
  done
  mark xterm-sized
  sleep 1
  XT=$(xdotool search --class xterm | head -1)
  XM=$(xdotool search --class xmessage | head -1)
  [ -n "$XT" ] && [ -n "$XM" ] && xprop -id "$XM" -f WM_TRANSIENT_FOR 32x -set WM_TRANSIENT_FOR "$XT" \
    && xprop -id "$XM" -f _NET_WM_WINDOW_TYPE 32a -set _NET_WM_WINDOW_TYPE _NET_WM_WINDOW_TYPE_DIALOG
fi
mark clients-ready
xwininfo -root -tree | grep -E '^\s+0x' | head -12

mkdir -p /root/e4
pkill e4agent 2>/dev/null
sleep 0.3
"$AGENT" -m "$MODE" -z "$HZ" -o /root/e4 2>/root/e4agent.log &
sleep 1
mark agent-started
cat /root/e4agent.log
