#!/bin/sh
# Os-x86 wine image (work item 10; decision 152 A): the step of the wine image when a prefix is made.
# Installed as /usr/lib/osx86/prefix.d/50-wine and run as root by /usr/lib/osx86/prefix-admin
# (linux-base) with <ensure|delete> <user> <home>: a new prefix gets its copy of the template prefix
# (prefix-copy: links to the image's files, a few MiB written; never a wineboot). Exits 0 when it
# copied, 10 when there was nothing to do (the Wine prefix is there, or the prefix is deleted with
# its home by prefix-admin).
set -eu
action="${1:?}"
user="${2:?}"
home="${3:?}"
case "$action" in
  ensure)
    if [ -e "$home/.wine" ] || [ -L "$home/.wine" ]; then exit 10; fi
    /usr/lib/osx86/wine/prefix-copy "$user" "$home/.wine"
    ;;
  *) exit 10 ;;
esac
