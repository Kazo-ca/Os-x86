#!/bin/sh
# Essai E7 — aide de l'agent (exécutée comme root) : une ligne « 0x<id> <pid> <titre> » par fenêtre
# cliente nommée et mappée portant _NET_WM_PID. Un seul xwininfo -tree (les cadres de twm n'ont pas de
# nom, on ne garde que les fenêtres nommées), puis un xprop par fenêtre retenue.
export DISPLAY="${DISPLAY:-:0}"
xwininfo -root -tree 2>/dev/null | grep -E '^ +0x[0-9a-f]+ "' | grep -o '^ *0x[0-9a-f]*' | tr -d ' ' | while read -r w; do
  props=$(xprop -id "$w" _NET_WM_PID WM_STATE _NET_WM_NAME WM_NAME 2>/dev/null)
  p=$(echo "$props" | awk -F'= ' '/^_NET_WM_PID/{print $2}')
  [ -n "$p" ] || continue
  echo "$props" | grep -q 'window state: Normal' || continue
  t=$(echo "$props" | sed -n 's/^[^=]*NAME[^=]*= "\(.*\)"$/\1/p' | head -1)
  echo "$w $p $t"
done
