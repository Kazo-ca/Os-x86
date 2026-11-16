# Os-x86 (milestone 0, work item 11): the assembler of the DOS test programs, NASM pinned to the
# version of the Alpine 3.24 branch. Built and run by images/freedos/build-freedos.py as the image
# osx86-nasm:3.01; the programs are assembled from the sources of this directory, never committed.
FROM alpine:3.24
RUN apk add --no-cache nasm=3.01-r0
WORKDIR /work
