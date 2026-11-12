> [Version française](../fr/moteur.md)

# The v86 engine in a Web Worker

Milestone 0, work item 2 (`j0/2-engine-v86`). The engine is what runs a virtual machine: v86 in a
Web Worker, driven from C# through a small JSON protocol. This work item ships the `IEngine`
abstraction, its v86 implementation, the worker, the display bridge, the serial and virtio-console
ports, the vendored v86 with its patches, an Alpine test image and the tests. The VM registry, the
image storage and the guest agent come with work items 3, 5 and 6
([milestone order](../conception/jalon-0-execution.md), French).

## Where things are

| Path | Content |
| --- | --- |
| `src/OsX86/Engine/` | `IEngine`, `EngineState`, `EngineStateMachine`, `EngineStartOptions`, `ConsoleId`, `IEngineConsole`, `LineFramer`, the exceptions |
| `src/OsX86/Engine/V86/` | `V86Engine` (the implementation), `IEngineWorkerChannel` and `JsEngineWorkerChannel` (JS interop), `V86Console`, the protocol |
| `src/OsX86/wwwroot/engine/` | `v86-host.js` (the module the C# imports; creates the worker) and `v86-worker.js` (hosts v86) |
| `src/OsX86/wwwroot/v86/` | vendored v86: `libv86.js` (patched), `v86.wasm`, `seabios.bin`, `vgabios.bin`, `LICENSE`, `COPYING.LESSER`, `VENDOR.json` |
| `tools/v86/` | `vendor-v86.py` (downloads, verifies, applies the patches) and `patches/` |
| `images/alpine-test/` | `build-alpine-test.py` and `init.sh`: the Alpine test image (never committed) |
| `tests/OsX86.Tests/Engine/`, `tests/OsX86.Tests/Guest/` | the `unit` tests and the `guest` test |

The files under `wwwroot/` are static web assets of the `OsX86` library, served under
`_content/OsX86/` (the address Kazo OS gives to an app's files, Kazo ADR 0206).

## How it works

```
C# (V86Engine) ──JSON──> JsEngineWorkerChannel ──import──> v86-host.js ──postMessage──> v86-worker.js ──> V86
                <──JSON── [JSInvokable] OnWorkerMessage <──── callback <──── postMessage <────┘   (OPFS, console, VGA)
```

- **`v86-host.js`** is the only JavaScript module the C# imports (Kazo lets an app import its own
  module only; calls on the object it returns are free). `createHost(callback)` creates the worker,
  tells it where v86 is (next to the module, same origin required) and relays messages both ways
  without reading them. `attachCanvas(canvas)` transfers an `OffscreenCanvas` to the worker.
- **`v86-worker.js`** loads `libv86.js` with `importScripts` (once, same origin), reads the kernel
  and the initramfs from the OPFS (`createSyncAccessHandle`, which only exists in a worker: trial
  E2; at most 512 MiB per file), creates the emulator and reports its state. It runs one guest at a
  time; the requested memory is clamped (16 to 4096 MiB).
- **The protocol** is documented at the top of `v86-worker.js`: commands `start`, `stop`, `pause`,
  `resume`, `query-state`, `write`; messages `ready`, `state`, `error`, `console`, `screen-size`,
  `event`. Console bytes travel as base64 inside the JSON (at most ~1.5 MiB per write).

### States

`EngineState`: `Stopped`, `Starting`, `Running`, `Paused`, `Stopping`. `EngineStateMachine` says
which operation is allowed in which state and which worker reports are legal. `StartAsync`
returns when the guest runs, `StopAsync` when everything is torn down (nothing when already
stopped; it joins a stop under way and abandons a pending start or pause), `PauseAsync` and
`ResumeAsync` need `Running` and `Paused` respectively. One operation at a time. An operation
cancelled before it reached the worker restores the previous state; cancelled after, the worker
finishes and its next reports bring the state up to date.

### Errors

`EngineStateException` (code `invalid-state`) for an operation the current state forbids;
`EngineException` with a code of `EngineErrorCodes` for what the worker reports:
`opfs-unavailable`, `image-not-found`, `image-read-failed`, `v86-load-failed`, `v86-init-failed`,
`download-error`, `protocol`, `worker-error`, and `stopped` when the engine stops during an
operation, `disposed` when it is released. Everything is also raised through the `ErrorReported`
event; a malformed message from the worker is reported without failing the pending operation.

### Consoles

`GetConsole(ConsoleId.Serial0)` is the serial port (`ttyS0`, the boot console);
`GetConsole(ConsoleId.Virtio(n))`, `n` from 0 to 3, are the virtio-console ports (`hvc0` to `hvc3`
under Linux, the channel of the agent and of the ptys of the plan). Each port is read as a raw
stream (`ReadRawAsync`) or by lines (`ReadLinesAsync`, UTF-8, LF, trailing CR removed, lines cut at
64 KiB) and written (`WriteAsync`, `WriteLineAsync`, allowed while starting, running and paused).
A reader is registered at the call (not at the first enumeration), keeps at most 4,096 chunks or
lines (the oldest are dropped when it does not keep up), ends with its token and completes when
the engine is disposed. On stop, the pending partial line (the prompt) is delivered as a line.

### Display

`AttachDisplayAsync(canvas)` transfers the control of a `<canvas>` to the worker;
`ScreenSizeChanged` reports VGA mode changes (columns and rows in text mode, pixels otherwise,
`bpp` 0 = text: the "graphical DOS" signal of 164 A, trial E14). In this work item the worker only
resizes and blackens the canvas; the rendering comes with the graphical guests of milestone 1.

## Vendored v86

v86 0.5.473 (npm package, commit `315b2b0`), BSD 2-Clause license, notice kept in `LICENSE`; the
BIOS files (`seabios.bin`, `vgabios.bin`) are the ones the v86 maintainers build from SeaBIOS
`rel-1.16.2` (LGPL-3.0, `COPYING.LESSER` kept). `VENDOR.json` records the provenance (URLs, SHA-256
before and after the patches). Nothing downloaded is executed by the script: it hashes while
writing (bounded size, a `.part` file renamed once verified), copies, patches.

```bash
python -I tools/v86/vendor-v86.py            # regenerates src/OsX86/wwwroot/v86
python -I tools/v86/vendor-v86.py --check    # the CI: rebuilds elsewhere and compares byte for byte
```

Local patches (`tools/v86/patches/`, applied in order, each documented at its top):

| Patch | Origin | Why |
| --- | --- | --- |
| `0001-speaker-drop-stale-dac-requests-pr-1661.patch` | [copy/v86#1661](https://github.com/copy/v86/pull/1661), open, ported to the compiled `libv86.js` | Without it a 300 ms stall of the thread that runs v86 kills `aplay` (trial E5); with it the playback completes without replay |

To move to a newer version: change `V86_VERSION`, `V86_COMMIT` and the SHA-256 values in
`vendor-v86.py`, rerun, and drop the patches upstream has merged (a patch that no longer applies
stops the script).

## Alpine test image

`python -I images/alpine-test/build-alpine-test.py` downloads the official `alpine-virt` ISO
(pinned SHA-256, the one published next to the ISO), extracts the `linux-virt` kernel and the
initramfs, replaces `/init` with `images/alpine-test/init.sh` (a busybox shell on `ttyS0`, the
`hvc0` to `hvc3` ports left free) and writes `images/build/alpine-test/` (git-ignored) with a
`manifest.json`. Boot to the `osx86# ` prompt: about 7 s in Chrome. Details in
[`images/alpine-test/README.md`](../../images/alpine-test/README.md).

## Tests

- `dotnet test --filter Category=unit`: state machine, serial framing, protocol, `V86Engine`
  against a simulated worker, `JsEngineWorkerChannel` against a fake `IJSRuntime`. The CI requires
  80 % of lines covered on `OsX86` and `OsX86.Feature`.
- `dotnet test --filter Category=guest`: Playwright opens `tests/OsX86.Tests/Guest/guest.html` in
  a headless Chromium, the page copies the image into the OPFS, the worker boots it, the test reads
  `OSX86-READY` and the prompt, writes a command and reads its output, exchanges bytes on `hvc1`
  both ways, pauses (nothing comes out), resumes, stops. Skipped with a message when the image is
  not built or no browser launches (`OSX86_BROWSER_CHANNEL` forces one). The CI runs it on the
  engine paths, nightly and on demand (`guest-tests.yml`).
- By hand: serve the repository root (for instance `python -I -m http.server 8765 --bind
  127.0.0.1`) and open `tests/OsX86.Tests/Guest/guest.html`.

## Sources and licenses (plan, section 12)

| Component | Version | License | Source |
| --- | --- | --- | --- |
| v86 | 0.5.473, commit `315b2b0` | BSD-2-Clause | `https://registry.npmjs.org/v86/-/v86-0.5.473.tgz`, `https://github.com/copy/v86` |
| SeaBIOS (BIOS and VGA BIOS) | rel-1.16.2, built by v86 | LGPL-3.0 | `https://git.seabios.org/seabios.git`, recipe `bios/fetch-and-build-seabios.sh` of v86 |
| Linux kernel `linux-virt`, BusyBox (test image) | Alpine 3.22.8 x86, kernel 6.12.115-0-virt | GPL-2.0 | `https://gitlab.alpinelinux.org/alpine/aports` (branch `3.22-stable`, `main/linux-lts`, `main/busybox`); ISO `https://dl-cdn.alpinelinux.org/alpine/v3.22/releases/x86/alpine-virt-3.22.8-x86.iso` |

The test image is never published; the real images and their sources follow work item 4.

## Decisions taken in this work item

- The JavaScript and v86 files live in `src/OsX86/wwwroot/` (x86-Lib), served under
  `_content/OsX86/`; their packaging in the Feature's `.kazoapp` is to be checked at work item 8
  (Kazo ADR 0221).
- The host module must be imported from a context that has the DOM: in Kazo, a command with
  `execution: pool` runs in a worker without `IJSRuntime` (ADR 0070), so the engine will be created
  by the Feature (the "Feature" app kind, ADR 0243), not by the `x86` command. Work item 8.
- The worker reports the state; the C# guesses nothing and refuses forbidden operations before
  sending them.
- Patch #1661 is ported onto the compiled file rather than v86's sources: no v86 build chain
  (Rust, Closure) in this repository.
- The test image comes from the official ISO rather than Docker: no daemon to run, one hash to pin,
  and release files (`releases/`) never move, unlike the `apk` packages of a branch.
