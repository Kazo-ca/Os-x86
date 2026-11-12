#!/bin/sh
# Os-x86 test guest (images/alpine-test): PID 1 of the initramfs, put in place of the Alpine
# init by build-alpine-test.py. No boot media, no network, no package manager: the kernel, a
# busybox and this script. Prints OSX86-READY on the serial console, then keeps a shell on it.
# When the image carries the guest agent (work item 5), it is started as root on hvc0, with the
# user consoles on hvc1; hvc2 and hvc3 stay free for the engine tests. Without the agent, the
# four virtio-console ports are free.
export PATH=/usr/bin:/bin:/usr/sbin:/sbin
bb=/bin/busybox
console=/dev/ttyS0

$bb --install -s 2>/dev/null
$bb mkdir -p /proc /sys /dev /tmp /run /home
$bb mount -t proc -o nodev,noexec,nosuid proc /proc
$bb mount -t sysfs -o nodev,noexec,nosuid sysfs /sys
$bb mount -t devtmpfs -o exec,nosuid,mode=0755,size=2M devtmpfs /dev 2>/dev/null || $bb mdev -s
$bb mkdir -p /dev/pts /dev/shm
$bb mount -t devpts -o gid=5,mode=0620,noexec,nosuid devpts /dev/pts
$bb mount -t tmpfs -o nodev,nosuid,mode=0755 tmpfs /run
$bb mount -t tmpfs -o nodev,nosuid,mode=1777 tmpfs /tmp
# cgroup v2: one cgroup per launch, created and killed by the agent as root (decision 206 A).
$bb mount -t cgroup2 -o nodev,noexec,nosuid cgroup2 /sys/fs/cgroup 2>/dev/null

# The root of the initramfs is 0700: a guest user could not even reach its home. The serial
# console and the virtio-console ports belong to root alone, as in linux-base (decision 210 A).
$bb chmod 755 /
$bb chmod 600 /dev/ttyS0 /dev/hvc0 /dev/hvc1 /dev/hvc2 /dev/hvc3 2>/dev/null
$bb chmod 666 /dev/null /dev/zero /dev/urandom /dev/tty /dev/ptmx 2>/dev/null

# The home of each guest user of the agent tests (osx86-<prefix>, added to /etc/passwd by the
# recipe): 0700, as in linux-base.
while IFS=: read -r user _ uid gid _ home _; do
  case "$user" in
    osx86-*) $bb mkdir -p "$home" && $bb chown "$uid:$gid" "$home" && $bb chmod 700 "$home" ;;
  esac
done < /etc/passwd

echo "OSX86-GUEST alpine=$($bb cat /etc/osx86-alpine-release) kernel=$($bb uname -r)" > $console

# The guest agent (work item 5): started as root when the image carries it; it owns hvc0 and hvc1.
if [ -x /usr/lib/osx86/agent ]; then
  /usr/lib/osx86/agent > /dev/null 2>&1 &
fi

echo "OSX86-READY" > $console

# A session leader whose first terminal is the serial port: that port becomes its controlling
# terminal, so the shell gets job control. Restarted if it ever exits (PID 1 must not).
while :; do
  $bb setsid $bb sh -c "exec $bb env ENV=/etc/osx86-profile $bb sh -i < $console > $console 2>&1"
  $bb sleep 1
done
