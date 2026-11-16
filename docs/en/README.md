> [Version française](../fr/README.md)

# Os-x86 documentation

| Document | Content |
| --- | --- |
| [`features.md`](features.md) | The features: available, in progress, planned |
| [`changelog.md`](changelog.md) | The changes per version |
| [`images.md`](images.md) | The guest images: recipes (linux-base, Alpine test, FreeDOS), packing format and manifest, image test, sources and licenses, publication, sizes |
| [`agent.md`](agent.md) | The guest agent and its C# client: protocol (JSON lines, events), life cycle of a launch (cgroup), multiplexed pty consoles, delays, measures, guarantees and limits |
| [`programs.md`](programs.md) | Prefixes, the detection of a program's type and its media types, `x86 detect`, `x86 prefix`, `x86 run`, the Wine session of a prefix, what `Ix86` adds |
| [`vm.md`](vm.md) | VMs: per-user registry, lifecycle and grace, memory limit, agent watchdog, `.x86profile` profiles, the `x86 vm`, `x86 shell`, `x86 profile` commands and their exit codes, what `Ix86` exposes |
| [`engine.md`](engine.md) | The v86 engine in a Web Worker: `IEngine`, protocol, vendored v86 and patches, Alpine test image, tests, sources and licenses |
| [`../conception/`](../conception/) | The design: approved plan, decisions register, question series, milestone 0 order (French only) |
| [`../essais/`](../essais/) | The reports of the measurement prototypes (French only) |

Every PR that ships, changes or plans a feature updates `features.md` and `changelog.md` in both languages; a public API change updates `src/OsX86.Interface/PublicAPI.Unshipped.txt`.

## Kazo OS version

The `external/KazoOs` submodule is pinned on the Kazo OS tag **`v3.4.0`** (before: `v3.2.1`, decision 220 A). Its `wasm2/KazoOs/build/Versions.props` gives the version of every `Kazo.Os.*` package of the repository and the `sdkCompatibility.minimumVersion` of the `OsX86.Feature` manifest.

Follow rule (decision 266 A, 2026-11-15): the repository follows the Kazo OS prereleases that bring an ADR of the Os-x86 series (`vX.Y.Z-preview.N`), then the stable version as soon as it is published on nuget.org; 3.4.0 was released the same day as the decision, so the repository went straight from `v3.2.1` to `v3.4.0`. Changing the version means moving the submodule to another tag (`git -C external/KazoOs checkout v<version>`) in a PR of its own. CI (`.github/scripts/check-kazoos-submodule.sh`) refuses a modified submodule or one that is not exactly on the `v<KazoOsVersion>` tag; a prerelease tag is accepted as is, since the version read from `Versions.props` carries its suffix.

A `Debug` build rewrites generated manifests inside the submodule: before pushing, `git -C external/KazoOs checkout -- .` puts them back to the tag's state.
