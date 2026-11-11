#!/bin/sh
# E6 — verification du confinement d'une bouteille (prototype jetable de mesure).
# Objet : prouver que les garanties de durcissement de verification-105-134.md tiennent.
# Lance comme l'usager non privilegie wine-b2 (uid 1002) DANS la bouteille b2, via
#   bottle-net.sh run 2 'sh /root/verify-confinement.sh'.
# Chaque controle imprime « RES check <nom> <HELD|LEAK> <detail> » : HELD = le confinement
# a tenu (resultat attendu) ; LEAK = la garantie serait prise en defaut (a signaler).
# Aucun controle n'essaie d'atteindre l'Internet reel : tout reste local a l'invite.
set -u
check() { echo "RES check $1 $2 $(echo "$3" | tr '\n' ' ' | cut -c1-200)"; }

# 1. Pas de changement d'espace de noms (garantie 1 : « un processus ne peut pas changer d'espace »).
# unshare -n demande des user namespaces non privilegies, desactives par user.max_user_namespaces=0.
o=$(unshare -n true 2>&1); [ $? -ne 0 ] && check unshare_netns HELD "$o" || check unshare_netns LEAK "$o"

# nsenter vers l'espace racine (PID 1) demande CAP_SYS_ADMIN, absent pour un usager non root.
o=$(nsenter -t 1 -n -- ip addr 2>&1); [ $? -ne 0 ] && check nsenter_root HELD "$o" || check nsenter_root LEAK "$o"

# 2. Pas d'acces au dossier wineserver d'une autre bouteille (garantie 2 : home/0700 d'un autre uid).
o=$(ls /tmp/.wine-1001 2>&1); echo "$o" | grep -qi 'denied\|permission' && check other_bottle_dir HELD "$o" || check other_bottle_dir LEAK "$o"
o=$(ls /home/wine-b1 2>&1); echo "$o" | grep -qi 'denied\|permission' && check other_bottle_home HELD "$o" || check other_bottle_home LEAK "$o"

# 2b. Canal de l'agent reserve a root : /dev/ttyS0 (console serie) en lecture par un non-root.
o=$(head -c1 /dev/ttyS0 2>&1); echo "$o" | grep -qi 'denied\|permission' && check agent_channel HELD "$o" || check agent_channel LEAK "ttyS0 lisible: $o"

# 3. Pas de socket brut (CAP_NET_RAW) : usurpation / sniff impossibles pour un non-root.
o=$(ping -c1 -W1 10.0.1.2 2>&1); echo "$o" | grep -qi 'denied\|operation not permitted\|socket' && check raw_socket HELD "$o" || check raw_socket "$(echo "$o" | grep -q '0 received\|100% packet' && echo HELD || echo LEAK)" "$o"

# Aucun setuid a exploiter dans l'image (garantie 2 : binaires setuid retires / nosuid).
o=$(find / -xdev -type f -perm -4000 2>/dev/null | tr '\n' ' '); [ -z "$o" ] && check no_setuid HELD "aucun" || check no_setuid LEAK "$o"

# tmpfs du home monte nosuid : un binaire setuid-root y depose ne confere rien.
if cp /bin/busybox /home/wine-b2/bb 2>/dev/null; then
  o=$(mount | grep ' /home/wine-b2 '); echo "$o" | grep -q nosuid && check home_nosuid HELD "$o" || check home_nosuid LEAK "$o"
else check home_nosuid INFO "copie refusee"; fi
