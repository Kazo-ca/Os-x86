#!/bin/sh
# Essai E14 — voie (b) : lance un programme dans un pty relié au port série ttyS1 ; la page joue
# l'émulateur de terminal. socat ouvre ttyS1 en brut (sans écho, clocal : sans porteuse DCD un open()
# ordinaire bloquerait) et crée un pty (setsid, ctty) pour pty-child.sh, qui fixe la taille, lance le
# programme et écrit « E14-EXIT <code> » à la fin (lanceur de 110). La discipline de ligne du pty (ICANON,
# ECHO, ISIG : Ctrl+C → SIGINT) est celle du noyau, pas de la page.
# Première version avec util-linux `script` abandonnée : script copie les attributs de stdin dans le pty
# (ligne brute héritée), passe stdin en brut avec TCSAFLUSH (premiers octets de sortie perdus) et ne
# transmettait pas la taille.
# Usage : pty-run.sh [-t TERM] [-s ROWSxCOLS] commande...   (commande découpée sur les espaces par socat)
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/root
export WINEPREFIX=/root/.wine WINEDLLOVERRIDES="mscoree,mshtml=" WINEDEBUG=-all XDG_RUNTIME_DIR=/run
TERM=xterm; SIZE=25x80
while [ $# -gt 0 ]; do
  case "$1" in
    -t) TERM=$2; shift 2;;
    -s) SIZE=$2; shift 2;;
    *) break;;
  esac
done
export TERM
rows=${SIZE%x*}; cols=${SIZE#*x}
# clocal avant l ouverture par socat (stty ouvre en O_NONBLOCK) : sans porteuse DCD, open() bloquerait.
stty -F /dev/ttyS1 clocal 2>/dev/null
setsid socat FILE:/dev/ttyS1,raw,echo=0,clocal=1,b115200 EXEC:"sh $(dirname "$0")/pty-child.sh $rows $cols $*",pty,setsid,ctty,stderr >/dev/null 2>&1 &
