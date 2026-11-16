#!/bin/sh
# Os-x86 wine image (work item 9; decisions 201 A, 210 A): the slim step of the wine image. Run as
# root by build-wine.py with `docker run` on the image with the template prefix, then committed.
# Mounts: /recipe (this directory), /linux-base (images/linux-base: its slim.sh runs last, the same
# step as linux-base), /out (hot-files.list from trace-hot.sh). Argument: the image version.
#
# 1. The Debian packages Wine pulled in that a console program never reaches are purged: Wine's
#    hard dependencies are kept except those of the media, sound server, scanner, camera,
#    OpenCL, packet capture and Wayland modules (those modules then fail to load, which is what
#    Wine does on a system without them); the rest is the dependency closure of what stays.
# 2. Wine's development tools and import libraries go (winegcc, widl, the .a files...).
# 3. The hot file list, the preloading hook, the prefix copy script and the image name go in.
# 4. images/linux-base/slim.sh: the package inventory, the license list and the common trimming.
set -eu
version="$1"
export DEBIAN_FRONTEND=noninteractive

# --- 1. packages ---------------------------------------------------------------------------------
dropped=" libgphoto2-6t64 libgphoto2-port12t64 libgstreamer-plugins-base1.0-0 libgstreamer1.0-0 libpcap0.8t64 \
libpulse0 libsane1 libwayland-client0 libwayland-egl1 ocl-icd-libopencl1 libasound2-plugins libglib2.0-0t64 \
libxkbcommon0 libxkbregistry0 "
# Wine's hard dependencies (first alternative of each), minus the dropped ones. The three Wine
# packages are kept but are not roots: their own dependencies would bring everything back.
roots="libfreetype6 libfontconfig1 libgnutls30t64"
for dependency in $(dpkg-query -W -f '${Depends}' wine-stable-i386 | tr ',' '\n' | sed 's/|.*//; s/(.*)//; s/ //g'); do
  case "$dropped" in *" $dependency "*) ;; *) roots="$roots $dependency";; esac
done
# What the base stage of linux-base had (install-wine.sh recorded it before installing anything).
roots="$roots $(cat /var/lib/osx86-build/base-packages.txt)"
apt-cache depends --recurse --installed --no-recommends --no-suggests --no-conflicts --no-breaks \
  --no-replaces --no-enhances $roots 2>/dev/null | grep -v '^ ' | sed 's/:i386$//' | sort -u > /tmp/osx86-keep
dpkg-query -W -f '${Package}\n' | sort -u > /tmp/osx86-installed
purge="$(comm -23 /tmp/osx86-installed /tmp/osx86-keep | grep -v -x -e winehq-stable -e wine-stable -e wine-stable-i386 | tr '\n' ' ')"
echo "purged ($(echo $purge | wc -w)): $purge"
# The maintainer scripts of a few of them (systemd, udev) expect a booted system: their errors
# are ignored, then nothing of the list may remain.
dpkg --purge --force-depends --force-remove-essential $purge > /tmp/osx86-purge.log 2>&1 || true
remaining="$(dpkg-query -W -f '${Package} ${Status}\n' $purge 2>/dev/null | grep -v 'not-installed' || true)"
test -z "$remaining" || { cat /tmp/osx86-purge.log; echo "still installed: $remaining"; exit 1; }
# The installers of xz-utils and ca-certificates were only needed by install-wine.sh.
dpkg --purge --force-depends ca-certificates xz-utils > /dev/null 2>&1 || true
rm -rf /etc/ssl/certs /usr/share/ca-certificates /etc/apt/keyrings /etc/apt/sources.list.d/winehq-trixie.sources /var/lib/osx86-build

# --- 2. Wine's development tools -------------------------------------------------------------------
wine_root=/opt/wine-stable
find "$wine_root" -name '*.a' -delete
for tool in function_grep.pl widl winebuild winecpp winedump wineg++ winegcc winemaker wmc wrc; do
  rm -f "$wine_root/bin/$tool" "/usr/bin/$tool"
done
rm -rf "$wine_root/include" "$wine_root/share/man" "$wine_root/share/applications"

# --- 3. the pieces of this image -------------------------------------------------------------------
install -d -m 755 /usr/lib/osx86/wine /usr/lib/osx86/init.d
install -m 644 /out/hot-files.list /usr/lib/osx86/wine/hot-files.list
install -m 755 /recipe/preload.sh /usr/lib/osx86/init.d/50-preload-wine
echo wine > /etc/osx86-image
echo "$version" > /etc/osx86-release
# 210 A: the packages installed after the base stage are hardened the same way.
find / -xdev -type f \( -perm -4000 -o -perm -2000 \) -exec chmod u-s,g-s {} +
# (/tmp/.wine-0 is the server directory of the wineboot run as root: hidden files too.)
rm -rf /run/lock /run/* /tmp/* /tmp/.[!.]* /var/tmp/* /var/tmp/.[!.]* /root/.cache /root/.wine /root/.local

# --- 4. the common slim step ----------------------------------------------------------------------
sh /linux-base/slim.sh
# The hot list names only files the image still has after the trimming.
while IFS= read -r path; do [ -f "$path" ] && echo "$path"; done < /out/hot-files.list > /usr/lib/osx86/wine/hot-files.list
echo "hot files: $(wc -l < /usr/lib/osx86/wine/hot-files.list) of $(wc -l < /out/hot-files.list) traced"
du -sm "$wine_root" /usr/share/wine /usr/lib/osx86/wine/prefix-template | cat
