#!/bin/sh
# Os-x86 linux-base: PID 1 of the guest, started by the Alpine initramfs once the ext4 root (the
# IDE disk of v86, read-only) is mounted. No sysvinit, no udev, no login: mounts, hardening
# (decision 210 A, trial E7), the guest agent when the image carries one (work item 5), then a
# root shell on the serial console. The virtio-console ports hvc0 to hvc3 stay closed to users
# (the agent, work item 5, opens them: hvc0 is its protocol port, hvc1 to hvc3 are user consoles).
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
console=/dev/ttyS0

mount -t proc -o nodev,noexec,nosuid,hidepid=invisible proc /proc 2>/dev/null || mount -t proc -o nodev,noexec,nosuid proc /proc
mount -t sysfs -o nodev,noexec,nosuid sysfs /sys
mountpoint -q /dev || mount -t devtmpfs -o nosuid,mode=0755,size=4M devtmpfs /dev
mkdir -p /dev/pts /dev/shm
mount -t devpts -o gid=5,mode=0620,noexec,nosuid devpts /dev/pts
mount -t tmpfs -o nodev,nosuid,noexec,mode=1777 tmpfs /dev/shm
# 210 A: /run is 0755 (not the 1777 default of a tmpfs); /tmp and /var/tmp are sticky and nosuid.
mount -t tmpfs -o nodev,nosuid,noexec,mode=0755 tmpfs /run
mount -t tmpfs -o nodev,nosuid,mode=1777 tmpfs /tmp
mount -t tmpfs -o nodev,nosuid,mode=1777 tmpfs /var/tmp
mount -t tmpfs -o nodev,nosuid,noexec,mode=0755 tmpfs /var/log
# Home directories of the guest users (one per prefix, work items 9 and 10): in memory until a
# disk replaces this mount; nosuid and nodev whatever the medium (R134.5).
mount -t tmpfs -o nodev,nosuid,mode=0755 tmpfs /home
# cgroup v2: one cgroup per launch, created and killed by the agent as root (decision 206 A).
mount -t cgroup2 -o nodev,noexec,nosuid cgroup2 /sys/fs/cgroup 2>/dev/null

# 210 A: the root of the tree is 0755 even if the medium said otherwise; the serial console, the
# virtio-console ports, the framebuffer and the input devices belong to root alone until the
# agent hands a port to a user.
chmod 755 /
chmod 600 /dev/ttyS0 /dev/hvc0 /dev/hvc1 /dev/hvc2 /dev/hvc3 2>/dev/null
chmod 600 /dev/fb0 /dev/input/* 2>/dev/null

# R134.5 (trial E7): no user namespaces (unshare -U, unshare -n), no ptrace across users, no
# kernel addresses or log for users, protected links in sticky directories.
sysctl -q -w user.max_user_namespaces=0 kernel.yama.ptrace_scope=1 kernel.dmesg_restrict=1 \
  kernel.kptr_restrict=2 fs.protected_symlinks=1 fs.protected_hardlinks=1 \
  fs.protected_fifos=2 fs.protected_regular=2 2>/dev/null

hostname -F /etc/hostname 2>/dev/null
echo "OSX86-GUEST image=linux-base version=$(cat /etc/osx86-release) kernel=$(uname -r)" > $console

# The guest agent (work item 5): started as root when the image carries it; it owns hvc0.
if [ -x /usr/lib/osx86/agent ]; then
  /usr/lib/osx86/agent > /dev/null 2>&1 &
fi

echo "OSX86-READY" > $console

# A root shell on the serial console (the fallback of decision 212 A for an image without agent),
# session leader so that the port is its controlling terminal; restarted if it exits.
while :; do
  setsid sh -c "exec env HOME=/root TERM=vt100 PS1='osx86# ' sh -i < $console > $console 2>&1"
  sleep 1
done
