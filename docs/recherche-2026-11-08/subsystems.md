# subsystems

## Summary
The two approaches complement each other; they are not competitors. As of 2026-11-08 nobody has shown a usable x64 Windows 7/10/11 running in a browser.

A) FULL-SYSTEM (WSL2-like). This is a mature option for 32-bit guests only.
- v86 (BSD-2): 32-bit CPU, roughly Pentium 4 with SSE3, with a JIT that translates to wasm. Win9x/2000 run well; XP, Vista and 8 only under conditions. It has virtio-9p, virtio-net, and wsproxy/wisp/fetch network backends.
- halfix (GPL-3): 32-bit, 10-30 MIPS in a browser. It has screenshots of Windows 7/10 32-bit, but there has been no activity since 2022.
- x86-64 guests need qemu-wasm, Bochs or browservm.js:
  - qemu-wasm: an out-of-tree fork with a wasm TCG JIT. Upstream QEMU 10.1 only has the 32-bit TCI interpreter for Emscripten. The wasm TCG backend was still at patch v4 in January 2026, and QEMU 11.0 does not list it. The build is about 46 MB and needs SharedArrayBuffer plus COOP/COEP headers.
  - Bochs (LGPL-2.1): an interpreter. Version 3.1 (August 2026) boots UEFI and Windows Server 2025. container2wasm already runs Bochs in wasm.
  - browservm.js: AGPL-3.
- Windows 11 needs 4 GB RAM, 64 GB of storage, UEFI with Secure Boot, TPM 2.0, 2 cores, and SSE4.2/POPCNT.
  - wasm32 is capped at 4 GB.
  - Memory64 is capped at 16 GB in browsers, runs 10-100% slower, and Safari support is uncertain.
  - I found no benchmark of an x64 Windows boot in a browser. My expectation that it would be impractically slow is an inference, not a measurement.
- Host integration needs guest drivers. That is easy for Linux (9p), but Windows needs virtio-win plus WinFsp, or SMB.
- Windows installation media cannot be redistributed. Windows 10 reached end of support on 2025-10-14.

B) USER-MODE / SYSCALL TRANSLATION (WSL1-like). This gives native access to the OS's files and network without guest drivers.

Linux:
- CheerpX/WebVM: x86-to-wasm JIT plus a Linux syscall layer, i386 only. It has IndexedDB, Data, Web, Overlay and HttpBytes devices, and Tailscale networking. The engine is proprietary. It is free for individuals and FOSS only when loaded from Leaning's CDN; self-hosting requires a commercial license.
- Blink (ISC): x86-64 Linux user mode, 150+ syscalls, VFS/overlays. An upstream Emscripten build exists. Inside wasm it runs as an interpreter (no JIT). It does not run i386 ELF binaries. Maintenance is slow: last release January 2024, last push December 2025.
- qemu-user in wasm does not exist.
- linux-wasm (Severin, 2025-26): runs only userland compiled to wasm.
- Box64 needs a native Linux host, but its native-library-wrapping model is worth copying.

Windows PE:
- Boxedwine (GPL-2): emulates a Linux kernel plus a 32-bit CPU to run real Wine 3.1-11.
  - 26R1 shipped April 2026.
  - The web build is slow. Its master branch already has wasm JIT targets, WebGL2 and a WebSocket network gateway.
  - It handles 16/32-bit programs only. Games after 2010 are hit-or-miss, and newer .NET versions do not work.
- Hangover (LGPL-2.1) is the most efficient blueprint. Wine runs natively on the host and only the application's PE code is emulated, via WoW64 (FEX/Box64; a Blink backend has been started).
- Wine 11 (2026-01-13) completed the new WoW64 mode and supports 16-bit apps. I found no mainline Wine port to wasm.
- Win32 API reimplementations:
  - retrowin32 (Apache-2.0): abandoned in 2024 in favour of Theseus (Apache-2.0), which statically translates DOS/Win32 to Rust and can target wasm.
  - RetroTick (CC0, TypeScript): runs simple classic apps.
  - Win32Emu (MIT, C#, Blazor WASM proof of concept): closest to the Kazo stack, but it covers only 15.4% of about 4,962 Windows ME DLL exports.
- Sogen (GPL-2, active): emulates x64 at the NT syscall level, runs in the browser, and uses the real ntdll/kernel32/user32. This requires Microsoft binaries (an "emulation root").
- nanox/nanokrnl (July 2026): a 67 KB x86-64 wasm core that runs unmodified cmd.exe on a reimplemented NT kernel.

DOS:
- js-dos 8.5.3 (GPL-2; DOSBox/DOSBox-X backends, Win9x images).
- MsDos.Clone already implements INT 21h at a high level (HLE), which is already approach B.

WSL REFERENCE:
- WSL1: pico processes plus lxss.sys/lxcore.sys, a clean-room translation of Linux syscalls. VolFs holds the Linux files and DriveFs exposes Windows drives under /mnt/c. It has faster cross-file-system access.
- WSL2: a real kernel in a managed VM, with full syscall compatibility.
  - File sharing: a Plan 9 server behind \\wsl.localhost.
  - Interop: a binfmt_misc entry routes .exe launches to /init, then the interop server, then hvsocket, then wsl.exe/wslhost.exe. Environment variables are shared through WSLENV.
  - GUI (WSLg): Weston with an RDP backend in VAIL mode, plus FreeRDP and PulseAudio. A plugin creates Start-menu entries from .desktop files.
- WSL was open-sourced under MIT on 2025-05-19, except lxcore.sys and the Plan 9 redirector components.

KAZO OS already has most of the "subsystem" plumbing: a persistent file system with Linux permissions, per-user default apps per file type, a host folder mounted as a network drive, a POSIX shell, a real TCP/WebSocket relay through its optional server, and a Store with kazopkg.

RECOMMENDED LAYERS:
- L0, in the kernel and small: a subsystem framework. A binfmt-like dispatcher reads MZ/NE/PE/ELF headers (Machine 0x14c/0x8664, GUI/CUI subsystem, CLR header for .NET). It provides bridges to the file system, network, console and windows, and lets the Store install engines and images.
- L1 DOS: MsDos.Clone HLE or js-dos.
- L2 Linux, WSL1-like: a Blink-derived x86-64 core in wasm whose syscalls go straight to Kazo's file system and network. CheerpX is an option only if its license fits.
- L3 Windows .exe:
  - Short term: Boxedwine as a separate Store app (32-bit only).
  - Target: Hangover-style. Our x86/x64 core plus Wine's LGPL PE DLLs, with the "Unix side" (NT syscalls, win32u, a display driver) implemented on top of Kazo. This gives deep integration with no Microsoft binaries.
- L4, WSL2-like full systems for complete OS installs: v86 for 32-bit guests (Win9x/2000/XP, ReactOS 0.4.16, 32-bit Linux). Bochs or qemu-wasm for x86-64 as experimental "bring your own ISO". Windows 7/10/11 x64 is a stretch goal.

Trade-offs:
- B: tight integration and speed, but limited compatibility (API coverage).
- A: full compatibility, but slow and heavy, with weak integration.
- Licensing: GPL engines must stay separate Store apps because CC BY-NC is not GPL-compatible, and the Os-x86 repo is MIT.
- Browser constraints: the 4 GB wasm32 limit, and the COOP/COEP headers needed for threads.

## Facts
1. [high] The reference MsDos.Clone already uses the user-mode / HLE model for DOS. DosKernel.cs is a C# implementation of the DOS INT 21h services (open/read/write file, EXEC 4Bh, terminate 4Ch) rather than booting a real DOS. It also contains a Cpu8086, Cpu32BitTests and Blazor/WinForms front-ends. — https://github.com/werddomain/MS-DOS/blob/copilot/create-webassembly-winform-clone/MsDos.Clone/src/MsDos.Core/Dos/DosKernel.cs (lines 8-11, 284-337); tree listing via gh api
2. [high] Kazo OS already provides several pieces a subsystem needs: a persistent browser file system with Linux-style permissions and owners, per-user default applications per file type, a real computer folder mounted as a network drive (Chrome and Edge only), a POSIX shell with 70+ commands, real network access through the optional Kazo OS server relay (TCP/TLS with ports declared in the app manifest), KazoClientWebSocket, and the Kazo OS Store / Os-AppMarketPlace with kazopkg. — https://github.com/Kazo-ca/Os/blob/main/doc/en/wasm/features.md (lines 8-39, 140-216)
3. [high] v86 is BSD-2-Clause. Its CPU is 32-bit only, roughly Pentium 4 with SSE3; 64-bit kernels are not supported. It translates machine code to WebAssembly modules at runtime. It provides virtio-9p file sharing and virtio-net/NE2000 networking, with wsproxy, wisp, fetch and inbrowser backends. — https://github.com/copy/v86 (README); https://github.com/copy/v86/blob/master/docs/networking.md; https://github.com/copy/v86/blob/master/docs/filesystem.md
4. [high] v86 guest compatibility: Windows 1.x/3.x/95/98/ME/NT/2000 work reasonably well, and 2000 and later need the Standard PC machine type. XP, Vista and 8 work only under certain conditions. ReactOS, Haiku, FreeBSD and many 32-bit Linux distributions work. — https://github.com/copy/v86 README; https://github.com/copy/v86/blob/master/docs/windows-nt.md
5. [high] halfix (GPL-3.0) emulates a 32-bit x86 CPU (FPU, MMX, SSE, SSE2, PAE) and has screenshots of Windows XP, Vista, 7 and 10. It runs at 10-30 MIPS in the browser versus 70-100 MIPS native. The last push was 2022-12-26. — https://github.com/nepx/halfix; gh api repos/nepx/halfix
6. [high] qemu-wasm (ktock/NTT) runs x86_64, aarch64 and riscv64 guests in the browser. It has a TCG backend that compiles hot translation blocks to wasm modules, multi-threaded TCG, and virtfs/9p. Upstream QEMU 10.1 contains only 32-bit-guest TCI (interpreter) Emscripten support; 64-bit TCI and the wasm TCG JIT were still under review. — https://github.com/ktock/qemu-wasm; https://pretalx.com/kvm-forum-2025/talk/EVRL9V
7. [medium] The upstream wasm TCG backend was still at patch v4 (wasm64-based) in January 2026. The QEMU 11.0.0 release highlights (2026-04-22) do not mention WebAssembly, so the merge is unconfirmed. — https://patchew.org/QEMU/cover.1769407033.git.ktokunaga.mail@gmail.com ; https://www.qemu.org/2026/04/22/qemu-11-0-0/
8. [medium] qemu-wasm is about 46 MB and needs threads, SharedArrayBuffer and COOP/COEP cross-origin isolation headers. — https://msuiche.com/posts/nanokrnl-cold-boot-fast-boot/ (2026-07-03)
9. [high] Bochs is LGPL-2.1 and an interpreter. Release 3.1 (2026-08-23) added UEFI/OVMF via fw_cfg, which enables booting Windows Server 2025 and modern Linux. Release 2.8 (2024-03-10) implemented the VMX MBE emulation required for a Windows 11 guest and fixed bugs that prevented Windows 10 from booting. — gh api repos/bochs-emu/Bochs/releases (REL_3_1_FINAL, REL_2_8_FINAL)
10. [high] The Bochs documentation says Windows 10 runs only after 'a few hacks', a 'considerable amount of time and a few reboots', and with ips=325000000 or more (lower values BSOD). — https://bochs.sourceforge.io/doc/docbook/user/guest-win10.html
11. [high] container2wasm (Apache-2.0) runs x86_64 containers in the browser using Bochs compiled to wasm, or QEMU wasm with TCG via --to-js. Networking goes through a Fetch-based proxy (subject to CORS limits) or a host-side WebSocket proxy. Host directories are mounted via virtio-9p. The project is described as experimental. — https://github.com/container2wasm/container2wasm
12. [medium] browservm.js (AGPL-3.0) is a QEMU/TinyEMU-based x86-64 full-system emulator. In interpreter mode it is about 300x slower than a modern CPU single-core; its experimental JIT is about 36x slower than native on Dhrystone. — https://github.com/RichardAH/browservm.js
13. [medium] No public benchmark or working demo of 64-bit Windows 10/11 booting in a browser was found. v86 lacks long mode, and qemu-wasm and Bochs-in-wasm are described as slow. — Web searches 2026-11-08; https://msuiche.com/posts/nanokrnl-cold-boot-fast-boot/; https://fosdem.org/2025/events/attachments/fosdem-2025-6290-running-qemu-inside-browser/slides/237638/slides_1dDtpcS.pdf
14. [high] Windows 11 minimum requirements: a 1 GHz+ 64-bit CPU with 2+ cores, 4 GB RAM, 64 GB storage, UEFI with Secure Boot, TPM 2.0, and a DirectX 12/WDDM 2.0 GPU. VMs must be Generation 2 with a virtual TPM. — https://learn.microsoft.com/en-us/windows/whats-new/windows-11-requirements (updated 2026-07-21)
15. [medium] Windows 11 24H2 refuses to boot on CPUs without SSE4.2/POPCNT. — https://www.ghacks.net/2024/04/23/windows-11-24h2-wont-boot-if-the-pc-does-not-support-this-cpu-feature/
16. [high] Windows 10 Home/Pro reached end of support on 2025-10-14. — https://learn.microsoft.com/en-us/lifecycle/products/windows-10-home-and-pro
17. [high] wasm32 linear memory is limited to 4 GB. Memory64 (shipped in Chrome 133 and Firefox 134) is limited to 16 GB by the JS API and can be 10% to over 100% slower because of explicit bounds checks. — https://spidermonkey.dev/blog/2025/01/15/is-memory64-actually-worth-using.html
18. [low] Safari support for Memory64 is unclear or partial as of 2026: it is listed as added in Safari Technology Preview 251, while caniuse lists iOS Safari 26.4/26.5 as unsupported. — https://caniuse.com/wf-wasm-memory64 ; https://www.mactrast.com/2026/08/apple-releases-safari-technology-preview-251-brings-bug-fixes-and-performance-improvements/
19. [high] CheerpX (Leaning Technologies) has an interpreter plus a JIT that emits WebAssembly. It runs 32-bit x86 binaries on a Linux-compatible syscall interface (user mode), with full-system emulation 'in development'. The current version is 1.2.8. — https://cheerpx.io/docs/overview
20. [medium] Leaning has stated plans for 64-bit support but no date; a 2024 statement cites wasm memory limits as the obstacle. — https://github.com/leaningtech/webvm/issues/165 ; https://news.ycombinator.com/item?id=42135204
21. [high] CheerpX file systems include WebDevice (HTTP, read-only), IDBDevice (persistent IndexedDB, with JS readFileAsBlob), DataDevice (JS writeFile into the VM), HttpBytesDevice (streamed ext2 blocks) and OverlayDevice (base plus a diff layer, chainable). — https://cheerpx.io/docs/guides/File-System-support
22. [high] WebVM (Apache-2.0 front-end on CheerpX) runs unmodified Debian, with an Alpine/Xorg/i3 desktop variant and Tailscale networking (no ICMP). Images are ext2 files built from a Dockerfile. — https://github.com/leaningtech/webvm
23. [high] CheerpX licensing: the free Community License covers individuals (including one-person companies), FOSS projects and technical evaluations, but only when CheerpX is loaded from cxrtnc.leaningtech.com, and credit is required. Self-hosting, redistribution and OEM use require a Commercial License. Whether a CC BY-NC project qualifies as FOSS is not stated. — https://cheerpx.io/docs/licensing
24. [high] Blink (ISC) is a user-mode x86-64 Linux emulator, the equivalent of qemu-x86_64. It supports 150+ syscalls including fork/clone, a VFS and BLINK_OVERLAYS. Its JIT exists only on x86_64/aarch64 hosts, and disabling the JIT is about 10x slower. Linux syscalls are accepted only from long-mode programs via SYSCALL, so 32-bit i386 Linux ELF binaries are not supported. — https://github.com/jart/blink README
25. [high] Blink upstream has an Emscripten/WebAssembly build: a GitHub Pages build was added in January 2023 and the source has __EMSCRIPTEN__ guards. A third-party fork (robalb emscripten_blink) exposes a JS API for x64.halb.it. The last release is 1.1.0 (2024-01-21) and the last push 2025-12-10. — gh api search commits repo:jart/blink 'emscripten'; https://github.com/jart/blink/blob/master/blink/web.h; https://github.com/robalb/x86-64-playground
26. [medium] No QEMU linux-user (user-mode) port to Emscripten/wasm was found. NTT slides list user-mode QEMU as future work. — https://fosdem.org/2025/events/attachments/fosdem-2025-6290-running-qemu-inside-browser/slides/237638/slides_1dDtpcS.pdf
27. [high] linux-wasm (Joel Severin, LKML post 2025-11-01) is a NOMMU Linux kernel compiled to wasm, now based on Linux 7.0 with wasm64. Userland must be compiled to wasm (patched LLVM, musl, BusyBox); it does not run x86 binaries. It is described as work in progress, and virtio is not ported yet. — https://github.com/joelseverin/linux-wasm ; https://lkml.iu.edu/2511.0/00248.html ; https://lwn.net/Articles/1044787/
28. [high] Box64 (MIT) is a Linux user-space x86-64 emulator for ARM64, RISC-V and LoongArch Linux hosts. It wraps host-native libraries (libc, SDL, OpenGL) instead of emulating them, and its DynaRec is 5-10x faster than its interpreter. No WebAssembly port is mentioned. — https://github.com/ptitSeb/box64
29. [high] Boxedwine (GPL-2.0) runs a 32-bit Linux build of Wine (3.1 to 11.0) by emulating the Linux kernel and an x86 CPU on a Tiny Core Linux 15 base. It runs 16- and 32-bit Windows programs. Games after 2010 have limited success, newer .NET versions do not work, and there is no joystick support. — https://github.com/danoon2/Boxedwine ; https://github.com/danoon2/Boxedwine/blob/master/docs/CPUemulation.md
30. [high] Boxedwine 26R1 (2026-04-22) is described as beta: '50% faster for all platforms except Web/WASM', Wine 11 support, improved but imperfect web audio. In releases the web build uses the slow 'normal' (interpreter) CPU core. — gh api repos/danoon2/Boxedwine/releases (26R1.0); docs/CPUemulation.md
31. [high] Boxedwine master (pushed 2026-11-09) has four browser targets: release, jit, multiThreaded and multiThreadedJit, with wasm JIT cache tooling. It starts with 512 MiB of wasm memory and can grow to 3 GiB. It requires WebGL2/UBO and wasm exnref exceptions (Chrome/Edge 137+, Firefox 131+, Safari 18.4+). Multi-threaded builds need COOP/COEP. A WebSocket network gateway (network-gateway.mjs) provides TCP/UDP. — https://github.com/danoon2/Boxedwine/blob/master/project/emscripten/buildInstructions.md ; project/emscripten/network-apps/README.md
32. [high] Hangover (LGPL-2.1) runs x86/x86_64 Windows apps on aarch64 Wine by emulating only the application. Wine and Unix-side code run natively, using Wine's WoW64: FEX via ARM64EC for 64-bit code, Box64 via wowbox64.dll for i386. A Blink backend has been started but is not yet in the repository. — https://github.com/AndreRH/hangover
33. [high] Wine 11.0 (released 2026-01-13) marks the new WoW64 mode as fully supported, at parity with the old mode. 32-bit apps no longer need 32-bit host libraries, and 16-bit support is included. Wine is LGPL-2.1-or-later. — https://linuxiac.com/wine-11-0-brings-fully-supported-wow64-mode/ ; https://github.com/wine-mirror/wine/blob/master/LICENSE
34. [medium] In Wine, PE code reaches the Unix side only through syscall-like entry points (__wine_unix_call is ioctl-like: a code plus a pointer-sized argument). Unix code cannot call PE functions directly. This makes it a clean boundary to reimplement on a non-Unix host. — https://www.winehq.org/pipermail/wine-devel/2021-August/193183.html ; wine-bugs developer guide (search results); verify against include/wine/unixlib.h
35. [medium] No mainline or maintained port of Wine itself to WebAssembly/Emscripten was found. Browser Wine efforts (Boxedwine and derived 'exe in browser' sites) emulate x86 Wine binaries. — Web search 2026-11-08; https://www.xda-developers.com/boxedwine-emulate-windows-applications-web-browsers/
36. [high] retrowin32 (Apache-2.0, Rust) interprets x86 and reimplements the Win32 API. It runs some console, GUI, DirectX and DirectDraw programs and demoscene productions. The author abandoned it (last push 2026-05-11, main work ended 2024) in favour of Theseus. — https://github.com/evmar/retrowin32 ; https://neugierig.org/software/blog/2026/04/theseus.html
37. [high] Theseus (Apache-2.0, active as of 2026-11-06) statically translates DOS and 32-bit Windows x86 executables to Rust, with a partial DOS/Win32 API, and can target WebAssembly. Runtime-generated or self-modifying code is out of scope. Generated code is not considered a derivative work. — https://github.com/evmar/theseus (README); https://neugierig.org/software/blog/2026/04/theseus.html
38. [high] RetroTick (CC0-1.0, TypeScript, created 2026-02-26, initial code AI-generated) loads PE/NE/MZ (plus WinCE ARM) and stubs Win32/Win16/DOS APIs. It runs FreeCell, Minesweeper, Solitaire, Calculator, Win 3.1 Notepad, QBasic and some DOS games, most with glitches. — https://github.com/lqs/retrotick ; gh api repos/lqs/retrotick
39. [high] Win32Emu (MIT, C#/.NET, created 2025-09, active) emulates x86 and reimplements Win32 APIs in C#: 84.2% of 1,090 tracked functions, but only 15.4% of about 4,962 Windows ME DLL exports. It has a Blazor WebAssembly front-end described as a slow proof of concept (IcedCpu interpreter), plus Win16 NE thunking and DirectDraw. — https://github.com/archanox/Win32Emu
40. [high] Sogen (GPL-2.0, very active, 3.9k stars) is a Windows/Linux user-space emulator working at the CPU plus syscall level. It runs the real ntdll, kernel32 and user32, supports x86-64 and arm64, and runs in the browser. It needs an emulation root and a registry dump taken from a real Windows install, i.e. Microsoft binaries. — https://github.com/momo5502/sogen (README lines 30-34, 76, 122); gh api repos/momo5502/sogen
41. [medium] nanox/nanokrnl (Matt Suiche, 2026-07-03): a 67,074-byte wasm x86-64 long-mode emulator plus a Rust 'NT-shaped' kernel. It runs unmodified Microsoft console binaries such as cmd.exe and needs no SharedArrayBuffer. Cold boot is about 100M instructions (a couple of seconds); snapshots are about 901 KB gzipped. — https://msuiche.com/posts/nanokrnl-cold-boot-fast-boot/
42. [high] js-dos 8.5.3 is GPL-2.0, with DOSBox and DOSBox-X backends and Windows 9x support through DOSBox-X prebuilt images. — https://github.com/caiiiycuk/js-dos (package.json, README)
43. [high] ReactOS (GPL-2.0) released 0.4.16 on 2026-08-29 and runs in v86. — gh api repos/reactos/reactos/releases; https://github.com/copy/v86
44. [medium] JSLinux (Bellard) is based on TinyEMU. Its x86 emulator now models a 64-bit CPU with SSE4.2, AVX2, AVX-512 and APX and runs Windows NT. Networking uses a WebSocket VPN capped at 40 kB/s. The license of the x86 emulator is not stated. — https://bellard.org/jslinux/tech.html
45. [high] WSL1 consists of an LXSS Manager service, pico processes and the pico provider drivers lxss.sys/lxcore.sys. These are a clean-room implementation that translates Linux syscalls to NT. VolFs gives full Linux semantics, and DriveFs mounts Windows volumes at /mnt/c. — https://learn.microsoft.com/en-us/archive/blogs/wsl/windows-subsystem-for-linux-overview (2016-04-22)
46. [high] WSL2 uses a real Linux kernel in a managed lightweight VM with full syscall compatibility. WSL1 remains faster for cross-OS file access. WSL2 uses NAT networking. — https://learn.microsoft.com/en-us/windows/wsl/compare-versions (updated 2026-06-02)
47. [high] WSL interop: Windows tools run from Linux as tool.exe, keeping the working directory and supporting pipes. Linux tools run from Windows via 'wsl <cmd>'. WSLENV shares variables, with /p /l /u /w flags for path translation. Linux files appear at \\wsl$ in Explorer. — https://learn.microsoft.com/en-us/windows/wsl/filesystems
48. [high] How WSL launches a Windows .exe: a binfmt_misc 'WSLInterop' entry points to /init. /init connects to an interop server through a Unix socket under /run/WSL, which forwards over hvsocket to wsl.exe or wslhost.exe, and that process creates the Windows process. In WSL2, file sharing goes through a plan9 server for //wsl.localhost. — https://wsl.dev/technical-documentation/interop/ ; https://wsl.dev/technical-documentation/
49. [high] WSLg: a system distro (Azure Linux 3.0) runs Weston with XWayland and an RDP backend in VAIL mode (per-window remoting), FreeRDP and PulseAudio. A WSLDVCPlugin virtual channel creates Windows Start-menu entries from Linux .desktop files. The GPU path uses the Mesa d3d12 driver. — https://github.com/microsoft/wslg
50. [high] Microsoft open-sourced WSL under MIT at Build 2025 (2025-05-19). lxcore.sys, P9rdr.sys and p9np.dll stayed proprietary. — https://betanews.com/2025/05/19/microsoft-opensources-wsl-linux-windows-benefits ; https://heise.de/-10390270
51. [high] A Windows guest needs virtio-win drivers plus WinFsp, a user-mode file system service, to use virtio-fs shared folders. Windows has no built-in 9p client. — https://virtio-win.github.io/Knowledge-Base/Virtiofs:-Shared-file-system
52. [high] PE header routing data: the e_lfanew field at offset 0x3C points to the 'PE\0\0' signature. Machine field: 0x14c = i386, 0x8664 = AMD64. Subsystem field: 2 = Windows GUI, 3 = Windows console (CUI). Data directory index 14 is the CLR Runtime Header, present in .NET assemblies. — https://learn.microsoft.com/en-us/windows/win32/debug/pe-format
53. [high] Without AOT, Blazor WebAssembly runs .NET IL on an interpreter (the 'Jiterpreter'). AOT gives 'dramatic improvements' for CPU-intensive code but roughly doubles app size. — https://learn.microsoft.com/en-us/aspnet/core/blazor/webassembly-build-tools-and-aot (updated 2026-11-04)
54. [high] The GNU project lists CC BY-NC as a nonfree license and therefore incompatible with the GNU GPL. Apache-2.0 is incompatible with GPLv2 but compatible with GPLv3. CC0 is GPL-compatible. — https://www.gnu.org/licenses/license-list.html
55. [medium] WebContainers (StackBlitz) is a proprietary Node.js-in-browser runtime; production use in a commercial, for-profit setting needs a license. BrowserPod (Leaning, 2025-26) reuses WebVM's syscall layer for Node.js and later bash, git and Python, with 'full Linux-class workloads' planned for late 2026. Neither runs arbitrary x86 Windows executables. — https://webcontainers.io/enterprise ; https://labs.leaningtech.com/blog/browserpod-20

## Implications
- Split the feature into two deliverables. First, a small kernel-side 'subsystem host' API that ships with the OS: a binfmt-like dispatcher, a VFS bridge, a network bridge, a console/pty bridge and a window/GUI bridge. Second, heavy engines plus OS images/rootfs as Kazo Store packages. This matches the user's goal of a small base download, and it keeps GPL engines out of the CC BY-NC OS.
- Design one CPU core that serves both families. It should support real, protected and long mode with i386 compatibility segments, and route traps and syscalls to pluggable handlers: Linux SYSCALL to a Kazo Linux personality (WSL1-like), NT syscalls or Wine unix-calls to a Kazo Win32 personality, and port I/O / MMIO to a device model (WSL2-like). Blink is a working example of one small core covering 16/32/64-bit modes.
- Prefer x86-64 with WoW64-style 32-bit support over a pure 32-bit core. Wine 11's completed WoW64 lets one 64-bit 'Unix side' serve both 32- and 64-bit PE applications. CheerpX (i386 only) and Blink (x86-64 only) each cover half of the Linux ELF space.
- For Windows .exe, the most promising architecture for deep integration is a Hangover-style split. Emulate only PE code (the app plus Wine's LGPL PE DLLs), and implement the 'Unix side' (NT syscalls, win32u/user/gdi driver, sockets, files) in wasm or C# directly on Kazo services. That means Kazo windows per HWND (like WSLg VAIL), the Kazo VFS mapped to drive letters (like DriveFs /mnt/c in reverse), and the Kazo network relay for Winsock. No Microsoft binaries are needed.
- A Win32-API-in-C# reimplementation (Win32Emu/retrowin32/RetroTick style) fits the Kazo C# stack and is fine for simple classic apps. The API surface is enormous, though: Win32Emu covers only 15.4% of about 4,962 Windows ME DLL exports. Treat this as a lightweight 'classic apps' mode, not the main path to broad compatibility.
- Approach B gives native file and network access without guest drivers. Approach A needs guest-side integration: 9p/virtio-fs is easy for Linux guests, while Windows guests need virtio-win plus WinFsp, or SMB/WebDAV over the virtual network, and a guest agent for clipboard and launching.
- Map networking onto existing Kazo pieces. Guest TCP/UDP sockets (user-mode) or virtual NIC frames (full-system) should go through the Kazo OS server relay or a WebSocket gateway. Prior art: Boxedwine network-gateway.mjs, v86 wisp/wsproxy/fetch backends, CheerpX Tailscale. A browser-only deployment gets at best HTTP via fetch (subject to CORS) plus WebSocket.
- User-facing subsystem UX, modelled on WSL:
- Install from the Store: an engine package plus a distro or Wine-prefix image, with persistence in IndexedDB/OPFS and an overlay over a streamed base image (like CheerpX OverlayDevice).
- A terminal profile per distro.
- Double-clicking an .exe or ELF in the File Explorer goes through the dispatcher, which reads MZ/NE/PE (Machine 0x14c/0x8664, GUI vs CUI, CLR header) or ELF headers and picks DOS, Win16, Win32, Win64, .NET or Linux. It opens a console window only for CUI programs.
- Default-app associations reuse Kazo's per-user file associations.
- Home folders are shared (Kazo /home mapped into /home or C:\\users).
- Interop commands in both directions (kazo.exe or wslpath-like tools, a WSLENV-like variable).
- Start-menu entries generated from installed .desktop files and Windows shortcuts (WSLg-style).
- Write the CPU core in C, C++ or Rust compiled to wasm, or in AOT-compiled C#, not interpreted .NET IL. Blazor's default IL interpreter would put an interpreter inside an interpreter. A later dynamic x86-to-wasm JIT (as in v86, CheerpX and qemu-wasm) has to build wasm modules at runtime through WebAssembly.compile/instantiate via JS interop.
- Treat full Windows 7/10/11 x64 installs as an experimental 'bring your own ISO and license' tier on a Bochs- or QEMU-class x86-64 core. Practical full-system Windows is the 32-bit tier (Win9x/2000/XP, ReactOS) on a v86-class engine. Windows 11 also needs UEFI, Secure Boot and TPM 2.0 emulation, at least 4 GB guest RAM (at or beyond the wasm32 limit) and SSE4.2/POPCNT.
- .NET Framework executables (detected by the CLR header) are a possible special route. A managed exe could run on Kazo's own .NET runtime if its API surface (WinForms, etc.) were provided, instead of running wine-mono under x86 emulation. This needs a decision, and its feasibility is unverified.
- DOS is already solved by the MsDos.Clone HLE model (DosKernel.cs) and/or js-dos. The DOS layer can be the first 'subsystem' that validates the dispatcher, file system and console bridges before the x86-64 engines exist.

## Risks
- Performance. Interpreted x86 in wasm is roughly 10-300x slower than native (examples: Blink without JIT is about 10x slower; browservm's interpreter is about 300x slower; halfix runs 10-30 MIPS in the browser). Booting x64 Windows 10/11 in a browser may be impractically slow. No benchmark exists to size this, so it is unverified.
- Memory limits. wasm32 is capped at 4 GB (Boxedwine caps itself at 3 GiB). Memory64 allows 16 GB but costs 10-100% speed, and Safari support is uncertain. Windows 11 needs at least 4 GB guest RAM.
- Threading needs cross-origin isolation. SharedArrayBuffer requires COOP/COEP headers; qemu-wasm and Boxedwine's multi-threaded builds depend on them. GitHub Pages does not send them (per the linux-wasm author), which could conflict with Kazo's static GitHub Pages deployment and with embedding third-party content.
- License incompatibility.
- GPL engines (Boxedwine GPL-2, halfix GPL-3, Sogen GPL-2, js-dos GPL-2, ReactOS GPL-2) cannot be merged into the CC BY-NC Kazo OS (nonfree, so GPL-incompatible) or into the MIT Os-x86 repo without making the combined work GPL.
- Wine LGPL-2.1 and Bochs LGPL-2.1 are friendlier, but still carry obligations for modifications.
- The CheerpX Community License does not allow self-hosting; whether CC BY-NC counts as 'FOSS' is unknown.
- Microsoft licensing. Windows media and system DLLs cannot be redistributed: Sogen's emulation root and a Windows disk image would both have to come from the user. Windows 10 reached end of support on 2025-10-14.
- Ecosystem fragility.
- halfix: inactive since 2022.
- retrowin32: abandoned.
- Blink: slow maintenance, last release January 2024.
- qemu-wasm JIT: out-of-tree, upstream merge unconfirmed.
- linux-wasm: unstable.
- CheerpX: proprietary and 32-bit only.
- RetroTick: largely AI-generated.
Building on any of these means owning a fork.
- Compatibility ceiling of the user-mode approach. The Win32 API is huge. Post-2010 games, DirectX 10-12 (DXVK/vkd3d need Vulkan, which browsers do not have; WebGPU would require a translation layer), anti-cheat and DRM, drivers, services and kernel-mode software will not work. Installers (MSI, InstallShield) depend on deep API coverage.
- Security. Running arbitrary untrusted .exe/ELF files with direct access to Kazo's file system and real network (through the server relay) needs capability prompts and sandboxing. The relay could be abused as an open proxy; Boxedwine's gateway defaults to a localhost allowlist with origin checks.
- Graphics and input fidelity. A per-window GUI integration needs a Wine display driver mapped to Kazo windows, which does not exist yet. Full-system mode shows only a framebuffer in a canvas. Audio latency in the browser is imperfect (Boxedwine says web audio is 'still not perfect').
- Download size. A Wine root file system, distro images, Bochs/QEMU builds (qemu-wasm is about 46 MB) and guest disk images are large. This needs streaming (HttpBytes-style) and overlay storage, and may exceed browser storage quotas.

## Open questions
- Priority. Is the main goal to run individual .exe and ELF programs integrated into Kazo (WSL1-like, recommended first), or to install complete operating systems (WSL2-like Windows 7/10/11)?
- Target programs. Which kinds of executable must work first: console and dev tools, classic Win9x/XP-era GUI apps and games, modern x64 apps, .NET Framework apps, installers (MSI/InstallShield), or DirectX 9+ games?
- Is x64 Windows 7/10/11 support a hard requirement or a long-term aspiration? It implies UEFI, Secure Boot and TPM emulation, at least 4 GB guest RAM (beyond wasm32), and likely unusable speed today.
- License policy. May GPL engines (Boxedwine, js-dos, ReactOS, halfix) be shipped as separate Store apps? Should the Os-x86 repo stay MIT, which limits it to MIT/BSD/ISC/Apache/LGPL-compatible reuse? Is the proprietary CheerpX, which cannot be self-hosted on the free tier, acceptable?
- Must the emulator core be written in C# (like MsDos.Clone) for consistency with Kazo, or may it be C, C++ or Rust compiled to wasm (better performance, reuse of Blink, Bochs or v86 code)? If C#, is AOT compilation acceptable despite the larger download?
- For Windows support, which route is acceptable: (a) Boxedwine as is, short term and 32-bit only; (b) a Kazo-native Wine 'Unix side', Hangover-style, which is a large but deep-integration effort; (c) a pure C# Win32 reimplementation in the Win32Emu style; or (d) a full Windows VM? Combinations are possible.
- Is a Kazo OS server (network relay) an acceptable requirement for subsystem networking, or must networking also work in the static, GitHub Pages-only deployment? There it is limited to fetch with CORS plus WebSocket.
- Which browsers and devices must be supported (Safari/iOS, mobile)? This decides whether Memory64, SharedArrayBuffer threads (COOP/COEP) and WebGL2 can be assumed.
- Will users supply their own Windows ISOs and licences, and is shipping Microsoft binaries ever acceptable (for example a Sogen-style emulation root)? Or must the Windows layer be 100% free (Wine/ReactOS)?
- Should .NET executables (CLR header) be routed to Kazo's own .NET runtime with a WinForms-compatible layer, instead of running wine-mono under x86 emulation?
- Delivery split. Confirm that only the dispatcher and bridges go in the kernel, and that each engine (DOS, Linux user-mode, Win32 user-mode, full-system VM) and each image (distros, Wine prefix) is a separate, optional Store package.
