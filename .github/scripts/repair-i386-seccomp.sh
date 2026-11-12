#!/usr/bin/env bash
# Repairs the socket rule of the Docker daemon's seccomp profile for 32-bit containers on a
# GitHub runner, then proves it. Used by every job that builds or runs linux/386 containers
# (images.yml: agent, alpine-test, linux-base; guest-tests.yml).
#
# Measured with strace on ubuntu-latest (Docker 29.4): under the daemon's default seccomp
# profile, an i386 container gets ENOSYS on socket(2) and socketcall(2) (24 refusals in one
# apt-get update), so nothing in it can open a socket, not even a unix one; amd64 containers
# are fine. The default profile allows socket(2) per address family through argument
# comparisons, which do not hold for a 32-bit container. The daemon of the job keeps the whole
# official default profile (moby/profiles, pinned commit and SHA-256) with the socket rule made
# unconditional.
set -euo pipefail

PROFILE_COMMIT=7b5ccfab2d6b4e0aa64c6cce401a2705788eafeb
PROFILE_SHA256=15434cc9c9037d85b35027a60721bf45f381782b4283b01d94663366e71569f8

curl -fsSL "https://raw.githubusercontent.com/moby/profiles/${PROFILE_COMMIT}/seccomp/default.json" -o /tmp/seccomp-default.json
echo "${PROFILE_SHA256}  /tmp/seccomp-default.json" | sha256sum -c -
jq '.syscalls |= (map(select((.names | length) != 1 or .names[0] != "socket")) + [{"names": ["socket"], "action": "SCMP_ACT_ALLOW"}])' /tmp/seccomp-default.json > /tmp/seccomp-osx86.json
sudo install -m 0644 /tmp/seccomp-osx86.json /etc/docker/seccomp-osx86.json
echo '{"seccomp-profile": "/etc/docker/seccomp-osx86.json"}' | sudo tee /etc/docker/daemon.json
sudo systemctl restart docker
docker info --format 'security options: {{.SecurityOptions}}'
docker run --rm --platform linux/386 debian:trixie-slim sh -c 'apt-get update 2>&1 | grep -E "^(Err|Get|Hit):" | head -3; apt-get update > /dev/null 2>&1'
