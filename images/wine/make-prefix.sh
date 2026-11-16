#!/bin/sh
# Os-x86 wine image (milestone 0, work item 9; decision 152 A): makes the template prefix, the
# Wine prefix every prefix of a VM is copied from (work item 10). Run as root by build-wine.py with
# `docker run` on the image of install-wine.sh (wineboot talks to wineserver over a Unix socket,
# which a BuildKit RUN step of a 32-bit container may not open on the GitHub runners), then the
# container is committed. wineboot runs here, natively, never in the VM of a user.
#
# The template:
#   /usr/lib/osx86/wine/prefix-template  owned by root, read-only in the image, made for the
#                                        Windows user "osx86-template" (prefix-copy renames it)
# Every file of the template whose bytes are those of a file of Wine, Mono or Gecko (the 667 MiB
# of builtin DLLs wineboot copies into system32, the NLS files, the fonts...) becomes a symbolic
# link to that file: the template keeps only what wineboot really wrote (registry, a few stubs),
# and a copy of it costs a few MiB. Wine only reads the header of a builtin DLL of system32 before
# loading its own; a program that writes over one gets EACCES (system32 belongs to the image).
set -eu
template=/usr/lib/osx86/wine/prefix-template
wine_root=/opt/wine-stable
test ! -e "$template"
install -d -m 755 /usr/lib/osx86/wine

# HOME=/root has no XDG folders: the shell folders of the template are plain directories, not
# links into a home of the build machine.
# winemenubuilder is disabled in the template: it writes .desktop files, MIME types and icons into
# ~/.local at every session start (seen in the trace of the first launch), for a desktop the VM
# does not have; BoxedWine reads shortcuts itself (BwLib).
env -i PATH=/usr/bin:/bin HOME=/root USER=osx86-template WINEPREFIX="$template" WINEDEBUG=-all \
  sh -c 'wineboot -u > /dev/null 2>&1; wineserver -w;
         wine reg add "HKCU\\Software\\Wine\\DllOverrides" /v winemenubuilder.exe /t REG_SZ /d "" /f > /dev/null 2>&1;
         wine reg delete "HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\RunServices" /v winemenubuilder /f > /dev/null 2>&1;
         wineserver -w'
grep -q '"winemenubuilder.exe"=""' "$template/user.reg"
# (wineboot would otherwise try to start the disabled winemenubuilder at every session start.)
if grep -q 'winemenubuilder.exe -a -r' "$template/system.reg"; then echo "RunServices still starts winemenubuilder" >&2; exit 1; fi
rm -rf /root/.local
test -f "$template/system.reg" -a -f "$template/user.reg" -a -d "$template/drive_c/users/osx86-template"

# Wine compares .update-timestamp with the modification time of wine.inf at every start and runs
# a full `wineboot -u` when they differ: they must be equal in the image (wineboot writes the
# number followed by CR LF).
test "$(tr -dc 0-9 < "$template/.update-timestamp")" = "$(stat -c %Y "$wine_root/share/wine/wine.inf")"

# Mono is a shared install (/usr/share/wine/mono): wineboot registered its support package only.
grep -q 'wine-mono-' "$template/system.reg"

# Duplicates of Wine, Mono and Gecko files become links.
find "$wine_root" /usr/share/wine -type f -size +0 -exec sha256sum {} + | sort -k1,1 -u > /tmp/osx86-reference.sum
linked=0
kept=0
find "$template" -type f -size +0 > /tmp/osx86-template.files
while IFS= read -r file; do
  digest="$(sha256sum "$file" | cut -d' ' -f1)"
  target="$(grep -m1 "^$digest " /tmp/osx86-reference.sum | cut -d' ' -f3- || true)"
  if [ -n "$target" ]; then
    ln -sf "$target" "$file"
    linked=$((linked + 1))
  else
    kept=$((kept + 1))
  fi
done < /tmp/osx86-template.files
rm -f /tmp/osx86-reference.sum /tmp/osx86-template.files

chown -R -h 0:0 "$template"
chmod -R u+rwX,go+rX,go-w "$template"
echo "template: $linked links, $kept files kept, $(du -sk "$template" | cut -f1) KiB"
