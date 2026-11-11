#!/bin/sh
# Essai E7 — arbre de processus de test (lancé par l'agent comme l'usager de la bouteille, « program »
# = /bin/sh, « args » = [tree.sh, N]) : un parent, deux enfants dans son groupe, un enfant « démon »
# (setsid, nouveau groupe, nouvelle session) : que tue « kill pid », « kill group », « cgroup.kill » ?
N="${1:-1000}"
sleep "$N" &
sleep "$N" &
setsid sleep "$N" &
echo "tree parent=$$ children: $(jobs -p | tr '\n' ' ')"
wait
