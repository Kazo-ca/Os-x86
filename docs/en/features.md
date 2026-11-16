> [Version française](../fr/features.md)

# Os-x86 features

Kept up to date by every PR (plan, section 12). Numbers in parentheses refer to the decisions of the [register](../conception/registre-reconcilie.md) (French); work items, to [`jalon-0-execution.md`](../conception/jalon-0-execution.md) (French).

## Available

| Feature | Since | Note |
| --- | --- | --- |
| `x86` command: `--version`, `vm list`, `create`, `start`, `stop`, `remove`, `rename`, `info`, `shell` (guest shell through the agent, line by line, Ctrl+C, exit code, serial fallback), `profile list`, `show`, `import`, `export`; stable exit codes | 0.1.0 | Packaged as a terminal command until Kazo OS ships the "Feature" app kind (ADR 0243); work item 6, [`vm.md`](vm.md); in Kazo the command does not start VMs yet (no emulator before work item 8) |
| Contracts package `Os-x86.Interface`: `Ix86` (version, VM list and state, state-change event, start, stop, `IVmConsole` console, coded errors `X86Exception`, typed budget refusal) | 0.1.0 | Generic, no Wine type (29 C); public API tracked in `PublicAPI.Unshipped.txt` |
| Guest images: packing tool (`pack-image.py`, framed zstd, 32 MiB chunks, manifest format 1), recipes linux-base (Debian trixie i386, Alpine `linux-virt` kernel, init, 210 A hardening), Alpine test and FreeDOS, image test (22 R134.5 checks), sources and licenses, `images.yml` workflow (Pages and R2 prepared) | 0.1.0 | Work item 4, [`images.md`](images.md); 201 A, 202 A, 210 A; root on an ext4 disk (`hda`), 9p kept for the shared folder; linux-base boots with work item 6 |
| v86 engine in a Web Worker: `IEngine` (start, stop, pause, states), serial and virtio-console (raw and line streams), OffscreenCanvas bridge, vendored v86 0.5.473 with patch #1661, Alpine test image, `unit` and `guest` tests | 0.1.0 | Work item 2, [`engine.md`](engine.md); D3, 187 A; internal to x86-Lib, not exposed by `Ix86` yet |
| Storage: image manifest (format 1, SHA-256 per 32 MiB chunk, expected hash), download and verification Web Worker (`SubtleCrypto`, OPFS writes through a `SyncAccessHandle`, resume after a cut or a reload, retries, cancellation, progress), image repository in the OPFS (list, delete, footprint, `estimate()`), write lock through `navigator.locks`, periodic `flush()`, versioned-format metadata, 800 MiB `guest` test with faults | 0.1.0 | Work item 3, [`storage.md`](storage.md); 169, 177 A, 188, 203 A, E11; internal to x86-Lib, not exposed by `Ix86` yet |
| VMs: registry by stable user identifier (format 1, `~/.config/ca.kazo.x86/vms/<guid>.json`), lifecycle (start on demand, application count, stop after a 5 s grace, start by hand, forced stop), memory limit (half of the announced memory, typed refusal with the compatible VMs), launches and consoles tracked as children of the VM, agent watchdog by ping (20 s, two misses), serial console fallback for an image without agent, `.x86profile` profiles (format 1, `alpine-test` and `linux-base` shipped); `unit` and `guest` tests | 0.1.0 | Work item 6, [`vm.md`](vm.md); 165 A, 166 A, 183 A, 190 A, 207 A, 212 A, 216 A; linux-base by default since work item 6b |
| Guest agent (109 KiB static C, JSON lines on `hvc0`, one cgroup v2 per launch, `kill` by launch identifier, `mounts`, `exec-in-prefix`, `open-url`, pty consoles multiplexed on `hvc1` with Ctrl+C and exit code, 208 A closing) and its C# client (`AgentClient`, `LaunchId`, `ConsoleHandle`, typed errors); installed in linux-base and in the Alpine test image; `unit` and `guest` tests | 0.1.0 | Work item 5, [`agent.md`](agent.md); 205 A, 206 A, 208 A, 213 A, 216 A; internal to x86-Lib, not exposed by `Ix86` yet; `list-windows` waits for an X server (work item 9) |
| Guest disks (work item 6a): `hda`/`hdb` read from `image.bin` in the OPFS, zstd frames decompressed on demand by the decoder of `v86.wasm`, bounded frame cache (32 MiB); persistent write layer (one file per disk, map of 4 KiB blocks, versioned metadata, one writer, `flush()` every 5 s and at stop) or discard; size and deletion through `ImageStore`; boot described by the `boot` section of the manifest (`ImageBoot`), same-major-version rule of v86; VGA text screen read; linux-base boots with its agent and its 22 R134.5 checks, FreeDOS to its prompt | 0.1.0 | [`engine.md`](engine.md); 169, 177 A, 211 A, E11; internal to x86-Lib, not exposed by `Ix86` yet |
| `linux-base` by default (work item 6b): `x86 vm create` without profile takes `linux-base`; image downloaded and verified at the first start from the origin of the catalog of the known images (manifest address and expected hash, configuration, never hard-coded; progress in `x86 shell` and `x86 vm start`), then booted from the OPFS; one persistent write layer per VM, deleted with it; `x86 shell` without prefix opens the root serial console; the dev host gives the command engine, storage and local packs | 0.1.0 | [`vm.md`](vm.md#linux-base-by-default-work-item-6b), [`storage.md`](storage.md#catalog-of-the-known-images-work-item-6b); 169, 203 A, 212 A; first `x86 shell` in 16 s |
| Wine image (work item 9): Debian trixie i386 + pinned WineHQ 11.0, Mono 10.4.1 and Gecko 2.47.4 as shared installs, measured slimming, template prefix made in the image (links to Wine's files, per-prefix copy with `prefix-copy`), list of hot files taken from a trace and preloaded by the init, sources and licenses (Wine, Mono, Gecko), image test of 210 A, `wine` job of `images.yml` (artifact, never published), `guest` test (`wine cmd /c echo` and a console program through `exec-in-prefix`, with and without preloading) | 0.1.0 | [`images.md`](images.md); 152 A, 201 A, 204 A, 210 A; no prefixes nor `./hello.exe` in the terminal yet (work item 10) |

## In progress (milestone 0, console programs)

| Feature | Work item | Decisions |
| --- | --- | --- |
| Admin pages, role, personal journal | 7 | 137, 140 B, 156, 160 |
| "Feature" app kind, declared permissions, `GetServiceAsync<Ix86>()` | 8 | D33, D10, 176 A |
| Prefixes, header detection, `./hello.exe` console in the terminal | 10 | 162 A, 193, 200 B |
| DOS text program in the terminal | 11 | 111, 214 A, 215 A, 217 A |
| Private network, relay, permission window, rules page | 12 | 161, 174 A, 176 A |
| Minimal BoxedWine (separate repository) | 13 | 51 D, 62 B |

## Planned

Milestones 1 to 4 of the [plan](../conception/plan-2026-11-11.md), section 8: full BoxedWine and administration, graphical Linux and DOS, sound, inputs, graphical Windows (J3), end of V1.
