> [Version française](readme-fr.md)

# Os-x86

Os-x86 is the x86 emulation feature of [Kazo OS](https://github.com/Kazo-ca/Os): it runs x86 virtual machines (Linux, FreeDOS, and Windows programs through Wine) inside the browser, with [v86](https://github.com/copy/v86), and lets Kazo OS apps launch programs in them through the `Os-x86.Interface` contracts package. The user-facing Windows app, [BoxedWine](https://github.com/Kazo-ca/Os-App-BoxedWine), is a separate repository that depends on this one.

The design was debated and recorded before any code: [`docs/conception/`](docs/conception/) (French; the approved plan is [`plan-2026-11-11.md`](docs/conception/plan-2026-11-11.md), the decisions register [`registre-reconcilie.md`](docs/conception/registre-reconcilie.md)). Throwaway measurement prototypes live in [`essais/`](essais/) with their reports in [`docs/essais/`](docs/essais/).

## Layout

| Path | Content |
| --- | --- |
| `src/OsX86.Interface/` | the contracts package `Os-x86.Interface` (`Ix86` and its types), embedded by client apps |
| `src/OsX86/` | x86-Lib: the implementation (engine, storage, network, share, agent, identity, VMs) |
| `src/OsX86.Feature/` | the Kazo OS package: the `x86` terminal command, the permission window, the rules page, the admin pages |
| `src/OsX86.Os/` | a Kazo OS host with the feature, for development and demos |
| `tests/` | xUnit tests (`unit`; `guest` boots the test image in a headless browser) |
| `tools/v86/` | the script and patches that vendor v86 into `src/OsX86/wwwroot/v86/` |
| `images/` | recipes of the guest images (linux-base, Alpine test, FreeDOS) and their packing and test tools; built, never committed |
| `docs/fr/`, `docs/en/` | product documentation (features, changelog), French first |
| `external/KazoOs` | Kazo OS as a submodule, pinned on a release tag |

## Build

Prerequisites: the .NET 10 SDK (`global.json`) and the submodule (`git submodule update --init`).

```bash
dotnet build OsX86.sln -c Release      # Kazo OS packages from nuget.org, at the submodule version
dotnet test OsX86.sln -c Release
dotnet run --project src/OsX86.Os      # Debug: Kazo OS from the submodule sources
```

The `Debug` profile compiles Kazo OS from the submodule (first build is long); `Release` uses the published packages, which is what CI builds. See the [`app-repository.md`](https://github.com/Kazo-ca/Os/blob/main/doc/en/wasm/framework/app-repository.md) guide of Kazo OS.

## Status

Milestone 0 (console programs) is in progress; see [`docs/en/features.md`](docs/en/features.md) and [`docs/conception/jalon-0-execution.md`](docs/conception/jalon-0-execution.md) (French).

## License

MIT. v86 is BSD-2-Clause; guest images carry their own licenses and sources, published next to them.
