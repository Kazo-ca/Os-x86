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
set -eu
template=/usr/lib/osx86/wine/prefix-template
user="${1:?usage: prefix-copy <user> [<destination>]}"
case "$user" in osx86-*) ;; *) echo "prefix-copy: $user is not a prefix user (osx86-*)" >&2; exit 2;; esac
case "$user" in *[!a-z0-9-]*) echo "prefix-copy: invalid user name $user" >&2; exit 2;; esac
entry="$(getent passwd "$user")" || { echo "prefix-copy: no user $user" >&2; exit 2; }
uid="$(echo "$entry" | cut -d: -f3)"
gid="$(echo "$entry" | cut -d: -f4)"
home="$(echo "$entry" | cut -d: -f6)"
test "$uid" -ne 0 || { echo "prefix-copy: $user is root" >&2; exit 2; }
destination="${2:-$home/.wine}"
test ! -e "$destination" -a ! -L "$destination" || { echo "prefix-copy: $destination exists" >&2; exit 2; }

cp -a "$template" "$destination"
mv "$destination/drive_c/users/osx86-template" "$destination/drive_c/users/$user"
sed -i "s/osx86-template/$user/g" "$destination/system.reg" "$destination/user.reg" "$destination/userdef.reg"
chown -R -h "$uid:$gid" "$destination"
chmod 700 "$destination"
