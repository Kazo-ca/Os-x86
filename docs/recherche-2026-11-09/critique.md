## Q14 wording: whose network call opens the Os-x86 permission window (blocking=True; D14, D13, D15)
WHY: The user wrote 'si l'application appelante fait un appel reseau' (literally: the CALLING application), then 'chaque executable devrait etre considere comme une autre application'. The journal (docs/conception/journal-decisions.md, row 14) recorded it as 'un executable lance'. The two readings produce different systems. A calling Kazo app's own traffic already goes through Kazo's gateway (network.real.access, AppNetworkGateway.cs:325-353), and Os-x86 never sees it. Guest programs are invisible to Kazo: all guest traffic reaches the relay as the single Os-x86 appId (AppNetworkGateway.cs:347). They can only be policed by Os-x86's own network adapter at the guest SYN (v86 src/browser/fake_network.js:253-310 @315b2b0).
OPTIONS:
  - A) The launched executable (guest program). Its network call is intercepted by Os-x86. Calling Kazo apps stay under Kazo's own permissions.
  - B) The calling Kazo app: Os-x86 asks again about the app's own network use. Not enforceable, because Os-x86 sees none of that traffic.
  - C) Both
REC: A
RATIONALE: It matches the journal's reformulation and 'chaque executable = une autre application', and it is the only reading Os-x86 can actually enforce.

## Can the network prompt only narrow rights, or can it grant rights the caller does not have? (the 'at most the caller's rights' cap vs an 'Allow' answer) (blocking=True; D14, D11, D15)
WHY: D14 has two rules that can conflict. Suppose the caller lacks network.real.access, which is dangerous and needs admin approval (AppCapabilities.cs:242-246; DangerousCapabilityGate.cs:13-27). An 'Allow' answer would then give the guest more than the caller has. More outer layers exist. Os-x86 itself needs network.real.access. Relay BlockedPorts always win: TCP 25, 111, 135, 137-139, 445, 465, 587, 2049 (ProxyOptions.cs:160-180). UDP is limited to 53/123. With no server connected there is no real network at all (sdk-network.md:105-133). Kazo never intersects rights: a child runs with its own grants (AppLifecycleOrchestrator.cs:1049-1062). Os-x86 must therefore compute the effective rights itself and define how Kazo capabilities map to guest resources: network.real.access to the Internet NIC, filesystem.* to 9p shares, clipboard.* to the clipboard bridge, and so on.
OPTIONS:
  - A) Narrowing only. effective = Os-x86 grants AND caller cap AND subsystem config AND per-executable decision AND relay policy. The prompt appears only when every outer layer would allow. Otherwise the connection is refused at once and an explanatory notification is shown. An ADR fixes the Kazo capability to guest resource mapping.
  - B) The prompt can elevate: the user's Allow overrides the caller cap for network. The cap still applies to files and clipboard.
  - C) Elevation only with administrator credentials in the prompt
REC: A
RATIONALE: This keeps D14's 'at most' intact and avoids showing an Allow the user cannot make work, for example on blocked ports or with no server. It needs no change to Kazo's grant model.

## Who counts as 'the caller' for each way a program is started (blocking=True; D14, D13, D11, D15)
WHY: The cap in D14 needs a caller, and the entry paths differ. (1) An app calls the D15 interface, for example Os-App-BoxedWine. (2) The user types ./setup.exe in the Kazo terminal: binfmt hands it to an Os-x86 Terminal command, which runs with its OWN capabilities, not the shell's (ShellInterpreter.Scripts.cs:107-116; AppLifecycleOrchestrator.cs:1049-1062). (3) The user double-clicks in the explorer: FileHandlers on an Os-x86 Window app (AppManifestValidator.cs:318-321). (4) The Subsystems app or the wsl-like command. (5) A program started inside the guest: setup.exe starting the installed game, or a command typed in a guest shell. The terminal and explorer are system apps with broad rights (shell.host is dangerous, AppCapabilities.cs:272-275), so capping a launch by their manifests means nothing.
OPTIONS:
  - A) User-initiated launches (2, 3, 4) are capped by the user's own Kazo rights and Os-x86's grants. Only app-initiated launches through the interface (1) are capped by the calling app's grants. Launches inside the guest (5) inherit their launcher's cap and identity.
  - B) Treat the terminal and explorer as ordinary callers and cap by their manifest grants
  - C) No cap for user-initiated launches; only per-executable prompts apply
REC: A
RATIONALE: Each path gets a meaningful cap. It matches the WSL model, where what the user runs gets the user's rights. Path 5 inherits automatically if each launch lives in its own network namespace (see the identity question).

## Who the caller cap protects against: guest code only, or also a malicious .NET calling app (blocking=True; D14, D10)
WHY: All installed Kazo apps share the page, the main thread and the Default ALC. The SDK states: 'An installed app's code runs with the same rights as the OS' (KazoAppPackages.cs:20-23). KazoExecutionContext.Enter is public, so a caller can make a fabricated context ambient (KazoExecutionContext.cs:27-31, 72-81). A hard boundary against a malicious calling app is therefore impossible today. Against guest programs, v86 is a real sandbox. The answer decides whether Kazo needs an ADR so the OS hands the provider a verified caller identity. Precedents: the bus stamps the topic PublisherAppId (AppGatewayContracts.cs:213-218), and the intent dispatcher stamps SenderAppId (AppIntentDispatcher.cs:382).
OPTIONS:
  - A) Hard boundary against guest code. Against calling .NET apps, a policy for honest apps that prevents confused-deputy mistakes, with the caller identity supplied by the OS: a Kazo ADR for an OS-built facade per caller carrying appId, userId, pid and a capability snapshot.
  - B) Same as A, but trust the ambient KazoExecutionContext: no OS change, and the identity can be forged
  - C) Hard boundary against .NET callers too: needs a Kazo isolation project (separate ALC, worker or iframe per app), out of reach today
REC: A
RATIONALE: This is honest about Kazo's own trust model ('the trust in its source is the real barrier'). It enforces what can be enforced and needs one small ADR that follows the existing identity-stamping precedents.

## What 'one application' is for Wine programs (the identity unit for permissions) (blocking=True; D14, D9, D11, D13)
WHY: Wine creates AND connects every TCP socket inside wineserver, not in the .exe's process (wine server/sock.c:1931, 2736 @78e5220). There is one wineserver per (uid, WINEPREFIX) (server/request.c:627-635), shared by every process of the prefix, CreateProcess children included. /proc/PID/exe is wine-preloader for every Wine process (dlls/ntdll/unix/loader.c:439-456), and the parent chooses the child's cmdline (process.c:442). OpenSnitch logs Wine traffic as wineserver (opensnitch #892). The host sees only Ethernet frames or TCP tuples, never the process (v86 docs/networking.md). Robust per-program network identity therefore needs one prefix per identity, with its own wineserver, network namespace and source IP. Inside a shared prefix, telling .exe files apart is only cooperative, and a hostile .exe can bypass it (Wine FAQ: Wine 'does not sandbox in any way at all'). This choice also decides who an installed program is (a game installed by setup.exe) and what key remembers a decision. For ELF programs started through binfmt, a network namespace per launch gives per-program identity directly.
OPTIONS:
  - A) One bottle per launched identity. The first .exe launched from Kazo gets its own prefix (copy-on-write over a shared pre-built base prefix) and its own netns and IP. Everything it installs or starts lives in its bottle and shares its identity. A later launch of an installed program runs in the bottle that installed it. Decisions are keyed by (Kazo user, subsystem, bottle). The prompt shows the origin .exe's path and SHA-256, and a changed hash asks again.
  - B) One shared prefix per subsystem (WSL-like single C: drive), identity per .exe by SHA-256 or path, enforced cooperatively through a patched wineserver or cmdline heuristics. Can be bypassed.
  - C) One identity per subsystem (per VM). Robust, but contradicts 'each executable is a separate application'.
  - D) A shared prefix by default, with bottles (A) as an opt-in per program
REC: A
RATIONALE: It is the only option the kernel enforces. It matches the CrossOver and Bottles model users know. Children inherit automatically, which is D14's 'at most'. Costs to accept: one wineserver, services and explorer per running bottle on a single emulated core, and wineboot copies full builtin DLLs into each prefix (dlls/setupapi/fakedll.c:938-976). Both are mitigated by the copy-on-write base and a cap on running bottles.

## Where .exe and ELF files run: a managed Wine subsystem, or the user's own Linux subsystems (blocking=True; D9, D11, D13, D16)
WHY: D9 puts Wine inside a v86 Linux guest, D11 gives each user their own subsystems, and D13 sends MZ/PE and ELF files from the terminal to the feature. Three things are undecided. Is Wine a managed image owned by Os-x86 (for example Alpine 3.24 x86 with a pinned Wine 11.0, aports community/wine/APKBUILD) that the user cannot break, or a package inside each user's Linux subsystem, as with `apt install wine` in WSL? Which subsystem receives an ELF when a user has several (WSL uses a default distro)? Is the Wine subsystem created automatically on the first ./setup.exe? That first launch means a download of an estimated 0.5-0.8 GB and building a prefix, which takes minutes.
OPTIONS:
  - A) A dedicated managed 'Windows programs' subsystem per user: a read-only Os-x86 base image plus a per-user overlay. It is created automatically on the first .exe launch with progress shown in the terminal or a window, and is listed in the Subsystems app but not modifiable there. ELF files go to the user's default Linux subsystem, chosen WSL-style.
  - B) Wine installed inside the user's general-purpose Linux subsystem; .exe and ELF files both go to the default subsystem
  - C) The user must explicitly create a Wine subsystem before any .exe can run
REC: A
RATIONALE: The image stays pinned and reproducible, which the warm snapshots, the bottles and the Wine pin all require. ./setup.exe works with no setup step, and general-purpose Linux subsystems stay WSL-like.

## How the contracts DLL reaches the runtime so that 'null when not installed' can work (blocking=True; D10, D15, D12)
WHY: As stated, D10 cannot work on Kazo today. (1) A caller's .kazoapp carries only declared assemblies, not NuGet or ProjectReference DLLs (AppPackageWriter.cs:54-66; issue #419 OPEN, label TODO, since 2026-11-07). (2) The install check refuses any reference the package does not ship and the runtime cannot load, so the CALLER fails to install with apps.package.missing-api before GetService is ever called (AppPackageApiCheck.cs:80-90; AppPackageInstaller.cs:647-654). (3) No run-time resolver serves app libraries; the only Resolving handler is for satellites (InstalledAppSatellites.cs:93-110). (4) Type identity: the first assembly loaded with a given simple name wins, versions are ignored, and the result is cached for the page's lifetime (InstalledAppsBoot.cs:300-306; BuildKnownAssemblyLoaderRegistry.cs:66-67). A caller's older copy can therefore shadow Os-x86's copy and cause MissingMethodException. D10 also changes the journal's C15 note ('interface definie par l'OS'): the Kazo mechanism must accept contracts that a third-party package defines.
OPTIONS:
  - A) Each caller carries the contracts DLL: Kazo finishes #419's 'libraries'. A new shared-library rule adds: one copy per simple name, the provider's or highest compatible version loaded before consumers, installs refused across incompatible major versions.
  - B) The contracts ship as a small separate 'shared library' kazopkg that callers declare as a hard dependency (installed by the D12 resolver), while Os-x86 stays optional. Needs a new package kind with no AppKind.
  - C) The contracts ship in the Kazo OS boot bundle as an SDK library, like IKazoTerminal (ADR 0148). Always loadable, but tied to Kazo's release cycle, which departs from 'published by Os-x86'.
  - D) No shared assembly: the NuGet is source-only (DTOs plus a client proxy) and talks to Os-x86 over an OS-mediated channel (intents carry at most 64 KiB of JSON; topics). No type-identity issue, but no typed objects or streams.
REC: A
RATIONALE: It keeps D10 literally. #419 is already planned upstream. The shared-library rule is needed anyway if Os-x86's own apps share code across packages.

## Synchronous GetService<T>() vs asynchronous resolution (blocking=True; D10, D15)
WHY: Providers are loaded lazily from the VFS on first use. ADR 0148 rejected a global DI service precisely because the provider would then load at startup (adr/0148-sdk-contracts-and-os-provided-controls.md:110-115), and KazoComponentHost.ResolveAsync loads through EnsureDescriptorAsync (KazoComponentHost.cs:129-182). A synchronous GetService<T>() cannot load a provider that is not loaded yet. It would return null although Os-x86 is installed, which breaks 'null only when not installed'. [Inject] cannot replace it either: Blazor throws on a missing service (aspnetcore ComponentFactory.cs:165-168), and KOSAPP0031 forbids injecting non-SDK types. It must be a gateway plus a facade (ADR 0055 §4).
OPTIONS:
  - A) Async: `await KazoFeatures.GetServiceAsync<T>()` returns null when no provider is usable. A synchronous `IsAvailable<T>()` is answered from the catalog without loading code.
  - B) Synchronous GetService<T>() answered from the catalog, returning a stub that loads the provider on its first member call. Every contract member must then be async, with a stub per interface shipped in the contracts DLL.
  - C) Synchronous GetService<T>() with providers preloaded at login: a startup cost on every boot, contrary to ADR 0148
REC: A
RATIONALE: Same semantics as ASP.NET (null when absent) and the same API shape as Kazo's existing ResolveAsync, with no startup cost. The sync check lets a UI enable or disable buttons immediately.

## What 'null' means beyond 'not installed' (blocking=True; D10, D15, D12, D11)
WHY: Several states lie between 'installed' and 'usable'. The provider may be disabled by an admin. It may have been installed after boot: a service is not started until the next boot (ADR 0214 §1.3), and loaded code is not updated until a restart (§1.2). The installed contract version may be older than the caller needs, and contract types load once per page. Os-x86 may be installed personally by another user, and only one version loads per page (InstalledApps.cs:227-233). The caller may lack the D14 permission. Separately, if a caller declares Os-x86 as an Optional dependency WITH a MaximumVersion and an out-of-range Os-x86 is installed, the CALLER is dropped from the catalog (AppCatalog.cs:487-525; HostAppCatalog.cs:82-130) rather than receiving null.
OPTIONS:
  - A) null means 'no usable provider for you right now': not installed, disabled, too old for the requested contract version, or restart pending. `GetStatus(contract)` returns the reason (NotInstalled, Disabled, UpdateRequired, RestartRequired) so the app can offer to install, update or restart. A missing permission is NOT null; it throws UnauthorizedAccessException on use, like other Kazo gateways. A Kazo ADR makes an out-of-range optional dependency count as absent.
  - B) null only when not installed; every other case throws a typed exception
  - C) Return a result object (Available, NotInstalled and so on) instead of null; departs from the ASP.NET analogy
REC: A
RATIONALE: null stays the simple 'can't use it' signal from the ASP.NET analogy, and the reason is still available. A permission error remains a developer bug, as everywhere else in Kazo.

## How the caller's new 'declared permission' is declared and granted (blocking=True; D14, D10, D12)
WHY: A manifest capability must be in Kazo's closed catalog or be a topic permission. Anything else fails with manifest.capability.unknown (AppManifestValidator.cs:846-863), and the catalog is 'deliberately not meant to grow per-app entries' (ADR 0040:20-23). Topic permissions are auto-granted at login in code, even though ADR 0040 requires approval (CleanProfileCapabilityGrantSeeder.cs:48-72; AppCapabilities.cs:318-331). Normal capabilities get no consent beyond being listed on the Store page. Dangerous ones need admin approval when the caller launches (DangerousCapabilityGate.cs:75-140). A Kazo ADR is needed whichever way, and the classification decides the user experience.
OPTIONS:
  - A) A generic Kazo ADR for 'provider-declared permissions'. A feature provider declares permission ids rooted in its AppId (for example ca.kazo.x86/run), each with a risk sentence and a Normal or Dangerous class. Callers list them in `capabilities`. Kazo's grant repository, Store page and approval overlay handle them, and the same ADR fixes the topic auto-grant bug.
  - B) A curated Kazo capability (for example subsystems.run) added to AppCapabilities
  - C) No Kazo-level declaration: Os-x86's own permission system also governs callers, and an app's first use opens an Os-x86 window
REC: A, with 'run' classified Normal (shown at install time; the per-executable prompts follow)
RATIONALE: The same feature-provider mechanism (D10) serves any future optional feature, and Kazo's consent screens and audit are reused. Kazo's core does not learn Os-x86-specific names.

## Permission tiers and ownership for full VM control in the interface (D15) (blocking=True; D15, D14, D11)
WHY: D15 v1 exposes disks, snapshots and devices. A v86 state contains the guest's non-zero RAM pages and disk writes (state.js:132-197; cpu.js:891-917; buffer.js:369-382), so a snapshot holds everything a user did in a subsystem. Attaching a disk or share, or changing the NIC, changes what guest code can reach. D11 makes subsystems per user, but nothing says whether app X may control a subsystem created by the user or by app Y. The Kazo precedent: controlling another app's service needs services.manage, which is dangerous (AppGatewayContracts.cs:613-675).
OPTIONS:
  - A) Two permissions: 'run programs' (Normal) and 'manage subsystems' (Dangerous, admin approval). A caller only ever acts on the session user's own subsystems. Subsystems an app creates are tagged with it; controlling any other subsystem needs 'manage' plus a one-time Os-x86 consent from the user.
  - B) One permission for everything
  - C) Three tiers: run, read status, manage
REC: A
RATIONALE: Least privilege for the common case (BoxedWine-like apps only run programs). Reading snapshots or attaching disks stays an explicit, audited act.

## Package shape for 'one Os-x86 package', and using it right after install (blocking=True; D11, D13, D12, D10)
WHY: A .kazoapp holds one manifest.json with one AppKind (AppPackage.cs:47, 78). A terminal command is allowed only on a Terminal app, and FileHandlers only on Window apps (AppManifestValidator.cs:295-321). D11 (service, Subsystems window, wsl-like command) plus D13 (binfmt handler command, .exe file handler) adds up to at least three apps. Separate packages sharing code run into the same-simple-name loading issue (InstalledAppsBoot.cs:300-306). Starting or stopping a service without services.manage (dangerous) is allowed only between entry points compiled into the same assembly (AppIntentDispatcher.cs:620-626). Independently: a service added without a restart is not started until the next boot (ADR 0214 §1.3). Boot startup is downgraded to login for non-system apps (AppLifecycleOrchestrator.cs:801-827). An auto-started service launches non-interactively, so the admin approval for network.real.access is deferred (AppLifecycleOrchestrator.cs:685; dangerous-capability-approval.md:121-126).
OPTIONS:
  - A) A Kazo ADR for multi-app packages (a 'bundle': one archive, several manifests, one shared lib/) installed, updated and removed atomically. v1 accepts 'restart to finish' after the first install, as `wsl --install` does.
  - B) Separate packages (service, Subsystems app, command) linked by hard dependencies and installed together by the D12 resolver behind one consent screen. Code is shared through #419 libraries, and the window app gets services.manage.
  - C) A, plus a Kazo ADR that lets a hot-added service start immediately with no restart
REC: A
RATIONALE: It keeps D11's 'one package' literal. One assembly hosts every entry point, so the service-control exemption applies and there is a single copy of the code. A restart after installing a system feature is the same experience as WSL. C can follow later.

## What 'a Kazo window opens' means for a GUI .exe, now that Wine runs inside a VM (blocking=True; D13, D9, D15 (map Q34 still open))
WHY: With Wine in a guest, windows come out of the guest display. Kazo windows have no position API (adr/0122:28-49) and render on the main thread (adr/0133). v86's graphical ScreenAdapter is DOM-bound and cannot be injected in a worker (starter.js:307-318). VRAM is not reachable through the public read_memory (cpu.js:313-321). Effort estimates below are low confidence. One window per subsystem: about 1-2 weeks. One window per .exe through per-app Wine virtual desktops tiled on the guest screen and sliced by the host: about 3-6 weeks, no Kazo change, menus stay clipped inside the window. Seamless through a custom winekazo.drv: an LGPL fork rebased on every Wine upgrade (38 gdi_driver.h commits since 2025), about 2-4 person-months. Seamless through winewayland.drv (still 'experimental' in 11.0) plus a guest bridge compositor: about 2-3 person-months. Both seamless routes need Kazo ADRs: pixel-surface windows painted from a worker, and owner-relative popups.
OPTIONS:
  - A) v1: one Kazo window per launched GUI .exe through per-app Wine virtual desktops (sliced framebuffer), with the subsystem's desktop window as fallback and VM console. Seamless later, after a spike.
  - B) v1: one window per subsystem; every GUI .exe appears inside it
  - C) Seamless from v1 (custom driver or winewayland bridge, plus Kazo platform ADRs)
REC: A
RATIONALE: It delivers 'a window per program', matching D13, with stock Wine and no Kazo platform change. The expensive seamless work waits for measured data.

## Console .exe fidelity in the Kazo terminal (Ctrl+C, raw keys, exit codes) (blocking=True; D13, D9)
WHY: Kazo's terminal is canonical-mode only. Ctrl+C cancels the command's token once, and a second Ctrl+C does nothing (TerminalPanel.razor:1132-1139). Ctrl+\ cannot be produced (TerminalInputDecoder.cs:78-85). Streams are UTF-16 TextReader/TextWriter (IShellSession.cs:101-105), and there is no raw tty. Windows programs can catch CTRL_C_EVENT and keep running (conhost.c:1519-1577) while the token stays cancelled. Wine's console APIs fail when stdio are pipes (ntdll env.c:1343-1373), so the agent needs a pty. Wine reads the terminal size once and ignores SIGWINCH. Wine truncates exit codes to 8 bits (ntdll thread.c:86-91): 0xC0000005 shows as 5. Without a Kazo change, TUIs, 'press any key' prompts and repeated Ctrl+C break.
OPTIONS:
  - A) A Kazo ADR for a 'raw terminal mode': raw key bytes in, no line discipline or echo, interrupt delivered as an event that does not cancel the command, resize events. It ships in v1. Full 32-bit exit codes come from a launcher helper .exe in the prefix; signal cases keep Kazo's 130/137/141.
  - B) v1 line-oriented only: the first Ctrl+C becomes CTRL_C_EVENT, then a forced stop after a grace period. Exit codes stay 8-bit. Raw mode comes later.
  - C) Reuse Kazo's full-screen session, where every key including Ctrl+C arrives as an event, without an ADR. Undocumented trick, reduced key set.
REC: A
RATIONALE: D13 promises Ctrl+C forwarding and the exit code. Only A makes 'Ctrl+C forwarded' true for programs that handle it. The ADR is small and also benefits ssh-like Kazo commands.

## Snapshot durability across Os-x86/v86 updates, and what 'devices' means (blocking=True; D15, D11)
WHY: v86 rejects a state saved by another STATE_VERSION and needs identical constructor options (disks, memory) to restore one (state.js:215-225; starter.js:891-896). Some device-state changes ship without a version bump (cpu.js:747), so any v86 upgrade can make saved states unusable. A snapshot API that silently loses users' snapshots after an Os-x86 update would be a data-loss surprise. v86's devices are fixed at construction (no hot-plug), so changing devices needs a reboot and invalidates suspend states. Also, the Kazo VFS caps a file at 16 MiB and writes whole files only (FileContentStoragePolicy.cs:15-18), so 'attach this ISO from my Kazo files' cannot work for real images.
OPTIONS:
  - A) Two concepts in the contract. Durable 'disk checkpoints': markers in the copy-on-write layer, independent of the engine, surviving updates. 'Suspend states' (RAM and devices): cache-like, discarded with a warning when the engine version changes. 'Devices' means a device profile applied at the next boot (RAM, NIC on or off, sound, display size, shares) plus hot media swap for CD and floppy only. Disks are imported and exported through OPFS or download, not VFS paths.
  - B) Full RAM-plus-disk snapshots only, tied to the engine version. Old ones are kept but restorable only with a matching engine, which means keeping old engines side by side.
  - C) No snapshots in the v1 interface (contradicts D15)
REC: A
RATIONALE: User-visible snapshots stay durable while the fast 'already booted' resume still works. Every promise made in the contract is technically keepable.

## Whether Wine-level concepts belong in the interface, and Os-App-BoxedWine's role next to the Subsystems app (blocking=True; D9, D15, D11, D10)
WHY: D9 makes Os-App-BoxedWine 'UI only', and D11 gives Os-x86 its own Subsystems manager app; how the work splits between them is undefined. D15 lists processes, subsystems and VM control, but not the Wine concepts a Wine front end needs: bottles or prefixes, installed Windows programs, Start-menu shortcuts and icons, per-program Windows version and DLL overrides. Driving these through a generic 'run a command in the guest' (wine reg, winecfg) would be fragile. The app is also named after Boxedwine (GPL-2.0, danoon2/Boxedwine) while containing none of its code.
OPTIONS:
  - A) Os-App-BoxedWine becomes the 'Windows programs' library: bottles, installed programs, shortcuts, per-program settings, launching. It is built on a Wine-specific contract (for example IWindowsPrograms) in the same contracts NuGet. The Subsystems app handles VM-level management. Consider renaming the app.
  - B) Merge: the Subsystems app does everything and Os-App-BoxedWine is dropped
  - C) The interface stays VM-generic; BoxedWine drives Wine through generic guest commands
REC: A
RATIONALE: Each UI gets a clear job, and the interface gains typed concepts that match the bottle identity model. The name stops implying an engine that is not used.

## Spike gate for D9, and the fallback if Wine in v86 is too slow (blocking=True; D9, D16, D13 (map Q41 open))
WHY: There is no public demonstration of Wine inside v86. v86 issue #316 calls it 'out of scope'; issue #455 reports a Puppy Linux with Wine taking about 15 minutes to boot to its GUI. v86 has one emulated core, x87 math through 'very slow' softfloat, and an expected slowdown of about 10x (v86 Readme.md:7-18; issue #1005). Wine's cold start can overrun its own 10 s wait for explorer.exe: Boxedwine patches it to 120 s (wine11_explorer_startup_timeout.patch). The only proxy is Buildroot's console-only, non-mingw Wine, whose wineboot finishes in under 45 s under qemu TCG (test_wine.py:60). The first milestone's criteria and the action on failure are both undecided.
OPTIONS:
  - A) A milestone-0 spike with pass criteria fixed after a first measurement (warm-snapshot launch time of a console .exe and of notepad on the reference machine) before committing to the Wine path. On failure: ship VM mode and DOS first, and re-evaluate at the x64 engine benchmark.
  - B) On failure, allow a Boxedwine-based engine as a separate GPL package (partly reverses D9)
  - C) No gate: commit now and optimize later
REC: A
RATIONALE: D9 rests on an unproven combination. A measured gate protects the plan and does not re-open D9 unless the numbers force it.

## How the permission prompt behaves: where, what, how long, when nobody answers, who answers (blocking=False; D14, D11, D13)
WHY: A Service app has no UI (ServiceAppBase.cs:33-225). Notification action ids never call back to the app (NotificationActionRunner.cs:9-52). The SDK has no prompt API, and no window can be system-modal or always on top (AppManifestWindow.cs:161-167). The prompt must therefore be an Os-x86 Window app launched by intent (apps.launch, normal). Holding a connection means keeping the guest's SYN, and each retransmitted SYN must be re-registered or v86 sends a RST (fake_network.js:257-266, 285). The guest is a Linux kernel, so its SYN retries last about 127 s (tcp(7)), though many apps time out sooner. OpenSnitch's popup defaults to 30 s and then denies (ui config.py:131, 195-198), and its daemon fails OPEN when no UI is connected (client.go:31-34). A silent drop stalls apps for about a minute (opensnitch #1404). Console programs compete with stdin for an inline prompt. Os-x86 cannot write Kazo's grant store or auth.log (AppGatewayContracts.cs:258-289), so it needs its own store under ~/.config/<appId>/ and /etc/<appId>/.
OPTIONS:
  - A) A desktop window, even for terminal launches, with a one-line note in the terminal. One prompt per identity, collecting all its pending connections. Choices: Allow or Deny, for once, until the program exits, or always. Scope: any destination by default, optionally only this host:port. The hostname is shown, since the host answers DNS. The connection is held up to 30 s, then that connection gets a RST while the prompt stays open for later attempts. Fail closed (deny plus a notification) when no interactive session exists. The subsystem's owning user answers. An admin can pre-set or lock machine-wide rules in /etc/<appId>/. Decisions are viewed and revoked in the Subsystems app. Kazo's simulated network is never prompted (mirrors network.simulated.* being normal); the real Internet is.
  - B) OpenSnitch-like: a prompt per destination, allow/deny/reject, many durations, 30 s timeout
  - C) Minimal: per identity, Allow or Deny always, with no timeout (wait until answered)
REC: A
RATIONALE: Fail-closed is required for 'ask before network'. One prompt per program avoids prompt storms, and the timeout respects app connect timeouts. The rules mirror Kazo's own classification.

## Scope of Os-x86's own permission system beyond network, and which files a launched .exe sees (blocking=False; D14, D13, D11 (map Q25 open))
WHY: D14 names network only. Other resources guest code can reach: Kazo files through 9p shares (open map item C25 recommends the home folder read-write); the clipboard (v86 VMware backdoor clipboard, vmware.js:88-93); launching Kazo apps through the guest agent. Wine's Z: drive maps the whole guest root, so bottles under the same guest uid can read each other's files. Separation is network-only unless mount namespaces or separate uids are added. A user who reads 'each exe is a separate application' may assume file isolation too.
OPTIONS:
  - A) v1: per-program prompts for network only. Files and clipboard are governed per subsystem and capped by the caller. A launched .exe sees its own folder plus the paths passed as arguments. Sharing the home folder is a subsystem setting, off by default in the managed Wine subsystem. Each bottle runs under its own guest uid so bottles cannot read each other's files.
  - B) Network plus per-program prompts for Kazo file access outside the .exe's folder (first access asks)
  - C) A general per-program framework (network, files, clipboard, launching Kazo apps) from v1
REC: A
RATIONALE: It delivers D14 as stated with a safe file default, and the store is designed to accept more resource types later.

## How launched programs appear as processes ('run/track' in D15) (blocking=False; D15, D13 (map Q19 related))
WHY: A Kazo process is a window, terminal, service or web app; a non-.NET guest program cannot be a first-class Kazo process. StartChild creates a bare record: no argv, no exit code, no capability check, unused anywhere, and it throws in workers (AppProcessGateway.cs:42-48; RemoteGateways.cs:329-330). `kill` only distinguishes -9 from a polite stop with a 5 s grace (KillCommand.cs:10-22). A terminal launch already has a Kazo process (the binfmt handler command); an interface launch has none. Each Wine launch also starts several guest processes (wineserver, services, explorer, the program and its children).
OPTIONS:
  - A) The unit is a 'launch': the program plus everything it spawns. Every launch is backed by one Kazo process: the handler command for terminal launches, and for interface launches a Kazo ADR for a 'foreign child process' with argv, exit code and a kill callback. ps, kill and the Task Manager then work. Guest-internal processes are shown only in the Subsystems app.
  - B) Launches are tracked only inside Os-x86 (interface and Subsystems app); Kazo's ps shows the Os-x86 service alone
  - C) Mirror the full guest process tree into Kazo's process table
REC: A
RATIONALE: It matches Kazo's 'behave like real hardware' principle at launch granularity and avoids the cost and noise of mirroring Wine's internal processes.

## Provider rules: one provider per contract, contract ids rooted in the provider, system-wide only, NuGet id (blocking=False; D10, D11, D12, D18)
WHY: Kazo's closest model, embeddable components, lets any window app register any non-platform contract, with an admin choosing between providers (ADR 0156; AppCatalog.cs:128-149). Applied to Os-x86, another package could claim Os-x86's contract and receive every caller's requests. D18 says copyleft engines plug into Os-x86, so Os-x86 stays the single provider. Kazo has no manifest field that refuses a personal install (AppManifest.cs has none). Personal installs load one version for every user of the page (InstalledApps.cs:227-233). A personally installed app with a hard dependency on a system-only package (BoxedWine depends on Os-x86) needs a rule. The NuGet id 'Kazo.Os.X86.Contracts' fails every caller's build with KOSAPP0030 (build/Kazo.Os.Apps.targets:401-430); 'Kazo.Os.Apps.X86.Contracts', or an id outside the Kazo.Os. prefix, passes.
OPTIONS:
  - A) Exactly one provider per contract. The contract id is rooted in the provider's AppId (for example ca.kazo.x86/subsystems.v1), so only that AppId may provide it. Engines plug into Os-x86 internally. A new manifest flag 'system-wide only' refuses personal installs, and a personal app that depends on such a package escalates its install to admin approval. NuGet id: Kazo.Os.Apps.X86.Contracts.
  - B) Several providers with an admin choosing (ADR 0156 model), with open contract ids
  - C) One provider, but personal installs allowed (the resolver prefers the system install)
REC: A
RATIONALE: It prevents provider impersonation, matches D11 and D18, and the build passes on the first try.

## Root inside the Linux guest (how strong per-program enforcement is) (blocking=False; D14, D11, D9)
WHY: Per-identity enforcement relies on each launch living in its own network namespace with a distinct source IP, which the host uses to attribute traffic. v86 replies to whatever source address the packet carries (fake_network.js:270-275, 1419-1447). Guest root can change IPs or namespaces and impersonate an allowed identity. WSL gives the user sudo by default.
OPTIONS:
  - A) Kazo-launched programs always run as an unprivileged guest user, and only the agent runs as root. No sudo in the managed Wine subsystem. In general-purpose Linux subsystems, sudo is a per-subsystem setting, with a warning that per-program network rules become advisory there.
  - B) WSL-like: the user has sudo everywhere and per-program rules are advisory
  - C) No root for the user anywhere
REC: A
RATIONALE: The per-program guarantee stays real where D14 matters most (Windows programs) without crippling general Linux use.

## Whether 'each executable is a separate application' applies to full VMs and to user-installed Linux distributions (blocking=False; D14, D11 (and tier 17: Windows VMs))
WHY: Without an agent inside the guest, the host sees one NIC per VM, so only per-VM granularity is possible (Windows Firewall prompts only for inbound 'listen' cases; learn.microsoft.com firewall rules, updated 2026-10-08). In a Windows guest, per-program control needs an agent: user-mode WFP blocks first and asks afterwards, while true hold-and-ask needs a kernel callout (FwpsPendOperation0). XP-era guests have no WFP at all. User-installed Linux distributions have no Os-x86 agent.
OPTIONS:
  - A) Per-program identity for Wine bottles and for ELF programs launched from Kazo. Full VMs (Windows guests, user-installed distributions) get one identity per VM, with an optional Windows guest agent later.
  - B) Promise per-program identity everywhere, which requires a Windows guest agent and an agent in every Linux guest
REC: A
RATIONALE: Only promise what can be enforced. The Windows tier is long-term research (decision 17).

## How many subsystems may run at once in one browser page (blocking=False; D11, D9, D14 (bottles))
WHY: WebAssembly memory is never returned to the system. Each v86 VM holds its guest RAM (up to about 2 GiB, cpu.js:970-995), its VRAM (8-256 MB) and its JIT in its worker until that worker is terminated (v86 issue #1005, 2024-09-18). D11 gives every user their own subsystems, possibly several users per page. Bottles multiply guest processes on one emulated core. Browser quota is per origin and shared by all Kazo users (BrowserStorageManager.cs:8-11).
OPTIONS:
  - A) By default one running VM per user session, with an admin-configurable cap per page. Idle VMs are suspended after N minutes and their worker terminated, the only way to free memory. A cap on running bottles applies inside the managed Wine subsystem.
  - B) No limit
  - C) One running VM per page, across all users
REC: A
RATIONALE: It keeps the tab alive on 8 GB machines and stays predictable.

## DOS programs: which route now that Wine cannot run them (blocking=False; D16, D9, D13 (map C10 open))
WHY: Wine removed DOS execution in 2018; winevdm now requires an external `dosbox` (wine programs/winevdm/winevdm.c:35, 108-164). Alpine's dosbox-staging excludes x86 (aports community/dosbox-staging/APKBUILD:7). D16 still says DOS first. v86 runs FreeDOS and MS-DOS 'very well' (v86 Readme.md:90). The binfmt dispatcher must tell pure MZ (DOS) apart from MZ+NE (Win16, which goes to Wine) and MZ+PE.
OPTIONS:
  - A) A small managed FreeDOS subsystem on v86: a separate VM that boots in seconds. Pure-MZ files go there, and text mode is shown in the Kazo terminal through v86's ANSI screen adapter (starter.js:307-318, medium confidence).
  - B) DOSBox inside the Linux guest: needs its own i686 build, and runs an emulator inside an emulator, slowly
  - C) A DOSBox engine compiled to wasm (js-dos/DOSBox, GPL-2, a separate package per D18) running beside v86
  - D) Drop DOS from the first tier
REC: A
RATIONALE: Reuses the BSD v86 engine already adopted, with fast boot and no GPL dependency. To be folded into the C10 debate.

## Wine version policy, and the i686 to x64 migration promise (blocking=False; D9, D16, decision 4 (tiers))
WHY: An i686 Wine can only create win32 prefixes, which Wine 11.0 deprecates and new WoW64 does not support (wine ANNOUNCE.md@wine-11.0:27-46). Upstream still builds i386 hosts as of 11.21 (configure.ac:167), with no promise. Moving to the x64 tier means recreating prefixes and reinstalling Windows programs (Arch news, 2025-06-16). Wine 11.11 and later, built with mingw, needs SSE2 (commit 508a30f0d3), which v86 has. v86 lacks SSSE3, SSE4 and AVX (instructions_0f.rs:3256-3375), so apps that require them fail regardless.
OPTIONS:
  - A) Pin one stable Wine per guest-image release (11.0 now). Prefixes are disposable and user documents live outside them. Users are told up front that the x64 tier means reinstalling Windows programs; Os-x86 can offer to re-run the recorded installers.
  - B) Follow Wine development releases (for example Void i686, 11.21): newer fixes, more churn
  - C) Skip the i686 Wine tier and wait for x64 (contradicts D16)
REC: A
RATIONALE: Reproducible images and honest expectations. Bottles fit the 'recreate later' constraint naturally.

## Delivering the guest image, which D9 makes mandatory for any .exe (blocking=False; D9, D18, D11, D13 (map C13-C14 open))
WHY: With Wine inside Linux, a large guest image is a prerequisite of D13. Alpine x86 wine 11.0 alone is 68.3 MiB as an apk and 220.7 MiB installed, with 25 direct dependencies; with an X server and one prefix the total is an estimated 0.5-0.8 GB, plus wine-mono (83 MB) and wine-gecko (55 MB) if included. A .kazoapp is capped at 64 MiB archive, 128 MiB extracted and 2000 files (AppPackage.cs:22-68). Manifest assets cannot be external URLs (AppManifestAssets.cs:42 plus the validator). VFS files are capped at 16 MiB. The image contains GPL-2 (kernel, BusyBox) and LGPL (Wine) code, so the corresponding source must be offered from the same place (Wine COPYING.LIB §4).
OPTIONS:
  - A) Os-x86 downloads the image on first use from a release origin that serves CORS (or from the Kazo server when present), in chunks verified by SHA-256, into OPFS. It is shared read-only by every user of that browser, with per-user overlays. The corresponding source is hosted next to it, and the image is published as its own artifact with a licence manifest. wine-mono and wine-gecko are downloaded on demand.
  - B) A new Kazo package kind for large data packages (an ADR raising the limits)
  - C) Split the image across several kazopkg packages of 64 MiB or less
REC: A
RATIONALE: Works within today's Kazo limits, keeps the copyleft content outside the MIT package (D18), and shares the base image across users. To be folded into C13-C14.

## Must ./setup.exe have the execute bit? (blocking=False; D13)
WHY: RunPathAsync returns 126 with a 'chmod +x' hint when no execute bit applies, before reading any content (ShellInterpreter.Scripts.cs:57-66), and the binfmt hook would sit after that check (Scripts.cs:57-83). Downloaded or copied .exe files will likely lack +x, so D13's './setup.exe' would fail out of the box. Windows has no execute bit, and WSL shows Windows files as executable.
OPTIONS:
  - A) Exempt MZ/PE files when their handler is installed (Windows semantics); keep the execute-bit rule for ELF (Linux semantics)
  - B) Keep Linux execve semantics for every file (126 plus 'chmod +x')
  - C) Kazo sets +x automatically on downloaded .exe files
REC: A
RATIONALE: ./setup.exe works as users expect while Linux binaries keep Linux rules.

# Feasibility warnings
- D10 as stated cannot work on today's Kazo (high confidence). A caller referencing an Os-x86 contracts DLL fails at ITS OWN install with apps.package.missing-api (AppPackageApiCheck.cs:80-90; AppPackageInstaller.cs:647-654). The cause: .kazoapp packs only declared assemblies (AppPackageWriter.cs:54-66), issue #419 has been OPEN with no PR since 2026-11-07, and no run-time resolver serves app libraries (InstalledAppSatellites.cs:93-110). There is no cross-app service registry: installed apps never add to the DI container (InstalledAppsBoot.cs:343-391), and [Inject] of non-SDK types is forbidden (KOSAPP0031). At least these Kazo ADRs are needed before any app can call Os-x86: app libraries (#419), shared-library versioning, a feature-provider manifest field plus a catalog index, an SDK resolution gateway and facade (ADR 0055 §4), and an OS-verified caller identity. The new SDK API also raises every caller's minimum SdkCompatibility (AppPackageInstaller.cs:630-637).
- Contract type identity hazard: the first assembly loaded with a given simple name wins, versions are ignored, and both loads and failed loads are cached for the page's lifetime (InstalledAppsBoot.cs:300-306; BuildKnownAssemblyLoaderRegistry.cs:66-67, 94-100). A caller's older copy of the contracts can shadow the copy Os-x86 was built against and cause MissingMethodException. Loaded assemblies never unload, so an Os-x86 update applies only after a restart (ADR 0214 §1.2).
- The NuGet id 'Kazo.Os.X86.Contracts' would break the build of every caller with KOSAPP0030 (wasm2/KazoOs/build/Kazo.Os.Apps.targets:401-430). Use Kazo.Os.Apps.X86.Contracts or an id outside the Kazo.Os. prefix.
- D11's 'one package' is at least three apps for Kazo: a .kazoapp holds one manifest and one AppKind (AppPackage.cs:47, 78); terminal commands are allowed only on Terminal apps and FileHandlers only on Window apps (AppManifestValidator.cs:295-321). Without a bundle ADR it becomes several packages, and sharing an assembly between them hits the same-name loading issue.
- Install-then-use: a service added without a restart is not started until the next boot (ADR 0214 §1.3, read directly). Boot startup is downgraded to login for non-system apps (AppLifecycleOrchestrator.cs:801-827). An auto-started service launches non-interactively, so the admin approval for network.real.access is deferred (AppLifecycleOrchestrator.cs:685; dangerous-capability-approval.md:121-126). Right after the first install, ./setup.exe will not work without a restart and an interactive approval.
- D14's cap on caller rights can be enforced against guest code only. Against calling .NET apps it is cooperative: Kazo never intersects rights (AppLifecycleOrchestrator.cs:1049-1062); KazoExecutionContext.Enter is public (KazoExecutionContext.cs:27-31); and installed apps 'run with the same rights as the OS' (KazoAppPackages.cs:20-23). D14's declared permission cannot be declared today (manifest.capability.unknown, AppManifestValidator.cs:846-863), and the only app-defined permissions (topic permissions) are auto-granted in code despite ADR 0040 (CleanProfileCapabilityGrantSeeder.cs:48-72).
- Per-executable network identity under Wine is much costlier than it sounds. wineserver creates and connects every TCP socket (wine server/sock.c:1931, 2736), there is one wineserver per prefix (server/request.c:627-635), /proc exe is wine-preloader, and the parent chooses cmdline. A robust per-program identity needs one prefix, wineserver, netns and source IP per identity. Each running bottle adds wineserver, services and explorer on v86's single core, and wineboot copies full DLLs into each prefix (fakedll.c:938-976; about 300 MB per prefix reported on macOS WoW64, unmeasured for i386). A copy-on-write base prefix is mandatory.
- Network path limits that no Allow answer can lift. The stock v86 Wisp adapter accepts every SYN immediately, and stock DNS uses a direct fetch() to cloudflare-dns.com that bypasses Kazo's grant (fake_network.js:58, 355-384; wisp_network.js:194-234). Os-x86 must write its own adapter over KazoTcpClient. On the Kazo relay: MaxStreamsPerDevice is 4 per DEVICE across all apps (ProxyOptions.cs:234; ProxyContracts.cs:314); BlockedPorts always win (TCP 25, 445, ...; ProxyOptions.cs:173-180); UDP is limited to 53/123, and v86 forwards no UDP (wisp_network.js:241). With no server there is no real network. UDP games and VoIP, SMB and SMTP will fail whatever the user answers.
- The prompt cannot come from the feature service: Service apps have no UI (ServiceAppBase.cs), notification actions do not call back (NotificationActionRunner.cs:9-52), the SDK has no prompt API, and windows cannot be system-modal or always on top (AppManifestWindow.cs:161-167). The prompt is an Os-x86 Window app launched by intent and can open behind other windows. Holding a SYN requires re-registering the newest connection object on each retransmission, or v86 sends a RST (fake_network.js:257-266, 285).
- D9 has no prior art (medium confidence that none exists): no public demo of Wine inside v86 (v86 #316 'out of scope'; #455 about 15 minutes to boot a Puppy+Wine GUI). v86 has a single core, 'very slow' softfloat x87, and an expected slowdown of about 10x (Readme.md:7-18; #1005). A cold start can overrun Wine's 10 s explorer wait (Boxedwine patch raises it to 120 s). The only proxy is a console-only, non-mingw Buildroot Wine whose wineboot finishes in under 45 s under qemu TCG. A warm snapshot is required for acceptable launch times, and the whole path should be gated by a measured spike.
- D9/D16 compatibility limits: an i686 Wine relies on WINEARCH=win32, deprecated in Wine 11.0 (ANNOUNCE.md:27-46). Win32 prefixes cannot move to new WoW64, so the x64 tier means reinstalling Windows programs. v86 offers no SSSE3, SSE4 or AVX (instructions_0f.rs:3256-3375), and some 16-bit protected-mode features are missing (Readme.md). Wine has not run DOS since 2018, and Alpine x86 ships no dosbox (APKBUILD:7), so DOS needs its own route.
- Guest image size versus Kazo limits: Wine alone installs to 220.7 MiB on Alpine x86 (est. 0.5-0.8 GB total with X and a prefix), but a .kazoapp is capped at 64 MiB archive, 128 MiB extracted and 2000 files (AppPackage.cs:22-68). Manifest assets cannot be external URLs (AppManifestAssets.cs:42). VFS files are capped at 16 MiB with whole-file writes (FileContentStoragePolicy.cs:15-18). The browser quota is per origin and shared by all Kazo users (BrowserStorageManager.cs:8-11). The image carries GPL and LGPL source obligations (COPYING.LIB §4). It must be delivered outside the package system.
- D13 console fidelity needs a Kazo terminal ADR. The terminal is canonical-only, Ctrl+C is a one-shot cancellation (TerminalPanel.razor:1132-1139), Ctrl+\ cannot be produced (TerminalInputDecoder.cs:78-85), streams are UTF-16 text, and there is no raw tty. Wine console APIs need a pty (ntdll env.c:1343-1373), Wine ignores resizes, and Wine truncates exit codes to 8 bits (thread.c:86-91). Without the ADR, TUIs, 'press any key' prompts and programs that catch Ctrl+C misbehave.
- D13 GUI: per-window seamless display is a large project, in effort estimates of low confidence: a custom Wine driver (LGPL fork, 38 gdi_driver.h changes since 2025) at about 2-4 person-months, or winewayland ('experimental') plus a bridge at about 2-3 person-months. Either also needs Kazo ADRs, because there is no window position API (ADR 0122) and windows render only on the main thread (ADR 0133). Other blockers: v86's ScreenAdapter cannot be injected in a worker (starter.js:307-318), VRAM is not readable through the public read_memory (cpu.js:313-321), and Linux bochs DRM moved to SHMEM in 6.13, which adds a guest copy. A small set of v86 patches is likely either way (virtio-console ports all forced to console mode and dropping host data when no buffer is posted, virtio_console.js:145-150, 208-215), which pushes open map item C6 toward a patched or forked v86.
- D15 snapshots and disks: v86 rejects states from another STATE_VERSION and needs identical constructor options (state.js:215-225; starter.js:891-896), so snapshots break on engine upgrades and device changes unless the contract separates durable disk checkpoints from volatile suspend states. Devices are fixed at construction (no hot-plug). A disk or ISO API cannot take Kazo VFS paths for real images, because of the 16 MiB per-file limit.
- D15 'track processes': Kazo has no foreign-process API. StartChild has no argv, no exit code and no kill callback, and it throws in workers (AppProcessGateway.cs:42-48; RemoteGateways.cs:329-330). Interface launches will not appear in ps or the Task Manager, and cannot be killed with kill, without a Kazo ADR.
- Optional-dependency trap: a caller that declares Os-x86 as Optional with a MaximumVersion is DROPPED from the catalog when an out-of-range Os-x86 is installed; it does not receive null (AppCatalog.cs:487-525; HostAppCatalog.cs:82-130). kazopkg and the Store resolve no dependencies today (AppPackageInstaller.cs:591-657), so D12's resolver ADR is a precondition for 'installing BoxedWine installs Os-x86'.
