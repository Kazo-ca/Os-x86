> [Version française](../fr/agent.md)

# The guest agent and its client

Milestone 0, work item 5 (`j0/5-agent`). The agent is a small program that runs as root in the
Linux guest and takes its orders from Os-x86: start a program for a prefix, list and stop, open a
console, report the state of the mounts. This work item ships the agent (static C), its recipe,
its installation in the images, the C# client of the protocol and their tests. The VM registry
and `x86 shell`, which use it, are work item 6
([milestone order](../conception/jalon-0-execution.md)). Decisions: 205 A (protocol), 206 A (one
launch = one cgroup), 207 A (process model), 208 A (closing the console), 213 A (a pty per
console), 216 A (Ctrl+C); the original trial is [E7](../essais/e7-agent-processus.md).

## Where things are

| Path | Content |
| --- | --- |
| `images/agent/src/` | the agent: `agent.c` (loop, port, replies), `launch.c` (launches, cgroups, kill, list), `jobs.c` (`exec-in-prefix`, `mounts`), `console.c` (ptys and frames), `url.c` (`open-url` socket), `json.c`, `agent.h`; `open-url.c` (the `osx86-open-url` program) |
| `images/agent/Dockerfile`, `build-agent.py` | the recipe: pinned Alpine i386 stage, `--check`, `--smoke` |
| `images/build/agent/` | the output (git-ignored): `agent`, `osx86-open-url`, `toolchain.txt`, `manifest.json` |
| `src/OsX86/Agent/` | the client: `AgentClient` (two files), `IAgentChannel` and `EngineAgentChannel`, `ConsoleHandle`, `ConsoleFrame`, the types (`AgentTypes.cs`), the errors, the protocol |
| `tests/OsX86.Tests/Agent/` | `unit` tests (simulated channel) and `guest` test (Alpine test image in a real browser) |

```bash
python -I images/agent/build-agent.py --smoke          # builds, checks, drives the agent in a container
python -I images/agent/build-agent.py --check          # checks an existing build, without Docker
python -I images/alpine-test/build-alpine-test.py      # the test image carries the agent when it is built
```

## Overview

```
C# AgentClient ── IAgentChannel ──> IEngineConsole virtio0 ──> hvc0: JSON lines ──> agent (root)
                                └─> IEngineConsole virtio1 ──> hvc1: console frames ──> pty ──> the user's shell or program
```

- **`hvc0`** carries the protocol: one JSON request per line with an `id`, the reply carries the
  same `id`, events have none. The port is `0600` root in the guest (the init of the images).
- **`hvc1`** carries the bytes of every open console, multiplexed by frames.
- `hvc2` and `hvc3` stay free.
- The agent is started by the init of the image when it exists as `/usr/lib/osx86/agent`. An
  image without agent stays legitimate (212 A): the client then fails to connect with `not-ready`.

The agent is one thread and one `poll` loop: the two ports, the ptys, the `open-url` socket and
its callers, a pipe for `SIGCHLD`, the pipes of the running commands and the `cgroup.events` file
of every launch. **No operation blocks another** (correction 1 of 205 A): `exec-in-prefix` and
`mounts` run in a child and answer when it is done; `kill` answers when the launch is gone. What
goes to the host passes through one queue per port: the agent never waits for a port to drain,
and a line or a frame is queued whole or not at all, never written in part.

## The protocol (version 1)

Request: `{"id":N,"op":"…", …}` followed by LF, 64 KiB at most; numbers out of range are brought
back to their bound, `nan` and `inf` are not JSON and get the line refused. Reply:
`{"id":N,"ok":true, …}` or `{"id":N,"ok":false,"error":"<code>","message":"…"}`. Event:
`{"event":"…", …}`. Unknown fields are ignored on both sides; strings are valid UTF-8 (an invalid
byte of a program's output becomes U+FFFD).

### Operations

| `op` | Request fields | Reply (`ok: true`) |
| --- | --- | --- |
| `ping` | — | `version`, `uptime` (s), `rssKb`, `launches`, `consoles`, `cgroups` |
| `list-processes` | — | `processes[]`: `pid`, `ppid`, `pgid`, `uid`, `state`, `prefix`, `launch`, `comm`, `cmd`, `cgroup` |
| `launch` | `prefix`, `program`, `args[]`, `env{}`, `cwd`, `nproc`, `log` | `launch` (identifier), `pid`, `uid`, `cgroup`, `ms` |
| `kill` by launch | `launch`, `graceMs` (default 2000, at most 10 min), `signal` (default 15; 0 refused) | `forced`, `ms`; arrives when the launch is gone |
| administrative `kill` | `scope` = `pid` \| `group` \| `prefix` \| `cgroup`, then `pid`, `prefix`, `seq`, `signal`, `allowRoot` | `count`, `signal`, `target`, `ms` |
| `mounts` | `timeoutMs` (default 8000, at most 1 h) | `mounts[]`: `path`, `type`, `device`, `state` (`ok`, `error`, `hung`), `entries`, `blocks`, `blockSize`, `freeBlocks`, `statfsErrno`, `readdirErrno`, `error`; `timedOut` |
| `exec-in-prefix` | `prefix`, `program`, `args[]`, `env{}`, `cwd`, `timeoutMs` (default 30000, at most 1 h), `maxOutput` (default 65536, at most 1 MiB) | `code`, `signal`, `timedOut`, `truncated`, `output`, `ms` |
| `open-console` | `prefix`, `program` (default: the user's shell, interactive), `args[]`, `env{}`, `cwd`, `cols`, `rows` | `launch`, `console` (number), `pid`, `uid`, `cols`, `rows` |
| `close-console` | `console` or `launch`, `graceMs`, `keep[]` (pids to keep) | `keptCount`, `kept` (the launch of the kept processes); immediate |
| `console-resize` | `console`, `cols`, `rows` | `console`, `cols`, `rows` |
| `list-windows` | — | error `no-x-server` until the image has an X server (work item 9) |

```json
→ {"id":12,"op":"launch","prefix":"test","program":"sleep","args":["300"]}
← {"id":12,"ok":true,"launch":"test/1","pid":448,"uid":1001,"ms":44.6,"cgroup":"/sys/fs/cgroup/osx86/test/1"}
→ {"id":16,"op":"kill","launch":"test/1"}
← {"event":"exit","launch":"test/1","pid":448,"code":-1,"signal":15,"remaining":0}
← {"id":16,"ok":true,"forced":false,"ms":3.1}
→ {"id":17,"op":"launch","prefix":"nobody","program":"sleep"}
← {"id":17,"ok":false,"error":"unknown-prefix","message":"no guest user osx86-nobody"}
```

### Events

| `event` | Fields | When |
| --- | --- | --- |
| `ready` | `version`, `pid`, `cgroups`, `port`, `consolePort` | the agent starts |
| `exit` | `launch`, `pid`, `code` (-1 when signalled), `signal`, `remaining` | the main process of a launch ends; `remaining` = processes still in its cgroup |
| `console-closed` | `launch`, `console`, `pid`, `code`, `signal`, `remaining` | the main process of a console ends |
| `open-url` | `prefix`, `uid`, `pid`, `url` | a guest program ran `osx86-open-url` |

### Error codes

`invalid-json`, `line-too-long`, `unknown-op`, `invalid-request`, `invalid-prefix`,
`unknown-prefix`, `program-not-found`, `exec-failed`, `cgroup-failed`, `no-cgroup`,
`unknown-launch`, `kill-pending`, `unknown-pid`, `root-process`, `kill-failed`, `no-x-server`,
`no-console-port`, `unknown-console`, `pty-failed`, `too-many-launches`, `too-many-consoles`,
`too-many-jobs`. The client adds its own: `timeout`, `not-ready`, `closed`, `protocol`,
`unsupported-version` (`AgentErrorCodes`).

## Life cycle of a launch (206 A)

1. **Prefix and user.** The prefix `p` (lowercase letters, digits, dashes, 1 to 16 characters)
   has the guest user `osx86-p`, which must exist in the image's `/etc/passwd` and not be root.
   The Alpine test image has `osx86-test` and `osx86-other`; the users of linux-base come with the
   prefixes (work items 9 and 10).
2. **`launch`.** The agent creates the cgroup `/sys/fs/cgroup/osx86/<prefix>/<seq>`, then the
   child: `setsid`, entering the cgroup, `RLIMIT_NPROC` (256 by default),
   `initgroups`/`setgid`/`setuid` to the user, `chdir` (the home by default), a rebuilt environment
   (`HOME`, `USER`, `LOGNAME`, `SHELL`, `PATH`, `TERM`, plus `env` except `LD_*`), every signal
   back to its default, `execvp`. **Never a shell line**: a program and its arguments. An `exec`
   or a step that fails (cgroup, limit, identity, directory) comes back through a pipe: the reply
   is an error (`program-not-found`, `exec-failed`), not a dead pid, and nothing runs outside its
   cgroup. A child that does not reach `exec` within 5 s (a working directory on a dead share) is
   killed.
3. **Identifier.** `<prefix>/<seq>` names the launch; this is what Os-x86 exposes, never the pid
   (under Wine the pid returned is the one of a launcher, E7).
4. **Natural end.** When the main process dies: an `exit` event with its code. The launch stays
   tracked while its cgroup has processes (`remaining`), then the cgroup is removed.
5. **`kill` by identifier.** SIGTERM (or `signal`) to every process of the cgroup; if some are
   left after `graceMs`, `cgroup.kill`; the reply (`forced` says which) arrives when the cgroup is
   empty. `graceMs: 0` = `cgroup.kill` at once. A launch that is already over answers
   `unknown-launch`.
6. **Without cgroup v2** (the container of the smoke test): the launch is a process group,
   `kill` aims at the group; the guarantees of E7 about `setsid` daemons do not hold.

Orphans come back to the agent (`PR_SET_CHILD_SUBREAPER`) and are reaped without an event: the
cgroup tracks a launch, not its pids.

## Consoles (213 A, 208 A)

`open-console` opens a pty, hands the slave to the prefix user (`0600`), makes it the controlling
terminal of the program (the user's shell, `-i`, by default) and puts it all in a cgroup of its
own: a console is a launch like any other (207 A).

**Choice: multiplexing on `hvc1`** rather than one console per port. v86 has four ports only:
one console per port would give three at most, while 213 A wants a pty per console program.
Frame, both ways:

```
0xAE  type  console (4 bytes, big endian)  length (2 bytes)  payload
```

| Type | Direction | Payload |
| --- | --- | --- |
| 1 `DATA` | host → guest: typing; guest → host: output of the pty | the bytes (4,096 at most per frame) |
| 2 `CLOSED` | guest → host, after the last `DATA` of a console | exit code and signal, two big-endian int32 |

- The console number is the sequence number of its launch.
- **Ctrl+C** is the byte `0x03` in a `DATA`: the line discipline of the pty makes it a SIGINT
  (216 A), with no other path.
- **The exit code travels on the channel of the bytes**, after them (`CLOSED`): the terminal
  knows everything has arrived when it gets the code. The `console-closed` event of `hvc0` says
  it again for the registry.
- A frame is never written in part: the stream does not lose its framing. A reader that joins
  in the middle and does not find `0xAE` at the start of a frame skips to the next one.
- **Flow control**: while the host has not taken 256 KiB of waiting output, the agent stops
  reading the ptys; a program that floods its terminal waits, as on a slow terminal, without
  disturbing the other consoles or the protocol.

Measured in the Alpine test image (v86 in Chrome, this machine, through the test bridge):

| Measure | Value | E7 (dedicated port, Debian image) |
| --- | --- | --- |
| `open-console` → first prompt | 86 to 109 ms | 154 to 170 ms |
| Ctrl+C → next prompt | 62 to 64 ms | — |
| Two consoles at once | nothing leaks from one console to the other | one console only |
| Overhead | 8 bytes per frame (0.2 % for a full frame; a keystroke costs 9 bytes) | 0 |

**Closing (208 A).** `close-console` first moves the processes of `keep` (those that have a
window; the host will name them once the image has X) to a sibling cgroup, which becomes a launch
in its own right (`kept`); the rest gets SIGHUP, then `cgroup.kill` after `graceMs`. A shell that
leaves by itself is handled the same way, keeping nothing. Checked in the `guest` test: of two
background jobs, the one named in `keep` lives on in its new launch, the other dies with the
console.

## `open-url` (139)

`osx86-open-url URL` writes the URL on the unix socket `/run/osx86/open-url.sock`. The agent
reads the caller's uid from the socket (`SO_PEERCRED`), derives the prefix and emits the event;
it does nothing else with the URL. The socket is open to every guest user: reading it never
blocks the loop (500 ms per caller, 8 callers at a time of which 2 at most for one user,
2,048 bytes).

## The C# client

```csharp
await using var agent = new AgentClient(EngineAgentChannel.For(engine));
AgentStatus status = await agent.ConnectAsync(cancellationToken);      // waits for the agent

LaunchResult launch = await agent.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "sleep", Arguments = ["300"] }, cancellationToken);
IReadOnlyList<ProcessInfo> processes = await agent.ListProcessesAsync(cancellationToken);
await agent.KillAsync(launch.Id);                                       // SIGTERM then cgroup.kill
ProcessExit exit = await launch.Exited;                                 // code or signal

ConsoleHandle console = await agent.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, cancellationToken);
await console.WriteAsync("ls\n", cancellationToken);
await console.SendInterruptAsync(cancellationToken);                    // Ctrl+C
await foreach (ReadOnlyMemory<byte> bytes in console.ReadOutputAsync(cancellationToken)) { /* to the terminal */ }
ProcessExit shellExit = await console.Exited;
```

- **`IAgentChannel`**: the two streams (protocol lines, console bytes).
  `EngineAgentChannel.For(engine)` puts them on `virtio0` and `virtio1`; `IEngine`, the worker and
  `Ix86` do not change.
- **Connection.** `ConnectAsync` sends a `ping` every second until a reply comes: the agent's
  `ready` event may have been written before anyone listened. Another version of the protocol is
  refused (`unsupported-version`). After a failure (`not-ready`), `ConnectAsync` may be called
  again on the same client.
- **Pairing.** Every request has an increasing `id`; the reply goes to whoever waits for it, in
  any order; a reply that arrives after its timeout is ignored, except the one of a `launch` or
  an `open-console`: nobody holds its identifier any more, so the client kills the launch or
  closes the console at once, and nothing runs unnamed in the guest. A reply that cannot be read
  but carries an `id` fails its request immediately (`protocol`).
- **Types.** `LaunchId` (`<prefix>/<seq>`), `LaunchResult` (with `Exited`), `ProcessInfo`,
  `MountInfo` and `MountState`, `ExecResult`, `KillResult`, `SignalResult`, `ConsoleHandle`
  (`ReadOutputAsync`, `WriteAsync`, `SendInterruptAsync`, `ResizeAsync`, `CloseAsync`, `Exited`).
- **Events.** `ReadEventsAsync`: `AgentReadyEvent`, `LaunchExitedEvent`, `ConsoleClosedEvent`,
  `OpenUrlEvent`, and `AgentFaultEvent` for what does not follow the protocol (reported, without
  stopping the client). A `ready` received after the connection means the agent restarted: the
  tracked launches and consoles fail with `closed`.
- **Errors.** `AgentException` (the agent's code), `AgentTimeoutException`,
  `AgentClosedException`, `AgentProtocolException`. Arguments that cannot reach the guest intact
  (a NUL character, a variable name with `=`, a malformed prefix) are refused before anything is
  sent.
- **End.** `DisposeAsync` sends nothing: what runs in the guest keeps running. Pending requests,
  `Exited` tasks and consoles fail with `closed`.

### Delays

| Delay | Default | Reason |
| --- | --- | --- |
| Reply to a request (`RequestTimeout`) | 30 s | the slowest, `list-processes`, takes 0.1 to 0.7 s (E7: 80 processes); a wide margin for a loaded VM |
| Connection (`ConnectTimeout`), probing (`ConnectProbeInterval`) | 120 s, 1 s | cold boot of the Linux image: 45 to 70 s (E7, E17) |
| `kill`: grace then margin (`LongOperationMargin`) | 2 s + 10 s | the grace is the agent's; the client waits for the requested grace plus the margin |
| `exec-in-prefix`, `mounts` | 30 s and 8 s + margin | the agent kills the command at its deadline; a silent mount comes back `hung` |
| Console end fallback (`ConsoleCloseFallback`) | 2 s | if the `CLOSED` frame never arrives, the `console-closed` event ends the console |
| Agent: a full port | a queue (8 MiB for the protocol, 4 MiB for the consoles) | the agent never waits for a port; above 256 KiB of waiting output the ptys are not read |
| Agent: a child that does not reach `exec` | 5 s then killed | a working directory on a dead share does not freeze the agent |
| Agent: writing to a full pty | 200 ms then given up | a program that does not read its keyboard does not freeze the agent |

## Measures

Alpine test image (kernel 6.12, 128 MiB), v86 0.5.473 in headless Chrome, Windows 11,
Ryzen 7 3700X; round trips measured in the `guest` test, hence through Playwright.

| Measure | Value | E7 |
| --- | --- | --- |
| `agent` / `osx86-open-url` binary | 111,656 / 29,668 bytes | 108 KiB / 18 KiB |
| Resident memory of the agent | 172 to 176 KiB | 160 to 184 KiB |
| Agent ready after the guest starts | ≈ 5 s (Alpine image) | 47 to 52 s (Wine image) |
| `ping`, round trip (10 times) | median 4.5 to 7.4 ms | 3.2 to 3.6 ms |
| `kill` by launch, round trip | 14 to 34 ms (3 to 25 ms in the guest) | 18 to 20 ms |
| `launch`, in the agent | 15 to 70 ms | 1.5 to 4 ms (without waiting for the `exec`) |
| `list-processes` (≈ 60 processes), in the agent | 95 to 330 ms | 380 to 475 ms (55 processes) |
| `mounts` | 3 ms | 42 to 70 ms (5 mounts) |

## Tests

- **`unit`** (`AgentProtocolTests`, `ConsoleFrameTests`, `AgentClientTests`, `AgentConsoleTests`,
  90 tests): encoding and decoding, pairing out of order, timeouts, cancellation, events,
  protocol faults, the late reply of an abandoned launch, a restart of the agent, consoles
  (frames cut anywhere, frames that arrive before the reply, an end without a `CLOSED` frame, a
  reused number), closing the client. The simulated channel (`SimulatedAgentChannel`) plays the
  agent. Coverage of the `Agent` folder: 96.9 % of the lines.
- **`guest`** (`AgentGuestTests`): the Alpine test image with the agent boots in the worker of a
  real browser and the C# client drives it; these are the checks of E7 that need neither X nor
  Wine: `ping`, `launch` (binaries of the image, never `sh -c`), `list-processes` with prefix,
  launch and cgroup, `kill` by identifier (A disappears, B and the VM stay), `cgroup.kill` of a
  tree, exit code, `mounts`, `exec-in-prefix` (identity, deadline, the agent's port and the other
  prefix's home refused), `open-url`, two consoles at once (user shell, size, resize, Ctrl+C,
  exit code, 208 A closing), the errors.
- **Smoke test** (`build-agent.py --smoke`): the agent driven over its standard input and output
  in an Alpine i386 container; it covers the fallback without cgroups.
- **CI**: `ci.yml` (`unit` tests, every PR), `guest-tests.yml` (triggered by
  `src/OsX86/Agent/**`, `tests/OsX86.Tests/Agent/**`, `images/**`), `images.yml` (`agent` job,
  triggered by `images/**`).

## Guarantees and limits

Held and checked here:

- A program runs as the user of its prefix, never root; no text of the host is interpreted by a
  shell.
- Stopping a launch touches no other launch nor the VM; a whole tree dies with its cgroup.
- A prefix user can neither read nor write the agent's port, nor read the home of another prefix
  (two of the 22 R134.5 checks, replayed in the Alpine image).
- The URL of `open-url` is attributed by the kernel, not by the message.
- In the image, the agent belongs to root and nobody else can write it (the
  `agent_files_root_only` check of the image test).

Known limits:

- **Not verified**: X and `list-windows` (a stub), Wine, booting linux-base (the worker does not
  handle `hda` yet: the agent is installed there, checked in the export, never run); the full 22
  R134.5 checks at run time (`verify-console.sh`) wait for that boot; Firefox and Safari.
- No encryption or authentication on the port: it is `0600` root in the guest and only the worker
  reaches it on the page side.
- The agent is not restarted when it dies; its tables (launches, consoles) do not survive a
  restart or a state restore.
- Bounded tables: 256 tracked launches, 32 consoles, 32 running commands, 8 `open-url` callers.
- The `exit` event is about the main process only; a launcher that leaves children gives
  `remaining` > 0 and the launch can still be killed by its identifier.
- If the host stops reading, the consoles wait (flow control); a protocol line is dropped,
  whole, only above 8 MiB of waiting lines. If a program does not read its terminal, the extra
  typing is dropped after 200 ms, and the loop waits until then.
- A launch whose `exec` lingers (up to 5 s) and a `kill` by prefix (up to 0.5 s) hold the loop
  for that time.
- No CPU or memory limit per launch (207 A): only `RLIMIT_NPROC` exists.

## What waits for work item 6

- Create the client on the engine (`EngineAgentChannel.For(engine)`), call `ConnectAsync` after
  `StartAsync`, treat `not-ready` as "image without agent" (212 A: serial console fallback).
- Register every `LaunchResult` as a Kazo process child of the VM and remove it at `Exited`
  (207 A); `x86 shell` = `OpenConsoleAsync` wired to the terminal.
- Handle `hda` in the worker to boot linux-base, then replay `verify-console.sh` from a console
  of the agent.
- Decide how the agent is watched (periodic ping) and what happens when it stops answering.
- Create the `osx86-<prefix>` users with the prefixes (work items 9 and 10); the agent has no
  operation for that.
