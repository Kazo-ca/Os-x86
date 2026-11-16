> [Version française](../fr/programmes.md)

# Prefixes, detection and `x86 run`

Milestone 0, work item 10 (`j0/10-prefixes-and-detection`). This work item opens the running of
Windows console programs: the prefixes (a guest user and a copy of the template prefix each), the
detection of a program's type by its header then its extension, `x86 detect`, `x86 prefix` and
`x86 run`, which runs a program in a prefix on a pty tied to the terminal and returns its real exit
code ([milestone order](../conception/jalon-0-execution.md), in French). Going through Kazo's
shell (`./hello.exe` typed in the terminal, Kazo ADR 0251 and 0252) waits until Kazo has coded those
ADRs; everything Os-x86 can do without them is here.

Decisions: 110 (real exit code), 144 (whatever runs in a prefix has its rights), 146 (folders
between prefixes), 152 A (template copied, never a `wineboot` at the user's), 162 A and 240 D
(Os-x86 detects, header then extension, media type), 193 A (Os-x86 decides the prefix), 194 A (one
VM per kind and image for all of a user's prefixes), 200 B ("prefix"), 206 A, 207 A, 208 A, 213 A
(one pty per program), 216 A (Ctrl+C goes to the program), 252 to 255 A (format detector declared
by the Feature, freedesktop or IANA media types).

## Where things are

| Path | Content |
| --- | --- |
| `src/OsX86/Programs/ProgramDetector.cs` | the detection, pure and stateless |
| `src/OsX86/Prefixes/Prefix.cs` | `PrefixId` (the name, 200 B) and `Prefix` (the prefix kept in the registry) |
| `src/OsX86/Vm/VmManager.Programs.cs` | choice of the VM and the prefix, refusals with their reason, `RunProgramAsync`, creating, deleting and listing prefixes |
| `src/OsX86/Vm/VmInstance.Programs.cs` | the prefixes in the guest, the copy of the program, the Wine session of each prefix, the program's console, the `sync` before a stop |
| `src/OsX86/Agent/AgentClient.Prefixes.cs` | the operations of protocol 2 of the agent, client side |
| `src/OsX86.Feature/X86Command.Programs.cs` | `x86 prefix`, `x86 detect`, `x86 run` |
| `src/OsX86.Interface/ProgramDetection.cs`, `VmConsole.cs` | `ProgramDetection`, `ProgramMimeTypes`, `ProgramRunOptions` (`Ix86`) |
| `images/agent/src/prefix.c` | the agent, protocol 2: `prefix-ensure`, `prefix-delete`, `prefix-list`, `put-file`, `sync` |
| `images/linux-base/prefix-admin.sh` | `/usr/lib/osx86/prefix-admin`: user, home and the image's steps, as root |
| `images/wine/prefix-hook.sh`, `prefix-session.sh` | copy of the template when a prefix is made; Wine session of a prefix |
| `tests/OsX86.Tests/Programs/`, `Vm/ProgramRunTests.cs`, `Agent/AgentPrefixTests.cs`, `X86ProgramCommandTests.cs` | `unit` tests |
| `tests/OsX86.Tests/Vm/X86RunWineGuestTests.cs` | `guest` test on the wine image |

## Prefixes (200 B, 193 A, 152 A)

A **prefix** ("préfixe" in French; "bouteille" in older texts) is a guest user of its own:
`osx86-<name>`, uid from 2000 up, home `/home/osx86-<name>` in 0700, shell `/bin/sh`, no password.
Its Wine prefix is `~/.wine`, a copy of the image's template prefix (152 A: a few MiB written,
links to Wine's files, never a `wineboot`). The name (`PrefixId`) is 1 to 16 lowercase letters,
digits and dashes, unique in its VM.

- **Whatever runs in a prefix has its rights (144)**: every program, console or Wine session runs
  as the prefix's user, in a cgroup of its own (206 A).
- **Two prefixes do not see each other's files (146)**: homes in 0700, `hidepid` on `/proc`, the
  `wineserver` directory in 0700 under `/tmp`; checked by the `guest` test (a shell of the prefix
  `work` gets "Permission denied" on the prefix `default`, and the other way round).
- **Kept with the VM** (the registry of work item 6, format 1 unchanged: two optional fields that
  older versions ignore):

```json
{ "id": "3f2c…", "name": "windows", "profile": { "name": "wine", "prefix": "default", … },
  "prefixes": [ { "id": "default", "created": "2026-11-16T10:00:00+00:00" },
                { "id": "work",    "created": "2026-11-16T10:05:00+00:00" } ],
  "pendingPrefixDeletions": [ "old" ] }
```

- **Default prefix (193 A)**: without `--prefix`, Os-x86 takes the prefix of the VM's profile, else
  `default`. The built-in profile `wine` declares `default`; it is recorded on its first use. Any
  other name must have been created (`x86 prefix create`): an unknown name is refused
  (`prefix-not-found`, code 4) rather than created, so that a typo never makes a new prefix.
- **Lazy creation**: `x86 prefix create` records the prefix at once; its user and files are made in
  the guest at once when the VM runs, else at its first use (no VM is started for it). In the guest,
  `prefix-ensure` is idempotent and repairs: a user without a home, or a home without `.wine`, is
  completed.
- **Deletion**: with the VM running, the prefix's programs are killed (`cgroup.kill` of the whole
  prefix), its user, home and `wineserver` directory removed; refused while programs or consoles
  run in it, unless `--force` (`vm-busy`, code 5). With the VM stopped, the prefix leaves the
  registry and its name is kept in `pendingPrefixDeletions`: the guest deletes it at the next start,
  before any program (only the guest writes its disk, 211 A).
- **Export**: `x86 prefix export` exists and says it is not there yet (code 6); it comes with the
  export of disks and restore points (plan, section 11; the export of a single prefix is an open
  question since series 6).

### Where prefixes live in the guest (choice)

Prefixes live **on the VM's disk**, in its persistent write layer (work item 6b): a program
installed in a prefix survives `x86 vm stop`. What changes in linux-base (and so in the wine image,
which starts from it):

- **The root stays read-only for everyone, `/home` is writable.** The init remounts the file system
  read-write, then remounts the mount point `/` read-only (`mount -o remount,bind,ro /`); `/home`
  becomes a bind mount of the disk, `nodev,nosuid` (R134.5); root alone has a writable view of the
  root, `/run/osx86-admin/root` (0700), through which `prefix-admin` replaces `/etc/passwd` and
  `/etc/group` in one `rename`. A user can write nothing outside its home and the `tmpfs` that were
  writable already (the 22 R134.5 checks hold, image test of 210 A).
- **ext4 journal** (8 MiB): a VM can stop uncleanly (its page closed); the journal is replayed at the
  next boot. Before work item 10, the root had no journal because it was always read-only.
- **256 MiB of free room** in the ext4 (`OSX86_FREE_MIB`), for the prefixes: zeros, which cost the
  pack almost nothing (linux-base 73.13 MB against 73.05; wine 365.9 MB against 365.8).
- **Writes flushed early**: `vm.dirty_expire_centisecs` and `dirty_writeback_centisecs` at 1 s, and
  the agent runs `sync` before Os-x86 stops the engine.

`/usr/lib/osx86/prefix-admin ensure|delete <prefix>` (root, run by the agent alone) makes the user,
its home, then runs the image's steps in `/usr/lib/osx86/prefix.d/`: the wine image puts `50-wine`
there, which copies the template with `prefix-copy` (work item 9). Codes: 0 done, 10 nothing to
do, anything else a failure whose last line says why.

## Protocol 2 of the agent

The agent had no operation to create a user or copy the template (note of work item 5). Rather
than a generic "run as root" operation, **named operations** call a fixed script of the image with
a prefix name already checked: nothing the host sends is interpreted by a shell, and a guest program
cannot ask the agent anything (its port belongs to root). Details in
[`agent.md`](agent.md#the-protocol-version-2).

| `op` | What it does |
| --- | --- |
| `prefix-ensure` | runs `prefix-admin ensure <prefix>`; returns `created`, `uid`, `home`. An image without `prefix-admin` (the Alpine test image) answers for the users it ships |
| `prefix-delete` | `cgroup.kill` of the whole prefix, then `prefix-admin delete`; returns `deleted` |
| `prefix-list` | the guest's `osx86-*` users |
| `put-file` | writes bytes (base64) into the user's home, **as that user**: the child becomes the user before it opens anything, makes missing directories in 0700, does not follow a final link |
| `sync` | `sync()` in a child |

The client accepts an agent of version 1 (older images): the new operations answer `unknown-op`
there, and Os-x86 then assumes the image ships its users.

## Detection (162 A, 240 D, 252 to 255 A)

`ProgramDetector.Detect(header, extension)`: **the header first, the extension when the header says
nothing, a contradiction refused**. The class is pure: no engine, no file, no app context, no
state. It has the shape of the **format detector declared by the Feature** once Kazo codes ADR
0251 (252 A): a name (`ca.kazo.x86.programs`), a parameterless constructor,
`DetectAsync(header, extension, cancellation)`.

| File | Media type (255 A) | `Kind` | Detail |
| --- | --- | --- | --- |
| `MZ` + `PE\0\0` | `application/vnd.microsoft.portable-executable` (IANA) | `Windows` | PE32 or PE32+, console or windowed, machine, .NET (CLR header), DLL |
| `MZ` + `NE` | `application/x-ms-ne-executable` | `Windows` | 16-bit Windows |
| `MZ` alone (or `LE`, `LX`) | `application/x-dosexec` | `Dos` | 16 bits |
| `.com` without `MZ` | `application/x-dosexec` | `Dos` | `.com` image, by its extension |
| `\x7fELF` | `application/x-executable` | `Linux` | 32 or 64 bits, type, machine |
| `.bat`, `.cmd` | `application/x-bat` | `Batch` | by the extension |
| `.msi` with the OLE signature | `application/x-msi` | `WindowsInstaller` | |
| ELF named `.exe`, `.dll`, `.bat`, `.cmd`, `.msi`; MZ named `.bat`, `.cmd`, `.msi`; `.exe` without `MZ`; `.msi` without OLE | `application/octet-stream` | `Unknown`, source `Conflict` | refused |
| anything else | `application/octet-stream` | `Unknown` | |

**64 bytes or 4 KiB.** The detector of ADR 0251 gets 64 bytes: the PE header is further
(`e_lfanew`, usually 0x80). With 64 bytes, an `MZ` whose relocation table is at 0x40 and whose
`e_lfanew` points past the DOS header is taken as a Windows executable (what every Windows linker
writes): the media type is right, the details stay unknown (`IsComplete = false`). `x86 detect`
and `x86 run` read 4 KiB and get everything.

```text
$ x86 detect osx86-hello.exe
osx86-hello.exe: application/vnd.microsoft.portable-executable
  PE32 console i386 (by its header)
  x86 run: runs it in a virtual machine of Windows programs
$ x86 detect old.exe
old.exe: application/x-dosexec
  DOS MZ executable (by its header)
  x86 run: no, this is a DOS program: DOS programs will run in a FreeDOS VM in the terminal (work item 11, with ADR 0251 and 0252 of Kazo).
```

## `x86 run`

```text
x86 run [--vm <vm>] [--prefix <prefix>] [--cols N] [--rows N] <program> [arguments...]
x86 prefix list [--vm <vm>]
x86 prefix create <name> [--vm <vm>]
x86 prefix delete <name> [--vm <vm>] [--force]
x86 prefix export <name> [--vm <vm>]
x86 detect <file>
```

The options of `x86 run` come **before** the program: everything after the program goes to it as
is, options included (`x86 run tool.exe --help`). Step by step:

1. **Detection** on the first 4 KiB. What cannot run yet is refused with the reason and what will
   come (`program-not-supported`, code 6): DOS (work item 11), Linux (not through `x86 run` yet;
   `x86 shell`), windowed, 16-bit and `.msi` (they need a display: BoxedWine, work item 13), 64-bit
   (the emulated processor, v86, is 32-bit), DLL, another machine. What runs: PE32 i386 console
   programs, .NET included (Mono is in the image), and `.bat`/`.cmd` files (through `cmd.exe /c`).
2. **The VM (194 A)**: `--vm`, else the user's VM of Windows programs (the only one; among several,
   the running one, else the oldest). When the user has none, Os-x86 makes one, `windows`, from the
   built-in profile `wine` (512 MiB, default prefix `default`), and says so on standard error.
3. **The prefix (193 A)**: `--prefix` (it must exist), else the default prefix.
4. **The VM starts** when needed (166 A; the program is an application of the VM), its image
   downloaded when needed with its progress (work item 6b).
5. **The prefix is made in the guest** if it is not yet (once per run of the VM).
6. **The program is copied into the prefix** (choice below):
   `C:\osx86\run\<16 digits of the SHA-256>\<name>`, that is
   `~/.wine/drive_c/osx86/run/<hash>/<name>`; a copy of the same bytes already there (same size,
   directory named by the hash) is not made again.
7. **The prefix's Wine session** is started when none runs (design below).
8. **The program runs on a pty** of the agent (213 A): `wine <path> <arguments>`, working directory
   the program's, `WINEDEBUG=-all`. The console is relayed to the terminal line by line (like
   `x86 shell`, work item 6): output filtered, each typed line sent with CR, `^C` and the terminal's
   Ctrl+C send Ctrl+C to the program (216 A), `~.` closes the console.
9. **The program's exit code is the command's (110)**.

```text
$ x86 run osx86-hello.exe a b
x86: created the virtual machine windows (031a…) for Windows programs, from the profile wine.
x86: wine 0.1.0 is not on this machine yet: downloading it.
…
osx86-hello windows=C:\windows user=osx86-default args=2 a b
$ echo $?
7
```

### Copy rather than share (choice)

The program is **copied** into the prefix by the agent (`put-file`, as the prefix's user), not
shared: sharing Kazo's folder over 9p (D22, E8) does not exist in the engine yet, and a copy in the
prefix has the prefix's rights (144) without opening anything else. Limits: one program alone (not
the DLLs next to it), 256 MiB at most, and the copy costs one round trip per piece of 6 KiB. The
pieces are not bounded by the agent's line size (64 KiB): **v86 hands a host write to the guest in
1 KiB pieces, one receive buffer of the guest each, and drops what finds no free buffer**; a line of
49 KiB was lost at the first try. One request at a time, each awaited, gives the guest the time to
post its buffers again. Sharing over 9p, with the translation of paths (an unnumbered topic of
series 15), is the next way for big programs.

### The `wineserver` of a prefix (design)

Finding of work item 9: a `wineserver` started by a command belongs to that command's cgroup, which
the agent ends when its program ends (SIGHUP then `cgroup.kill`, 208 A); a second program connected
to that server hangs or dies with the first one. The session's services (`services.exe`,
`plugplay`, `winedevice`), started by the first program, share that fate.

**Choice: one Wine session per prefix, launched apart.** Before the first program of a prefix,
Os-x86 launches through the agent `/usr/lib/osx86/wine/prefix-session start <token>`, a launch of
its own (its cgroup, 206 A), a child of the VM of kind `Session` (207 A) but not an application: it
does not keep the VM running. The script starts `wineserver -p` (persistent; without `-f` it
returns once ready), then a first Wine process (`wine cmd.exe /c exit 0`) that runs the session's
`wineboot --init`: server and services are in the session's cgroup, not a program's. An
`exec-in-prefix` of `prefix-session wait <token>` returns once the session is up. Programs connect
to it; closing their console does not touch the session. **Stop**: `PrefixSessionGrace` (30 s)
after the prefix's last program ended, Os-x86 kills the launch (SIGTERM then `cgroup.kill`); the
session also dies with the VM. A program started during the grace reuses the session.

Options set aside: `wineserver -p<n>` stopping by itself (a race between its exit and a program that
arrives, whose fallback server would land in a program's cgroup), and one session per program (no
fast second run, and services left from one program to the next).

### What the terminal sees

- **CR as Enter.** Wine's console reads its terminal raw: there LF is Ctrl+J and ends no line. The
  relay now sends each line with CR, what the Enter key sends; a shell (`x86 shell`) gets it as LF
  through its line discipline (ICRNL).
- **Spaces.** Wine's console draws the blanks of a line as cursor moves (`CSI n C`): the line filter
  turns them into spaces (`a b` came out as `ab`).
- **Typing before the read.** Wine's console sets the terminal raw when it starts, which drops what
  was typed before; a line typed while the program starts may be lost (the test waits for the
  program to say it reads).
- **Exit code.** `wine`'s, so the program's modulo 256 (a bigger Windows code, such as `0xC000013A`,
  arrives truncated): after Ctrl+C, the program exits with 58.

## `Ix86`: what is added, and why

| Addition | For whom |
| --- | --- |
| `DetectProgramAsync(header, name or extension)` → `ProgramDetection` | BoxedWine and the Terminal decide what to do with a file (162 A: the Terminal runs a console program itself, else it hands it to BoxedWine) |
| `RunProgramAsync(ProgramRunOptions)` → `IVmConsole` | running a console program with its console (213 A), its Ctrl+C (216 A) and its exit code (110, `IVmConsole.Exited`) |
| `ProgramRunOptions` | the program's bytes, its name, its arguments, the optional VM and prefix, the terminal's size |
| `ProgramDetection`, `ProgramKind`, `ProgramSubsystem`, `ProgramDetectionSource`, `ProgramMimeTypes` | the result of the detection |
| `X86ErrorCodes.PrefixNotFound`, `PrefixExists`, `ProgramNotSupported` | refusals with a stable code |

Not in `Ix86`: managing prefixes (`x86 prefix` only; BoxedWine will ask for it with its interface)
and the permission `x86.programs.launch` (work item 8). `RunProgramAsync` takes the program's bytes
rather than a path: the Feature does not have to read the caller's files.

## Measurements

Development host of the `guest` tests: Windows 11, Ryzen 7 3700X, headless Chrome through
Playwright, the wine pack copied from a local origin.

| Measurement | Value |
| --- | --- |
| First `x86 run osx86-hello.exe a b`: VM made, pack downloaded and verified (348.9 MiB), boot, prefix made (template copied), Wine session, program | 37.0 to 37.7 s (three runs) |
| Second `x86 run` (VM and session up, program already copied) | 3.3 to 3.5 s |
| `x86 shell --prefix default` after `x86 vm stop`, boot included (the file written before the stop is there) | 20.0 s |
| `x86 run` right after, VM up but session to start (guest cache cold) | 15.3 s |
| Ctrl+C on a program that waits: exit code returned | 58 (`0xC000013A`, `STATUS_CONTROL_C_EXIT`, truncated to 8 bits) |
| A line typed right after the program says it reads | lost once in two runs, read at the second try |

Starting the Wine session (the session's `wineboot --init`) costs about 12 s when the guest's cache
is cold; that is what the second `x86 run` no longer pays. The preloading of the hot files of work
item 9 runs when the image boots and takes part of it.

## Tests

- **`unit`**: `ProgramDetectorTests` (headers built byte by byte, no binary in the repository: PE32,
  PE32+, console, windowed, .NET, DLL, machines, NE, DOS, LE, ELF 32 and 64-bit, big and little
  endian, `.bat`, `.cmd`, `.com`, `.msi`, contradictions, 64 bytes alone, cut headers),
  `AgentPrefixTests` (protocol 2: operations, pieces of `put-file`, refused paths, agent of
  version 1), `ProgramRunTests` (choice of the VM, default prefix, pending deletion done at the
  start, session reused then ended after its grace, session that does not start, `sync` before the
  stop, `.bat`, refusals), `X86ProgramCommandTests` (exact output and code of `x86 detect`,
  `x86 run`, `x86 prefix`), plus the updated tests of work item 6.
- **`guest`**: `X86RunWineGuestTests`, on the wine pack, in a real browser, with the production
  engine, storage and `VmManager`: `x86 detect`; first `x86 run osx86-hello.exe a b` (its line in the
  terminal, code 7); a faster second `x86 run` (code 3); a typed line reaches the program; Ctrl+C
  interrupts a program that waits; `x86 prefix create work`, a program runs there as `osx86-work`;
  two prefixes cannot read each other's files; `x86 prefix list` and `delete`; after `x86 vm stop`,
  the prefix and a file written in it are still there. Its name holds `WineGuestTests`: the
  `guest-wine` job of `images.yml` runs it with the pack and `osx86-hello.exe`.
- **Agent smoke test** (`build-agent.py --smoke`): `prefix-list`, `prefix-ensure` without
  `prefix-admin`, `put-file` in two pieces as the user, `sync`, the refusals.

## What waits for Kazo ADR 0251 and 0252, and the other work items

- **ADR 0251** (the shell routes to execute-capable apps): the Feature will declare
  `ProgramDetector` as its format detector (`FormatDetectors`, 252 A) and an execute capability for
  `application/vnd.microsoft.portable-executable` and `application/x-bat`; the shell will then call
  `x86 run` (or `Ix86.RunProgramAsync`) for `./hello.exe`. Nothing changes in the detection.
- **ADR 0252** (the Terminal shows a guest program): the Terminal will relay the pty's raw bytes to
  its VT emulator instead of the line filter; keystrokes will go one by one (and the typing lost
  while Wine's console starts goes with the line mode).
- **Work item 8**: permission `x86.programs.launch` on `RunProgramAsync`, registration of `Ix86`.
- **Work item 11**: DOS programs; **work item 13**: BoxedWine (windowed programs, installers).

## Known limits and not verified

- **Not verified**: Firefox and Safari; a real .NET program (Mono present, not run); a program
  bigger than a few tens of KiB (the copy is linear, about one round trip per 6 KiB); a page closed
  during a write in a prefix (the journal is there, not provoked); the command in a Kazo other than
  the dev host (no emulator before work item 8).
- One file copied: a program that needs DLLs or data next to it does not find them (9p sharing to
  come).
- A VM's prefixes go with its layer (`x86 vm remove`); an update of the wine image keeps prefixes
  copied from the old template (migration to do, work item 9).
- The copy of a program stays in the prefix (`C:\osx86\run`); nothing cleans it yet.
