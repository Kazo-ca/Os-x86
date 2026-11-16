#!/bin/sh
# Os-x86 linux-base: the slim step of every Linux image built on the `base` stage (milestone 0,
# work items 4 and 9). Run as root on the tree that becomes the published root: by the `slim`
# stage of images/linux-base/Dockerfile, and by images/wine/build-wine.py (in a `docker run`, after
# the Wine packages). Writes the package inventory and the license list of what is really in the
# image (plan section 12), then removes what nothing in the VM runs.
set -eu
# Packages that are only useful to install more packages, or that nothing in the VM runs.
dpkg --purge --force-depends --force-remove-essential apt libapt-pkg7.0 debian-archive-keyring sqv perl-base 2>/dev/null || true
# Package inventory and licenses, written after the purge and before the dpkg database is trimmed.
dpkg-query -W -f '${binary:Package}\t${Version}\t${source:Package}\t${source:Version}\n' | sort > /usr/lib/osx86/packages.tsv
for copyright in /usr/share/doc/*/copyright; do
  package="$(basename "$(dirname "$copyright")")"
  grep -q "^$package" /usr/lib/osx86/packages.tsv || continue
  licenses="$(grep -E '^License:' "$copyright" | sed 's/^License: *//' | sort -u | tr '\n' ';' | sed 's/;$//')"
  printf '%s\t%s\n' "$package" "${licenses:-see copyright file}"
done > /usr/lib/osx86/licenses.tsv
for doc in /usr/share/doc/*; do grep -q "^$(basename "$doc")" /usr/lib/osx86/packages.tsv || rm -rf "$doc"; done

rm -rf /var/lib/apt /var/cache/apt /var/cache/debconf/*-old /var/log/* /var/lib/dpkg/info /var/lib/dpkg/*-old
rm -rf /usr/share/man /usr/share/info /usr/share/locale /usr/share/lintian /usr/share/bug /usr/share/perl /usr/share/perl5 /usr/lib/i386-linux-gnu/perl* /usr/lib/i386-linux-gnu/perl-base /usr/bin/perl*
# /usr/share/doc: the copyright files stay (they are the license texts of the image), the rest goes.
find /usr/share/doc -mindepth 2 ! -name copyright -type f -delete
find /usr/share/doc -mindepth 2 -type d -empty -delete
# Character set converters other than the Unicode ones; time zones other than UTC.
find /usr/lib/i386-linux-gnu/gconv -type f ! -name 'gconv-modules*' ! -name 'UTF-*' ! -name 'UNICODE*' ! -name 'IBM437*' ! -name 'CP1252*' ! -name 'ISO8859-1*' -delete
find /usr/share/zoneinfo -mindepth 1 ! -name UTC ! -name UCT ! -name Zulu ! -name 'Etc' ! -path '*/Etc/*' ! -name 'zone*.tab' ! -name 'tzdata.zi' ! -name 'leap*' ! -name 'iso3166.tab' -delete 2>/dev/null || true
rm -f /etc/machine-id /var/lib/dbus/machine-id
du -sm /usr /lib /var /etc /opt 2>/dev/null | cat
