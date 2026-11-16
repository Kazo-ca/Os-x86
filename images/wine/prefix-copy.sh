#!/bin/sh
# Os-x86 wine image (work item 9; decision 152 A): copies the template prefix for one guest user.
# Installed as /usr/lib/osx86/wine/prefix-copy; run as root (by the agent or the root console):
#
#     /usr/lib/osx86/wine/prefix-copy <user> [<destination>]
#
# <user> is the guest user of the prefix (osx86-<prefix>, in /etc/passwd); <destination> is its
# Wine prefix, $HOME/.wine by default, and must not exist. The copy keeps the links of the
# template (a few MiB are written), renames the Windows user "osx86-template" to <user> in the
# profile folder and the registry, and gives everything to the user (Wine refuses a prefix that is
# not owned by the user running it). No wineboot: the template is ready (work item 10 decides
# where prefixes live and calls this or does the same).
#
# The destination's parent may belong to the user, who could swap paths while root works there:
# the copy is made and adjusted in a staging directory of root under /run, then moved into place
# in one step, and nothing is done by path at the destination afterwards.
set -eu
template=/usr/lib/osx86/wine/prefix-template
user="${1:?usage: prefix-copy <user> [<destination>]}"
fail() { echo "prefix-copy: $*" >&2; exit 2; }
case "$user" in osx86-*) ;; *) fail "$user is not a prefix user (osx86-*)";; esac
case "$user" in *[!a-z0-9-]*) fail "invalid user name $user";; esac
entry="$(getent passwd "$user")" || fail "no user $user"
uid="$(echo "$entry" | cut -d: -f3)"
gid="$(echo "$entry" | cut -d: -f4)"
home="$(echo "$entry" | cut -d: -f6)"
test "$uid" -ne 0 -a "$gid" -ne 0 || fail "$user has uid or gid 0"
destination="${2:-$home/.wine}"
parent="$(dirname "$destination")"
test -d "$parent" -a ! -L "$parent" || fail "$parent is not a directory"
test ! -e "$destination" -a ! -L "$destination" || fail "$destination exists"

staging="$(mktemp -d /run/osx86-prefix-copy.XXXXXX)"
trap 'rm -rf "$staging"' EXIT
copy="$staging/prefix"
cp -a "$template" "$copy"
mv "$copy/drive_c/users/osx86-template" "$copy/drive_c/users/$user"
sed -i "s/osx86-template/$user/g" "$copy/system.reg" "$copy/user.reg" "$copy/userdef.reg"
chown -R -h "$uid:$gid" "$copy"
chmod 700 "$copy"
# -T: the destination is never taken for a directory to move into (a link put there meanwhile
# makes the move fail instead of writing elsewhere).
mv -T "$copy" "$destination"
