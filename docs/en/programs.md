> [Version française](../fr/programmes.md)

# Prefixes, detection, `x86 run` and DOS programs

Milestone 0, work item 10 (`j0/10-prefixes-and-detection`). This work item opens the running of
Windows console programs: the prefixes (a guest user and a copy of the template prefix each), the
detection of a program's type by its header then its extension, `x86 detect`, `x86 prefix` and
`x86 run`, which runs a program in a prefix on a pty tied to the terminal and returns its real exit
code ([milestone order](../conception/jalon-0-execution.md), in French). Going through Kazo's
shell (`./hello.exe` typed in the terminal, Kazo ADR 0251 and 0252) waits until Kazo has coded those
ADRs; everything Os-x86 can do without them is here. Work item 11 adds DOS programs, shown in the
terminal ([below](#dos-programs-work-item-11)).

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
  x86 run: runs it in a FreeDOS virtual machine, its screen in the terminal
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
   come (`program-not-supported`, code 6): Linux (not through `x86 run` yet;
   `x86 shell`), windowed, 16-bit and `.msi` (they need a display: BoxedWine, work item 13), 64-bit
   (the emulated processor, v86, is 32-bit), DLL, another machine. What runs: PE32 i386 console
   programs, .NET included (Mono is in the image), and `.bat`/`.cmd` files (through `cmd.exe /c`). A
   DOS program takes its own way ([below](#dos-programs-work-item-11)).
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
- **The pty starts raw (work item 10b).** Wine's console sets the terminal raw only when it first
  reads. A line typed just before went through the line discipline: its CR became LF (ICRNL), which
  Wine takes as Ctrl+J, and the line never ended (8 times out of 12 in a loop, intermittent in CI).
  The agent now opens a program's pty in the mode Wine will set (`open-console` with `raw`): what
  is typed arrives as typed, whenever it is typed. Only ISIG stays on until Wine sets its mode: a
  program that never reads its input never makes Wine's console read, and Ctrl+C must then remain a
  SIGINT (first CI try: Ctrl+C no longer stopped `osx86-hello --wait`).
- **Exit code.** `wine`'s, so the program's modulo 256 (a bigger Windows code, such as `0xC000013A`,
  arrives truncated): after Ctrl+C, the program exits with 58.

## DOS programs (work item 11)

Milestone 0, work item 11 (`j0/11-dos`). `x86 run` of a DOS program (`application/x-dosexec`: a
`.com`, or an MZ without a newer header) runs it in a FreeDOS VM and shows its text screen in the
terminal, keyboard included. Decisions: 111 (FreeDOS), 164 A (graphical DOS), 166 A (the VM stops
with its last application), 212 A (image without agent), 214 A (VGA way at 33 ms, comparison by row,
attributes), 215 A (full screen, Ctrl+letter, Escape chord), 216 A (Ctrl+C goes to the program),
217 A (signal of the graphical mode), 218 A (internal fields of v86 in a single adapter). Going
through Kazo's shell (`./prog.com`, Kazo ADR 0251) and the richer display of a guest program by the
Terminal (Kazo ADR 0252) wait until Kazo has coded those ADRs; until then, the Feature uses the
`IFullScreenTerminalSession` of the Kazo 3.4.0 SDK.

| Path | Content |
| --- | --- |
| `src/OsX86/Dos/DosProgramDisk.cs` | the program disk: MBR, one FAT12 partition, the files in the root |
| `src/OsX86/Dos/DosRunScript.cs` | 8.3 names, DOS command line, `OSX86RUN.BAT`, reading the COM1 markers |
| `src/OsX86/Dos/GuestKeyboard.cs` | `GuestKey`, translation to PS/2 scancodes (set 1, US layout), Escape chord (`EscapeChord`) |
| `src/OsX86/Dos/TextScreenDiff.cs` | the comparison by row (text and attributes) and of the cursor |
| `src/OsX86/Vm/VmManager.Dos.cs`, `VmInstance.Dos.cs`, `DosProgramSession.cs` | choice of the DOS VM, start with the program disk and the keyboard, session (screen, keys, start, end, graphical mode), stop |
| `src/OsX86/Engine/EngineTextScreen.cs` | the text screen with attributes and cursor, `ITextModeEngine` |
| `src/OsX86/wwwroot/engine/vga-text.js` | the only reader of the internal fields of v86's VGA (218 A) |
| `src/OsX86.Feature/X86Command.Dos.cs` | `x86 run` of a DOS program |
| `src/OsX86.Feature/GuestScreenTerminal.cs` | `IGuestScreenTerminal` (the boundary ADR 0252 will enrich), its adapter `KazoFullScreenTerminal` (SDK 3.4.0), the VGA colours |
| `src/OsX86.Feature/DosTerminalBridge.cs` | the bridge: polling, comparison, rendering, keys, measures |
| `images/freedos/FDAUTO.BAT`, `images/freedos/programs/` | the call of `D:\OSX86RUN.BAT`; the test programs in assembly (NASM) |
| `tests/OsX86.Tests/Dos/`, `Engine/TextModeEngineTests.cs` | `unit` tests |
| `tests/OsX86.Tests/Vm/X86RunFreeDosGuestTests.cs` | `guest` test on FreeDOS |

```text
$ x86 detect old.exe
old.exe: application/x-dosexec
  DOS MZ executable (by its header)
  x86 run: runs it in a FreeDOS virtual machine, its screen in the terminal
$ x86 run tool.com a1 b2
x86: created the virtual machine dos (…) for DOS programs, from the profile dos.
        (the program's screen fills the terminal until it ends)
OSX86 DOS TEST                          (then its last screen stays in the scrollback)
ARGS:[ a1 b2]
…
```

### `x86 run` of a DOS program

1. **Detection** (162 A): an `application/x-dosexec` program takes the DOS way. `--prefix`, `--cols`
   and `--rows` are refused (code 2): a DOS VM has no prefix and the screen has the size the program
   gives it.
2. **The terminal must offer its full screen** (`TerminalExecutionContext.FullScreen`); otherwise
   (script, pipe), refused with code 6.
3. **The DOS VM**: `--vm` (a stopped VM of the kind `dos`), otherwise the user's oldest stopped DOS
   VM; when none is stopped, Os-x86 makes one, `dos` (then `dos-2`…), from the built-in profile `dos`
   (image `freedos` 0.2.0, 64 MiB, no agent nor serial console, 212 A), and says so.
4. **The program disk** is made in memory (choice below) and the VM starts with it as `hdb` and the
   keyboard on; the image is downloaded when needed, with its progress (work item 6b).
5. **The screen** of the terminal goes to the alternate screen: "starting the DOS virtual machine…"
   while FreeDOS boots, then the program's screen as soon as the batch says so on COM1.
6. **The keys** typed go to the program; Ctrl+C too (216 A).
7. **The end**: when the batch writes the program's code on COM1, the last screen is read, the
   alternate screen given back, that last screen written to the output (it stays in the terminal's
   scrollback), and the VM **stops at once** (166 A, without the grace: see below). The program's
   code (`ERRORLEVEL`, 0 to 255) is the command's (110).
8. **Graphical mode**: message, the program and the VM stopped, code 6 (217 A).

`OSX86_DOS_STATS=1` in the environment writes the measures of the refreshes on standard error
(`x86: DOS screen: … polls, … frames, … rows changed; read …; frame …; key to screen …`, median,
90th percentile and maximum in milliseconds).

### The DOS VM (choice)

- **One program per start.** The program comes on a disk that can only be given at the start (v86
  does not plug a disk while running): a running DOS VM is busy; named with `--vm`, it is refused
  (`vm-busy`, code 5); without `--vm`, Os-x86 takes a stopped DOS VM or makes another (165 A: no
  question below the memory budget, which the start checks).
- **Stopped as soon as the program ends.** The program is the VM's application (166 A). When it
  ends, the VM stops without waiting for the grace, which would only keep a VM nobody can use any
  more. A DOS VM held by `x86 vm start` (no program) takes no program.
- **No agent.** FreeDOS has none (212 A): the start of a `dos` VM waits for no agent connection.
  `x86 shell --vm dos` is refused (`no-console`): the profile declares no serial console, COM1 only
  carries the start and the end of a program.
- **The FreeDOS disk (`hda`)** keeps the VM's persistent write layer (work item 6b): what a program
  writes on `C:` stays from one run to the next.

### The program on a second disk (choice)

The program and its batch go on a **second hard disk** (`hdb`), made in memory by the C# side for
each run and sent with the start options (`EngineFileSource.InMemory`, source `{inline}` of
`disk.js`): never stored in the OPFS, its writes dropped with the engine. Set aside: writing the
program into the write layer of `hda`, which would mean keeping FreeDOS's FAT16 (allocation,
directory) up to date from the host in a layer the guest writes too, and would leave copies on the
VM's disk.

- **Format**: an MBR, one FAT12 primary partition (type 0x01) at sector 63, v86's geometry (16
  heads, 63 sectors per track), size rounded to the cylinder. FreeDOS mounts it as `D:` (`hda` has a
  single partition, `C:`). FAT12 because the disk is small: at most 16 MiB of files, plus 1 MiB free
  for what the program writes next to itself (lost at the stop).
- **Files**: the program under an 8.3 name (`ToShortName`: last part of the name, upper case,
  letters, digits, `_` and `-`, 8 characters, extension `.COM` or `.EXE` kept, otherwise `.EXE` for an
  MZ and `.COM` for the rest; a device name such as `CON`, or the batch's, takes a `_`) and
  `OSX86RUN.BAT`.
- **Arguments**: joined by blanks, an argument with a blank in double quotes, `%` doubled for the
  batch; refused (code 6): what is not printable ASCII, `|`, `<`, `>`, `"`, and a line longer than 125
  characters (DOS's command tail).

### Start and end without agent

The image's `FDAUTO.BAT` (version 0.2.0) ends with `if exist D:\OSX86RUN.BAT call D:\OSX86RUN.BAT`.
The batch, written by Os-x86:

```bat
@ECHO OFF
REM Os-x86 (work item 11): runs one DOS program, its end and ERRORLEVEL told on COM1.
CLS
D:
CD \
ECHO OSX86-START >COM1
D:\TOOL.COM a1 b2
ECHO OSX86-EXIT %ERRORLEVEL% >COM1
PAUSE >NUL
```

Both markers go through **COM1** (the serial port, never the screen); the session reads them on
`serial0`. `OSX86-START` says FreeDOS booted and the program starts (the screen is shown from there);
`OSX86-EXIT <n>` gives the **real exit code** (FreeCOM's `%ERRORLEVEL%`, checked by the `guest`
test: 3 and 42). `PAUSE >NUL` waits for a key that never comes (FreeDOS idles with `FDAPM APMDOS`):
the program's last screen does not move until the VM stops. Without the `D:` disk, `FDAUTO.BAT`
gives the prompt as before, without a message (checked by `FreeDosGuestTests`).

### The screen (214 A, 218 A)

- **Reading**: the bridge reads the screen every **33 ms** (`ReadTextScreenAsync`). The worker reads
  it from v86's VGA memory through `vga-text.js`, **the only file that touches internal fields of
  v86** (`vga_memory`, `start_address`, `cursor_address`, `attribute_mode`… of v86 0.5.473, 218 A):
  CP437 characters turned into Unicode (VGA glyphs, `☺`, `╔`…), one attribute byte per cell, the
  hardware cursor (position, shown or hidden), blinking. When a version of v86 lacks those fields, it
  falls back to the characters of the public `screen-put-char` event, light grey on black, without
  cursor.
- **Comparison by row** (`TextScreenDiff`): only the rows whose text or attributes changed are
  formatted again; nothing is sent to the terminal when neither a row nor the cursor changed.
- **Rendering** (`IGuestScreenTerminal`, adapter `KazoFullScreenTerminal`): the 3.4.0 SDK replaces
  the whole frame on each `RenderAsync`; each row carries its colours as 24-bit SGR sequences (the
  default VGA palette; the terminal's theme does not change how a DOS program looks), the way `htop`
  colours its rows; the cursor follows the VGA's. When the window has fewer rows than the program's
  screen, the rows shown follow the cursor; the columns past its width are cut (80 x 25 wanted,
  215 A). A resize redraws everything.
- **ADR 0252** will enrich `IGuestScreenTerminal` (colours per cell and native function keys,
  sending only the changed rows when the Terminal allows it) without touching the bridge: the
  capabilities (`GuestTerminalCapabilities`) already say what a terminal can do.

### The keyboard and the chords (215 A, 216 A)

v86's PS/2 keyboard gets **scancodes of set 1** (the worker's `keys` command), for the DOS way only:
the worker refuses `keys` to an engine started without `keyboard: true`.

| Terminal key | What the program gets |
| --- | --- |
| letters, digits, punctuation of the US layout | the key, with Shift when needed (`A`, `!`) |
| Enter, Backspace, Tab, Shift+Tab, Escape | the key |
| arrows, Home, End, Delete, Page Up, Page Down | the extended key (0xE0 prefix); Kazo 3.4.0 has them |
| Ctrl+letter | Ctrl held around the letter; **Ctrl+C goes to the program** (216 A) |
| Alt+letter (Meta prefix decoded by Kazo) | Alt held |
| **Escape then letter** | **Alt+letter** (chord, 215 A) |
| **Escape then 1 to 9, 0** | **F1 to F10** |
| **Escape then `-`, `=`** | **F11, F12** (the two keys after 0 on the same row: this work item's proposal) |
| Escape then Escape | one Escape |
| Escape alone | sent after 800 ms without a second key |
| Escape then another key | Escape then that key |
| character with no key on a US layout (`é`…) | nothing |

Alt is held over three sends **40 ms** apart (Alt down, the key, Alt up): E14 measured that EDIT
ignores Alt+F sent in one burst. In full screen, Kazo 3.4.0's Terminal gives Ctrl+C to the program as
a key when nothing is selected (a selection is copied). To **kill** a DOS program that does not give
control back: `x86 vm stop --force dos` in another tab of the terminal (the command then returns
`console-closed`); there is neither Ctrl+Break nor a Terminal button in 3.4.0.

### Graphical mode (217 A, 164 A)

The signal is v86's `screen-set-size` event with `bpp ≠ 0`, passed on by the worker (`screen-size`)
then `IEngine.ScreenSizeChanged`. Only a switch after the program's start counts (the BIOS and
FreeDOS change modes while booting); a switch seen before the `OSX86-START` line counts as soon as it
arrives (COM1 is flushed every 4 ms while the mode change is posted at once: the `guest` test showed
it). Without BoxedWine, the alternate screen is given back, the message gives the mode and the
decision, the program and the VM are stopped, code 6:

```text
x86: the DOS program switched to a graphical mode (320x200, 8 bits per pixel). Graphical DOS programs are shown by BoxedWine (decisions 164 A and 217 A), which is not installed: the program and its virtual machine dos were stopped.
```

BoxedWine taking the VM over (217 A: same VM, canvas in a window, back to the Terminal in text mode)
comes with BoxedWine (work item 13).

### Measurements (DOS)

`guest` test `X86RunFreeDosGuestTests`, three passes, same host as below (Windows 11, Ryzen 7 3700X,
headless Chrome through Playwright, FreeDOS pack from a local origin):

| Measure | Value |
| --- | --- |
| First `x86 run`: VM made, pack downloaded and verified (16.4 MiB), FreeDOS boot to the program's screen | 2.7 to 2.9 s |
| Next `x86 run` (image installed), to the program's screen | 2.5 to 2.7 s |
| Graphical program, from the run to the VM's stop | 2.6 to 2.9 s |
| **Key → screen** (from the key the terminal gave to the frame that shows it; 20 keys one at a time) | median 47.8 to 48.6 ms, p90 49.5 to 51.6 ms, max 64 to 65 ms |
| Reading the screen (C# → worker round trip, 80 x 25 with attributes) | median 1.8 to 2.0 ms, max 2.8 to 3.9 ms |
| Formatting and rendering a frame | ≤ 0.3 ms |
| Useful polls | 22 frames for 35 polls while typing; nothing sent while idle |

Key → screen matches E14 (about 50 ms median, under 100 ms): FreeDOS's share (v86 waking up on the
PIT tick, 55 ms, with `FDAPM APMDOS`) dominates, not the terminal's.

### Checked in the dev host

In the Kazo 3.4.0 Terminal of `OsX86.Os` (guest session), with `OSX86KEY.COM` and `OSX86GFX.COM`
sent through the "Upload" button of the File Explorer then copied into `~/.config/ca.kazo.x86/` (the
command only reads its private folder, see the limits): `x86 run …/osx86key.com a1 b2` makes the
`dos` VM, downloads FreeDOS 0.2.0, shows the screen (yellow on blue title, cursor), gets `a`, `B`,
Tab, Escape+x (`K=2D00`, Alt+X), Escape+1 (`K=3B00`, F1), the up arrow, Ctrl+A, Ctrl+C (code 3); a
second run ends with Enter (code 42); the graphical program gives the message and code 6;
`x86 vm list` shows the `dos` VM stopped after each program. The Ctrl+C typed by the browser's
automation tool did not reach the page (the tool takes it as "copy"): sent to xterm.js as a key, it
reached the program.

### Tests (DOS)

- **`unit`**: `DosProgramDiskTests` (the disk read back as DOS reads it: MBR, BPB, cluster chains,
  FAT12 by the cluster count, large program, 8.3 names, arguments, batch, markers),
  `GuestKeyboardTests` (scancodes, Shift, Ctrl+C, Alt held 40 ms, extended keys, F1 to F12, Escape
  chord, delay of a lone Escape, comparison of rows and attributes), `DosRunTests` (choice and making
  of the DOS VM, start with `hdb` in memory, keyboard and no agent, COM1 markers, stop at once,
  graphical mode before and after the start line, VM stopped under the program, refusals),
  `DosTerminalTests` (`x86 run` on a simulated full screen: screen, colours, cursor, keys and chords,
  exit code, last screen, measures, graphical mode, dead VM, refusals; frames, smaller window, Kazo's
  keys), `TextModeEngineTests` (`keys` command, `keyboard` option, `{inline}` source, screen with
  attributes), and `disk-tests.js` under Node (`{inline}` disk, `vga-text.js` on a simulated VGA and
  without its fields).
- **`guest`**: `X86RunFreeDosGuestTests`, on the FreeDOS pack and the test programs, in a real
  browser, with the production engine, storage and `VmManager` and a simulated full screen (Kazo's
  Terminal does not exist in the test): `x86 detect`; `x86 run osx86key.com a1 b2` (screen, colours,
  cursor, `a`, `B`, Tab, Alt+X and F1 by the chord, arrow, Ctrl+C → code 3, VM stopped); a second
  `x86 run` (20 keys one at a time, Enter → 42); `x86 run osx86gfx.com` (mode 13h → message, code 6,
  VM stopped). Its name holds `FreeDosGuestTests`: the `guest` job of `images.yml` runs it with the
  pack and the `freedos-test-programs` artifact.
- **Test programs**: `images/freedos/programs/osx86key.asm` (title in colour, arguments, reads keys
  with the BIOS int 16h, writes `K=` scancode and ASCII, Enter → 42, Ctrl+C → 3, int 23h hooked so
  that DOS never takes Ctrl+C) and `osx86gfx.asm` (mode 13h, a diagonal, waits for a key); assembled
  by NASM 3.01 pinned (`nasm=3.01-r0` of Alpine 3.24) in the `osx86-nasm:3.01` container, never
  committed.

### Limits and not verified (DOS)

- **Reading the user's files**: the `x86` command only declares `filesystem.private.read` and
  `.write`; in Kazo it therefore cannot read `~/prog.com` ("no file"), nor a Windows program (same
  limit for work item 10). The declared permissions come with the Feature (work item 8).
- **Ctrl+C taken by DOS**: a program that reads through DOS functions with `BREAK` can be stopped by
  DOS itself, and FreeCOM then asks whether to end the batch ("Terminate batch job (Y/N)?"); not
  provoked. The test program reads through the BIOS and hooks int 23h.
- **Not verified**: EDIT and other real FreeDOS programs through `x86 run` (only the test programs);
  a 50-row text mode; a real `.EXE` program and a program that writes on `D:`; Firefox and Safari;
  ADR 0252 (not coded).
- A single file is copied: a program that needs files next to it does not find them.
- No layout but US (no `KEYB` in FreeDOS, an `é` is not sent); no Alt+numeric keypad.
- The 16 colours of the default VGA palette: a palette changed by the program (DAC) is not followed;
  blinking (bit 7 when it is on) is not rendered.

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
- **Work item 13**: BoxedWine (windowed programs, installers, graphical DOS).

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
