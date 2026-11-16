#!/bin/sh
# Os-x86 linux-base (milestone 0, work item 12; decisions 161 A, 174 A, 146, trial E6): the guest
# side of the private network. Installed as /usr/lib/osx86/net-admin (root, 0755) and run as root
# by the guest agent only (operation net), with numbers and a prefix name it has already checked:
#
#     net-admin vm-up <vm-slot>                          eth0 at 10.86.0.<vm-slot>/24, default
#                                                        route through the gateway 10.86.0.1, the
#                                                        rules of the VM's namespace
#     net-admin prefix-up <prefix> <vm-slot> <slot>      the namespace osx86-<prefix>: its link
#                                                        vx<slot> (10.86.<vm-slot>.4*<slot>+1/30 on
#                                                        the VM side), its own address
#                                                        10.86.<vm-slot>.4*<slot>+2, its rules
#     net-admin prefix-down <prefix>                     the namespace of the prefix removed
#
# Exit codes: 0 something was done, 10 nothing to do, anything else a failure (its last line says why).
#
# Routing only, no NAT (E6): the prefix's address is what the gateway of Os-x86 reads in every
# frame to know which prefix speaks; the decision, the limits and the journal are there (174 A).
# The rules here are the defence in depth: a prefix reaches only TCP out of the private network,
# never another prefix, another VM or the VM's own services; UDP 53 and IPv6 are refused by an
# explicit rule; a user of the VM outside a prefix namespace has no network at all.
set -eu
umask 022
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
NOTHING=10
rules=/usr/lib/osx86/net

fail() { echo "net-admin: $*"; exit 2; }

slot() {
  case "$1" in ''|*[!0-9]*) fail "invalid slot '$1'";; esac
  [ "${#1}" -le 3 ] && [ "$1" -ge "$2" ] && [ "$1" -le "$3" ] || fail "slot $1 is not $2 to $3"
}

prefix_name() {
  case "$1" in ''|-*|*[!a-z0-9-]*) fail "invalid prefix '$1'";; esac
  [ "${#1}" -le 16 ] || fail "invalid prefix '$1' (more than 16 characters)"
}

[ "$(id -u)" -eq 0 ] || fail "must run as root"
action="${1:-}"

# One change of the network at a time.
mkdir -p /run/osx86-admin
exec 9> /run/osx86-admin/net.lock
flock 9

# The modules of the private network (modules.keep): loaded once, quietly; a missing one shows
# when ip or nft fail below.
load_modules() {
  for module in virtio_net veth nf_tables nf_conntrack nft_ct nft_reject nft_reject_inet; do
    modprobe -q "$module" 2> /dev/null || true
  done
}

case "$action" in
  vm-up)
    vm="${2:-}"
    slot "$vm" 2 254
    load_modules
    ip link show eth0 > /dev/null 2>&1 || fail "the VM has no network card (eth0)"
    address="10.86.0.$vm"
    changed=$NOTHING
    if ! ip -4 -o addr show dev eth0 | grep -q " inet $address/24 "; then
      ip -4 addr flush dev eth0
      ip addr add "$address/24" dev eth0
      changed=0
    fi
    ip link set lo up
    ip link set eth0 up
    ip route replace default via 10.86.0.1 dev eth0
    sysctl -q -w net.ipv4.ip_forward=1 net.ipv6.conf.all.disable_ipv6=1 net.ipv6.conf.default.disable_ipv6=1 \
      net.ipv4.conf.all.rp_filter=1 net.ipv4.conf.default.rp_filter=1
    nft -f "$rules/vm.nft"
    echo "net-admin: eth0 at $address"
    exit "$changed"
    ;;
  prefix-up)
    prefix="${2:-}"
    vm="${3:-}"
    index="${4:-}"
    prefix_name "$prefix"
    slot "$vm" 2 254
    slot "$index" 0 63
    namespace="osx86-$prefix"
    link="vx$index"
    link_address="10.86.$vm.$((4 * index + 1))"
    address="10.86.$vm.$((4 * index + 2))"
    if [ -e "/run/netns/$namespace" ]; then
      if ip link show "$link" > /dev/null 2>&1 && ip -n "$namespace" -4 -o addr show dev eth0 2> /dev/null | grep -q " inet $address/30 "; then
        exit "$NOTHING"
      fi
      ip netns del "$namespace" # from another slot: made again
    fi
    load_modules
    ip link del "$link" 2> /dev/null || true # left by a prefix that had this slot
    ip netns add "$namespace"
    ip link add "$link" type veth peer name eth0 netns "$namespace"
    ip addr add "$link_address/30" dev "$link"
    ip link set "$link" up
    ip -n "$namespace" link set lo up
    ip -n "$namespace" addr add "$address/30" dev eth0
    ip -n "$namespace" link set eth0 up
    ip -n "$namespace" route add default via "$link_address"
    ip netns exec "$namespace" sysctl -q -w net.ipv6.conf.all.disable_ipv6=1 net.ipv6.conf.default.disable_ipv6=1
    ip netns exec "$namespace" nft -f "$rules/prefix.nft"
    echo "net-admin: $namespace at $address"
    exit 0
    ;;
  prefix-down)
    prefix="${2:-}"
    prefix_name "$prefix"
    if [ -e "/run/netns/osx86-$prefix" ]; then
      ip netns del "osx86-$prefix"
      echo "net-admin: osx86-$prefix removed"
      exit 0
    fi
    exit "$NOTHING"
    ;;
  *)
    fail "usage: net-admin vm-up <vm-slot> | prefix-up <prefix> <vm-slot> <slot> | prefix-down <prefix>"
    ;;
esac
