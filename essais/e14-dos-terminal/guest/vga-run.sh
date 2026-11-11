#!/bin/sh
# Essai E14 — voie (a) : lance un programme sur la console VGA tty1 (clavier AT, écran texte lu par
# get_text_screen). Écrit « E14-EXIT <code> » sur tty1 et sur ttyS0 à la fin (110 : code récupéré).
# Usage : vga-run.sh <commande...>
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/root
export WINEPREFIX=/root/.wine WINEDLLOVERRIDES="mscoree,mshtml=" WINEDEBUG=-all XDG_RUNTIME_DIR=/run TERM=linux
setsid sh -c '
  "$@" </dev/tty1 >/dev/tty1 2>&1
  rc=$?
  echo "E14-EXIT $rc" >/dev/tty1
  echo "E14-EXIT $rc" >/dev/ttyS0
' sh "$@" &
