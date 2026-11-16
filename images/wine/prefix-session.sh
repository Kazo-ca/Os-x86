#!/bin/sh
# Os-x86 wine image (work item 10): the Wine session of a prefix. Installed as
# /usr/lib/osx86/wine/prefix-session; Os-x86 runs it as the prefix's user through the agent:
#
#     prefix-session start <token>            a launch of its own (its own cgroup, 206 A)
#     prefix-session wait <token> <seconds>   an exec-in-prefix that returns once the session is up
#
# start: the prefix's wineserver (persistent, -p: it does not leave after its last client) and the
# session's first Wine process, which runs `wineboot --init` (services, plugplay, winedevice):
# all of them are in this launch's cgroup, not in the console of the first program. That is the
# point: the agent closes a console with SIGHUP then cgroup.kill once its program ends (208 A), so
# a wineserver started by a program would die with it, and a second program connected to it would
# hang (seen in work item 9: 480 s). The launch lasts as long as the wineserver; Os-x86 ends it
# (kill of the launch: SIGTERM then cgroup.kill) a while after the last program of the prefix.
# The token names this session's ready marker, in a directory of the user's own (0700).
set -u
dir="/tmp/.osx86-session-$(id -u)"
action="${1:-}"
token="${2:-}"
case "$token" in ''|*[!a-z0-9]*) echo "prefix-session: invalid token" >&2; exit 2;; esac
case "$action" in
  start)
    umask 077
    mkdir -p "$dir"
    rm -f "$dir"/ready-*
    export WINEDEBUG="${WINEDEBUG:--all}"
    # Without -f the server returns once it listens on its socket (and stays in this cgroup).
    wineserver -p || exit $?
    wine cmd.exe /c exit 0 < /dev/null > /dev/null 2>&1
    : > "$dir/ready-$token"
    exec wineserver -w
    ;;
  wait)
    seconds="${3:-120}"
    case "$seconds" in ''|*[!0-9]*) exit 2;; esac
    tenths=$((seconds * 10))
    while [ "$tenths" -gt 0 ]; do
      [ -e "$dir/ready-$token" ] && exit 0
      sleep 0.1
      tenths=$((tenths - 1))
    done
    echo "prefix-session: the session $token is not ready after $seconds s" >&2
    exit 1
    ;;
  *) echo "usage: prefix-session start <token> | wait <token> <seconds>" >&2; exit 2 ;;
esac
