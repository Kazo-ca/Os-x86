#!/bin/sh
# Os-x86 linux-base (milestone 0, work item 10; decisions 144, 146, 152 A, 193 A, 200 B): the guest
# side of the prefixes. Installed as /usr/lib/osx86/prefix-admin (root, 0755) and run as root by
# the guest agent only (operations prefix-ensure and prefix-delete), with a prefix name the agent
# has already checked:
#
#     prefix-admin ensure <prefix>    the guest user osx86-<prefix>, its home and its files exist
#     prefix-admin delete <prefix>    the prefix's user, home and files are removed
#
# Exit codes: 0 something was done, 10 nothing to do (already there, or already gone), anything
# else a failure (its last line says why).
#
# A prefix is a guest user of its own (osx86-<prefix>, uid from 2000 up, home /home/osx86-<prefix>
# in 0700, shell /bin/sh, no password): what runs in a prefix has that user's rights (144), and two
# prefixes see neither each other's files (146) nor each other's processes (hidepid). The users
# are written in /etc/passwd and /etc/group through the writable view of the root that the init
# gives root alone (/run/osx86-admin/root): the file is replaced in one rename, readers never see
# half a file. The homes are on the VM's disk (the init mounts /home from it), so a prefix and what
# was installed in it survive a stop of the VM.
#
# The images built on linux-base add their own steps in /usr/lib/osx86/prefix.d/ (the wine image
# copies its template prefix there, 152 A): each executable is run, in name order, as
#     <hook> ensure|delete <user> <home>
# and exits 0 (done), 10 (nothing to do) or anything else (failure).
set -eu
umask 022
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
NOTHING=10
FIRST_UID=2000
LAST_UID=59999
writable=/run/osx86-admin/root
hooks=/usr/lib/osx86/prefix.d

fail() { echo "prefix-admin: $*"; exit 2; }

action="${1:-}"
prefix="${2:-}"
case "$action" in ensure|delete) ;; *) fail "usage: prefix-admin ensure|delete <prefix>";; esac
case "$prefix" in ''|-*|*[!a-z0-9-]*) fail "invalid prefix '$prefix'";; esac
[ "${#prefix}" -le 16 ] || fail "invalid prefix '$prefix' (more than 16 characters)"
user="osx86-$prefix"
home="/home/$user"
[ "$(id -u)" -eq 0 ] || fail "must run as root"
[ -d "$writable/etc" ] || fail "the root file system is not writable here (no $writable): prefixes cannot be kept"

# One change of the user database at a time.
exec 9> /run/osx86-admin/lock
flock 9

# Replaces /etc/<file> by the output of a command run on it, in one rename (same file system).
rewrite() {
  file="$writable/etc/$1"
  shift
  temporary="$file.osx86-new"
  rm -f "$temporary"
  "$@" < "$file" > "$temporary"
  chmod --reference="$file" "$temporary"
  chown --reference="$file" "$temporary"
  mv -f "$temporary" "$file"
}

append_user() { cat; echo "$user:x:$1:$1:Os-x86 prefix $prefix:$home:/bin/sh"; }
append_group() { cat; echo "$user:x:$1:"; }
without_user() { grep -v "^$user:" || true; }

# Runs the hooks; returns 0 when one of them did something.
run_hooks() {
  # (Its own variable: a function of sh shares the caller's, and "changed" is the caller's.)
  hooks_changed=1
  for hook in "$hooks"/*; do
    [ -f "$hook" ] && [ -x "$hook" ] || continue
    set +e
    "$hook" "$1" "$user" "$home"
    status=$?
    set -e
    case "$status" in
      0) hooks_changed=0 ;;
      "$NOTHING") ;;
      *) fail "$(basename "$hook") $1 $user failed with code $status" ;;
    esac
  done
  return "$hooks_changed"
}

if [ "$action" = ensure ]; then
  changed=1
  if ! getent passwd "$user" > /dev/null; then
    uid=$FIRST_UID
    while getent passwd "$uid" > /dev/null || getent group "$uid" > /dev/null; do
      uid=$((uid + 1))
      [ "$uid" -le "$LAST_UID" ] || fail "no free user identifier left"
    done
    # A group left without its user (a stop between the two rewrites, here or in a deletion) goes first.
    if getent group "$user" > /dev/null; then rewrite group without_user; fi
    rewrite group append_group "$uid"
    rewrite passwd append_user "$uid"
    changed=0
  fi
  entry="$(getent passwd "$user")"
  uid="$(echo "$entry" | cut -d: -f3)"
  gid="$(echo "$entry" | cut -d: -f4)"
  [ "$uid" -ne 0 ] && [ "$gid" -ne 0 ] || fail "$user has uid or gid 0"
  # A user left without its group (a deletion cut short) gets it back.
  if ! getent group "$user" > /dev/null; then
    rewrite group append_group "$gid"
    changed=0
  fi
  [ "$(echo "$entry" | cut -d: -f6)" = "$home" ] || fail "$user does not have the home $home"
  if [ ! -d "$home" ] || [ -L "$home" ]; then
    rm -f "$home"
    install -d -m 700 -o "$uid" -g "$gid" "$home"
    changed=0
  fi
  if run_hooks ensure; then changed=0; fi
  sync
  if [ "$changed" -eq 0 ]; then
    echo "prefix-admin: $user ready (uid $uid, $home)"
    exit 0
  fi
  exit "$NOTHING"
fi

# delete
entry="$(getent passwd "$user" || true)"
if [ -z "$entry" ]; then
  # What a deletion cut short may have left: the group, the home.
  if getent group "$user" > /dev/null || [ -e "$home" ]; then
    rm -rf --one-file-system "$home"
    if getent group "$user" > /dev/null; then rewrite group without_user; fi
    sync
    exit 0
  fi
  exit "$NOTHING"
fi
uid="$(echo "$entry" | cut -d: -f3)"
[ "$uid" -ne 0 ] || fail "$user has uid 0"
# The agent killed the prefix's launches (their cgroups); whatever the user left elsewhere goes too.
pkill -KILL -U "$uid" 2> /dev/null || true
# Work item 12: its network namespace goes with it (a new prefix of the same name gets its own).
if [ -e "/run/netns/$user" ]; then ip netns del "$user" 2> /dev/null || true; fi
run_hooks delete || true
rm -rf --one-file-system "$home" "/tmp/.wine-$uid" "/var/tmp/.wine-$uid"
# The next prefix may get the same uid: nothing of this one may be left for it (146).
find /tmp /var/tmp /dev/shm -xdev -user "$uid" -delete 2> /dev/null || true
# The group goes before the user: a deletion cut in between leaves a user without its group,
# which the next deletion finishes (its entry is still there).
rewrite group without_user
rewrite passwd without_user
sync
echo "prefix-admin: $user deleted"
exit 0
