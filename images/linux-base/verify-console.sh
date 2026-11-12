#!/bin/sh
# Os-x86 linux-base: the 22 R134.5 checks of trial E7 (guarantees 1 and 2 of section 134 of the
# design), to run inside the guest from a shell of an ordinary user (the console the agent opens
# for a prefix, work item 5), not as root. Argument 1: the pid of a process of another user (the
# agent's own pid when there is no other user yet; default 1). Each check prints
# "RES check <name> <HELD|LEAK> <detail>"; informational lines say INFO. The last line is
# "RES verify-done <held> <leak>". images/tools/image-check.py applies the static counterpart of
# every check to the exported tree at build time; this script is the runtime version, kept in the
# image at /usr/lib/osx86/verify-console.sh for the guest tests of the next work items.
set -u
OTHER="${1:-1}"
OTHER_HOME="${2:-/root}"
PROBES=/usr/lib/osx86
held=0
leak=0
check() {
  case "$2" in HELD) held=$((held + 1));; LEAK) leak=$((leak + 1));; esac
  echo "RES check $1 $2 $(printf '%s' "$3" | tr '\n' ' ' | cut -c1-160)"
}
denied() { printf '%s' "$1" | grep -qi 'denied\|permission\|not permitted\|refused'; }
# A target that does not exist is reported, never counted as held.
present() { [ -e "$1" ] && return 0; check "$2" INFO "$1 absent"; return 1; }

echo "RES who $(id) tty=$(tty 2>/dev/null) cgroup=$(cat /proc/self/cgroup 2>/dev/null)"
# 1-3. namespaces
o=$(unshare -n true 2>&1); [ $? -ne 0 ] && check unshare_netns HELD "$o" || check unshare_netns LEAK "$o"
o=$(unshare -U true 2>&1); [ $? -ne 0 ] && check unshare_userns HELD "$o" || check unshare_userns LEAK "$o"
o=$(nsenter -t 1 -m -- true 2>&1); [ $? -ne 0 ] && check nsenter_root HELD "$o" || check nsenter_root LEAK "$o"
# 4-6. another user's files (home 0700, that user's /tmp directory, a file of its prefix)
if present "$OTHER_HOME" other_bottle_home; then o=$(ls "$OTHER_HOME" 2>&1); denied "$o" && check other_bottle_home HELD "$o" || check other_bottle_home LEAK "$o"; fi
o=$(stat -c '%a %U %n' /tmp 2>&1); case "$o" in 1777*) check other_bottle_wineserver HELD "sticky /tmp: $o";; *) check other_bottle_wineserver LEAK "$o";; esac
if present "$OTHER_HOME" other_bottle_prefix; then o=$(ls "$OTHER_HOME/.profile" "$OTHER_HOME/.wine" 2>&1 | head -2); denied "$o" && check other_bottle_prefix HELD "$o" || check other_bottle_prefix LEAK "$o"; fi
# 7-10. the serial console and the virtio-console ports: root only (hvc0 is the agent's port)
if present /dev/ttyS0 serial_ttyS0; then o=$(head -c1 /dev/ttyS0 2>&1); denied "$o" && check serial_ttyS0 HELD "$o" || check serial_ttyS0 LEAK "$o"; fi
if present /dev/hvc0 agent_port_read; then o=$(head -c1 /dev/hvc0 2>&1); denied "$o" && check agent_port_read HELD "$o" || check agent_port_read LEAK "$o"; fi
if present /dev/hvc0 agent_port_write; then o=$({ echo '{"id":99,"op":"ping"}' > /dev/hvc0; } 2>&1); denied "$o" && check agent_port_write HELD "$o" || check agent_port_write LEAK "$o"; fi
if present /dev/hvc3 other_port_hvc3; then o=$(head -c1 /dev/hvc3 2>&1); denied "$o" && check other_port_hvc3 HELD "$o" || check other_port_hvc3 LEAK "$o"; fi
check port_modes INFO "$(ls -l /dev/ttyS0 /dev/hvc0 /dev/hvc1 /dev/hvc2 /dev/hvc3 2>&1)"
# 11. raw socket
o=$("$PROBES/osx86-rawsock" 2>&1); denied "$o" && check raw_socket HELD "$o" || check raw_socket LEAK "$o"
# 12-14. setuid programs, nosuid on /home and /tmp
o=$(find / -xdev -type f \( -perm -4000 -o -perm -2000 \) 2>/dev/null | tr '\n' ' '); [ -z "$o" ] && check no_setuid HELD "none" || check no_setuid LEAK "$o"
o=$(grep ' /home ' /proc/mounts); printf '%s' "$o" | grep -q nosuid && check home_nosuid HELD "$o" || check home_nosuid LEAK "$o"
o=$(grep ' /tmp ' /proc/mounts); printf '%s' "$o" | grep -q nosuid && check tmp_nosuid HELD "$o" || check tmp_nosuid LEAK "$o"
# 15. chown to root
touch "$HOME/.osx86-chown-probe" 2>/dev/null; o=$(chown 0 "$HOME/.osx86-chown-probe" 2>&1); denied "$o" && check chown_root HELD "$o" || check chown_root LEAK "$o"; rm -f "$HOME/.osx86-chown-probe"
# 16-22. ptrace, environment, signals, cgroups of another user, of init and of the agent
check other_pid_info INFO "pid $OTHER: $(grep -E '^(Name|Uid):' /proc/$OTHER/status 2>/dev/null | tr '\n' ' ')"
o=$("$PROBES/osx86-ptrace" "$OTHER" 2>&1); denied "$o" && check ptrace_other HELD "$o" || check ptrace_other LEAK "$o"
o=$(cat /proc/$OTHER/environ 2>&1 | head -c 200); denied "$o" && check proc_environ_other HELD "$o" || check proc_environ_other LEAK "$o"
o=$(kill -0 "$OTHER" 2>&1); denied "$o" && check kill_other HELD "$o" || check kill_other LEAK "kill -0 accepted: $o"
o=$(kill -9 "$OTHER" 2>&1); denied "$o" && check kill9_other HELD "$o" || check kill9_other LEAK "$o"
o=$(kill -9 1 2>&1); denied "$o" && check kill_init HELD "$o" || check kill_init LEAK "$o"
agent_pid=$(pgrep -x agent 2>/dev/null | head -1)
if [ -n "$agent_pid" ]; then
  o=$(kill -9 "$agent_pid" 2>&1); denied "$o" && check kill_agent HELD "$o" || check kill_agent LEAK "$o"
else
  o=$(kill -9 "$OTHER" 2>&1); denied "$o" && check kill_agent HELD "no agent in this image; $o" || check kill_agent LEAK "$o"
fi
o=$({ echo 1 > /sys/fs/cgroup/cgroup.kill; } 2>&1); denied "$o" && check cgroup_kill_other HELD "$o" || check cgroup_kill_other LEAK "$o"
check fb0_read INFO "framebuffer bytes readable: $(cat /dev/fb0 2>/dev/null | head -c 1 | wc -c)"
echo "RES verify-done $held $leak"
[ "$leak" -eq 0 ]
