> [Version française](../fr/vm.md)

# VMs, their lifecycle and the `x86` command

Milestone 0, work item 6 (`j0/6-vm-and-command`). This work item puts the VM layer on top of the
engine (work item 2), the storage (work item 3) and the guest agent (work item 5): the per-user VM
registry, the VM lifecycle, the memory limit, `.x86profile` profiles, and the `x86 vm`,
`x86 shell` and `x86 profile` commands ([milestone order](../conception/jalon-0-execution.md),
French only). Decisions: 165 A (several VMs, memory limit), 166 A (what an "application" is),
183 A (profiles), 190 A (stored by stable user identifier), 200 B (`prefix`), 206 A and 207 A
(launches and processes), 212 A (image without agent), 213 A (pty), 216 A (Ctrl+C).

## Where things are

| Path | Content |
| --- | --- |
| `src/OsX86/Vm/` | `VmRegistry` and its document (`VmRegistryDocument`, `VmRecord`, `IVmRegistryStore`), `VmManager` (the machine's VMs, the budget), `VmInstance` (the lifecycle of one VM), `VmApplication`, the consoles (`VmConsoles.cs`), `MemoryBudget`, `IEngineFactory` and `IBootImageSource` with `BootImage`, `JsEngineFactory` and `VmHost` (`VmHostServices.cs`), `StoreBootImageSource` (work item 6b), `X86Service` (the implementation of `Ix86`) |
| `src/OsX86/Profiles/` | `VmProfile` (the `.x86profile` format), `BuiltInProfiles` (`alpine-test`, `linux-base`) |
| `src/OsX86.Feature/` | `X86Command` in four files (dispatch, `vm`, `shell`, `profile`), `CommandArguments`, `TerminalTextFilter`, `X86Runtime` (the shared manager), `X86Files` (Kazo files, the registry store) |
| `src/OsX86.Interface/` | `Ix86` extended, `IVmConsole`, `VmConsoleOptions`, `X86Exception` and `X86ErrorCodes`, `VmMemoryBudgetExceededException` |
| `src/OsX86.Os/` | the dev host: `Program.cs` installs engine, storage and catalog (`VmHost`), `DevImages.cs` reads the packs built locally (work item 6b) |
| `tests/OsX86.Tests/Vm/` | `unit` tests (simulated engine and agent, manual clock; `LinuxBaseDefaultTests`) and `guest` tests (`VmShellGuestTests`, `X86ShellLinuxBaseGuestTests`, `vm.html`) |

## Overview

```
x86 (command) ──┐
Ix86 (apps)  ───┴─> VmManager ──> VmRegistry ──> IVmRegistryStore (one document per user)
                        │
                        ├──> MemoryBudget (165 A)
                        └──> VmInstance (one per VM) ──> IEngineFactory ──> IEngine (one per run)
                                   │                        └──> AgentClient (EngineAgentChannel)
                                   └──> applications, children, watchdog, consoles
```

One `VmManager` per host holds the VMs of the whole machine: the memory budget counts those of
every user. Every operation names the user and reaches that user's VMs only (190 A); another
user's VM answers `vm-not-found`, exactly like a VM that does not exist. Administrator rights over
other users' VMs are work item 7.

## The registry (190 A)

A VM has a GUID, a name, a creation date and its profile (kind, image and version, memory,
declared serial port, default prefix); its state is not stored, it lives in memory. The registry is
**one document per stable user identifier**, never per login name: an account deleted and created
again with the same name does not get the old account's VMs back.

- **In Kazo**, the user's document is in the OPFS of the Feature, next to the disks:
  `osx86/users/<guid>/registry.json` (work item 8, decision 287 A; `UserStateStore`, written by the
  storage worker). Kazo 3.5 lets neither the command (the child `ca.kazo.x86.cli`) nor the `Ix86`
  service reach the user's `~/.config/ca.kazo.x86/`, where work item 6 kept it. The identifier
  comes from `IAppExecutionContext.UserId` for the command and from `Caller.UserId` for the service
  (the 32 hexadecimal digits of Kazo's `LocalUserId`); a login name is refused (`invalid-user`). The
  code of Os-x86 separates the users: each only reads the directory of its identifier. An eviction
  by the browser erases the registry with the disks; the marker of ADR 0250 covers both. These
  files are not visible in the File Explorer.
- **Format 1** (section 13 of the plan), versioned like the other documents of Os-x86:

```json
{
  "format": 1,
  "user": "0000000000000000000000000000a001",
  "vms": [
    { "id": "3f2c…", "name": "Alpine", "created": "2026-11-12T10:00:00+00:00",
      "profile": { "format": 1, "name": "alpine-test", "kind": "linux", "image": "alpine-test",
                   "imageVersion": "0.1.0", "memoryMiB": 128, "serialConsole": true, "prefix": "test" } }
  ],
  "profiles": []
}
```

- An unknown format is refused and nothing is written (169). Also refused: another user's
  document, an empty or duplicate identifier, two names equal ignoring case, an invalid profile.
- Operations: create (from a profile copied into the VM: the recipe, 121), list, find by GUID or
  by name (ignoring case, the GUID wins), rename (allowed while running), remove (refused while
  running unless `--force`, which stops it first). A name has 1 to 64 characters, no control
  character, no leading or trailing blank, and is unique for the user.
- The document is read again before each operation and written after each change, under a lock.

## The lifecycle (166 A)

An **application** keeps the VM running: a program launched through the agent, an interactive
shell (`x86 shell`), a console. `VmInstance.AcquireAsync` takes one; the first one starts the VM.

1. **Start on demand.** Image found (`IBootImageSource`), budget checked, state `Starting`, engine
   created by the factory (`IEngineFactory`, injectable), `StartAsync`, then the `AgentClient`
   connects (`EngineAgentChannel.For(engine)`), then `Running`. Applications that arrive during
   the boot wait for the same one: one engine per run.
2. **Application count.** Each `VmApplication` is released by `Dispose`: a console releases it when
   it closes, a program at its `exit`.
3. **Stop after the last application**, once the **grace** has passed (`StopGrace`, 5 s by
   default). An application that comes back during the grace cancels it.
4. **Start by hand** (`x86 vm start`, `Ix86.StartVmAsync`): the VM is held (72.6) and no longer
   stops by itself; `x86 vm stop` releases it and stops it.
5. **Stop**: refused (`vm-busy`) while other applications hold it. **Forced** (`--force`), it
   closes everything: consoles fail with `console-closed`, the agent client is closed, the engine
   stopped and released. Stopping erases nothing.
6. **Guest death**: if the engine stops by itself (a worker crash), the run ends like a stop.
7. **DOS VM (work item 11)**: the start waits for no agent (FreeDOS has none); a DOS program is the
   application of a run of its own (its disk is only given at the start) and the VM **stops as soon
   as it ends**, without the grace (`VmInstance.StopIdleRunAsync`); see
   [`programs.md`](programs.md#dos-programs-work-item-11).

Exposed states: `Stopped`, `Starting`, `Running`, `Stopping` (`VmState`), with the
`VmManager.InstanceStateChanged` event and, filtered per user, `Ix86.VmStateChanged`.

**Grace choice: 5 s.** 166 A says "as soon as". A short grace lets a shell that reopens at once, or
a program that chains another, keep the VM, while a boot costs 8 s for the Alpine image (measured
below) and about 16 s for linux-base (work item 6b). It is configurable (`VmManagerOptions.StopGrace`, 0 = stop at
once); its writing by the administrator is work item 7.

## The memory limit (165 A)

`MemoryBudget`: the limit is half of the memory the browser announces (`navigator.deviceMemory`),
or the administrator's limit (`VmManagerOptions.MemoryLimitMiB`, read here, written by work item 7).
Without an announcement (Firefox, Safari), 4 GiB is assumed, a budget of 2 GiB.

- Under the limit (memory of the VMs starting or running plus the new one), **no question**.
- Over it, a typed refusal `VmMemoryBudgetExceededException` (`memory-budget-exceeded`), before any
  engine is created. It carries the requested memory, the running memory, the limit, **the caller's
  running VMs** and among them **the compatible ones** (same kind, same image). Other users' VMs are
  counted, never named. The choice window is a matter of user interface.
- The check and the move to `Starting` are one step for the whole machine: two VMs starting
  together cannot both pass a budget that only one of them fits in.

`navigator.deviceMemory` is rounded by the browser (8 GB at most in Chrome). The Feature will read
it through its JS module (work item 8); the skeleton's command cannot and assumes 4 GiB.

## Agent, processes and images without agent (206 A, 207 A, 212 A)

- **Children.** Each launch (`VmInstance.LaunchAsync`) and each console is registered as a child of
  the VM with its `LaunchId` at the agent's reply, and removed at `Exited` (207 A); `x86 vm info`
  lists them. This is the shape the Kazo child processes of the VM will have once the Feature is a
  real process (work item 8); nothing goes through `StartChild`.
- **Restarted agent** (a `ready` event after the connection): the children are forgotten, their
  handles fail with `closed` (behaviour of the client of work item 5).
- **Image without agent.** When the agent does not answer the connection (`not-ready`), the VM runs
  without children (`HasAgent = false`). `x86 shell` then opens the **serial console** when the
  profile declares a serial port, and refuses otherwise (`no-console`, code 6). A serial console is
  the whole `ttyS0`, shared with the boot messages, without exit code (0 when closed).

### Agent watchdog

**Choice: a periodic ping**, every 20 s (`AgentWatchdogInterval`), each ping having 10 s to answer
(`AgentWatchdogTimeout`). After **2 misses in a row** (`AgentWatchdogMisses`), the agent is declared
**lost**. A ping on demand also exists (`VmInstance.PingAgentAsync`).

- Cost: a `ping` takes 3 to 9 ms; one every 20 s is negligible. A dead agent is seen within 40 to
  60 s, without false alarms for a loaded VM thanks to the 10 s margin.
- **What happens when it stops answering**: the `VmManager.AgentLost` event is raised, the children
  are forgotten, `x86 vm info` shows "lost", and any new console or launch fails with `agent-lost`.
  **The VM is not stopped**: an open console may still work if only the protocol channel is stuck,
  and stopping it is the user's decision (`x86 vm stop --force`). The agent is not restarted: it
  cannot be (work item 5).

Measured by the `guest` test over six runs (headless Chrome, Windows 11, Ryzen 7 3700X, Alpine
test image):

| Measure | Value |
| --- | --- |
| `x86 shell` to the first prompt, cold boot of the VM included | 8.2 to 11.6 s |
| Ctrl+C (a `^C` line) to the next prompt | 61 to 64 ms |
| Agent `ping`, round trip through the test bridge | 8.7 to 45 ms |
| `exit` to the start of the stop, with the test's 3 s grace | 3.0 s |

**Echo of the guest terminal at connection.** Until the agent opens `hvc0` in raw mode, the guest's
terminal driver echoes what the host writes: the first `ping` can come back as is and fail as a
protocol error. Found by the `guest` test, which failed one run in two. The connection retries it,
like the silence of a booting guest, until the connection delay; another protocol error (an unknown
version) fails the start.

## Profiles (183 A)

A profile is the **recipe** of a VM (121): a versioned `.x86profile` JSON document.

| Field | Meaning |
| --- | --- |
| `format` | 1; an unknown format is refused and nothing is written |
| `name` | the profile name (`--profile <name>`) |
| `kind` | `linux`, `dos`, `windows-programs` |
| `image`, `imageVersion` | the store's image and its version |
| `memoryMiB` | 16 to 4096, counted in the budget |
| `serialConsole` | the image has a serial console: the only console of an image without agent (212 A) |
| `prefix` | the prefix a console opens in without `--prefix` (200 B), or absent |
| `programs` | an opaque section reserved to BoxedWine, kept as is |

Unknown properties are ignored, comments and trailing commas accepted, 256 KiB at most. Shipped
profiles: **`linux-base`** (256 MiB, serial, no prefix: its guest users come with the prefixes,
work items 9 and 10; default of `x86 vm create` since work item 6b) **`wine`** (the wine image of work item 9, VM of Windows programs, 512 MiB, serial, default
prefix `default`; work item 10, [`programs.md`](programs.md)), **`dos`** (image `freedos` 0.2.0,
VM of DOS programs of the kind `dos`, 64 MiB, neither agent nor serial console, no prefix; work item
11, [`programs.md`](programs.md#dos-programs-work-item-11)) and **`alpine-test`** (the
Alpine test image with the agent, 128 MiB, serial, prefix `test`). An imported profile (`x86 profile import`) is kept in the
user's registry and hides a shipped profile of the same name.

## The `x86` command

```text
x86 vm list
x86 vm create <name> [--profile <profile | file.x86profile>]
x86 vm start <vm>
x86 vm stop <vm> [--force]
x86 vm remove <vm> [--force]
x86 vm rename <vm> <name>
x86 vm info <vm>
x86 shell [--vm <vm>] [--prefix <prefix>] [--cols N] [--rows N]
x86 profile list | show <profile> | import <file> | export <profile> <file>
x86 rules list | allow | deny | ask | remove | log …   (work item 12, see network.md)
```

`<vm>` is a name or a GUID. Options are written `--name value` or `--name=value`, in any order; an
unknown option is an error; `--` ends the options. Messages are in English, like the rest of the
command: the skeleton has no translated resources.

```text
$ x86 vm create base
Created base (3f2c…) from the profile linux-base.
$ x86 shell
x86: linux-base 0.1.0 is not on this machine yet: downloading it.
…
x86: this is the serial console of the virtual machine: the guest's root shell, shared with its boot messages. A line ~. closes it.
osx86# echo vm-$((6*7))
vm-42
osx86# ~.
$ x86 vm create --profile alpine-test Alpine
Created Alpine (8d01…) from the profile alpine-test.
$ x86 vm list
ID                                    KIND              STATE     MEMORY  IMAGE                 NAME
3f2c…                                 linux             stopped     256M  linux-base 0.1.0      base
8d01…                                 linux             stopped     128M  alpine-test 0.1.0     Alpine
$ x86 shell --vm Alpine
~ $ echo vm-$((6*7))-$(id -u)
vm-42-1001
~ $ sleep 600
^C
~ $ exit 5
$ echo $?
5
```

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | done |
| 1 | the operation failed: agent, invalid profile or name, unknown format, start, deleting the disk of a VM (`storage-failed`) |
| 2 | wrong command line: unknown command or option, missing argument; the usage goes to standard error |
| 3 | memory budget exceeded; the running and compatible VMs are listed |
| 4 | no VM, profile, prefix or network rule of that name |
| 5 | VM busy: stop or remove without `--force` |
| 6 | nothing on this host can run it: no emulator, image missing with no configured origin, failed download, image of another v86 version, no console, no private network (`x86 rules`) |
| 130 | Ctrl+C before a console was open |

`x86 shell` returns the code of the guest shell, 128 plus the signal when a signal ended it.

### `x86 shell`, line by line

Until ADR 0252 lands, the Kazo terminal is line-based. `x86 shell` relays the agent's console
(213 A) this way:

- **output**: the pty bytes become text (UTF-8 decoded across chunks; carriage returns and CSI, OSC
  and ESC sequences removed; other control characters removed except LF, tab and BEL); the pty's
  echo of each line sent is removed once, since the Kazo terminal already shows the typed line;
- **input**: each line read is sent with CR, as the Enter key sends it (since work item 10; a
  shell's line discipline turns it into LF, Wine's console, raw, sees Enter). A `^C` line sends Ctrl+C to the guest program, as
  does the interruption of the command itself (Ctrl+C in the Kazo terminal, 216 A). A `~.` line
  closes the console, as in ssh. The end of input sends Ctrl+D;
- **end**: the exit code of the shell is returned. The console was an application of the VM, which
  stops after the grace when it was the last one.

The shell counts as an application (166 A). Without `--vm`, it takes the user's only VM; without
VM, it says so (code 4); with several, it asks for `--vm` (code 2).

## `linux-base` by default (work item 6b)

`x86 vm create <name>` without `--profile` takes the shipped profile **`linux-base`**: image
`linux-base` 0.1.0 (Debian trixie i386, agent installed), 256 MiB, serial port, no default prefix.
`alpine-test` stays for the tests (`--profile alpine-test`).

**The image comes from the repository.** The `IBootImageSource` of a host with storage is
`StoreBootImageSource`: a version complete in the OPFS is used as is; a missing or partial one is
first downloaded by the storage worker of work item 3 (every chunk verified, resumed after a cut)
from the origin the **catalog of the known images** gives
([storage](storage.md#catalog-of-the-known-images-work-item-6b)). `ImageStore.Locate` then
places the kernel, the initramfs and the disk in `image.bin` (`BootImage(ImageManifest)`), and
`ImageBoot.CreateStartOptions` starts the VM. An image made for another major version of v86 is
refused before any engine (`image-unavailable`, code 6). One download at a time: two VMs that
start on the same missing image wait for the same one.

**Progress.** During a download, `x86 shell` and `x86 vm start` write it to standard error, at the
start, every 10 % and at the end; nothing when the image is already there
(`VmManager.ImageProgress`):

```text
x86: linux-base 0.1.0 is not on this machine yet: downloading it.
x86: downloading linux-base 0.1.0: 0 % (0.0 of 69.7 MiB)
x86: downloading linux-base 0.1.0: 91 % (64.0 of 69.7 MiB, 284.6 MiB/s)
x86: downloading linux-base 0.1.0: 100 % (69.7 of 69.7 MiB, 276.0 MiB/s)
x86: linux-base 0.1.0 downloaded and verified (69.7 MiB).
```

The 10 % step is a minimum: a 32 MiB chunk covers 46 % of it, hence the jumps.

**One write layer per VM.** The writes of disk `hda` go to
`osx86/layers/vm-<32 hexadecimal digits>/hda.layer`, persistent: they survive `x86 vm stop`. The
set name is kept with the VM in the registry (`layerSet` of `VmRecord`); a VM created before work
item 6b takes the same name, from its GUID. `x86 vm remove` deletes the layer before the VM:
refused (`vm-busy`) while an engine holds it, `storage-failed` (code 1) for another failure; the
VM then stays listed and the removal can be done again. The root of linux-base is mounted
read-only; `/tmp`, `/run` and `/var/log` are in memory; since work item 10, `/home` (the prefixes)
is on the disk. From the root serial console, `mount -o remount,rw /` makes the root writable
(checked by the guest test of 6b); a later `mount -o remount,ro /` also makes `/home` read-only
until the next boot.

### `x86 shell` on a profile without prefix (decision)

The agent opens a shell only as the user `osx86-<prefix>`, and linux-base has none: they come with
the prefixes (work items 9 and 10). **Without `--prefix` and without a default prefix in the
profile, `x86 shell` opens the serial console when the profile declares one**: the guest's root
shell (prompt `osx86# `), shared with the boot messages. A notice says so on standard error; `~.`
closes it and returns 0; the VM then stops after the grace. Without a serial port, `no-prefix`
(code 1) asks for `--prefix`. With `--prefix`, it is the agent console as before (in linux-base,
`agent-error` `unknown-prefix` until the user exists).

Why this rather than a message alone: without it, `x86 shell` would open nothing in the default
image. Being root in one's own VM gives nothing beyond what the user already has: the VM is theirs
and its disk is their layer. The isolation of the prefixes (R134.5) applies to the programs
launched in the prefixes, which do not change. Once prefixes exist, the shipped profile may get a
default prefix; the serial fallback stays for the images without one.

### Measures (headless Chrome, Ryzen 7 3700X, local origin)

| Measure | Value |
| --- | --- |
| First `x86 shell` to the answer of a command, download included | 16.0 s |
| of which download and verification of the pack (69.7 MiB, local origin) | under 1 s, about 280 MiB/s |
| Second `x86 shell` after `x86 vm stop`, image already there | 16.1 s |
| Whole `guest` test (three boots, download, removal) | 49 s |

The guest boot dominates. From a real hosting, the download will depend on the network (69.7 MiB,
once per image version).

### In the dev host

A command has no `IJSRuntime` (ADR 0070 of Kazo). `src/OsX86.Os/Program.cs` therefore installs,
before any command, the environment of a browser host (`VmHost.Configure(VmHost.ForBrowser(...))`):
v86 engine through JS interop, `ImageStore` of the OPFS, catalog of the packs built locally.
`X86Runtime` reads it when it creates its `VmManager`; without it, the command stays without
emulator (code 6).

Since work item 8 (decision 288 A) the Feature installs its own: its hidden Service child
`ca.kazo.x86.host` starts at each login on the main thread and receives the page's `IJSRuntime` in
its constructor; the `x86` command, built by Kazo with the `IJSRuntime`, does the same install when
the service has not yet (`X86Host`, `VmHost.TryConfigure`). A host that installed its own before is
kept: the dev host thus keeps its catalog of packs and its test relay. The catalog of the installed
Feature is empty until the hosting of the images is settled. Before the service started, `Ix86`
answers `engine-unavailable`; services made without a host are made again once one is installed.

**Limit seen in work item 8b (Kazo 3.5.1).** At login Kazo starts only the services whose descriptor
is already loaded (`AutomaticServiceCandidates` reads the known descriptors). The assembly of the
Feature is loaded on demand, at the first launch of one of its children: without the workaround
removed in work item 8b, `systemctl status ca.kazo.x86.host` thus shows the service
`inactive (dead)` after login. The `x86` command installs the host itself, and the dev host installs
its own before anything; an app that calls `Ix86` before any `x86` command gets
`engine-unavailable`. What to do next is still to decide.

Two fixes found while trying it in Kazo (the first runs of the command in Kazo itself):

- **The command runs on the main thread** (`[AppManifest(Execution = AppExecutionHost.MainThread)]`,
  `"execution": "mainThread"` in `manifest.json`). By default Kazo runs a command in a worker of its
  pool, with its own .NET runtime (ADR 0070 of Kazo): the environment the host installs and the
  VMs of the machine, which live on the main thread, did not exist there (`x86 shell` answered
  "image not available").
- **The registry was created in `~/.config/ca.kazo.x86/`** the way Kazo's settings are
  (`SettingsTextFile.EnsureDirectoryAsync`): `KazoDirectory.CreateDirectoryAsync` walks up to
  `~/.config`, which the app may not read, and the first `x86 vm create` of a user failed
  (`CapabilityDenied`). Since decision 287 A it is in the OPFS (see the registry).

Verified in the terminal of the dev host (guest session): `x86 vm create base`, then `x86 shell`
downloads linux-base (69.7 MiB verified, about 120 MiB/s), boots the VM and opens the serial
console where `echo`, `uname -m` (`i686`) and `cat /etc/osx86-release` (`0.1.0`) answer; `~.`
returns 0, the VM stops after the grace; a second `x86 shell` downloads nothing. To try it:

```text
python -I images/linux-base/build-linux-base.py
dotnet run --project src/OsX86.Os -c Release
x86 vm create base        (in the Kazo terminal)
x86 shell
```

## The VM's network (work item 12)

When the manager has a network (`VmManager.Network`, a `NetworkHub`; the Feature always creates
one), a VM that is not a DOS one starts with a virtio-net card plugged into its user's private
switch. As soon as the agent answers, the VM takes the lowest free slot (address `10.86.0.S`) and
the agent configures `eth0` (operation `net`, protocol 3); each prefix gets its network namespace
and its address on its first use in the run. An image without `net-admin`, or an agent of
protocol 1 or 2, runs without network and without error; a failure of `net-admin` is reported in
the background and the VM runs without network. When it stops, the VM is unplugged and its flows
end (`vm-stopped` in the journal); removing the VM removes its network rules.
`VmInstance.NetworkAddress` and `NetworkAddressOf(prefix)` give the addresses of the run. The
rules, the limits and the journal: [`network.md`](network.md).

## What `Ix86` exposes, and why

What the Terminal and BoxedWine will need to call, nothing more (`PublicAPI.Unshipped.txt`):

| Addition | For whom, why |
| --- | --- |
| `VmSummary` with `Image`, `ImageVersion`, `MemoryMiB` | BoxedWine shows the VMs and offers a compatible VM (same image); the memory feeds the choice window of 165 A |
| `VmStateChanged`, `VmStateChangedEventArgs` | the VM page and BoxedWine follow the state without polling |
| `StartVmAsync`, `StopVmAsync(force)` | "start, stop a VM" of the table in section 9 of the plan |
| `OpenConsoleAsync`, `IVmConsole`, `VmConsoleOptions` | the Terminal opens a console (213 A): bytes, Ctrl+C (216 A), size, exit code |
| `X86Exception`, `X86ErrorCodes` | errors with a stable code rather than internal exceptions |
| `VmMemoryBudgetExceededException` | the refusal of 165 A carries what the choice window needs |

Not yet: creating and removing VMs, profiles, program launches, prefixes; they come with their
clients (BoxedWine, work items 10 and 13). The service is bound to a user
(`new X86Service(manager, userId)`); registering it in Kazo is work item 8.

## Tests

- **`unit`** (`VmLifecycleTests`, `VmModelTests`, `X86CommandTests`): the simulated engine
  (`FakeEngine`) talks on `virtio0` and `virtio1` to the simulated agent of work item 5; a manual
  clock runs the grace and the watchdog without waiting. Covered: the lifecycle (first start,
  applications during the boot, grace and its cancellation, start by hand, stop refused then
  forced, guest death, start failures), children, the serial fallback, the lost agent, the budget
  (refusal, compatible VMs, another user's VM not named, administrator's limit), the registry and
  its format, profiles, the service, the terminal filter, the parsing and exact output of each
  subcommand with its code. Coverage of the work item's files: 94 % of lines.
- **`guest`** (`VmShellGuestTests`): the Alpine image with the agent boots in a real browser under
  the production `V86Engine`; only the JS interop is replaced by Playwright (`vm.html`). The `x86`
  command drives it: `x86 vm create`, then `x86 shell` starts the VM on demand and opens a shell in
  the guest as the user of the `test` prefix; a command answers with no doubled echo; Ctrl+C
  interrupts `sleep 600` (status 130); `exit 5` returns 5; the VM stops after the grace and its
  worker is released. Triggered by `src/OsX86/Vm/**`, `src/OsX86/Profiles/**`,
  `src/OsX86.Feature/**` and `tests/OsX86.Tests/Vm/**` in `guest-tests.yml`.
- **`unit` of work item 6b** (`LinuxBaseDefaultTests`, `ImageCatalogTests`): the catalog (reading,
  refusals, relative addresses), `StoreBootImageSource` on the simulated storage worker (image
  present, downloaded with its progress, missing without origin, failed download, unreadable
  format, wrong origin), `BootImage` of a stored image on the VM's layer, the layer set kept,
  preserved on rename, checked when read and deleted with the VM (refused while an engine holds
  it), an image of another v86 version refused before any engine, the serial fallback without
  prefix, `VmHost`, the progress lines and the notice of `x86 shell`.
- **`guest` of work item 6b** (`X86ShellLinuxBaseGuestTests`): in a real browser, with the
  production engine, `ImageStore` and `VmManager`, `x86 vm create base` without profile takes
  linux-base; the first `x86 shell` downloads the pack from a local origin (catalog: address and
  hash), boots the VM on its layer and opens the serial console; a command answers; a file is
  written to the disk. After `x86 vm stop`, a second `x86 shell` downloads nothing and finds the
  file; `x86 vm remove` deletes the layer, and a new VM of the same image does not have it. It
  needs the pack (`images/build/linux-base/pack`): skipped in `guest-tests.yml`, which does not
  build linux-base, it runs in the `guest` job of `images.yml` (its name holds
  `LinuxBaseGuestTests`).

## What the other work items will do

- **7 (admin)**: write the memory limit and the grace, see and manage other users' VMs, keep the
  personal log; the choice window of 165 A on the interface side.
- **8 (Feature)**: done for the install of the environment (child `ca.kazo.x86.host`, 288 A), the
  registry in the OPFS (287 A) and `Ix86` resolved by Kazo. Left for later: reading
  `navigator.deviceMemory`, and making each VM a Kazo process and each child a child process (207 A).
- **Hosting of the images**: the production catalog (R2 or Pages addresses and hashes) waits for
  the hosting; no address is written in the code.
- **10 (prefixes)**: done, see [`programs.md`](programs.md). `x86 shell --prefix <p>` now needs a
  prefix of the VM (made by `x86 prefix create`, or the default prefix of its profile), made in the
  guest at its first use; an unknown name answers `prefix-not-found` (code 4). Without `--prefix`,
  nothing changes: the profile's prefix, else the serial console (6b). `x86 run` has its own way
  (`VmInstance.RunProgramAsync`: pty console, Wine session). The `linux-base` profile keeps "no
  prefix": its default shell stays the serial console.
- **ADR 0252**: once the Terminal has its full screen, `x86 shell` will relay the raw bytes to a VT
  emulator instead of the line filter.
- **11 (DOS)**: done, see [`programs.md`](programs.md#dos-programs-work-item-11). A VM of the kind
  `dos` starts without waiting for an agent; `x86 run` of a DOS program starts it with a second disk
  (`hdb`) and the keyboard, and stops it as soon as the program ends; `x86 shell --vm dos` is refused
  (`no-console`: the `dos` profile declares no serial console).

## Known limits and not verified

- **In a Kazo other than the dev host, the command does not start VMs**: a command has no
  emulator (`engine-unavailable`, code 6) until the Feature exists (work item 8). The dev host
  `OsX86.Os` gives it one (work item 6b). `vm list`, `create`, `rename`, `remove`, `info` and
  `profile` work everywhere on the persistent registry.
- **Not verified**: Ctrl+C of the Kazo terminal passed as the command's cancellation, Firefox and
  Safari, a Kazo user other than the guest session.
- The VMs live in the host process: reloading the page stops them all; the registry stays.
- The line filter renders neither colours nor cursor: a full-screen program (`vi`) is unreadable
  until ADR 0252. A program that turns echo off (a password) can leave at most eight lines waiting
  for an echo that never comes.
