#!/bin/sh
# Os-x86 wine image (work item 9; decision 204 A): lists the hot files of the first Wine launch.
# Run as root by build-wine.py in a throwaway container of the image with the template prefix
# (`docker run --rm`; strace is installed in that container only). A guest user gets a copy of
# the template (prefix-copy), then, traced with strace, runs what a first session runs: `wine cmd
# /c echo`, then the console program of the recipe. Every regular file opened or executed with
# success is kept, in the order of its first use, with the files under the copy mapped back to the
# template (the copy is made from them) and links resolved (the image holds one copy of each).
# Writes /out/hot-files.list (one absolute path per line) and /out/hot-trace.txt (counts).
set -eu
export DEBIAN_FRONTEND=noninteractive
apt-get -qq -o Acquire::Retries=5 update
apt-get -qq -o Acquire::Retries=5 install -y --no-install-recommends strace > /dev/null

template=/usr/lib/osx86/wine/prefix-template
user=osx86-trace
useradd -m -u 1001 -s /bin/sh "$user"
/usr/lib/osx86/wine/prefix-copy "$user"
prefix="/home/$user/.wine"

trace() {
  su "$user" -s /bin/sh -c "cd && WINEDEBUG=-all strace -f -qq -e trace=open,openat,execve -o /tmp/$1.strace $2"
}
start=$(date +%s%N)
trace cmd 'wine cmd /c echo osx86-hot' > /tmp/cmd.out
middle=$(date +%s%N)
trace hello 'wine /usr/lib/osx86/wine/osx86-hello.exe hot' > /tmp/hello.out || true
end=$(date +%s%N)
grep -q osx86-hot /tmp/cmd.out
grep -q 'osx86-hello' /tmp/hello.out

# 123 openat(AT_FDCWD, "/path", O_RDONLY|O_CLOEXEC) = 3   and   123 execve("/path", [...]) = 0
cat /tmp/cmd.strace /tmp/hello.strace \
  | sed -n 's/^[0-9]* \(open\|openat\|execve\)(\(AT_FDCWD, \)\{0,1\}"\([^"]*\)".* = [0-9][0-9]*$/\3/p' > /tmp/opened
: > /out/hot-files.list
seen=" "
while IFS= read -r path; do
  case "$path" in /*) ;; *) continue;; esac
  case "$path" in /proc/*|/sys/*|/dev/*|/tmp/*|/run/*|/etc/ld.so.cache) continue;; esac
  case "$path" in "$prefix"/*) path="$template/${path#"$prefix"/}";; esac
  real="$(readlink -f "$path" 2>/dev/null || true)"
  [ -n "$real" ] && [ -f "$real" ] || continue
  case "$seen" in *" $real "*) continue;; esac
  seen="$seen$real "
  echo "$real" >> /out/hot-files.list
done < /tmp/opened

files=$(wc -l < /out/hot-files.list)
bytes=$(tr '\n' '\0' < /out/hot-files.list | xargs -0 stat -c %s | awk '{ s += $1 } END { print s + 0 }')
{
  echo "opened $(wc -l < /tmp/opened)"
  echo "files $files"
  echo "bytes $bytes"
  echo "cmdMs $(( (middle - start) / 1000000 ))"
  echo "helloMs $(( (end - middle) / 1000000 ))"
} > /out/hot-trace.txt
cat /out/hot-trace.txt
