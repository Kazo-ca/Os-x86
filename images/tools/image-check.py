#!/usr/bin/env python3
"""The image test of decision 210 A (milestone 0, work item 4): run on the exported root tree of a
Linux image before it is packed, it inventories the writable places and applies the 22 R134.5
checks of trial E7 (guarantees 1 and 2 of section 134: a guest user cannot leave its prefix,
reach another prefix, the agent, the host ports or the kernel). Fails (exit 1) when a check breaks.

The 22 checks were measured in E7 from a user shell inside the VM (images/linux-base/
verify-console.sh is that runtime script, kept in the image). Here, each check has its static
counterpart on the tree and on the init script, which is what a CI job can run without booting:
the permission bits, the setuid programs, the file capabilities, the sysctls and mount options
the init sets. A check whose guarantee comes from the kernel alone (signals between users, the
protection of PID 1) is held when nothing in the tree (a setuid program, a capability) lets a
user escalate. The report says for every check what was looked at.

Usage (standard library only; a directory is read with its real modes, so run it where the
tree was extracted as root, e.g. inside the osx86-tools container; a tar from `docker export`
is read directly):

    python -I images/tools/image-check.py --tree /work/rootfs [--init sbin/init] [--report out.json]
    python -I images/tools/image-check.py --tar build/linux-base/rootfs.tar [--allow-tar-root-mode]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import stat
import sys
import tarfile
from pathlib import Path

INIT_PATH = "sbin/init"
AGENT_PATH = "usr/lib/osx86/agent"
PROBES = ("usr/lib/osx86/osx86-rawsock", "usr/lib/osx86/osx86-ptrace")
MAX_INIT_BYTES = 256 * 1024
# World-writable paths that are expected, all sticky (checked), everything else is a leak.
ALLOWED_WORLD_WRITABLE = {"tmp", "var/tmp"}
# Directories expected to be world-writable only through a mount the init makes (they must be
# plain 0755 directories in the tree).
MOUNT_POINTS = {"run", "tmp", "var/tmp", "home", "dev", "proc", "sys"}


class Entry:
    __slots__ = ("path", "mode", "uid", "gid", "size", "linkname", "capability")

    def __init__(self, path: str, mode: int, uid: int, gid: int, size: int, linkname: str | None, capability: bool) -> None:
        self.path = path
        self.mode = mode
        self.uid = uid
        self.gid = gid
        self.size = size
        self.linkname = linkname
        self.capability = capability

    @property
    def is_dir(self) -> bool:
        return stat.S_ISDIR(self.mode)

    @property
    def is_file(self) -> bool:
        return stat.S_ISREG(self.mode)

    @property
    def is_link(self) -> bool:
        return stat.S_ISLNK(self.mode)


class Tree:
    """The exported root: entries by path (no leading slash, "" for the root) and file contents."""

    def __init__(self) -> None:
        self.entries: dict[str, Entry] = {}
        self._read = None

    def read(self, path: str, limit: int) -> bytes:
        assert self._read is not None
        return self._read(path, limit)


def normalise(name: str) -> str:
    name = name.replace("\\", "/")
    while name.startswith("./"):
        name = name[2:]
    return name.strip("/")


def load_tar(path: Path) -> Tree:
    tree = Tree()
    archive = tarfile.open(path, "r:*")
    members = {}
    for member in archive.getmembers():
        name = normalise(member.name)
        if ".." in name.split("/"):
            raise SystemExit(f"{path.name}: entry {member.name!r} escapes the tree")
        mode = member.mode & 0o7777
        if member.isdir():
            mode |= stat.S_IFDIR
        elif member.issym():
            mode |= stat.S_IFLNK
        elif member.isfile() or member.islnk():
            mode |= stat.S_IFREG
        elif member.ischr():
            mode |= stat.S_IFCHR
        elif member.isblk():
            mode |= stat.S_IFBLK
        elif member.isfifo():
            mode |= stat.S_IFIFO
        capability = any(key.endswith("security.capability") for key in (member.pax_headers or {}))
        tree.entries[name] = Entry(name, mode, member.uid, member.gid, member.size, member.linkname if member.issym() else None, capability)
        members[name] = member

    def read(name: str, limit: int) -> bytes:
        member = members.get(normalise(name))
        if member is None or not member.isfile():
            return b""
        stream = archive.extractfile(member)
        return stream.read(limit) if stream else b""

    tree._read = read
    return tree


def load_directory(root: Path) -> Tree:
    tree = Tree()
    root = root.resolve()
    for current, directories, files in os.walk(root):
        relative = normalise(os.path.relpath(current, root))
        relative = "" if relative == "." else relative
        info = os.lstat(current)
        tree.entries[relative] = Entry(relative, info.st_mode, info.st_uid, info.st_gid, info.st_size, None, has_capability(Path(current)))
        for name in files:
            full = Path(current) / name
            info = os.lstat(full)
            path = f"{relative}/{name}" if relative else name
            link = os.readlink(full) if stat.S_ISLNK(info.st_mode) else None
            tree.entries[path] = Entry(path, info.st_mode, info.st_uid, info.st_gid, info.st_size, link, has_capability(full))
        # symlinks to directories appear in `directories` on some platforms: record and do not follow
        for name in list(directories):
            full = Path(current) / name
            if os.path.islink(full):
                info = os.lstat(full)
                path = f"{relative}/{name}" if relative else name
                tree.entries[path] = Entry(path, info.st_mode, info.st_uid, info.st_gid, info.st_size, os.readlink(full), False)
                directories.remove(name)

    def read(name: str, limit: int) -> bytes:
        target = root / normalise(name)
        try:
            with target.open("rb") as stream:
                return stream.read(limit)
        except OSError:
            return b""

    tree._read = read
    return tree


def has_capability(path: Path) -> bool:
    getxattr = getattr(os, "getxattr", None)
    if getxattr is None:
        return False
    try:
        getxattr(path, "security.capability", follow_symlinks=False)
        return True
    except OSError:
        return False


# --- the checks ---------------------------------------------------------------------------------


class Report:
    def __init__(self) -> None:
        self.controls: list[dict] = []
        self.inventory: dict = {}

    def check(self, name: str, held: bool, evidence: str, looked_at: str) -> None:
        self.controls.append({"id": name, "status": "HELD" if held else "LEAK", "evidence": evidence, "lookedAt": looked_at})

    @property
    def leaks(self) -> list[dict]:
        return [control for control in self.controls if control["status"] == "LEAK"]


def mount_options(init: str, target: str) -> set[str] | None:
    """Options of the `mount ... <target>` line of the init (None when there is no such line)."""
    for line in init.splitlines():
        line = line.strip()
        if not line.startswith("mount ") or not re.search(rf"\s{re.escape(target)}(\s|$)", line.split("||")[0]):
            continue
        match = re.search(r"-o\s+(\S+)", line)
        return set(match.group(1).split(",")) if match else set()
    return None


def sysctls(init: str) -> dict[str, str]:
    values: dict[str, str] = {}
    for line in init.splitlines():
        if "sysctl" not in line:
            continue
        for key, value in re.findall(r"([a-z_.0-9]+)=([^\s\\]+)", line):
            values[key] = value
    return values


def run_checks(tree: Tree, init_path: str, allow_tar_root_mode: bool) -> Report:
    report = Report()
    entries = tree.entries
    init = tree.read(init_path, MAX_INIT_BYTES).decode("utf-8", "replace")
    if not init:
        raise SystemExit(f"{init_path} not found in the tree or empty")
    options = sysctls(init)
    lines = init.splitlines()

    # Inventory (210 A): world-writable paths, setuid/setgid files, file capabilities, root mode.
    world_writable = sorted(entry.path for entry in entries.values()
                            if not entry.is_link and entry.mode & 0o002 and entry.path != "")
    setuid = sorted(entry.path for entry in entries.values() if entry.is_file and entry.mode & (stat.S_ISUID | stat.S_ISGID))
    capabilities = sorted(entry.path for entry in entries.values() if entry.capability)
    root = entries.get("")
    root_mode = (root.mode & 0o7777) if root else None
    report.inventory = {
        "entries": len(entries),
        "worldWritable": world_writable,
        "setuidOrSetgid": setuid,
        "fileCapabilities": capabilities,
        "rootMode": f"{root_mode:04o}" if root_mode is not None else None,
        "init": init_path,
    }

    unexpected_writable = [path for path in world_writable
                           if path not in ALLOWED_WORLD_WRITABLE or not entries[path].mode & stat.S_ISVTX]
    report.check("world_writable_inventory", not unexpected_writable,
                 "only sticky /tmp and /var/tmp" if not unexpected_writable else "unexpected: " + " ".join(unexpected_writable),
                 "every entry of the tree with the o+w bit")
    root_ok = root_mode == 0o755 or (allow_tar_root_mode and "chmod 755 /" in init)
    report.check("root_mode_755", root_ok, f"root directory mode {root_mode:04o}" if root_mode is not None else "no root entry",
                 "the mode of the root directory (210 A: chmod 755 /)")
    run_options = mount_options(init, "/run")
    report.check("run_mode_0755", run_options is not None and "mode=0755" in run_options and "nosuid" in run_options,
                 f"/run mounted with {sorted(run_options) if run_options else 'nothing'}", "the mount line of /run in the init (210 A)")
    device_lines = [line for line in lines if "chmod 600" in line and "/dev/fb0" in line and "/dev/input/" in line]
    report.check("display_input_devices_0600", bool(device_lines), device_lines[0].strip() if device_lines else "no chmod 600 of /dev/fb0 and /dev/input/* in the init",
                 "the init (210 A: /dev/fb0 and /dev/input/* 0600)")

    # The 22 R134.5 checks of E7, static counterparts.
    userns = options.get("user.max_user_namespaces")
    report.check("unshare_netns", userns == "0" and not setuid, f"user.max_user_namespaces={userns}, setuid programs: {len(setuid)}",
                 "sysctl user.max_user_namespaces in the init; no setuid unshare (CLONE_NEWNET needs CAP_SYS_ADMIN)")
    report.check("unshare_userns", userns == "0", f"user.max_user_namespaces={userns}", "sysctl user.max_user_namespaces in the init")
    report.check("nsenter_root", not setuid and not capabilities, "no setuid program, no file capability",
                 "setns needs CAP_SYS_ADMIN: nothing in the tree grants it")

    def home_mode_ok() -> tuple[bool, str]:
        login_defs = tree.read("etc/login.defs", MAX_INIT_BYTES).decode("utf-8", "replace")
        adduser = tree.read("etc/adduser.conf", MAX_INIT_BYTES).decode("utf-8", "replace")
        home_mode = re.search(r"^HOME_MODE\s+0?700", login_defs, re.M) is not None
        dir_mode = re.search(r"^DIR_MODE=0?700", adduser, re.M) is not None
        homes = [entry for path, entry in entries.items() if path.startswith("home/") and path.count("/") == 1 and entry.is_dir]
        open_homes = [entry.path for entry in homes if entry.mode & 0o077]
        root_home = entries.get("root")
        root_ok = root_home is not None and not root_home.mode & 0o077
        ok = home_mode and dir_mode and not open_homes and root_ok
        return ok, f"HOME_MODE 0700: {home_mode}, DIR_MODE 0700: {dir_mode}, open homes: {open_homes or 'none'}, /root 0700: {root_ok}"

    ok, evidence = home_mode_ok()
    report.check("other_bottle_home", ok, evidence, "login.defs, adduser.conf, the modes of /home/* and /root")
    tmp_options = mount_options(init, "/tmp")
    report.check("other_bottle_wineserver", tmp_options is not None and "mode=1777" in tmp_options,
                 f"/tmp mounted with {sorted(tmp_options) if tmp_options else 'nothing'}", "sticky /tmp (a user's wineserver directory is 0700 inside it)")
    report.check("other_bottle_prefix", ok, evidence, "same as other_bottle_home: a prefix lives in a 0700 home")
    port_lines = [line for line in lines if "chmod 600" in line and "/dev/ttyS0" in line]
    hvc_ok = bool(port_lines) and all(f"/dev/hvc{index}" in port_lines[0] for index in range(4))
    report.check("serial_ttyS0", bool(port_lines), port_lines[0].strip() if port_lines else "no chmod 600 /dev/ttyS0 in the init", "the init")
    report.check("agent_port_read", hvc_ok, "hvc0 to hvc3 chmod 600 in the init" if hvc_ok else "the init does not close hvc0 to hvc3", "the init")
    report.check("agent_port_write", hvc_ok, "hvc0 to hvc3 chmod 600 in the init" if hvc_ok else "the init does not close hvc0 to hvc3", "the init")
    report.check("other_port_hvc3", hvc_ok, "hvc0 to hvc3 chmod 600 in the init" if hvc_ok else "the init does not close hvc0 to hvc3", "the init")
    report.check("raw_socket", not setuid and not capabilities, "no setuid program, no file capability (CAP_NET_RAW)",
                 "setuid and capability inventory")
    report.check("no_setuid", not setuid, "none" if not setuid else " ".join(setuid), "every regular file with the setuid or setgid bit")
    home_options = mount_options(init, "/home")
    report.check("home_nosuid", home_options is not None and "nosuid" in home_options,
                 f"/home mounted with {sorted(home_options) if home_options else 'nothing'}", "the mount line of /home in the init")
    report.check("tmp_nosuid", tmp_options is not None and "nosuid" in tmp_options,
                 f"/tmp mounted with {sorted(tmp_options) if tmp_options else 'nothing'}", "the mount line of /tmp in the init")
    report.check("chown_root", not setuid and not capabilities, "no setuid program, no file capability (CAP_CHOWN)", "setuid and capability inventory")
    ptrace_scope = options.get("kernel.yama.ptrace_scope")
    report.check("ptrace_other", ptrace_scope in ("1", "2", "3") and not setuid,
                 f"kernel.yama.ptrace_scope={ptrace_scope}, setuid programs: {len(setuid)}", "sysctl in the init; no setuid program (CAP_SYS_PTRACE)")
    proc_options = mount_options(init, "/proc")
    hidepid = proc_options is not None and any(option.startswith("hidepid=") for option in proc_options)
    report.check("proc_environ_other", hidepid and not setuid, f"/proc mounted with {sorted(proc_options) if proc_options else 'nothing'}",
                 "hidepid on /proc in the init (environ is 0400 anyway); no setuid program")
    report.check("kill_other", not setuid and not capabilities, "no setuid program, no file capability (CAP_KILL)", "setuid and capability inventory")
    report.check("kill9_other", not setuid and not capabilities, "no setuid program, no file capability (CAP_KILL)", "setuid and capability inventory")
    report.check("kill_init", not setuid, "no setuid program (PID 1 ignores signals it does not handle)", "setuid inventory")
    agent_lines = [line for line in lines if AGENT_PATH.split("/")[-1] in line and ("exec" in line or "&" in line)]
    agent_as_root = bool(agent_lines) and not any(word in agent_lines[0] for word in ("su ", "setpriv", "runuser", "chroot"))
    report.check("kill_agent", agent_as_root, agent_lines[0].strip() if agent_lines else "the init does not start /usr/lib/osx86/agent",
                 "the init starts the agent as root, before any user exists")
    cgroup_options = mount_options(init, "/sys/fs/cgroup")
    cgroup_ok = cgroup_options is not None and {"nosuid", "nodev", "noexec"} <= cgroup_options and "chown" not in "".join(
        line for line in lines if "cgroup" in line)
    report.check("cgroup_kill_other", cgroup_ok, f"cgroup2 mounted with {sorted(cgroup_options) if cgroup_options else 'nothing'}, never chowned",
                 "the cgroup2 mount in the init (the agent creates per-launch cgroups as root, 206 A)")

    # Things the image must carry for the runtime version of these checks.
    missing = [path for path in PROBES + ("usr/lib/osx86/verify-console.sh",) if path not in entries]
    report.check("runtime_probes_present", not missing, "present" if not missing else "missing: " + " ".join(missing),
                 "verify-console.sh and its two probes under /usr/lib/osx86")
    mountpoint_modes = {path: f"{entries[path].mode & 0o7777:04o}" for path in sorted(MOUNT_POINTS) if path in entries}
    report.inventory["mountPoints"] = mountpoint_modes
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--tree", type=Path, help="extracted root directory (read as root for exact modes)")
    source.add_argument("--tar", type=Path, help="a `docker export` archive")
    parser.add_argument("--init", default=INIT_PATH, help=f"path of the init inside the tree (default {INIT_PATH})")
    parser.add_argument("--report", type=Path, help="write the JSON report here")
    parser.add_argument("--allow-tar-root-mode", action="store_true",
                        help="accept a root entry that is not 0755 when the init does chmod 755 / (docker export writes 0777)")
    args = parser.parse_args(argv)

    tree = load_tar(args.tar) if args.tar else load_directory(args.tree)
    report = run_checks(tree, normalise(args.init), args.allow_tar_root_mode)
    r134 = [control for control in report.controls if control["id"] not in
            ("world_writable_inventory", "root_mode_755", "run_mode_0755", "display_input_devices_0600", "runtime_probes_present")]
    result = {
        "ok": not report.leaks,
        "inventory": report.inventory,
        "controls": report.controls,
        "r134_5": {"total": len(r134), "held": sum(1 for control in r134 if control["status"] == "HELD")},
    }
    for control in report.controls:
        print(f"  {control['status']:4} {control['id']:30} {control['evidence']}")
    print(f"R134.5: {result['r134_5']['held']} of {result['r134_5']['total']} held; "
          f"world-writable: {len(report.inventory['worldWritable'])}, setuid: {len(report.inventory['setuidOrSetgid'])}, "
          f"root mode {report.inventory['rootMode']}")
    if args.report:
        args.report.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    if report.leaks:
        print(f"FAILED: {len(report.leaks)} check(s) broke: {', '.join(control['id'] for control in report.leaks)}")
        return 1
    print("OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
