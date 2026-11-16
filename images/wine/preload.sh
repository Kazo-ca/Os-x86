#!/bin/sh
# Os-x86 wine image (work item 9; decision 204 A): preloading of the hot files of the first Wine
# launch. Installed as /usr/lib/osx86/init.d/50-preload-wine, run by the init of linux-base as root
# before the ready marker, with the kernel command line as arguments. Reads, in the background and
# at a low priority, every file of /usr/lib/osx86/wine/hot-files.list (made at build time by
# trace-hot.sh): the guest keeps them in its page cache, so the first `wine` reads memory instead
# of disk frames decompressed one at a time. "osx86.preload=off" on the command line turns it off
# (to measure without). Prints "OSX86-PRELOAD ..." lines on the serial console.
list=/usr/lib/osx86/wine/hot-files.list
console=/dev/ttyS0
for argument in "$@"; do
  if [ "$argument" = "osx86.preload=off" ]; then
    echo "OSX86-PRELOAD off" > $console
    exit 0
  fi
done
[ -r "$list" ] || exit 0
(
  start=$(cut -d' ' -f1 /proc/uptime)
  tr '\n' '\0' < "$list" | nice -n 10 xargs -0 cat > /dev/null 2>&1
  end=$(cut -d' ' -f1 /proc/uptime)
  echo "OSX86-PRELOAD done files=$(wc -l < "$list") start=$start end=$end" > $console
) < /dev/null > /dev/null 2>&1 &
echo "OSX86-PRELOAD started" > $console
