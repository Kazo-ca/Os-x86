#!/bin/sh
# Essai E14 — côté « enfant » de la voie (b) : tourne DANS le pty créé par socat. Fixe la taille, lance le
# programme, puis écrit le code de sortie sur le pty (donc sur ttyS1) et sur ttyS0 : c'est le lanceur de 110.
# Usage : pty-child.sh ROWS COLS commande...
rows=$1; cols=$2; shift 2
# Ctrl+C : la discipline de ligne du pty envoie SIGINT à tout le groupe au premier plan, donc à ce lanceur
# aussi. Un gestionnaire (pas SIG_IGN, que le programme hériterait) le laisse survivre pour écrire le code.
trap ":" INT
stty rows "$rows" cols "$cols" 2>/dev/null
"$@"
rc=$?
printf '\r\nE14-EXIT %s\r\n' "$rc"
echo "E14-EXIT $rc" >/dev/ttyS0
exit $rc
