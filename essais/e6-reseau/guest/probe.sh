#!/bin/sh
# E6 — inventaire de l'invite : config noyau, modules, applets, binaires setuid.
res() { echo "RES $*"; }
cfg=/root/kernel-config
[ -f "$cfg" ] || { zcat /proc/config.gz > /tmp/kc 2>/dev/null && cfg=/tmp/kc; }
res kernel "$(uname -r)"
for k in CONFIG_NET_NS CONFIG_USER_NS CONFIG_VETH CONFIG_NF_TABLES CONFIG_NFT_CT CONFIG_NF_CONNTRACK CONFIG_IP_NF_IPTABLES CONFIG_IP_NF_FILTER CONFIG_NETFILTER_XT_MATCH_OWNER CONFIG_NFT_META CONFIG_VIRTIO_NET CONFIG_VIRTIO_PCI CONFIG_IPV6 CONFIG_PACKET CONFIG_NAMESPACES CONFIG_PID_NS CONFIG_SECCOMP; do
  res kconfig "$k=$(grep -E "^$k=" "$cfg" 2>/dev/null | cut -d= -f2)"
done
res modules "$(lsmod | awk 'NR>1{print $1}' | tr '\n' ' ')"
res applets "$(busybox --list 2>/dev/null | grep -E -x 'nsenter|unshare|setpriv|ping|nc|httpd|nslookup|su' | tr '\n' ' ')"
res which "nsenter=$(command -v nsenter) unshare=$(command -v unshare) setpriv=$(command -v setpriv) su=$(command -v su) sudo=$(command -v sudo) doas=$(command -v doas) nft=$(command -v nft) iptables=$(command -v iptables) curl=$(command -v curl)"
res setuid "$(find / -xdev -type f \( -perm -4000 -o -perm -2000 \) 2>/dev/null | tr '\n' ' ')"
res ttyS0 "$(ls -l /dev/ttyS0 | awk '{print $1, $3, $4}')"
res ping_group_range "$(cat /proc/sys/net/ipv4/ping_group_range | tr '\t' ' ')"
res root_mount "$(grep ' / ' /proc/mounts)"
res mem "$(awk '/MemTotal/{t=$2}/MemAvailable/{a=$2}END{print "total_kb=" t " used_kb=" t-a}' /proc/meminfo)"
