#!/bin/sh
# E6 — reseau par bouteille (prototype jetable, invite Alpine dans v86).
# Un espace de noms reseau (netns) par bouteille, relie a l'espace racine par un veth ;
# routage pur, PAS de NAT : l'adresse source 10.0.N.2 est l'etiquette que la page (v86) voit
# dans chaque trame et qu'elle traduit en « bouteille N ». Un usager invite par bouteille
# (wine-bN, uid 100N), home en tmpfs nosuid,nodev,0700 ; nftables dans l'espace racine :
# b1 -> tcp 80 seulement, b2 -> rien, usager non root dans l'espace racine -> rien.
#
# usage : bottle-net.sh up          (eth0 par DHCP, netns, usagers, nft)
#         bottle-net.sh nft on|off|open  (pose / retire / ouvre les regles nft)
#         bottle-net.sh run N cmd...  (execute cmd dans la bouteille N, comme wine-bN)
#         bottle-net.sh runroot N cmd... (idem mais root, pour le diagnostic)
#         bottle-net.sh user0 cmd...  (usager wine-b2 dans l'espace racine : « programme hors bouteille »)
set -u
res() { echo "RES $*"; }
mark() { echo "MARK $1 $(cut -d' ' -f1 /proc/uptime)"; }

nft_on() {
  nft -f - <<'NFT' 2>&1
flush ruleset
table ip bottles {
  chain forward {
    type filter hook forward priority 0; policy drop;
    iifname "veth-b1" ip saddr 10.0.1.2 tcp dport 80 accept
    oifname "veth-b1" ip daddr 10.0.1.2 tcp sport 80 accept
    oifname "veth-b1" ip saddr 192.168.86.1 ip daddr 10.0.1.2 tcp dport 8080 accept
    iifname "veth-b1" ip saddr 10.0.1.2 tcp sport 8080 ip daddr 192.168.86.1 accept
  }
  chain output {
    type filter hook output priority 0; policy accept;
    oifname "eth0" meta skuid != 0 drop
  }
}
NFT
}

nft_open() {
  nft -f - <<'NFT' 2>&1
flush ruleset
table ip bottles {
  chain forward {
    type filter hook forward priority 0; policy drop;
    iifname "veth-b1" ip saddr 10.0.1.2 accept
    oifname "veth-b1" ip daddr 10.0.1.2 accept
  }
}
NFT
}

case "${1:-}" in
up)
  mark bottle-up-start
  modprobe virtio_net 2>/dev/null
  ip link set eth0 up
  udhcpc -i eth0 -q -n -t 8 >/dev/null 2>&1; res dhcp "rc=$? $(ip -4 -o addr show eth0 | awk '{print $4}')"
  sysctl -w net.ipv4.ip_forward=1 >/dev/null
  sysctl -w net.ipv4.conf.all.rp_filter=1 >/dev/null
  res sysctl_userns "$(sysctl -w user.max_user_namespaces=0 2>&1)"
  grep -q web.test /etc/hosts || echo "203.0.113.9 web.test" >> /etc/hosts  # pas de DNS : UDP 53 reste bloque par nft
  res devnull_avant "$(stat -c '%A %U' /dev/null)"; chmod 666 /dev/null   # mdev sans udev laisse /dev/null a root seul
  cp /root/verify-confinement.sh /tmp/verify-confinement.sh && chmod 644 /tmp/verify-confinement.sh  # /root est 0700
  for i in 1 2; do
    ip netns add b$i
    ip link add veth-b$i type veth peer name eth0 netns b$i
    ip addr add 10.0.$i.1/24 dev veth-b$i && ip link set veth-b$i up
    ip -n b$i addr add 10.0.$i.2/24 dev eth0
    ip -n b$i link set lo up && ip -n b$i link set eth0 up
    ip -n b$i route add default via 10.0.$i.1
    useradd -M -u 100$i -U -s /bin/sh wine-b$i 2>&1 | grep -v '^$'
    mkdir -p /home/wine-b$i
    mount -t tmpfs -o nosuid,nodev,mode=0700,uid=100$i,gid=100$i tmpfs /home/wine-b$i
    mkdir -m 700 /tmp/.wine-100$i && chown 100$i:100$i /tmp/.wine-100$i
    res netns_b$i "$(ip -n b$i -4 -o addr show eth0 | awk '{print $4}') uid=$(id -u wine-b$i)"
  done
  res nft "$(nft_on | tr '\n' ' ')rc=$?"
  res nft_rules "$(nft list ruleset 2>/dev/null | grep -c -E 'accept|drop') regles"
  mark bottle-up-done
  ;;
nft)
  case "$2" in
    on)   res nft "$(nft_on | tr '\n' ' ')rc=$?";;
    off)  nft flush ruleset; res nft "flushed";;
    open) res nft "$(nft_open | tr '\n' ' ')rc=$?";;
  esac
  ;;
run)
  n=$2; shift 2
  exec ip netns exec b$n su -s /bin/sh wine-b$n -c "$*"
  ;;
runroot)
  n=$2; shift 2
  exec ip netns exec b$n sh -c "$*"
  ;;
user0)
  shift
  exec su -s /bin/sh wine-b2 -c "$*"
  ;;
*) echo "usage: $0 up|nft on|off|open|run N cmd|runroot N cmd|user0 cmd"; exit 2;;
esac
