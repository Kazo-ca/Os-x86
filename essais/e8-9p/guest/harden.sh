#!/bin/sh
# Essai E8 — durcissement : ce que le serveur 9p de v86 accepte depuis l'invité. Imprime "HARD <test> <résultat>".
# Usage : sh /root/harden.sh <dossier partagé monté>
S="$1"; SKIP2G="$2"
h() { echo "HARD $1 $2"; }
try() { k="$1"; shift; out=$("$@" 2>&1); rc=$?; h "$k" "rc=$rc $(echo "$out" | head -c 160 | tr '\n' ' ')"; }
mkdir -p "$S" && mount -t 9p -o trans=virtio,cache=loose host9p "$S" 2>/dev/null
rm -rf "$S/h"; mkdir -p "$S/h"; cd "$S/h" || exit 1
echo "MARK harden-start $(cut -d' ' -f1 /proc/uptime)"

# (a) liens symboliques hors du dossier partagé
try a_symlink_abs ln -s /etc/passwd "$S/h/x_abs"
try a_symlink_abs_read sh -c "head -c 40 $S/h/x_abs"
try a_symlink_rel ln -s ../../../../etc/hostname "$S/h/x_rel"
try a_symlink_rel_read sh -c "cat $S/h/x_rel"
try a_symlink_readlink readlink "$S/h/x_abs"
try a_symlink_loop sh -c "ln -s loop2 $S/h/loop1; ln -s loop1 $S/h/loop2; cat $S/h/loop1"
try a_symlink_dir_escape sh -c "ln -s / $S/h/rootlink; ls $S/h/rootlink/etc | head -3"

# (b) noms que Kazo (Windows) refuse
mkdir -p "$S/h/names"; cd "$S/h/names"
for n in CON NUL PRN AUX COM1 LPT1 con.txt nul.tar 'trailing.' 'trailing ' '...' 'a:b' 'a\b' 'a*b' 'a?b' 'a"b' 'a<b' 'a>b' 'a|b' ' ' '  lead' 'Readme' 'readme' 'README'; do
  if touch -- "$n" 2>/dev/null; then r=ok; else r=refused; fi
  h "b_name_$(printf '%s' "$n" | od -An -tx1 | tr -d ' \n')" "$r"
done
try b_name_ctrl touch "$(printf 'a\001b')"
try b_name_del touch "$(printf 'a\177b')"
try b_name_newline touch "$(printf 'a\nb')"
try b_name_tab touch "$(printf 'a\tb')"
try b_name_latin1 touch "$(printf 'caf\351')"
try b_name_latin1_bis touch "$(printf 'caf\350')"
try b_name_badutf8 touch "$(printf '\300\257etc')"
try b_name_nfc touch "$(printf 'caf\303\251-nfc')"
try b_name_nfd touch "$(printf 'cafe\314\201-nfd')"
try b_name_255 touch "$(printf 'L%.0s' $(seq 1 255))"
try b_name_256 touch "$(printf 'M%.0s' $(seq 1 256))"
try b_name_300 touch "$(printf 'N%.0s' $(seq 1 300))"
try b_name_1000 touch "$(printf 'P%.0s' $(seq 1 1000))"
try b_dir_count sh -c "ls -a | wc -l"
try b_case_distinct sh -c "echo A > Readme; echo B > readme; cat Readme readme | tr '\n' ' '"
# chemin très long (profondeur)
try b_deep_path sh -c "cd $S/h; d=.; for i in \$(seq 1 300); do d=\$d/d0123456789; done; mkdir -p \$d && echo \$(echo \$d | wc -c) bytes"

# (c) tailles
cd "$S/h"
try c_17MiB dd if=/dev/zero of="$S/h/big17" bs=1M count=17
try c_17MiB_stat stat -c '%s' "$S/h/big17"
try c_17MiB_rm rm -f "$S/h/big17"
try c_sparse_1G sh -c "truncate -s 1G $S/h/sparse1 && stat -c '%s %b' $S/h/sparse1"
try c_sparse_1G_tail sh -c "dd if=$S/h/sparse1 of=/dev/null bs=1M skip=1023 count=1 2>&1 | tail -1"
try c_sparse_1G_rm rm -f "$S/h/sparse1"
echo "MARK harden-before-2G $(cut -d' ' -f1 /proc/uptime)"
[ -n "$SKIP2G" ] || try c_sparse_2G sh -c "truncate -s 2G $S/h/sparse2 && stat -c '%s %b' $S/h/sparse2"
[ -n "$SKIP2G" ] || try c_sparse_2G_write sh -c "dd if=/dev/zero of=$S/h/sparse2 bs=4k seek=524287 count=1 conv=notrunc 2>&1 | tail -1"
[ -n "$SKIP2G" ] || try c_sparse_2G_rm rm -f "$S/h/sparse2"
echo "MARK harden-after-2G $(cut -d' ' -f1 /proc/uptime)"

# (e) .., liens durs, nœuds de périphérique, setuid, xattr, rename hors du partage
try e_dotdot_ls sh -c "ls $S/h/../.. | head -3 | tr '\n' ' '"
try e_dotdot_create sh -c "echo z > $S/../e8_escape && ls -l $S/../e8_escape; rm -f $S/../e8_escape"
try e_hardlink_in sh -c "echo a > $S/h/hl1 && ln $S/h/hl1 $S/h/hl2 && stat -c '%h' $S/h/hl1"
try e_hardlink_out ln /etc/hostname "$S/h/hl_out"
try e_hardlink_dir ln "$S/h/names" "$S/h/hl_dir"
try e_mknod_char sh -c "mknod $S/h/zero c 1 5 && ls -l $S/h/zero && head -c 4 $S/h/zero | od -An -tx1"
try e_mknod_block sh -c "mknod $S/h/sda b 8 0 && ls -l $S/h/sda"
try e_mkfifo sh -c "mkfifo $S/h/fifo && ls -l $S/h/fifo"
try e_setuid sh -c "cp /bin/busybox $S/h/suidbb && chmod 4755 $S/h/suidbb && ls -l $S/h/suidbb | cut -c1-10"
try e_setuid_exec sh -c "cp $S/h/suidbb $S/h/id && chmod 4755 $S/h/id && $S/h/id && grep ' / ' /proc/mounts | cut -d' ' -f3,4"
try e_xattr_set setfattr -n user.e8 -v hello "$S/h/hl1"
try e_xattr_get getfattr -n user.e8 "$S/h/hl1"
try e_rename_out sh -c "echo m > $S/h/mv1 && mv $S/h/mv1 /root/mv1_out && ls -l /root/mv1_out; rm -f /root/mv1_out"
try e_rename_in sh -c "echo m > /root/mv2 && mv /root/mv2 $S/h/mv2_in && ls -l $S/h/mv2_in"
try e_root_perms sh -c "ls -ld $S $S/h | cut -c1-10 | tr '\n' ' '"
try e_chown sh -c "chown 1234:5678 $S/h/hl1 && stat -c '%u:%g' $S/h/hl1"
echo "MARK harden-done $(cut -d' ' -f1 /proc/uptime)"
