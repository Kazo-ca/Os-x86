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
| `src/OsX86.Feature/wwwroot/engine/` | `v86-host.js` (the module the C# imports; creates the worker), `v86-worker.js` (hosts v86), `zstd-seekable.js` (zstd seek table) and `disk.js` (disks, frame cache, write layers), work item 6a; `vga-text.js` (text screen with attributes and cursor), work item 11 |
| `src/OsX86/Engine/ImageBoot.cs`, `EngineDisk.cs` | the boot described by the manifest and the same-major-version rule (169); sources, disks and layers of the start options (work item 6a) |
| `src/OsX86.Feature/wwwroot/v86/` | vendored v86: `libv86.js` (patched), `v86.wasm`, `seabios.bin`, `vgabios.bin`, `LICENSE`, `COPYING.LESSER`, `VENDOR.json` |
| `tools/v86/` | `vendor-v86.py` (downloads, verifies, applies the patches) and `patches/` |
| `images/alpine-test/` | `build-alpine-test.py` and `init.sh`: the Alpine test image (never committed) |
| `tests/OsX86.Tests/Engine/`, `tests/OsX86.Tests/Guest/` | the `unit` tests and the `guest` test |

The files under `wwwroot/` are static web assets of the Feature project, `OsX86.Feature`, served under
`_content/OsX86.Feature/` (the address Kazo OS gives to an app's files, Kazo ADR 0206).

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
Work item 11 uses it: a switch to `bpp ≠ 0` during a DOS program stops it (217 A).

## The DOS way: screen with attributes, keyboard, disk in memory (work item 11)

What work item 11 adds to the engine for `x86 run` of a DOS program
([`programs.md`](programs.md#dos-programs-work-item-11)):

- **`ITextModeEngine`** (`EngineTextScreen.cs`), implemented by `V86Engine`: `ReadTextScreenAsync`
  now returns an `EngineTextScreen` with `Attributes` (one VGA byte per cell), `Cursor`
  (`EngineTextCursor`: row, column, shown), `BlinkEnabled` and `Graphical`; `SendScancodesAsync`
  sends PS/2 scancodes of set 1 (`keys` command).
- **`vga-text.js`**: the only file that reads internal fields of v86 (218 A) — the VGA device
  (`emulator.v86.cpu.devices.vga`), its text memory, the start address, the cursor, the attribute
  mode — following `text_mode_redraw()` and `update_cursor()` of `vga.js`; without those fields
  (another version of v86) it falls back to the characters of `screen-put-char`, without attributes
  nor cursor (`source: "events"`). Its CP437 table went from control characters to the VGA glyphs
  (`☺`, `♥`, `⌂`…).
- **`screen` answer**: `cols`, `rows`, `lines`, `attributes` (base64), `cursor` (`{row, col,
  visible}` or `null`), `blink`, `graphical`, `source`.
- **Keyboard**: the start option `keyboard` (`EngineStartOptions.Keyboard`); without it the worker
  refuses `keys` (`invalid-state`). v86's `disable_keyboard` stays true: it only turns off the DOM
  adapter (absent from a worker); the guest's PS/2 keyboard is always there and
  `keyboard_send_scancodes` feeds it. At most 256 scancodes per message; the pauses (Alt held) are
  made on the C# side.
- **Disk in memory**: `EngineFileSource.InMemory(bytes)` (at most 64 MiB) becomes the source
  `{inline: base64}`; `disk.js` makes it an `InlineEntry` (no OPFS file), to put on a discard layer.
  It is the program disk of a DOS run (`hdb`).

## Disks and the boot described by the manifest (work item 6a)

Work item 6a (`j0/6a-disk`) boots the published images as the storage keeps them: everything
comes from `osx86/images/<image>/<version>/image.bin` in the OPFS, with no extracted file.

### Starting an image

```csharp
ImageManifest manifest = await store.ReadManifestAsync("linux-base", "0.1.0", ct);
EngineStartOptions options = ImageBoot.CreateStartOptions(manifest, new ImageBootOptions
{
    LayerMode = EngineWriteLayerMode.Persistent,   // Discard by default: nothing is kept
    LayerSet = "vm-1",                             // osx86/layers/vm-1/
});
await engine.StartAsync(options, ct);
```

- `ImageBoot` reads the `boot` section of the manifest (`kind`, `cmdline`, `disks`, `memoryMiB`):
  `linux` gives the kernel and the initramfs as ranges of `image.bin` (`EngineFileSource.Seekable`:
  offset, size, `rawSize`, `blockSize`), `dos` boots `hda` through the BIOS. The disks of
  `boot.disks` (`hda`, `hdb`) get their write layer.
- **Same-major-version rule (169)**: the image must be built for the same major version of v86 as
  the engine (`V86Engine.V86Version`, equal to `VENDOR.json`, checked by a test; before 1.0 the
  minor counts, 0.5.x and 0.6.x differ). Otherwise an `EngineException` with the code
  `engine-version-mismatch`, before anything is read or written. A manifest without `boot`, of an
  unknown kind, with an unknown disk or without the named file gives `boot-unsupported`.
- `EngineStartOptions` gains `Kind`, `Kernel`, `Initrd`, `Disks` and `FrameCacheMiB`; the
  `KernelPath` and `InitrdPath` paths of work item 2 remain (whole file). `IEngine` is unchanged.

### The disk, read

`V86Disk` (`disk.js`) is the buffer v86 gets as `hda`: a `FileSystemSyncAccessHandle` on
`image.bin`, opened once for every logical file. When the entry opens, its seek table is read in
two reads (the 9-byte footer, then the table it sizes) and compared with `rawSize` and
`blockSize` of the manifest; a damaged or disagreeing table gives `image-corrupt`. A sector read
is cut into frames; a missing frame is read from `image.bin`, decompressed, and kept in a
**bounded LRU cache** shared by the disks: 32 MiB by default, 2 to 512 MiB (`FrameCacheMiB`),
with its counters (bytes, frames, hits, misses, evictions). The kernel and the initramfs are
decompressed whole in memory before the emulator is created.

**zstd decoder: v86's own.** `v86.wasm` exports the decoder v86 uses for its own `.zst` images
(`zstd_create_ctx`, `zstd_read`, …). `disk.js` instantiates `v86.wasm` a second time in the
worker with stub imports, as v86's own `zstd_decompress_worker` does. Nothing is added to the
repository: no code, no license, no hash (`v86.wasm` is already pinned by `vendor-v86.py`). The
instance is separate because the kernel must be decompressed before the emulator exists, and so
that a disk read never re-enters the wasm of the running CPU. This decoder traps on an invalid
frame instead of reporting it, so the frame header is checked first (magic number, declared size
equal to the table's) and the instance is thrown away after a trap. Cost: 13 MiB of wasm memory
(measured).

### The write layer

The published image is never written. The blocks the guest writes go to a layer:

- **Discard** (`EngineWriteLayer.Discard`, the default): the blocks stay in memory and vanish at
  the stop. For tests and VMs without state.
- **Persistent**: one file `osx86/layers/<set>/<disk>.layer`. A 4 KiB header (`OSX86-LAYER`,
  format 1, 4 KiB blocks, disk size, block count, offsets), a **block map** (one u32 per block:
  0 = not written, n = slot n − 1), then the written blocks, appended in the order of their first
  write. Chosen over a sparse file the size of the disk: the OPFS exposes no holes, and a
  `truncate` to the disk size counts against the quota and does not say what was written. The
  map says it, and the file only weighs the written blocks plus 0.1 % of map (124 KiB for the
  130 MiB of linux-base). A partial block is read whole (layer or image), patched and written
  whole; a block is written before its map entry, so the map is never ahead of the data.
- **Versioned formats**: `<disk>.layer.meta.json` is a `StoredObjectMetadata` of kind `layer`
  (format, image, image version, `engine.v86`), written at creation. When the layer is reopened:
  another image version or disk size → `layer-mismatch`, another major version of v86 →
  `engine-version-mismatch`, another format → `format-unsupported`; nothing is written (169).
- **One writer** (E11): the `navigator.locks` lock `osx86:layer:osx86/layers/<set>`, waited for
  2 s then `layer-locked`, plus the exclusivity of the `SyncAccessHandle`.
- **`flush()`** every 5 s while the layer is open, at pause and at stop (177 A).
- **Size and deletion**: `ImageStore.ListLayersAsync(set)` and `ImageStore.DeleteLayersAsync(set)`
  (see [`storage.md`](storage.md)); while it runs, `V86Engine.GetDiskStatisticsAsync` gives the
  counters of the disks, the layer and the cache.

### Protocol and C#

The `start` command carries `kind`, `kernel` and `initrd` (sources), `disks` (source and layer per
disk) and `cacheMiB`. Three commands are added: `read-screen` (the VGA text screen, kept by the
worker from the `screen-put-char` events, CP437 to Unicode, as in trial E14), `query-disks` (the
counters) and `disk-benchmark` (reads outside the guest, for the measures).
`V86Engine.ReadTextScreenAsync` and `GetDiskStatisticsAsync` expose them. New error codes:
`image-corrupt`, `disk-io-failed`, `quota-exceeded`, `layer-locked`, `layer-mismatch`,
`format-unsupported`, `engine-version-mismatch`, `boot-unsupported`.

**Channel fix**: `JsEngineWorkerChannel` installs its callback before `createHost`, as
`JsStorageWorkerChannel` does; a `ready` posted by a fast worker is no longer lost (test
`A_ready_posted_before_createHost_returns_is_not_lost`).

### Measures

Headless Chrome (Playwright), Windows 11, Ryzen 7 3700X, linux-base 0.1.0 with 256 MiB, 32 MiB
cache.

| Measure | Value |
| --- | --- |
| Copy of the linux-base pack (73 MB) into the OPFS by the test page | 0.3 to 0.6 s |
| linux-base: boot to `OSX86-READY` | 19 to 27 s (four boots: 24, 25, 26, 27 s; 19 s in another run) |
| Agent `ping`, round trip | median 4.4 to 5.8 ms |
| Sequential read of `hda` outside the guest (128 MiB) | 358 MB/s |
| Random 4 KiB reads outside the guest (4,000 reads over 130 MiB) | 465 reads/s (almost every read decompresses a 1 MiB frame) |
| linux-base boot: guest reads on `hda` | 438 reads, 10.5 MB read, 16.4 MB of compressed bytes read from `image.bin`, 61 frames decoded in 154 ms |
| Memory: frame cache, decoder | 32 MiB (full after the boot), 13 MiB |
| Layer after writing one file | 217 KiB (header, 124 KiB map, written blocks) |
| FreeDOS: boot to `C:\>` | 3.2 s |

Random reads are the weak point: a 4 KiB read that misses the cache costs the decompression of a
1 MiB frame. For the guest, which mostly reads near what it just read, the cache is enough (402
hits for 48 misses during the boot). Should a use show otherwise, smaller frames (`--block-size`
of `pack-image.py`) or a bigger cache fix it without changing the format.

### What the real boot found

Booting linux-base and running `verify-console.sh` in the VM found two image defects, fixed in
the recipes (see [`images.md`](images.md)): a Windows Git checkout in CRLF that left the
initramfs without `sd_mod` and `ext4`, and `/dev` left in 0660 by the `mdev` of the initramfs (no
user could open `/dev/null`).

### Tests of work item 6a

- `unit`: `DiskScriptTests` runs `tests/OsX86.Tests/Engine/disk-tests.js` under Node 22.15 or
  later (seek table, read plan, frame header, the decoder of `v86.wasm` and an invalid frame,
  cache, entries, discard and persistent layers on a fake OPFS, one writer, refusal of another
  image version, major version or format without writing, quota, an entry made by
  `pack-image.py`); `ImageBootTests` (options, the `start` command, rule 169, the queries of
  `V86Engine`); `ImageBootSectionTests` and `LayerStoreTests` on the storage side; the `ready` of
  `JsEngineWorkerChannel`.
- `guest`: `FreeDosGuestTests` (the pack copied into the OPFS, `hda` to the prompt, text screen
  read) and `LinuxBaseGuestTests`: four boots, persistent then discard layer; `OSX86-READY`, the
  agent answers `ping` through `AgentClient`, `verify-console.sh` run from an agent console of an
  ordinary user holds its 22 R134.5 checks, a write survives a clean stop with the layer and is
  gone in discard mode. linux-base has no prefix user yet (work items 9 and 10): the test adds two
  in memory from the root serial shell. The `guest` job of `images.yml` runs them on the packs
  built in the same workflow, without building them again.

## Vendored v86

v86 0.5.473 (npm package, commit `315b2b0`), BSD 2-Clause license, notice kept in `LICENSE`; the
BIOS files (`seabios.bin`, `vgabios.bin`) are the ones the v86 maintainers build from SeaBIOS
`rel-1.16.2` (LGPL-3.0, `COPYING.LESSER` kept). `VENDOR.json` records the provenance (URLs, SHA-256
before and after the patches). Nothing downloaded is executed by the script: it hashes while
writing (bounded size, a `.part` file renamed once verified), copies, patches.

```bash
python -I tools/v86/vendor-v86.py            # regenerates src/OsX86.Feature/wwwroot/v86
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

- The JavaScript and v86 files live in `src/OsX86.Feature/wwwroot/`, served under
  `_content/OsX86.Feature/`. They were in `src/OsX86/wwwroot/` (x86-Lib) until work item 8:
  `KazoOsPack` packs only the resources of the app's project (Kazo ADR 0221), and an installed app
  is served only at `_content/<its assembly>/` (ADR 0206); decision 290 B.
- The host module must be imported from a context that has the DOM: in Kazo, a command with
  `execution: pool` runs in a worker without `IJSRuntime` (ADR 0070), so the engine will be created
  by the Feature (the "Feature" app kind, ADR 0243), not by the `x86` command. Work item 8.
- The worker reports the state; the C# guesses nothing and refuses forbidden operations before
  sending them.
- Patch #1661 is ported onto the compiled file rather than v86's sources: no v86 build chain
  (Rust, Closure) in this repository.
- The test image comes from the official ISO rather than Docker: no daemon to run, one hash to pin,
  and release files (`releases/`) never move, unlike the `apk` packages of a branch.
