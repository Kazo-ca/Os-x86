> [Version française](../fr/changelog.md)

# Os-x86 changelog

## Unreleased (0.1.0, milestone 0)

- Repository scaffold on the `kazoos-solution` template of Kazo OS: Kazo OS submodule pinned on `v3.2.1`, projects `OsX86.Interface`, `OsX86`, `OsX86.Feature`, host `OsX86.Os`, tests, CI.
- `x86` command (`--version`, `vm list`) and `Ix86` contract (version, VM list).
- Engine (work item 2): v86 0.5.473 vendored in `src/OsX86/wwwroot/v86` (BSD-2, SeaBIOS LGPL-3, script `tools/v86/vendor-v86.py`, patch #1661 ported), Web Worker `v86-worker.js` and host module `v86-host.js`, `IEngine` abstraction with `V86Engine` (states stopped, starting, running, paused, stopping; coded errors), serial and virtio-console ports read raw or by lines and written, OffscreenCanvas bridge, Alpine test image (`images/alpine-test`, verified official ISO, never committed) read from the OPFS, `unit` tests (80 % coverage required by the CI) and `guest` test (Playwright, `guest-tests.yml`). See [`engine.md`](engine.md).
