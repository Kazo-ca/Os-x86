#!/bin/sh
# Os-x86 test guest (images/alpine-test): PID 1 of the initramfs, put in place of the Alpine
# init by build-alpine-test.py. No boot media, no network, no package manager: the kernel, a
# busybox and this script. Prints OSX86-READY on the serial console, then keeps a shell on it.
# The virtio-console ports (hvc0 to hvc3) are left free for the engine tests.
export PATH=/usr/bin:/bin:/usr/sbin:/sbin
bb=/bin/busybox
console=/dev/ttyS0

$bb --install -s 2>/dev/null
$bb mkdir -p /proc /sys /dev /tmp /run
$bb mount -t proc -o nodev,noexec,nosuid proc /proc
$bb mount -t sysfs -o nodev,noexec,nosuid sysfs /sys
$bb mount -t devtmpfs -o exec,nosuid,mode=0755,size=2M devtmpfs /dev 2>/dev/null || $bb mdev -s
$bb mkdir -p /dev/pts /dev/shm
$bb mount -t devpts -o gid=5,mode=0620,noexec,nosuid devpts /dev/pts
$bb mount -t tmpfs -o nodev,nosuid,mode=0755 tmpfs /run

echo "OSX86-GUEST alpine=$($bb cat /etc/osx86-alpine-release) kernel=$($bb uname -r)" > $console
echo "OSX86-READY" > $console

# A session leader whose first terminal is the serial port: that port becomes its controlling
# terminal, so the shell gets job control. Restarted if it ever exits (PID 1 must not).
while :; do
  $bb setsid $bb sh -c "exec $bb env ENV=/etc/osx86-profile $bb sh -i < $console > $console 2>&1"
  $bb sleep 1
done
