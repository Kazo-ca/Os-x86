#!/bin/sh
# Essai E8 — Wine : bouteille sur 9p (racine) contre bouteille copiée sur le disque ext4 (/mnt/disk).
# Usage : sh /root/wine9p.sh        (suppose /mnt/disk monté ; wineboot -u prend ~7 min dans v86, voir E1)
. /etc/profile.d/wine.sh
up() { cut -d' ' -f1 /proc/uptime; }
res() { echo "RES wine $1 $2"; }
timed() { k="$1"; shift; a=$(up); "$@" >/dev/null 2>/root/err.txt; rc=$?; b=$(up); res "$k" "$(awk -v a="$a" -v b="$b" 'BEGIN{printf "%.2f", b-a}') rc=$rc"; }
echo "MARK wine-start $(up)"
timed wineboot_9p wineboot -u
res prefix_kb "$(du -sk "$WINEPREFIX" | cut -f1)"
res prefix_files "$(find "$WINEPREFIX" -type f | wc -l)"
timed cmd_dir_9p_1 wine cmd /c dir 'C:\windows\system32'
timed cmd_dir_9p_2 wine cmd /c dir 'C:\windows\system32'
wineserver -k; sleep 2
timed cmd_dir_9p_coldserver wine cmd /c dir 'C:\windows\system32'
wineserver -k; sleep 2
timed prefix_copy_to_disk cp -a "$WINEPREFIX" /mnt/disk/wine
export WINEPREFIX=/mnt/disk/wine
timed cmd_dir_disk_coldserver wine cmd /c dir 'C:\windows\system32'
timed cmd_dir_disk_2 wine cmd /c dir 'C:\windows\system32'
wineserver -k; sleep 2
sync; echo 3 > /proc/sys/vm/drop_caches
timed cmd_dir_disk_dropped wine cmd /c dir 'C:\windows\system32'
wineserver -k
echo "MARK wine-done $(up)"
