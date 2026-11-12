> [Version française](../fr/README.md)

# Os-x86 documentation

| Document | Content |
| --- | --- |
| [`features.md`](features.md) | The features: available, in progress, planned |
| [`changelog.md`](changelog.md) | The changes per version |
| [`images.md`](images.md) | The guest images: recipes (linux-base, Alpine test, FreeDOS), packing format and manifest, image test, sources and licenses, publication, sizes |
| [`agent.md`](agent.md) | The guest agent and its C# client: protocol (JSON lines, events), life cycle of a launch (cgroup), multiplexed pty consoles, delays, measures, guarantees and limits |
| [`engine.md`](engine.md) | The v86 engine in a Web Worker: `IEngine`, protocol, vendored v86 and patches, Alpine test image, tests, sources and licenses |
| [`../conception/`](../conception/) | The design: approved plan, decisions register, question series, milestone 0 order (French only) |
| [`../essais/`](../essais/) | The reports of the measurement prototypes (French only) |

Every PR that ships, changes or plans a feature updates `features.md` and `changelog.md` in both languages; a public API change updates `src/OsX86.Interface/PublicAPI.Unshipped.txt`.
