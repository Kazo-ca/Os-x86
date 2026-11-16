#!/bin/sh
# Os-x86 wine image (milestone 0, work item 9; decision 201 A): installs the pinned WineHQ
# packages, Mono and Gecko on the `base` stage of linux-base. Run by build-wine.py with
# `docker run` (not a Dockerfile RUN: the seccomp profile BuildKit applies to its RUN sandboxes
# refuses socket(2) to a 32-bit container on the GitHub runners, see images/linux-base), then the
# container is committed. Arguments: the pinned Wine version (Debian version string), the Mono
# version, the Gecko version. Mounts: /recipe (this directory, read-only) and /download (the
# archives checked by build-wine.py against their pinned SHA-256, read-only).
set -eu
wine_version="$1"
mono_version="$2"
gecko_version="$3"
export DEBIAN_FRONTEND=noninteractive
# --force-confold: the hardened /etc/adduser.conf of the base stage is kept (adduser is pulled in).
apt_get() { apt-get -o Acquire::Retries=5 -o APT::Install-Recommends=false -o Dpkg::Options::=--force-confold "$@"; }

# The packages of the base stage, kept by slim-wine.sh whatever Wine pulls in.
install -d -m 755 /var/lib/osx86-build
dpkg-query -W -f '${Package}\n' | sort -u > /var/lib/osx86-build/base-packages.txt

# apt reaches dl.winehq.org over HTTPS: the certificate store comes from Debian first (xz-utils
# unpacks Mono and Gecko; both are purged again by the slim step).
apt_get -qq update
apt_get -qq install -y ca-certificates xz-utils > /dev/null
install -d -m 755 /etc/apt/keyrings
install -m 644 /download/winehq.key /etc/apt/keyrings/winehq-archive.key
install -m 644 /recipe/winehq-trixie.sources /etc/apt/sources.list.d/winehq-trixie.sources
apt_get -qq update

# Wine itself, pinned, and the three libraries it recommends that a console program can reach
# (fonts for gdi32, gnutls for bcrypt and the TLS of the crypto API); no X server, no sound
# server, no Wine recommendation beyond these (milestone 0 is console only).
apt_get -qq install -y "winehq-stable=$wine_version" "wine-stable=$wine_version" "wine-stable-i386=$wine_version" \
  libfreetype6 libfontconfig1 libgnutls30t64 > /dev/null
test "$(dpkg-query -W -f '${Version}' wine-stable-i386)" = "$wine_version"

# Mono and Gecko as shared installs (Wine looks in /usr/share/wine/<addon>/ before asking to
# download): unpacked once in the image, never copied into a prefix.
install -d -m 755 /usr/share/wine/mono /usr/share/wine/gecko
tar -xJf "/download/wine-mono-$mono_version-x86.tar.xz" -C /usr/share/wine/mono --no-same-owner
tar -xJf "/download/wine-gecko-$gecko_version-x86.tar.xz" -C /usr/share/wine/gecko --no-same-owner
test -d "/usr/share/wine/mono/wine-mono-$mono_version" -a -d "/usr/share/wine/gecko/wine-gecko-$gecko_version-x86"
chmod -R u=rwX,go=rX /usr/share/wine

apt_get clean
