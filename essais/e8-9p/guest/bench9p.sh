#!/bin/sh
# Essai E8 — mesures côté invité. Imprime "RES <label> <clé> <valeur>" et "MARK <nom> <uptime>".
# Usage : sh /root/bench9p.sh <cible> <label>     label = share (9p) | disk (ext4 sur hda) | tmpfs
# La cible est montée par le script (le temps de montage est mesuré).
T="$1"; L="$2"
up() { cut -d' ' -f1 /proc/uptime; }
mark() { echo "MARK $1 $(up)"; }
res() { echo "RES $L $1 $2"; }
# timed <clé> <commande...> : durée en secondes (résolution 0,01 s) ; imprime aussi le code de retour.
timed() { k="$1"; shift; a=$(up); "$@" >/dev/null 2>/root/err.txt; rc=$?; b=$(up); res "$k" "$(awk -v a="$a" -v b="$b" 'BEGIN{printf "%.2f", b-a}') rc=$rc"; [ $rc -ne 0 ] && echo "ERR $k: $(head -c 200 /root/err.txt)"; }
dropc() { sync; echo 3 > /proc/sys/vm/drop_caches; }

mark "bench-start-$L"
mkdir -p "$T"
case "$L" in
  share) timed mount mount -t 9p -o trans=virtio,cache=loose host9p "$T" ;;
  disk)
    timed modprobe modprobe ext4
    DEV=""; for d in /dev/sda /dev/vda /dev/hda; do [ -b "$d" ] && DEV="$d" && break; done
    res dev "${DEV:-none} $(dmesg | grep -ci 'ata\|sda')"
    timed mount mount -t ext4 "$DEV" "$T" ;;
  tmpfs) timed mount mount -t tmpfs -o size=400m tmpfs "$T" ;;
esac
res mounts "$(grep -c " $T " /proc/mounts)"
echo "MOUNTINFO $(grep " $T " /proc/mounts)"

# ---------- 1. Reproduction de l'issue #1451 : 500 x 5 répertoires, cat de 2 500 petits fichiers ----------
W="$T/w1451"
rm -rf "$W"; mkdir -p "$W"
a=$(up)
i=0; while [ $i -lt 500 ]; do
  j=0; while [ $j -lt 5 ]; do mkdir -p "$W/row/$i/col/$j"; echo "Content" > "$W/row/$i/col/$j/data.txt"; j=$((j+1)); done
  i=$((i+1))
done
b=$(up); res 1451_create "$(awk -v a="$a" -v b="$b" 'BEGIN{printf "%.2f", b-a}')"
timed 1451_cat_1 sh -c "cat $W/row/*/col/*/data.txt"
timed 1451_cat_2 sh -c "cat $W/row/*/col/*/data.txt"
dropc
timed 1451_cat_dropped sh -c "cat $W/row/*/col/*/data.txt"
timed 1451_find_stat sh -c "find $W -type f | xargs stat"
timed 1451_rm rm -rf "$W"

# ---------- 2. Gros fichier : écriture puis lecture ----------
timed big_write_64M dd if=/dev/zero of="$T/big.bin" bs=1M count=64
timed big_read_64M_cached dd if="$T/big.bin" of=/dev/null bs=1M
dropc
timed big_read_64M_dropped dd if="$T/big.bin" of=/dev/null bs=1M
timed big_rm rm -f "$T/big.bin"

# ---------- 3. Archive ~50 Mio / ~3 000 fichiers : copie, extraction, copie d'arbre, stat, suppression ----------
ARCH=/mnt/tmp/arch.tar
if [ ! -f "$ARCH" ]; then
  mkdir -p /mnt/tmp; mount -t tmpfs -o size=300m tmpfs /mnt/tmp 2>/dev/null
  # Liste : fichiers de l'image (< 1 Mio, Wine inclus), petits d'abord, jusqu'à 3 000 fichiers ou 50 Mio (busybox find n'a pas -printf).
  find /usr /etc /lib /bin /sbin -type f -size -1024k -exec stat -c '%s %n' {} + | sort -n | awk 'BEGIN{s=0;n=0}{s+=$1;n++; if(n>3000||s>52428800) exit; print $2}' > /mnt/tmp/list.txt
  a=$(up); tar cf "$ARCH" -T /mnt/tmp/list.txt; b=$(up)
  res arch_build_from_9p_root "$(awk -v a="$a" -v b="$b" 'BEGIN{printf "%.2f", b-a}')"
fi
res arch_bytes "$(stat -c %s "$ARCH")"
res arch_files "$(wc -l < /mnt/tmp/list.txt)"
rm -rf "$T/x" "$T/y" "$T/arch.tar"
timed arch_copy_in cp "$ARCH" "$T/arch.tar"
mkdir -p "$T/x"
timed arch_extract tar xf "$T/arch.tar" -C "$T/x"
timed tree_find_count sh -c "find $T/x -type f | wc -l"
timed tree_stat_all sh -c "find $T/x -type f | xargs stat"
timed tree_cat_all sh -c "find $T/x -type f | xargs cat"
timed tree_copy cp -a "$T/x" "$T/y"
dropc
timed tree_cat_all_dropped sh -c "find $T/y -type f | xargs cat"
timed tree_rm_x rm -rf "$T/x"
timed tree_rm_y rm -rf "$T/y"
timed arch_rm rm -f "$T/arch.tar"

# ---------- 4. Lecture à froid d'un gros fichier de l'image (téléchargé à la demande) ----------
if [ "$L" = share ]; then
  F=/usr/lib/libgallium-26.1.6.so
  res image_file_bytes "$(stat -c %s $F)"
  dropc
  timed image_read_cold dd if=$F of=/dev/null bs=1M
  timed image_read_pagecache dd if=$F of=/dev/null bs=1M
  dropc
  timed image_read_v86cache dd if=$F of=/dev/null bs=1M
  # Petits fichiers de l'image : stat et lecture de tous les fichiers de Wine (à froid pour v86, sauf ce que le boot a touché).
  timed wine_find_stat sh -c "find /usr/lib/wine -type f | xargs stat"
  res wine_files "$(find /usr/lib/wine -type f | wc -l)"
fi
res umount "$(umount "$T" 2>&1 && echo ok)"
mark "bench-done-$L"
