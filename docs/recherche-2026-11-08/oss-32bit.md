# oss-32bit

## Summary
Survey of open-source x86 PC emulators that run in a browser, as of 2026-11-08. Every repo was checked with gh api.

**v86** (BSD-2-Clause, last commit 2026-10-30) is the clear base for full-system 32-bit emulation.
- **CPU and speed:** a Pentium-4-class CPU with full SSE3 and an x87 FPU (SoftFloat). It compiles x86 code to WebAssembly at runtime (JIT).
- **Guests:** 32-bit Linux, ReactOS, FreeBSD and Windows 1.x/3.x/95/98/ME/NT/2000 work. XP/Vista/8 boot only under some conditions. No 64-bit and no multicore; the x86_64 issue #133 has been open since 2017.
- **Integration API:** a 9p shared filesystem with a `handle9p` callback, so Kazo OS could serve its own file system to the guest; `create_file`/`read_file`; NE2000 or virtio network cards with inbrowser/wsproxy/wisp/fetch backends; `save_state`/`restore_state`; serial and virtio console; TypeScript definitions; an official npm package.
- **Size and hosting:** about 2.0 MB of wasm plus 360 KB of JS. No SharedArrayBuffer use was found, so the host page likely needs no COOP/COEP headers.
- **x86-64 forks:** several v86 forks appeared in Sept–Oct 2026. The furthest along, portlandhodl/v86_64 (BSD-2), claims 64-bit Linux desktops booting, SSE4.2/POPCNT and a 64-bit JIT. It is 1–2 months old, has one maintainer and 1 star, and shows no Windows x64 results. These claims are unverified.

**Boxedwine** (GPL-2.0, very active: commit 2026-11-09, release 26R1 on 2026-04-22) is the only mature way to run Win32 .exe files directly, with no Windows licence needed.
- It runs Wine 3.1 to 11.0 on an emulated Linux kernel and CPU (x87/MMX/SSE/SSE2).
- **Limits:** 16/32-bit Windows programs only; newer .NET does not work; games after 2010 have limited success.
- **Web build:** the released web build uses only the slow interpreter. The master branch now has experimental wasm-JIT and multithreaded browser targets (multithreaded ones need COOP/COEP) and a WebSocket network gateway (Oct 2026). Applications ship as zip files.
- Small 2026 wrappers already reuse it (exebrowser, windowsappplayer).

**x64 track (Windows 7/10/11):**
- **Bochs** (LGPL-2.1; 3.1 released 2026-08-23) has full x86-64 up to AVX-512, UEFI/OVMF support, and Windows 11 support since 2.8. It is only an interpreter, and upstream has no web build. container2wasm compiles it to wasm with `--enable-x86-64 --enable-avx`, which shows it is feasible.
- **QEMU:** upstream QEMU can be built with Emscripten (wasm64, interpreter only). The wasm JIT backend lives only in the ktock/qemu-wasm fork (GPL-2.0, last commit 2025-09). That fork runs x86_64 Alpine, but it is about 46 MB and needs threads and cross-origin isolation.
- No open-source project shows Windows 10 or 11 running at usable speed in a browser. Windows 11 24H2 also needs SSE4.2/POPCNT, and wasm32 caps guest memory at 4 GiB.

**Excluded or limited:**
- **CheerpX/WebVM:** proprietary, 32-bit Linux user programs only (no Windows), and needs SharedArrayBuffer. Its licence allows businesses only technical evaluation; self-hosting needs a commercial licence.
- **JSLinux:** the x86/x86_64 emulator (64-bit added 2026-01, AVX-512/APX 2026-03) has no published source. TinyEMU (MIT) only includes a KVM-based x86.
- **halfix:** GPL-3.0, inactive since 2021, 10–30 MIPS in the browser. It does boot 32-bit Windows 7/10.
- **js-dos / DOSBox-X:** GPL-2.0; good for DOS and Windows 9x; no NT-family Windows.
- **PCjs:** MIT, pure JavaScript, 8088 to 386, Windows 1–3.1, 95 and OS/2 1.x.
- **86Box:** no web port found.
- **em-dosbox:** inactive since 2023.
- **retrowin32:** abandoned in 2026-05 in favour of Theseus.
- **Theseus:** Apache-2.0; translates each .exe ahead of time; needs manual work per program.
- **C# emulators** (Spice86 real mode, Aeon 486/DOS, Enzan): none can run Windows or Linux, and none target Blazor.

**Licences:**
- **Bundling inside the CC BY-NC 4.0 OS:** only permissive code is safe: BSD-2 (v86), MIT, Apache-2.0. LGPL-2.1 (Bochs, Wine) is acceptable if it stays a separately loaded, replaceable module with its source provided. GPL code (Boxedwine, QEMU, DOSBox, js-dos, halfix) cannot be combined with an NC-restricted program.
- **As a separate download from the MIT repo:** the repo can host GPL components, but any add-on that includes GPL code must be distributed as a whole under the GPL. It should talk to Kazo OS at arm's length, through messages, sockets or a 9p-style protocol, not shared in-process data structures.
- **Version clashes:** GPL-2.0-only code (QEMU) cannot be mixed with GPL-3.0 (halfix) or with Apache-2.0.

**Top 3:**
1. **v86** for Windows 9x/2000/XP and 32-bit Linux. It has the best licence, the JIT and the ready-made file-sharing, network and state hooks.
2. **Boxedwine** as a GPL Marketplace add-on for running .exe files directly.
3. **Bochs-wasm** (LGPL, best compatibility) or **qemu-wasm** (GPL, JIT, faster) as a research path for x64 and Windows 7–11, with the v86_64 forks worth watching.

## Facts
1. [high] v86 is BSD-2-Clause, written in JavaScript+Rust, ~23.7k stars, last commit 2026-10-30 (315b2b0b), rolling 'latest' release 2026-10-30; npm package v86 0.5.473 (BSD-2-Clause) — gh api repos/copy/v86 and repos/copy/v86/commits; npm view v86
2. [high] v86 translates machine code to WebAssembly modules at runtime (JIT). The CPU is around Pentium 4 level with full SSE3. It lacks task gates, some 16-bit protected-mode features, multicore and 64-bit extensions. The x87 FPU uses Berkeley SoftFloat. — https://github.com/copy/v86/blob/master/Readme.md (Emulated hardware section)
3. [high] v86 compatibility: 32-bit Linux works well (no 64-bit kernels). ReactOS, FreeBSD, Haiku, KolibriOS and 9front work. Windows 1, 3.x, 95, 98, ME, NT and 2000 work reasonably well. XP, Vista and 8 work only under certain conditions (Win2000+ needs the 'Standard PC' HAL; Vista+ needs ACPI and at least 512 MB) — https://github.com/copy/v86/blob/master/Readme.md (Compatibility); https://github.com/copy/v86/blob/master/docs/windows-nt.md sections 4-5
4. [high] v86 embedding API: 9p virtio filesystem with baseurl/basefs, a handle9p(reqbuf, reply) callback to back it with a custom 9p server, or a websocket proxy_url; create_file/read_file; save_state/restore_state; serial0_send; virtio_console; net_device — https://github.com/copy/v86/blob/master/v86.d.ts lines ~470-498, 684-689, 791, 842-850
5. [high] v86 networking: NE2000 or virtio NIC, with inbrowser, wsproxy (ws/wss), wisp and fetch (CORS proxy) backends selected by relay_url — https://github.com/copy/v86/blob/master/docs/networking.md
6. [high] v86 release asset sizes: v86.wasm 2,007,347 bytes; libv86.js 359,857 bytes; libv86.mjs 359,819 bytes — gh api repos/copy/v86/releases/tags/latest
7. [medium] No SharedArrayBuffer or crossOriginIsolated usage was found in the v86 source, so it likely does not need COOP/COEP headers — gh search code --repo copy/v86 SharedArrayBuffer (0 results)
8. [high] The v86 x86_64 support issue #133 has been open since 2017-02-26. The maintainer (copy) called it 'a huge project'. Upstream has no long-mode commits. — https://github.com/copy/v86/issues/133; gh search commits --repo copy/v86 'long mode'
9. [high] v86 does not accept pull requests or issues written partially or entirely by generative AI tools, except case by case — https://github.com/copy/v86/blob/master/Readme.md (Generative AI section)
10. [low] portlandhodl/v86_64 (BSD-2-Clause, created 2026-10-02, last commit 2026-10-15, 1 star) claims: long mode, SSE through SSE4.2, POPCNT, a 64-bit JIT (~530 MIPS vs ~80 interpreted), Debian Xfce and Ubuntu 26.04 GNOME desktops booting. Limits: one CPU, 4 GiB guest RAM (wasm32), no AVX, no 3D. It lists no 64-bit Windows guest. — https://github.com/portlandhodl/v86_64 README; gh api repos/portlandhodl/v86_64
11. [high] Other young v86 x86-64 forks exist: debugdoctor/v64 (created 2026-09-21), TheEvalon/v86_x64 (2026-09-08), pmalacho-mit/v86_64 (2026-11-05). All are BSD-2-Clause with 0-1 stars. — gh api repos/debugdoctor/v64, repos/TheEvalon/v86_x64, repos/pmalacho-mit/v86_64
12. [high] Wanix (MIT, last push 2026-10-26) is an embeddable browser runtime that boots x86 Linux in v86 and exposes OPFS, the DOM and 9P namespaces. It is a reference design for a browser OS hosting v86. — https://github.com/tractordev/wanix README
13. [high] Halfix is GPL-3.0, written in C99, last commit 2021-01-28 (inactive). CPU: x86-32 with FPU, MMX, SSE, SSE2, some SSE3 and PAE; RAM up to 3584 MB. It boots DOS, most Windows except 8 (Vista, 7 and 10 32-bit screenshots), OS/2 Warp, ReactOS and Linux. Browser speed is 10-30 MIPS. Files are transferred via ISO images. It has savestates and no JIT. — https://github.com/nepx/halfix README (System Specifications, Known Issues, License); docs/savestates.md; gh api repos/nepx/halfix/commits
14. [high] Bochs is LGPL-2.1, C++, last commit 2026-11-08. Version 3.1 (2026-08-23) added UEFI/OVMF via fw_cfg (boots Windows Server 2025) and 8192 MB max host memory. Version 2.8 (2024-03-10) added VMX MBE emulation 'required for Windows 11 guest'. — gh api repos/bochs-emu/Bochs/releases/tags/REL_3_1_FINAL and REL_2_8_FINAL; bochs/COPYING
15. [high] Upstream Bochs has no Emscripten/wasm target (the only code-search hit is config.sub). container2wasm builds a Bochs fork (ktock/Bochs, LGPL-2.1, last push 2024-08-27) for wasm32 WASI and Emscripten with --enable-x86-64 --enable-avx --with-nogui and asyncify. It is interpreter-only. — gh search code --repo bochs-emu/Bochs emscripten; https://github.com/container2wasm/container2wasm/blob/main/Dockerfile lines ~34-35, 1015-1062
16. [high] container2wasm (Apache-2.0, last commit 2026-11-04) converts containers to wasm, using Bochs for x86_64, TinyEMU for riscv64, and QEMU-wasm. Browser networking goes through c2w-net-proxy (Fetch API) or a WebSocket delegate. — https://github.com/container2wasm/container2wasm README
17. [high] Upstream QEMU merged Emscripten build support on 2025-05-06 and wasm64 --cpu support on 2026-01-16. Upstream has no tcg/wasm backend directory, so only the TCI interpreter runs there. The Wasm TCG JIT backend (patch v4, Jan 2026) lives in the ktock/qemu-wasm fork (QEMU license, GPL-2.0 as a whole, last commit 2025-09-04). — gh api repos/qemu/qemu/contents/tcg; gh search commits --repo qemu/qemu emscripten/wasm64; https://patchew.org/QEMU/cover.1747744132.git.ktokunaga.mail@gmail.com; gh api repos/ktock/qemu-wasm
18. [medium] qemu-wasm supports x86_64, AArch64 and RISC-V guests; an x86_64 Alpine demo exists — https://ktock.github.io/qemu-wasm-demo/alpine-x86_64.html; FOSDEM 2025 slides https://fosdem.org/2025/events/attachments/fosdem-2025-6290-running-qemu-inside-browser/slides/237638/slides_1dDtpcS.pdf
19. [medium] A July 2026 write-up says qemu-wasm is about 46 MB and needs threads, SharedArrayBuffer and cross-origin isolation. It also says v86 could not run its 64-bit kernel. — https://msuiche.com/posts/nanokrnl-cold-boot-fast-boot/ (2026-07-03)
20. [high] Boxedwine is GPL-2.0, C++, last commit 2026-11-09, release 26R1.0 on 2026-04-22. It runs 16/32-bit Windows programs via 32-bit Wine (versions 3.1 to 11.0) on an emulated Linux kernel and x86 CPU. Newer .NET does not work; games after 2010 have limited success. — https://github.com/danoon2/Boxedwine README; gh api repos/danoon2/Boxedwine/releases
21. [high] Boxedwine CPU emulation supports x87, MMX, SSE and SSE2. The released WASM build uses only the 'normal' interpreter CPU; the native JIT exists only for x86, x64 and ARMv8 hosts. — https://github.com/danoon2/Boxedwine/blob/master/docs/CPUemulation.md
22. [high] Boxedwine master has four Emscripten targets (release, jit, multiThreaded, multiThreadedJit) with 512 MiB to 3 GiB wasm memory. Multithreaded builds need COOP/COEP for SharedArrayBuffer. All targets need wasm exnref (Chrome/Edge 137+, Firefox 131+, Safari 18.4+). WebSocket networking files (boxedwine-net.js, network-gateway.mjs) were added from 2026-10-07. — https://github.com/danoon2/Boxedwine/blob/master/project/emscripten/buildInstructions.md; gh api commits?path=project/emscripten/boxedwine-net.js
23. [high] Boxedwine web apps are packaged as zips and launched by URL parameters (e.g. boxedwine.html?app=ski32&p=ski32.exe). The 26R1 Web release zip is 122,244,710 bytes. — https://github.com/danoon2/Boxedwine/blob/master/docs/How-To-Create-FileSystem-For-Web.md; gh api releases/tags/26R1.0
24. [medium] Community 'EXE in browser' wrappers built on Boxedwine and Wine: andrewnakas/exebrowser (GPL-2.0, created 2026-05-22) and bassdrum4/windowsappplayer (GPL-2.0, 2026-09-24). Separate experiments: codebutler/wine-assembly (MIT, Win98-era PE interpreter in WAT) and evangit2/winebrowser (MIT, 0 stars). — gh api repos for each; https://exebrowser.com/
25. [high] CheerpX is proprietary (npm @leaningtech/cheerpx 1.4.0, 2026-10-07). It JIT-compiles x86 to wasm and emulates Linux syscalls. Only 32-bit x86 binaries are supported; 3D graphics are not. — npm view @leaningtech/cheerpx readme/version; https://github.com/leaningtech/cheerpx-meta/blob/main/ChangeLog
26. [high] CheerpX licence: individuals may use it for any purpose. Businesses may use it 'strictly for the purpose of technical evaluation and testing', not in production or public apps. The website says self-hosting, redistribution and OEM use need a commercial licence, the free tier is loaded from cxrtnc.leaningtech.com, and credit is required. — https://github.com/leaningtech/cheerpx-meta/blob/main/LICENSE.txt clause 1.4; https://cheerpx.io/docs/licensing
27. [high] CheerpX requires SharedArrayBuffer and therefore cross-origin isolation (COEP require-corp plus COOP same-origin). It offers IDBDevice, OverlayDevice, WebDevice and DataDevice filesystems. WebVM networking uses Tailscale. — https://cheerpx.io/docs/getting-started; https://github.com/leaningtech/webvm README
28. [high] WebVM (the CheerpX front end) is Apache-2.0, last commit 2026-11-02 — gh api repos/leaningtech/webvm
29. [high] The JSLinux x86 browser emulator gained 64-bit x86 on 2026-01-12 and AVX2/AVX-512/APX on 2026-03-09, but no source or licence is published. TinyEMU (MIT, last release 2019-12-21) includes only a KVM-based x86 emulator. — https://bellard.org/jslinux/news.html; https://bellard.org/jslinux/tech.html; https://bellard.org/tinyemu/
30. [high] js-dos 8.5.3 (2026-10-07) is GPL-2.0 (package.json). It is a front end for DOSBox and DOSBox-X wasm cores (caiiiycuk/emulators, GPL-2.0), with Win 9x via DOSBox-X, OPFS persistence, WebRTC IPX networking and worker mode. — https://github.com/caiiiycuk/js-dos README and package.json; gh api repos/caiiiycuk/emulators
31. [high] DOSBox-X (GPL-2.0, last commit 2026-11-08, release 2026.10.01) emulates the 8086 through the Pentium III with MMX. It focuses on Windows 1.0 to ME, then NT to XP; Vista and later are out of scope. An Emscripten port exists (yksoft1). — https://github.com/joncampbell123/dosbox-x README lines ~66, 220-247, 361-366
32. [high] em-dosbox (GPL-2.0) last commit was 2023-01-06. DOSBox Pure moved to Codeberg in Oct 2026 (GPL-2.0-or-later). DOSBox Staging (GPL-2.0, v0.83.0 on 2026-08-27) has no official wasm build. — gh api repos/dreamlayers/em-dosbox, schellingb/dosbox-pure README, dosbox-staging/dosbox-staging
33. [high] PCjs is MIT, pure JavaScript, last commit 2026-11-08. It emulates IBM PC/XT/AT and COMPAQ DeskPro 386 machines running Windows 1.0-3.11, Windows 95 and OS/2 1.x. — https://github.com/jeffpar/pcjs/blob/master/LICENSE.txt; https://www.pcjs.org/machines/pcx86/
34. [medium] 86Box (GPL-2.0, v6.0 on 2026-05-31) has no known WebAssembly or browser port — gh api repos/86Box/86Box; web search found none
35. [high] retrowin32 (Apache-2.0) was abandoned on 2026-05-11 in favour of Theseus. Theseus (Apache-2.0, last push 2026-11-06) statically translates DOS/win32 x86 .exe files to Rust and can target wasm; the author calls it very experimental, needing per-program work. — https://github.com/evmar/retrowin32 README; https://github.com/evmar/theseus README; https://neugierig.org/software/blog/2026/04/theseus.html
36. [medium] C#/.NET x86 emulators: Spice86 (Apache-2.0, real-mode DOS only), Aeon (Apache-2.0, 486DX-class DOS PC, Windows/.NET 6) and Enzan (MIT, 10 stars). No Blazor-hosted x86 PC emulator was found. — gh api and READMEs of OpenRakis/Spice86, gregdivis/Aeon, ycanardeau/Enzan; gh search repos
37. [high] Kazo-ca/Os is licensed CC BY-NC 4.0 (LICENSE.md). Kazo-ca/Os-x86 is MIT. — gh api repos/Kazo-ca/Os/contents/LICENSE.md; gh api repos/Kazo-ca/Os-x86
38. [high] Creative Commons recommends against using CC licences for software and says they are 'currently not compatible with the major software licenses' — https://creativecommons.org/faq/#can-i-apply-a-creative-commons-license-to-software
39. [high] GPL FAQ: the GPL is designed to prevent further restrictions such as commercial-use bans (#NoMilitary). Modules in the same executable are combined, while separate programs may be aggregated (#MereAggregation). Plug-ins that share data structures form a single combined program (#GPLPlugins). Code under a GPL-incompatible licence cannot be combined with GPL code. — https://www.gnu.org/licenses/gpl-faq.en.html#NoMilitary, #MereAggregation, #GPLPlugins, #GPLIncompatibleAlone
40. [high] Apache-2.0 is compatible with GPLv3 but not GPLv2 — https://www.apache.org/licenses/GPL-compatibility.html
41. [medium] Windows 11 24H2 requires a CPU with SSE4.2/POPCNT to boot — https://www.tomshardware.com/software/windows/microsoft-updates-windows-11-24h2-requirements-cpu-must-support-sse42-or-the-os-will-not-boot (2024)
42. [medium] WebAssembly memory64 shipped in Chrome 133 and Firefox 134. Safari support was not confirmed as of 2026. — https://spidermonkey.dev/blog/2025/01/15/is-memory64-actually-worth-using.html; https://bugs.webkit.org/show_bug.cgi?id=300538

## Implications
- Use v86 (BSD-2-Clause) as the base for a 32-bit full-system 'Windows/Linux subsystem'. It can be bundled in the CC BY-NC OS or shipped from the MIT Os-x86 repo with only an attribution notice.
- Kazo OS file sharing maps naturally onto v86's handle9p callback. Kazo OS's virtual file system can answer 9p requests in C# through JS interop. Linux guests mount 9p natively; Windows guests would need a 9p driver or ISO/disk-image injection instead.
- Kazo OS networking can plug into v86's net_device backends (fetch, wisp, wsproxy). A Kazo-provided Wisp/WebSocket relay could be the 'OS network service'. Raw sockets are impossible from a browser, so some proxy is always needed for real TCP.
- Don't port or write the emulator in C# for Blazor. A .NET-on-wasm interpreter cannot generate wasm code at runtime the way v86 does, so Windows XP or later would be far too slow. Recommended split: the Blazor shell hosts a JS/wasm emulator in a Web Worker or iframe and controls it via JSImport/JSExport messages. (MsDos.Clone-style C# emulation suits 8086/DOS only.)
- Ship the emulator as an optional Marketplace add-on and load it lazily: about 2.4 MB of engine, plus guest disk images of hundreds of MB to GB, streamed with HTTP range requests and cached in OPFS/IndexedDB. Never put it in the kernel's default download.
- For running .exe files without a Windows install, a second, GPL-2.0 add-on wrapping Boxedwine is the realistic path. It must be distributed under the GPL and talk to Kazo OS only at arm's length (postMessage protocol, 9p, sockets), not by sharing in-process data structures.
- x64 (Windows 7/10/11) has no production-ready open-source browser emulator as of Nov 2026. A Bochs-wasm (LGPL) or qemu-wasm (GPL) prototype is research-grade and very slow. The young v86_64 forks may change this but are unproven, and none show 64-bit Windows.
- Several engines (CheerpX, qemu-wasm, multithreaded Boxedwine) need SharedArrayBuffer and therefore COOP/COEP on the whole Kazo OS page. That forces every cross-origin asset Kazo OS loads to carry CORP/CORS headers. v86 avoids this.
- An emulator abstraction in Os-x86 (an IVirtualMachine interface: disks, 9p share, network, display, state) would let several backends (v86 now, a 64-bit backend later, Boxedwine for .exe files) plug into the same Kazo OS app model.
- Save/restore state (v86 save_state) can give the user a 'resume' or fast boot instead of a full Windows cold boot; the 2026 nanokrnl write-up does exactly this to skip cold boot.

## Risks
- GPL code (Boxedwine, QEMU, DOSBox/js-dos, halfix, Unicorn) cannot be compiled into or tightly linked with the CC BY-NC 4.0 Kazo OS. Doing so would violate the GPL's no-further-restrictions rule. The MIT Os-x86 repo cannot relicense such code; any add-on that includes it becomes GPL.
- GPL-2.0-only QEMU cannot be combined with GPL-3.0 halfix or with Apache-2.0 components (Apache-2.0 is incompatible with GPLv2)
- CheerpX's licence allows business use only for technical evaluation, and self-hosting needs a commercial licence. CC BY-NC is not an OSI/FSF open-source licence, so Kazo OS likely does not qualify for the free open-source tier; this needs Leaning Technologies' confirmation.
- Windows guest licensing: Windows 7/10/11 (and XP) installation media and keys cannot be redistributed. Users must bring their own ISO and licence. Windows 7 and Windows 10 are past end of support.
- Windows 11 needs x86-64, SSE4.2/POPCNT, TPM 2.0/Secure Boot and 4 GB RAM. wasm32 caps guest RAM near 4 GiB, and memory64 is not confirmed in Safari. Interpreted x64 emulation would make Win10/11 boot take tens of minutes or more (speed not measured; estimate).
- v86 does not accept AI-written upstream contributions, so Copilot-generated patches would have to stay in a fork
- Boxedwine's web JIT and networking are only on master (2026), not in a release, so their stability is unknown. The web build without JIT is slow.
- The v86_64 forks are 1-2 months old with single maintainers. Their claims are unverified and they could be abandoned.
- Browser limits: no raw sockets (network proxies needed, adding hosting cost and abuse risk for a public Wisp/WebSocket relay), large image downloads, and browser storage quotas for multi-GB virtual disks.

## Open questions
- Is Kazo-ca a business or organisation, or an individual? This decides whether CheerpX's free tier is even possible and how the CC BY-NC 'non-commercial' scope applies.
- What does 'run .exe' mean? (a) Run individual Win32 programs directly (Wine/Boxedwine style, no Windows licence), (b) a full Windows install the user brings (v86/Bochs style), or (c) both?
- Is a GPL-licensed Marketplace add-on acceptable (needed for Boxedwine or QEMU), or must every component stay permissive (MIT/BSD/Apache/LGPL)?
- How to prioritise x64 versus usability now: deliver 32-bit (Win 9x/2000/XP plus 32-bit Linux) first with v86, and treat Windows 7/10/11 x64 as a separate research track?
- Should Os-x86 wrap an existing engine (v86 via JS interop), or be a from-scratch emulator in C# like MsDos.Clone (which would cap realistic targets at DOS/early Windows)?
- Can the Kazo OS host page be served with COOP/COEP (cross-origin isolation)? Some engines need it, and it constrains every third-party asset Kazo OS loads.
- Which browsers must be supported (Safari/iOS has no confirmed memory64, and Boxedwine needs wasm exnref)?
- What download and storage budget is acceptable for guest images (tens of MB for minimal Linux; GB for Windows)?
- Will Kazo provide network relay infrastructure (a Wisp/WebSocket proxy) for guest internet access, or is networking limited to in-browser/peer-to-peer?
