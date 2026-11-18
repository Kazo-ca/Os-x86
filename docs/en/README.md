> [Version française](../fr/README.md)

# Os-x86 documentation

| Document | Content |
| --- | --- |
| [`features.md`](features.md) | The features: available, in progress, planned |
| [`changelog.md`](changelog.md) | The changes per version |
| [`images.md`](images.md) | The guest images: recipes (linux-base, Alpine test, FreeDOS), packing format and manifest, image test, sources and licenses, publication, sizes |
| [`agent.md`](agent.md) | The guest agent and its C# client: protocol (JSON lines, events), life cycle of a launch (cgroup), multiplexed pty consoles, delays, measures, guarantees and limits |
| [`programs.md`](programs.md) | Prefixes, the detection of a program's type and its media types, `x86 detect`, `x86 prefix`, `x86 run`, the Wine session of a prefix, what `Ix86` adds; DOS programs in the terminal (FreeDOS VM, screen, keyboard and chords, graphical mode) |
| [`network.md`](network.md) | The private network: per-user switch, address plan, network namespace per prefix and nftables, gateway and relay (identity per flow), rules and permission window, limits, journal, `x86 rules`, what waits for Kazo ADRs 0255 and 0246 |
| [`vm.md`](vm.md) | VMs: per-user registry, lifecycle and grace, memory limit, agent watchdog, `.x86profile` profiles, the `x86 vm`, `x86 shell`, `x86 profile` commands and their exit codes, what `Ix86` exposes |
| [`engine.md`](engine.md) | The v86 engine in a Web Worker: `IEngine`, protocol, vendored v86 and patches, Alpine test image, tests, sources and licenses |
| [`../conception/`](../conception/) | The design: approved plan, decisions register, question series, milestone 0 order (French only) |
| [`../essais/`](../essais/) | The reports of the measurement prototypes (French only) |

Every PR that ships, changes or plans a feature updates `features.md` and `changelog.md` in both languages; a public API change updates `src/OsX86.Interface/PublicAPI.Unshipped.txt`.

## Kazo OS version

The `external/KazoOs` submodule is pinned on the Kazo OS tag **`v3.5.2`** (before: `v3.5.1` in work item 8b, `v3.5.0` in work item 8, `v3.4.0` since work item 1c, `v3.2.1` before it, decision 220 A; 3.5.0 brings ADR 0246, 0247, 0248, 0250 and 0283, which work item 8 needs; 3.5.1 fixes the launch of a Feature's children, Kazo-ca/Os#621, work item 8b; 3.5.2 starts at login the services of a Feature not loaded yet, Kazo-ca/Os#625, work item 8c). Its `wasm2/KazoOs/build/Versions.props` gives the version of every `Kazo.Os.*` package of the repository and the `sdkCompatibility.minimumVersion` of the `OsX86.Feature` manifest.

Follow rule (decision 266 A, 2026-11-15): the repository follows the Kazo OS prereleases that bring an ADR of the Os-x86 series (`vX.Y.Z-preview.N`), then the stable version as soon as it is published on nuget.org; 3.4.0 was released the same day as the decision, so the repository went straight from `v3.2.1` to `v3.4.0`. Changing the version means moving the submodule to another tag (`git -C external/KazoOs checkout v<version>`) in a PR of its own. CI (`.github/scripts/check-kazoos-submodule.sh`) refuses a modified submodule or one that is not exactly on the `v<KazoOsVersion>` tag; a prerelease tag is accepted as is, since the version read from `Versions.props` carries its suffix.

A `Debug` build rewrites generated manifests inside the submodule: before pushing, `git -C external/KazoOs checkout -- .` puts them back to the tag's state.
