> [Version française](../fr/features.md)

# Os-x86 features

Kept up to date by every PR (plan, section 12). Numbers in parentheses refer to the decisions of the [register](../conception/registre-reconcilie.md) (French); work items, to [`jalon-0-execution.md`](../conception/jalon-0-execution.md) (French).

## Available

| Feature | Since | Note |
| --- | --- | --- |
| `x86` command: `--version`, `vm list` | 0.1.0 | Packaged as a terminal command until Kazo OS ships the "Feature" app kind (ADR 0243) |
| Contracts package `Os-x86.Interface`: `Ix86` (version, VM list) | 0.1.0 | Generic, no Wine type (29 C); public API tracked in `PublicAPI.Unshipped.txt` |
| Guest images: packing tool (`pack-image.py`, framed zstd, 32 MiB chunks, manifest format 1), recipes linux-base (Debian trixie i386, Alpine `linux-virt` kernel, init, 210 A hardening), Alpine test and FreeDOS, image test (22 R134.5 checks), sources and licenses, `images.yml` workflow (Pages and R2 prepared) | 0.1.0 | Work item 4, [`images.md`](images.md); 201 A, 202 A, 210 A; root on an ext4 disk (`hda`), 9p kept for the shared folder; linux-base boots with work item 6 |
| v86 engine in a Web Worker: `IEngine` (start, stop, pause, states), serial and virtio-console (raw and line streams), OffscreenCanvas bridge, vendored v86 0.5.473 with patch #1661, Alpine test image, `unit` and `guest` tests | 0.1.0 | Work item 2, [`engine.md`](engine.md); D3, 187 A; internal to x86-Lib, not exposed by `Ix86` yet |
| Storage: image manifest (format 1, SHA-256 per 32 MiB chunk, expected hash), download and verification Web Worker (`SubtleCrypto`, OPFS writes through a `SyncAccessHandle`, resume after a cut or a reload, retries, cancellation, progress), image repository in the OPFS (list, delete, footprint, `estimate()`), write lock through `navigator.locks`, periodic `flush()`, versioned-format metadata, 800 MiB `guest` test with faults | 0.1.0 | Work item 3, [`storage.md`](storage.md); 169, 177 A, 188, 203 A, E11; internal to x86-Lib, not exposed by `Ix86` yet |
| Guest agent (109 KiB static C, JSON lines on `hvc0`, one cgroup v2 per launch, `kill` by launch identifier, `mounts`, `exec-in-prefix`, `open-url`, pty consoles multiplexed on `hvc1` with Ctrl+C and exit code, 208 A closing) and its C# client (`AgentClient`, `LaunchId`, `ConsoleHandle`, typed errors); installed in linux-base and in the Alpine test image; `unit` and `guest` tests | 0.1.0 | Work item 5, [`agent.md`](agent.md); 205 A, 206 A, 208 A, 213 A, 216 A; internal to x86-Lib, not exposed by `Ix86` yet; `list-windows` waits for an X server (work item 9) |

## In progress (milestone 0, console programs)

| Feature | Work item | Decisions |
| --- | --- | --- |
| VM registry, lifecycle, `x86 vm`, `x86 shell`, profiles | 6 | 165, 166, 183, 190, 212 A |
| Admin pages, role, personal journal | 7 | 137, 140 B, 156, 160 |
| "Feature" app kind, declared permissions, `GetServiceAsync<Ix86>()` | 8 | D33, D10, 176 A |
| Wine image and template prefix | 9 | 152 A, 187 A, 204 A |
| Prefixes, header detection, `./hello.exe` console in the terminal | 10 | 162 A, 193, 200 B |
| DOS text program in the terminal | 11 | 111, 214 A, 215 A, 217 A |
| Private network, relay, permission window, rules page | 12 | 161, 174 A, 176 A |
| Minimal BoxedWine (separate repository) | 13 | 51 D, 62 B |

## Planned

Milestones 1 to 4 of the [plan](../conception/plan-2026-11-11.md), section 8: full BoxedWine and administration, graphical Linux and DOS, sound, inputs, graphical Windows (J3), end of V1.
