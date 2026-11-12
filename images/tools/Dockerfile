# Os-x86 image tools (milestone 0, work item 4): the Linux tools the recipes run in a container
# named osx86-tools, so that the host needs only Docker and Python. e2fsprogs makes the ext4 root
# of linux-base from an exported tree (mkfs.ext4 -d, no root or loop device needed), mtools writes
# into the FAT16 disk of FreeDOS, python3 runs image-check.py on the exported tree, tar extracts
# the docker export with its owners and modes.
FROM alpine:3.24
RUN apk add --no-cache e2fsprogs mtools python3 tar
WORKDIR /work
