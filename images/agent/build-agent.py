#!/usr/bin/env python3
"""Builds the guest agent of Os-x86 (milestone 0, work item 5; decisions 205 A, 206 A, 208 A, 213 A).

The agent is two static musl i386 programs compiled from images/agent/src in an Alpine stage of
the Dockerfile next to this script (pinned compiler packages, -Wall -Wextra -Werror):

    agent             root in the guest, /usr/lib/osx86/agent, started by the init
    osx86-open-url    what a guest program runs to hand a URL to the host, /usr/bin/osx86-open-url

Output (git-ignored, never committed): images/build/agent/
    agent, osx86-open-url    the binaries
    toolchain.txt            the compiler and libc packages that built them
    manifest.json            sizes and SHA-256 of the binaries, SHA-256 of every source file,
                             protocol version, name of the Docker image that holds them in /out

images/linux-base/build-linux-base.py and images/alpine-test/build-alpine-test.py install the
binaries from there. Docker images and containers created here are named osx86-agent-*; nothing
else is touched.

Run from the repository root with an isolated interpreter (standard library only):

    python -I images/agent/build-agent.py            build, then check
    python -I images/agent/build-agent.py --smoke    build, check, then drive the agent in a container
    python -I images/agent/build-agent.py --check    check an existing build (no Docker)

--check verifies that the output matches its manifest and the current sources (a stale binary is
refused), that both programs are static ELF32 i386 executables and that they stay small. --smoke
talks to the agent over its standard input and output (option -p -) in an Alpine i386 container:
ping, launch, list-processes, kill by launch, exec-in-prefix, mounts, open-url, the errors. The
container has no cgroup of its own to write to, so the smoke test exercises the fallback without
cgroups; cgroups and consoles are exercised by the `guest` tests in the real guest.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import queue
import shutil
import struct
import subprocess
import sys
import threading
import time
from pathlib import Path

AGENT_VERSION = "0.1.0"
PROTOCOL_VERSION = 1
BUILD_IMAGE = f"osx86-agent-build:{AGENT_VERSION}"
SMOKE_IMAGE = f"osx86-agent-smoke:{AGENT_VERSION}"
COPY_CONTAINER = "osx86-agent-copy"
SMOKE_CONTAINER = "osx86-agent-smoke"
BINARIES = ("agent", "osx86-open-url")
MAX_BINARY_BYTES = 512 * 1024
MIN_FREE_BYTES = 5 * 1024 ** 3
SMOKE_TIMEOUT_S = 30.0

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[1]
SOURCES = HERE / "src"
DEFAULT_OUTPUT = REPO_ROOT / "images" / "build" / "agent"


def run(command: list[str], **kwargs) -> subprocess.CompletedProcess:
    print("  $ " + " ".join(command))
    return subprocess.run(command, check=True, **kwargs)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def source_hashes() -> dict[str, str]:
    """SHA-256 of every source file, line endings normalised (a checkout on Windows may hold CR LF)."""
    files = sorted(SOURCES.glob("*.[ch]")) + [HERE / "Dockerfile"]
    return {path.relative_to(HERE).as_posix(): sha256_bytes(path.read_bytes().replace(b"\r\n", b"\n")) for path in files}


def check_free_space(directory: Path) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    free = shutil.disk_usage(directory).free
    if free < MIN_FREE_BYTES:
        raise SystemExit(f"{free // 1024 ** 2} MiB free under {directory}: at least 5 GiB are required")


def build(output: Path) -> None:
    check_free_space(output)
    # OSX86_DOCKER_BUILD_ARGS: extra arguments for docker build (see images/linux-base/build-linux-base.py).
    extra = os.environ.get("OSX86_DOCKER_BUILD_ARGS", "").split()
    run(["docker", "build", "--platform", "linux/386", *extra, "--target", "build", "-t", BUILD_IMAGE, str(HERE)])
    subprocess.run(["docker", "rm", "-f", COPY_CONTAINER], capture_output=True)
    run(["docker", "create", "--platform", "linux/386", "--name", COPY_CONTAINER, BUILD_IMAGE], capture_output=True)
    try:
        for name in (*BINARIES, "toolchain.txt"):
            target = output / name
            if target.exists():
                target.unlink()
            run(["docker", "cp", f"{COPY_CONTAINER}:/out/{name}", str(target)], capture_output=True)
    finally:
        subprocess.run(["docker", "rm", "-f", COPY_CONTAINER], capture_output=True)
    manifest = {
        "format": 1,
        "agent": AGENT_VERSION,
        "protocol": PROTOCOL_VERSION,
        "dockerImage": BUILD_IMAGE,
        "architecture": "i386",
        "libc": "musl (static)",
        "files": {name: {"size": (output / name).stat().st_size, "sha256": sha256_bytes((output / name).read_bytes())}
                  for name in BINARIES},
        "toolchain": (output / "toolchain.txt").read_text(encoding="utf-8").split(),
        "sources": source_hashes(),
        "licenses": "The agent is part of Os-x86 (the repository's license); it is linked statically with musl "
                    "(MIT, https://musl.libc.org/, Alpine aports main/musl).",
    }
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")


# --- check -------------------------------------------------------------------------------------

ELF_MAGIC = b"\x7fELF"
ET_EXEC = 2
EM_386 = 3
PT_DYNAMIC = 2
PT_INTERP = 3


def check_static_i386(path: Path) -> list[str]:
    """The problems of path as a static ELF32 i386 executable (an empty list when it is one)."""
    data = path.read_bytes()
    if len(data) < 52 or data[:4] != ELF_MAGIC:
        return [f"{path.name} is not an ELF file"]
    problems = []
    if data[4] != 1 or data[5] != 1:
        problems.append(f"{path.name} is not a 32-bit little-endian ELF")
        return problems
    e_type, e_machine = struct.unpack_from("<HH", data, 16)
    e_phoff, = struct.unpack_from("<I", data, 28)
    e_phentsize, e_phnum = struct.unpack_from("<HH", data, 42)
    if e_type != ET_EXEC:
        problems.append(f"{path.name} is not a fixed-address executable (type {e_type}): it is not linked statically")
    if e_machine != EM_386:
        problems.append(f"{path.name} is not an i386 program (machine {e_machine})")
    for index in range(e_phnum):
        offset = e_phoff + index * e_phentsize
        if offset + 4 > len(data):
            problems.append(f"{path.name} has a truncated program header table")
            break
        p_type, = struct.unpack_from("<I", data, offset)
        if p_type in (PT_INTERP, PT_DYNAMIC):
            problems.append(f"{path.name} needs a dynamic loader (program header {p_type}): it is not static")
    if len(data) > MAX_BINARY_BYTES:
        problems.append(f"{path.name} is {len(data)} bytes: more than the {MAX_BINARY_BYTES} allowed")
    return problems


def check(output: Path) -> int:
    manifest_path = output / "manifest.json"
    if not manifest_path.is_file():
        print(f"FAIL {manifest_path} missing: run `python -I images/agent/build-agent.py`")
        return 1
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    problems = []
    if manifest.get("protocol") != PROTOCOL_VERSION or manifest.get("agent") != AGENT_VERSION:
        problems.append(f"the build is agent {manifest.get('agent')} protocol {manifest.get('protocol')}, "
                        f"the recipe is agent {AGENT_VERSION} protocol {PROTOCOL_VERSION}")
    for name in BINARIES:
        path = output / name
        entry = manifest.get("files", {}).get(name)
        if not path.is_file() or entry is None:
            problems.append(f"{name} missing from the build")
            continue
        data = path.read_bytes()
        if len(data) != entry.get("size") or sha256_bytes(data) != entry.get("sha256"):
            problems.append(f"{name} differs from manifest.json")
        problems.extend(check_static_i386(path))
    if manifest.get("sources") != source_hashes():
        problems.append("the sources changed since the build: the binaries are stale, build again")
    for problem in problems:
        print(f"FAIL {problem}")
    if problems:
        return 1
    sizes = ", ".join(f"{name} {manifest['files'][name]['size']} bytes" for name in BINARIES)
    print(f"OK agent {manifest['agent']} protocol {manifest['protocol']}: {sizes}; static ELF32 i386; sources unchanged")
    return 0


# --- smoke test ----------------------------------------------------------------------------------


class AgentProcess:
    """The agent in a container, driven over its standard input and output."""

    def __init__(self) -> None:
        subprocess.run(["docker", "rm", "-f", SMOKE_CONTAINER], capture_output=True)
        self.process = subprocess.Popen(
            ["docker", "run", "--rm", "-i", "--init", "--platform", "linux/386", "--name", SMOKE_CONTAINER, SMOKE_IMAGE,
             "/usr/lib/osx86/agent", "-p", "-", "-c", "none", "-l", "/tmp/agent.log"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.lines: queue.Queue[str | None] = queue.Queue()
        self.seen: list[dict] = []
        self.next_id = 0
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self) -> None:
        assert self.process.stdout is not None
        for raw in self.process.stdout:
            self.lines.put(raw.decode("utf-8", "replace").rstrip("\n"))
        self.lines.put(None)

    def _next(self, deadline: float) -> dict:
        try:
            line = self.lines.get(timeout=max(0.01, deadline - time.monotonic()))
        except queue.Empty:
            raise SystemExit(f"smoke: no answer from the agent; seen so far: {self.seen[-5:]}") from None
        if line is None:
            stderr = self.process.stderr.read().decode("utf-8", "replace") if self.process.stderr else ""
            raise SystemExit(f"smoke: the agent ended; stderr: {stderr.strip()}")
        message = json.loads(line)
        self.seen.append(message)
        return message

    def wait(self, predicate, what: str) -> dict:
        """The first message, already seen or to come, that satisfies predicate."""
        for message in self.seen:
            if predicate(message):
                self.seen.remove(message)
                return message
        deadline = time.monotonic() + SMOKE_TIMEOUT_S
        while True:
            message = self._next(deadline)
            if predicate(message):
                self.seen.remove(message)
                return message
            if time.monotonic() > deadline:
                raise SystemExit(f"smoke: timeout waiting for {what}")

    def send_raw(self, text: str) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write(text.encode("utf-8") + b"\n")
        self.process.stdin.flush()

    def request(self, op: str, **arguments) -> dict:
        self.next_id += 1
        request_id = self.next_id
        self.send_raw(json.dumps({"id": request_id, "op": op, **arguments}))
        return self.wait(lambda m: m.get("id") == request_id, f"the reply to {op}")

    def close(self) -> None:
        try:
            if self.process.stdin:
                self.process.stdin.close()
            self.process.wait(timeout=20)
        except (subprocess.TimeoutExpired, OSError):
            self.process.kill()
        subprocess.run(["docker", "rm", "-f", SMOKE_CONTAINER], capture_output=True)


def expect(condition: bool, what: str, detail: object = "") -> None:
    if not condition:
        raise SystemExit(f"smoke: FAIL {what} {detail}")
    print(f"  ok   {what}")


def smoke() -> None:
    extra = os.environ.get("OSX86_DOCKER_BUILD_ARGS", "").split()
    run(["docker", "build", "--platform", "linux/386", *extra, "--target", "smoke", "-t", SMOKE_IMAGE, str(HERE)])
    agent = AgentProcess()
    try:
        ready = agent.wait(lambda m: m.get("event") == "ready", "the ready event")
        expect(ready.get("version") == PROTOCOL_VERSION, "ready event with the protocol version", ready)

        pong = agent.request("ping")
        expect(pong.get("ok") is True and pong.get("version") == PROTOCOL_VERSION, "ping", pong)

        launched = agent.request("launch", prefix="test", program="sleep", args=["300"])
        expect(launched.get("ok") is True and launched.get("launch", "").startswith("test/"), "launch of sleep as osx86-test", launched)
        other = agent.request("launch", prefix="test", program="sleep", args=["301"])
        expect(other.get("ok") is True and other["launch"] != launched["launch"], "second launch", other)

        processes = agent.request("list-processes")["processes"]
        mine = [p for p in processes if p["pid"] == launched["pid"]]
        expect(len(mine) == 1 and mine[0]["uid"] == 1001 and mine[0]["prefix"] == "test" and mine[0]["cmd"] == "sleep 300",
               "list-processes shows the launch with its user and prefix", mine)

        started = time.monotonic()
        killed = agent.request("kill", launch=launched["launch"])
        expect(killed.get("ok") is True and killed.get("forced") is False, "kill by launch (SIGTERM was enough)", killed)
        exited = agent.wait(lambda m: m.get("event") == "exit" and m.get("launch") == launched["launch"], "the exit event")
        expect(exited.get("signal") == 15, "exit event with the signal", exited)
        print(f"       kill round trip {1000 * (time.monotonic() - started):.0f} ms (through docker)")
        pids = {p["pid"] for p in agent.request("list-processes")["processes"]}
        expect(launched["pid"] not in pids and other["pid"] in pids, "the killed launch is gone, the other one stays")
        again = agent.request("kill", launch=launched["launch"])
        expect(again.get("ok") is False and again.get("error") == "unknown-launch", "kill of a launch that is gone", again)

        executed = agent.request("exec-in-prefix", prefix="test", program="id", args=["-u"])
        expect(executed.get("ok") is True and executed.get("code") == 0 and executed.get("output") == "1001\n", "exec-in-prefix runs as the prefix user", executed)
        slow = agent.request("exec-in-prefix", prefix="test", program="sleep", args=["60"], timeoutMs=300)
        expect(slow.get("timedOut") is True and slow.get("signal") == 9, "exec-in-prefix is killed at its deadline", slow)
        binary = agent.request("exec-in-prefix", prefix="test", program="printf", args=[r"\377\376ok"])
        expect(binary.get("output") == "��ok", "output that is not UTF-8 arrives as valid JSON", binary)

        mounts = agent.request("mounts")
        expect(mounts.get("ok") is True and any(m["path"] == "/" and m["state"] == "ok" for m in mounts["mounts"]), "mounts", mounts)

        url = agent.request("exec-in-prefix", prefix="test", program="osx86-open-url", args=["https://example.test/doc?x=1"])
        expect(url.get("code") == 0, "osx86-open-url", url)
        event = agent.wait(lambda m: m.get("event") == "open-url", "the open-url event")
        expect(event.get("prefix") == "test" and event.get("uid") == 1001 and event.get("url") == "https://example.test/doc?x=1",
               "open-url event with the caller's prefix (SO_PEERCRED)", event)

        errors = {
            "unknown-op": agent.request("no-such-operation"),
            "invalid-prefix": agent.request("launch", prefix="Bad Prefix", program="sleep"),
            "unknown-prefix": agent.request("launch", prefix="nobody", program="sleep"),
            "program-not-found": agent.request("launch", prefix="test", program="/no/such/program"),
            "no-x-server": agent.request("list-windows"),
            "no-console-port": agent.request("open-console", prefix="test"),
            "root-process": agent.request("kill", scope="pid", pid=ready["pid"], signal=0),
            "unknown-pid": agent.request("kill", scope="pid", pid=999999),
        }
        for code, reply in errors.items():
            expect(reply.get("ok") is False and reply.get("error") == code, f"error {code}", reply)
        agent.send_raw("this is not json")
        invalid = agent.wait(lambda m: m.get("error") == "invalid-json", "the invalid-json error")
        expect(invalid.get("id") is None, "a line that is not JSON is answered without id", invalid)

        swept = agent.request("kill", scope="prefix", prefix="test", signal=9)
        expect(swept.get("ok") is True and swept.get("count", 0) >= 1, "kill by prefix", swept)
        agent.wait(lambda m: m.get("event") == "exit" and m.get("launch") == other["launch"], "the exit event of the second launch")
    finally:
        agent.close()
    print("smoke: OK")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT, help="build directory (default images/build/agent, git-ignored)")
    parser.add_argument("--check", action="store_true", help="check an existing build against its manifest and the sources; no Docker")
    parser.add_argument("--smoke", action="store_true", help="after the build, drive the agent in a container")
    args = parser.parse_args()
    output: Path = args.output_dir.resolve()
    if args.check:
        return check(output)
    print(f"agent {AGENT_VERSION} (protocol {PROTOCOL_VERSION})")
    build(output)
    status = check(output)
    if status == 0 and args.smoke:
        smoke()
    return status


if __name__ == "__main__":
    sys.exit(main())
