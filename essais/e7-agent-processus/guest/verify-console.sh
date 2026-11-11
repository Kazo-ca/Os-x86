#!/bin/sh
# Essai E7 — contrôles R134.5 depuis la console d'une bouteille (shell non root wine-b2 ouvert par
# l'agent sur hvc2). Reprend les 8 contrôles de E6 (verify-confinement.sh) et ajoute : port de
# l'agent (hvc1) et second port (hvc2), ptrace et kill d'un processus d'une autre bouteille.
# Chaque contrôle imprime « RES check <nom> <HELD|LEAK|INFO> <détail> ». Argument 1 : un pid de b1.
set -u
OTHER="${1:-1}"
check() { echo "RES check $1 $2 $(echo "$3" | tr '\n' ' ' | cut -c1-160)"; }
echo "RES who $(id) tty=$(tty 2>/dev/null) cgroup=$(cat /proc/self/cgroup 2>/dev/null) nproc_limit=$(grep processes /proc/self/limits | awk '{print $4}')"
# E6 : 1. espaces de noms
o=$(unshare -n true 2>&1); [ $? -ne 0 ] && check unshare_netns HELD "$o" || check unshare_netns LEAK "$o"
o=$(unshare -U true 2>&1); [ $? -ne 0 ] && check unshare_userns HELD "$o" || check unshare_userns LEAK "$o"
o=$(nsenter -t 1 -m -- true 2>&1); [ $? -ne 0 ] && check nsenter_root HELD "$o" || check nsenter_root LEAK "$o"
# E6 : 2. autre bouteille (home 0700, dossier wineserver de l'autre uid)
o=$(ls /home/wine-b1 2>&1); echo "$o" | grep -qi 'denied\|permission' && check other_bottle_home HELD "$o" || check other_bottle_home LEAK "$o"
o=$(ls /tmp/.wine-1001 2>&1); echo "$o" | grep -qi 'denied\|permission\|No such' && check other_bottle_wineserver HELD "$o" || check other_bottle_wineserver LEAK "$o"
o=$(cat /home/wine-b1/.wine/user.reg 2>&1 | head -c 200); echo "$o" | grep -qi 'denied\|permission' && check other_bottle_prefix HELD "$o" || check other_bottle_prefix LEAK "$o"
# E6 : 2b. canal de l'agent : ttyS0 (shell root de l'essai), hvc1 (protocole), hvc2 (console, déjà notre tty)
o=$(head -c1 /dev/ttyS0 2>&1); echo "$o" | grep -qi 'denied\|permission' && check serial_ttyS0 HELD "$o" || check serial_ttyS0 LEAK "$o"
o=$(head -c1 /dev/hvc1 2>&1); echo "$o" | grep -qi 'denied\|permission' && check agent_port_read HELD "$o" || check agent_port_read LEAK "$o"
o=$({ echo '{"id":99,"op":"ping"}' > /dev/hvc1; } 2>&1); echo "$o" | grep -qi 'denied\|permission' && check agent_port_write HELD "$o" || check agent_port_write LEAK "$o"
o=$(ls -l /dev/hvc1 /dev/hvc2 /dev/ttyS0 2>&1); check port_modes INFO "$o"
o=$(head -c1 /dev/hvc3 2>&1); echo "$o" | grep -qi 'denied\|permission' && check other_port_hvc3 HELD "$o" || check other_port_hvc3 LEAK "$o"
o=$({ echo x > /dev/hvc2; } 2>&1); check console_port_direct INFO "écriture directe sur hvc2 (notre propre tty via pty ? sinon canal) : ${o:-ok}"
# E6 : 3. socket brut
o=$(e7-rawsock 2>&1); echo "$o" | grep -qi 'denied\|not permitted' && check raw_socket HELD "$o" || check raw_socket LEAK "$o"
# E6 : setuid, nosuid
o=$(find / -xdev -type f -perm -4000 2>/dev/null | tr '\n' ' '); [ -z "$o" ] && check no_setuid HELD "aucun" || check no_setuid LEAK "$o"
o=$(grep ' /home ' /proc/mounts); echo "$o" | grep -q nosuid && check home_nosuid HELD "$o" || check home_nosuid LEAK "$o"
o=$(grep ' /tmp ' /proc/mounts); echo "$o" | grep -q nosuid && check tmp_nosuid HELD "$o" || check tmp_nosuid LEAK "$o"
cp /bin/true "$HOME/suid-test" 2>/dev/null && chmod u+s "$HOME/suid-test" 2>/dev/null && o=$(ls -l "$HOME/suid-test" 2>&1) && check suid_in_home INFO "bit posé mais nosuid + propriétaire non root : $o"; rm -f "$HOME/suid-test"
o=$(chown 0 "$HOME/.profile" 2>&1); echo "$o" | grep -qi 'not permitted\|denied' && check chown_root HELD "$o" || check chown_root LEAK "$o"
# E7 : ptrace et kill d'un processus d'une autre bouteille (uid 1001)
o=$(cat /proc/$OTHER/status 2>/dev/null | grep -E '^(Name|Uid):' | tr '\n' ' '); check other_pid_info INFO "pid $OTHER : $o"
o=$(e7-ptrace "$OTHER" 2>&1); echo "$o" | grep -qi 'not permitted\|denied' && check ptrace_other HELD "$o" || check ptrace_other LEAK "$o"
o=$(cat /proc/$OTHER/environ 2>&1 | head -c 200); echo "$o" | grep -qi 'denied\|permission' && check proc_environ_other HELD "$o" || check proc_environ_other LEAK "$o"
o=$(kill -0 "$OTHER" 2>&1); echo "$o" | grep -qi 'not permitted\|denied' && check kill_other HELD "$o" || check kill_other LEAK "kill -0 accepté: $o"
o=$(kill -9 "$OTHER" 2>&1); echo "$o" | grep -qi 'not permitted\|denied' && check kill9_other HELD "$o" || check kill9_other LEAK "$o"
o=$(kill -9 1 2>&1); echo "$o" | grep -qi 'not permitted\|denied' && check kill_init HELD "$o" || check kill_init LEAK "$o"
o=$(kill -9 "$(pgrep -x e7-agent | head -1)" 2>&1); echo "$o" | grep -qi 'not permitted\|denied' && check kill_agent HELD "$o" || check kill_agent LEAK "$o"
o=$({ echo 1 > /sys/fs/cgroup/e7/b1/cgroup.kill; } 2>&1); echo "$o" | grep -qi 'denied\|permission' && check cgroup_kill_other HELD "$o" || check cgroup_kill_other LEAK "$o"
o=$(cat /dev/fb0 2>&1 | head -c 1 | wc -c); check fb0_read INFO "octets lus du framebuffer : $o (affichage partagé, hors garanties 1-2)"
o=$(ls /tmp/.X11-unix/ 2>&1; xdpyinfo 2>&1 | head -1); check x_display INFO "$o"
echo "RES verify-done"
