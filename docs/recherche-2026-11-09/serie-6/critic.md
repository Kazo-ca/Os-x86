# Critic of the series-5 analyses, and a series-6 proposal

**Abbreviations used below:**
- **J**: `docs/conception/journal-decisions.md`.
- **S5**: `docs/conception/serie-5-envoyee.md`.
- **A5**: `docs/conception/serie-5-reponses.md`.
- **K**: `.../repos/kazo-os/wasm2/KazoOs/`. I checked again today: the clone is still at cd89ee0 (2026-11-08 22:06 -0500).
- **KA**: `.../repos/kazo-os/doc/en/wasm/dev/adr/`.
- **RD**: `docs/recherche-2026-11-09/`.
- **CONS, KF, WF**: the consistency report, the Kazo facts report, and the Wine/Boxedwine/v86 facts report.

Nothing was written to the repository.

---

## 1. Claims checked

### 1.1 Errors

**E1 (KF, section c and "Effects" 1): wrong, and it matters.**
- KF says the shell "returns its exit code" for a window app, and that `boxedwine …` runs "with its exit code". A window app has **no exit code**.
- How the shell handles it:
  - The window route returns `WaitForExitAsync` (K/Platform/Kazo.Os.Platform.Core/Shell/ShellInterpreter.SimpleCommands.cs:289).
  - That method returns "0 when it ended without one (window closed)", or 1 if the process faulted (K/Platform/Kazo.Os.Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs:1232, 1259).
  - Ctrl+C returns 130 (SimpleCommands.cs:295).
- Exit codes are recorded only for commands and hsh scripts, through `Complete(pid, exitCode)` (AppLifecycleOrchestrator.cs:1335, 1370).
- A grep of `Shared/` finds `ExitCode` only in service types (K/Shared/Kazo.Os.AppSdk/ServiceProcess/*).
- CONS and RD/reponse-40/kazo-facts.md:48 have this right.
- Consequence: `boxedwine setup.exe` can never give the "code de sortie rendu" of D13 (J:64). Only a Terminal-kind entry point can: the binfmt route or `x86`.

**E2 (CONS 1.1 g; the 60 A rewrite): understated.** The `boxedwine` short name is weaker than CONS says.
- hsh resolves terminal commands before window short names (SimpleCommands.cs:158-168 come before :177-179).
- So any ordinary Terminal app that declares a command or alias `boxedwine` shadows BoxedWine.
- 60 A's rule that Feature commands go first (S5:166) cannot protect BoxedWine, because BoxedWine is no longer a Feature.
- A single-instance BoxedWine would also return 0 at once (SimpleCommands.cs:282-284).

**E3 (CONS 1.1 d and i): partial.**
- "The real Boxedwine UI configures containers" describes only the older ImGui UI in `source/ui`. Boxedwine's current WPF, Swift and Python UIs give each app its own private environment. They keep a `library.json` with `LastOpened` (WF a1, a4, a5), which is exactly the "fichier récent" of 51 D.
- CONS's "it scans the mounts for new .exe files" refers to `getNewApps`. That is the after-install finder, not a library scan (WF a3; local copy `.../scratchpad/wf6/bw_boxedContainer.cpp:221-226`).
- "No network setting" is true for the desktop UIs, but the web build takes a `network` URL parameter (WF a5).
- Low impact.

**E4 (CONS 1.1 g "Installation"; the 43 rewrite): incomplete.**
- CONS assumes that the dependency on Os-x86 "brings in an admin". Today, a personal install of an app whose required dependency is missing succeeds. The app is then dropped from the catalog with only a log line (K/Platform/Kazo.Os.Platform.Core/AppCatalog.cs:487-503; KF e).
- The dependency ADR (D12) must cover the case "ordinary app installed for one user, requiring a system-only Feature".
- KF caught this; CONS's change list and its Q68 do not.

**E5 (CONS 1.1 h): over-reading.**
- CONS writes: "Os-x86 has no user settings, so only BoxedWine can remember a choice".
- Os-x86 already stores per-user state: decisions (D23, D46), the default Linux (D24) and sudo (61.3).
- "Pas de paramètres utilisateur" rules out a settings window, not stored state. CONS says this itself in 1.1 d.

**E6 (CONS Q68 A, "a double-clicked console program opens in the Kazo Terminal"): unstated security gap.**
- This needs a new Terminal entry point that other apps can call with `apps.launch`, a normal permission.
- If that entry point accepts a command line, it bypasses `shell.host`, which is dangerous (K/Shared/Kazo.Os.AppSdk/Shell/KazoShell.cs:6-10).
- Today the Terminal declares no intent and no handler, and it is single-instance (K/Apps/System/Kazo.Os.Apps.Terminal/manifest.json).

**E7 (CONS Q68 A, third bullet): weakens decision 32 silently.**
- CONS puts "configuring the managed subsystem" under "lancer des programmes".
- Every app that holds that normal permission could then change the RAM, disk and network card of a subsystem that CONS's own Q67 A shares between the terminal and BoxedWine.
- This goes beyond 42 (J:178), because Os-x86, not the app, creates that subsystem.

### 1.2 Verified (the claims the series depends on)

**App kinds and the shell**
- Kind rules (AppManifestValidator.cs):
  - file handlers only on window apps (:318-321);
  - components only on window apps (:419-422);
  - context menus only on window or service apps (:473-476);
  - command metadata only on Terminal apps (:294-315).
- A window app is found by its short name, and the lookup fails if two apps share it (ShellCommandResolver.cs:61-82).
- The Terminal app (Terminal manifest.json):
  - id `org.kazoos.terminal`, a window app, single-instance;
  - declares `shell.host`, `intents:[]` and the component `kazoos.terminal`.
- The full-screen terminal session takes plain strings plus a cursor, with no attributes (K/Shared/Kazo.Os.AppSdk/FullScreenTerminalContracts.cs:58-85).

**Launches and file opening**
- `LaunchAsync` carries no sender (K/Platform/Kazo.Os.Platform.Core/Intents/AppIntentDispatcher.cs:209-220).
- Opening a file through an association (AppIntentDispatcher.cs:222-243):
  - it resolves by extension and launches with `[path]`, with no sender;
  - `ChooserRequired` goes back to the calling app as a status, with no dialog (:233-234).

**Start menu, icons and shortcuts**
- The Start menu launches with no arguments (K/Platform/Kazo.Os.Platform.Blazor/Shell/DesktopShell.razor:383).
- Pins hold only app ids, in `~/.config/kazoos/start-menu.json` (StartMenuPreferencesService.cs:7-16).
- Window icons:
  - A window icon comes only from the manifest (WindowLaunchCoordinator.cs:111-127).
  - The internal `SetWindowIconCommand` exists (K/Platform/Kazo.Os.Windowing.Abstractions/WindowRuntimeMessages.cs:112-118).
  - Child windows copy their owner's icon (K/Platform/Kazo.Os.Platform.Blazor/Windows/AppWindowGatewayFactory.cs:141).
  - `IAppWindowGateway` has no icon method (:35-89).
  - Changing the title needs no capability (WindowAppBase.WindowTitle.cs:53-70).
- In `.desktop` files, `Icon=` is parsed and written (KazoDesktopEntry.cs:72-90) but never drawn (ExplorerFileView.Desktop.cs:320).

**Install and dangerous permissions**
- Dangerous capabilities are decided at launch (AppLifecycleOrchestrator.cs:462-470).
- An admin "allow" goes to the machine file, keyed by app id (KA/0049 §4-5).
- No install code refers to these decisions: grep of `Apps/Packages` and `AppManagementGateway*`.
- A personal install needs no admin when the source is trusted (AppPackageInstaller.cs:46; KazopkgCommand.cs remarks, `--system`).

**Storage**
- Only Web-kind apps get a browser database (K/Shared/Kazo.Os.AppSdk/IAppExecutionContext.cs:371-376).
- An app may import its own JS module (K/Tools/Kazo.Os.Tools.ManifestGenerator/SdkBoundaryInspector.cs:5-9).

**Alpine (both fetched today)**
- Alpine's 3.24 virt x86 kernel has `CONFIG_BINFMT_MISC=m` (aports 3.24-stable, `main/linux-lts/virt.x86.config:96`).
- The `community/wine` APKBUILD of 3.24 (pkgver=11.0) never mentions binfmt. So Alpine's wine package most likely registers nothing, and Os-x86's agent would register it. This settles CONS's UNVERIFIED point at the APKBUILD level; I did not list the package contents.

**v86**
- `hda`/`hdb` are fixed when the VM is built, and the 9p backends are as WF describes (local copy `.../scratchpad/v86/src_cpu.js:1221-1247`).

**Journal and series-5 citations**
- These are correct as given: J:22, :64, :109, :133-147, :162, :174-180; S5:21-22, :34, :127, :138, :150, :166, :173-180.

---

## 2. Readings of the user's answers that the analysts got wrong or dismissed too fast

**R1. 51 D could mean "BoxedWine is the GUI of all of Os-x86".** CONS gives this reading (its A2) about 20%; that is too low.
- For it:
  - "une façon simple d'utiliser Os-x86" (A5:5) names Os-x86, not Wine.
  - "disque, machine, réseau" are VM settings (D15 J:66, D34 J:146), and no Boxedwine UI has them (WF a2: no RAM, disk-size or network settings).
- Against it:
  - 49 A as accepted kept "l'app Sous-systèmes" (S5:21);
  - the Wine-specific name.
- This should be a real blocking choice (Q63), not a footnote.

**R2. The literal wording of 53.**
- The user wrote: "Le terminal devra … avoir des modifications pour lui permettre d'utiliser Os-x86" (A5:7).
- Read literally, the Terminal becomes an Os-x86.Interface client, the way BoxedWine is.
- CONS dismisses this (its Q66 B) with "Kazo learns no product name". The user should be shown two things:
  - the generic ADRs (binfmt and raw mode) *are* "the terminal modifications";
  - the literal reading would ship a dependency on Os-x86 inside a Kazo system app, which contradicts 58 A's "aucun nom de produit codé en dur" (S5:138).

**R3. A uniform rule nobody stated.**
- Answers 49, 53 and 55 together imply a single rule: "Os-x86 renders; whoever started the program displays it".
  - BoxedWine displays the programs it launches.
  - The terminal displays the console programs it launches.
- CONS's Q64 A and Q68 A both break this rule.
  - Q64 A (an Os-x86 window for a graphical program started from the terminal) is still defensible, because Os-x86 keeps VM screens under 49 A. The question should say so.
  - Q68 A should be reversed. An Os-x86 console view inside BoxedWine's window follows "Os-x86 génère le rendu, BoxedWine l'affiche" and needs neither `shell.host` nor a Terminal launch entry (see E6).

**R4. The badge in 57.**
- The user's purpose is informational: "pour savoir que cette application sera ouverte avec BoxedWine" (A5:11), on shortcuts.
- CONS's Q73 A turns it into a security marker and extends it to window and taskbar icons, which goes beyond the user's words.
- A better fit: Kazo draws the small icon of the app the shortcut opens, on any shortcut or Start entry whose icon is not that app's own.
  - That app is known from `Exec=<appId>` (KazoDesktopEntry.cs:16-29), so the badge says exactly what the user wants and cannot lie.
  - BoxedWine then needs no image compositing.

**R5. "Déclarer un raccourci avec une icône" (A5:11) has two possible meanings.**
- It may mean a static declaration in the manifest, like Android app shortcuts.
- BoxedWine actually needs per-user entries created at run time.
- No analyst separated the two.

**R6. "Fermer une autre VM" (59 C).**
- 59 A's "mettre en veille ou arrêter" (S5:150) was not chosen.
- "Fermer" most naturally means stop. Suspend should come only where the milestone-0 measurement allows it (48, J:184).

**R7. "Os-x86 comme des DLL utilisées par BoxedWine" (A5:3) has a literal reading nobody raised.**
- Read literally, Os-x86's VMs would live inside BoxedWine's process.
- That contradicts three things:
  - 59 C ("ouvrir cette application dans une VM existante": VMs are shared);
  - 61.4 (S5:175);
  - the terminal path.
- I suggest recording the metaphor as: "BoxedWine calls Os-x86.Interface and embeds Os-x86's view. The VMs run in Os-x86's own worker, shared by every front end."

---

## 3. Ambiguities no analyst raised

**A1. 57 A conflicts with 52 and 60 A.**
- 57 A (accepted) says that BoxedWine "n'accepte qu'un numéro de sa bibliothèque et refuse un chemin" for any launch with arguments (S5:127).
- So "another app asks BoxedWine directly" with a file (52) and `boxedwine file.exe` (60 A) are both refused.
- They work only if the sender ADR of 52 lets BoxedWine tell an OS-stamped request apart from a plain launch or shortcut.
  - OS-stamped: opening a file, an intent, a command typed in a terminal the user opened.
  - A plain launch or a shortcut carries no sender (AppIntentDispatcher.cs:209-220).

**A2. How BoxedWine shows each program is undecided: one instance per program, or one instance with child windows?**
- Child windows take their owner's icon (AppWindowGatewayFactory.cs:141) and close with their owner.
- With a single instance:
  - `boxedwine x` returns 0 at once (SimpleCommands.cs:282-284);
  - a crash would end every program (61.4).
- D35's "un lancement = un processus Kazo" (J:147) points to one instance per program.

**A3. Choosing a bottle from the terminal.**
- 41 says "une option de terminal" opens the picker (J:177), and 61.5 forbids consent typed in the terminal (S5:176).
- A typed `--bottle=<name>` would give a new file a bottle's network decision through typed text.
- CONS's Q72 A ("`--bottle` with the warning of 41") leaves this open.

**A4. What "réseau" means in BoxedWine's configuration (51 D).**
- It must stop at the VM's network card and relay (D34).
- Granting network access to a program stays with Os-x86's window and rules page (49 A, S5:22; 61.2, S5:173).

**A5. What "créer une nouvelle VM" means (59 C).**
- It could be a second **persistent** subsystem, which changes D24's "un … par usager", or a throw-away instance.
- v86 disks are fixed when the VM is built (J:109), and a disk cannot be started twice with writes.
- So a program installed in VM 2 stays in VM 2.

**A6. A new VM is also a sandbox.**
- 61.9 says that programs in one VM share the display, keyboard and clipboard (S5:180).
- "Nouvelle VM" is therefore the only way to keep an untrusted program away from your other bottles.

**A7. 59 C also applies to ELF and DOS launches.** An "existing VM" must be compatible with the program:
- Windows programs: the minimum Wine version (44 A, J:180);
- DOS programs: FreeDOS (D28);
- ELF programs: a Linux of the same architecture.

**A8. "Ask the Shell" with Os-x86 alone.**
- Under Q62 A the terminal runs Windows programs, but opening a `.exe` through the association finds no handler (58 A).
- So another app cannot use the Shell route without BoxedWine. This is probably acceptable, but it should be stated.

**A9. Several VMs mean several sound streams and clipboards.**
- There is one SB16 stream and one guest clipboard per *running VM*, not per subsystem.
- Os-x86 must mix the sound streams (C38) and keep the clipboards apart (C37).

**A10. Os-x86's own program windows need icons too.**
- A window Os-x86 opens itself (Q64 A) needs the same icon API and the same in-guest icon extraction as BoxedWine.

---

## 4. Series-6 proposal (62-75, blocking questions first)

**62. Where does `./setup.exe` typed in the terminal go? (blocking)**

Context:
- Only a Feature may register a binfmt (38 A, J:174).
- BoxedWine stays an ordinary app, and "tout faire par ligne de commande avec seulement Os-x86" (51 D).
- The terminal shows what it launches (53, 55).
- `./x` never looks at file associations (K/Platform/Kazo.Os.Platform.Core/Shell/ShellInterpreter.Scripts.cs:26-128).

Options:
- **A (Rec.)** To Os-x86, through Kazo's generic binfmt.
  - The terminal shows console programs (raw-mode ADR) and gets the full exit code.
  - Os-x86 alone runs Windows programs from the command line: D7 is restored, and 53 A and 58 A bullets 2 and 5 change.
  - Wine's run recipe lives in Os-x86's image: guest binfmt_misc, the launcher and the base prefix. Os-x86.Interface stays generic (29).
  - "The terminal modifications" means the binfmt and raw-mode ADRs.
  - Double-clicking a `.exe` and the Shell route still need BoxedWine.
- **B** The shell hands Windows programs to the default `.exe` app (BoxedWine) through the associations. There is no exit code, and Os-x86 alone runs no Windows program.
- **C** The Kazo Terminal references Os-x86.Interface and calls it itself. This contradicts 58 A.

Reason: A is the only option that gives the console and the exit code (13) without making BoxedWine a Feature.

**63. What does BoxedWine's GUI cover, and what is left of Os-x86's? (blocking)**

Context: 49 A kept the Sous-systèmes app (S5:21), while 51 D says Os-x86 has no settings, no recent files and no configuration utility. Both hold only if that app has no configuration.

Options:
- **A (Rec.)**
  - **BoxedWine** covers Windows programs and the VMs that run them:
    - the library, recent files, per-program settings and bottles;
    - RAM, disk and network card of the "Programmes Windows" VMs.
  - BoxedWine gets a dangerous permission limited to those VMs.
  - **Os-x86** keeps:
    - the `x86` command, which does everything;
    - the security windows;
    - a thin Sous-systèmes app: list, start and stop, screens, delete, and no settings.
  - Linux, DOS and full-VM settings go through `x86` only in v1.
- **B** BoxedWine is the GUI of all of Os-x86, with "gérer les sous-systèmes". The Sous-systèmes app goes away (11 and 49 A change).
- **C** A in v1, B later.

Reason: A satisfies 49 A and 51 D together, and gives BoxedWine only the rights it uses.

**64. Who shows a graphical program started from the terminal? (blocking)**
- **A (Rec.)** A plain Os-x86 window, with the same view as BoxedWine's and the program's title and icon. This works with or without BoxedWine. The terminal waits and gets the exit code.
- **B** BoxedWine when it is installed (Os-x86 asks it by app id), otherwise A.
- **C** The Terminal opens a window that holds Os-x86's view. Closing the terminal closes the program, as on Linux.

Reason: A works with Os-x86 alone, Os-x86 already has VM screens (49 A), and the Feature never depends on an ordinary app.

**65. The same program through the terminal and through BoxedWine: one bottle or two? (blocking)**

Context: 50 A reuses a bottle only for the same user, subsystem and creating app (S5:34). Under 62 A, a double-click on a file you already ran from the terminal would create a second bottle, a second network question and a second install.

Options:
- **A (Rec.)** One bottle for your own launches.
  - Your own launches are: a terminal you opened, a double-click, or one of your shortcuts. They share one bottle per user, subsystem and SHA-256.
  - The app that relayed your launch may act in that bottle (42).
  - A launch requested by another app keeps its own bottle.
  - This requires 61.10.
- **B** Keep 50 A: separate bottles.
- **C** When BoxedWine is installed, the terminal asks it which bottle to use.

Reason: one program gets one bottle and one question, and other apps still cannot reuse your approvals.

**66. Several VMs (59 C): when do you choose, and what do the choices mean? (blocking for D24 and D35)**
- **A (Rec.)**
  - The question appears only when the program's own VM is stopped and another VM is running. A program that already has a bottle runs in that bottle's VM without a question.
  - "Fermer": stop the VM, with a warning about unsaved data. Suspend is offered only if milestone 0 shows it is fast enough.
  - "VM existante": a compatible running VM, only for a program that has no bottle yet. In v1 bottles stay in their VM's disk, because a shared folder is slow and two VMs on one bottle corrupt it (WF c4, d2).
  - "Nouvelle VM": a new persistent subsystem. It is also how you isolate a program you do not trust (61.9). It is greyed out when over the budget.
  - Os-x86 sets the budget: the guest RAM of running VMs within an admin limit.
  - BoxedWine asks for its own launches. An Os-x86 window asks for terminal launches; the command waits, and options can answer in advance.
- **B** Ask every time a VM is running.
- **C** Start silently when the budget allows, and ask only when it does not.

Reason: A asks only when there is a real choice, and it fits v86 and Wine.

**67. Where does a Windows console program opened by double-click appear?**
- **A (Rec.)** In a BoxedWine window holding a console view from Os-x86, the same view Os-x86 uses for a DOS `.com`. BoxedWine needs no `shell.host`.
- **B** As 55 A said: a Kazo terminal inside BoxedWine's window. This needs `shell.host` (dangerous), approved by an admin at BoxedWine's first launch.
- **C** In a Kazo Terminal tab opened by Os-x86 with a launch handle, never with a command line.

Reason: A applies "Os-x86 génère le rendu, BoxedWine l'affiche" with the fewest rights.

**68. What can `x86` do with Windows programs?**
- **A (Rec.)** It can:
  - run a file;
  - choose a bottle (the option opens Os-x86's picker window, never a typed bottle name, per 61.5);
  - list and delete bottles;
  - revoke decisions;
  - run a command or a shell in a bottle.
  
  It counts as you in a terminal you opened, and as the app otherwise (52).
- **B** Run files, and list and delete bottles, only. 58 A bullet 3 is kept.
- **C** A, but commands and shells only from a terminal you opened.

Reason: "tout faire par ligne de commande avec seulement Os-x86".

**69. The Kazo ADR for icons, Start entries and the BoxedWine mark.**
- **A (Rec.)** One generic ADR:
  - **Window icon:** any app can change its window icon at run time with no permission, as with the title. Child windows are included and the taskbar follows. The image is a package asset or a PNG the app supplies, with a size cap.
  - **Start entries:** with a new normal permission, an app can add per-user Start entries (name, icon and an opaque number). The OS stores them, removes them at uninstall, and makes them searchable and pinnable.
  - **Desktop shortcuts:** Kazo draws the `Icon=` of `.desktop` files, which it ignores today.
  - **The mark you asked for:** Kazo draws, bottom right, the small icon of the app the shortcut opens, on every shortcut or Start entry whose icon is not that app's own.
- **B** Exactly as you wrote: BoxedWine composes its icon into each shortcut's PNG, and Kazo draws no mark.
- **C** A, plus marks on window and taskbar icons changed at run time.

Reason: A gives the same visual result as you asked, and another app can neither fake nor remove "opens with BoxedWine".

**70. "Ajouter une application à Kazo OS": where do you browse, and what does the library show?**
- **A (Rec.)**
  - **Where you browse:**
    - BoxedWine browses inside its own bottles (installed programs and Start Menu `.lnk` files); 42 already lets it.
    - Kazo's file picker is used for a portable `.exe`, which stays where it is.
    - Os-x86's picker is used for a bottle BoxedWine does not own.
  - **Untrusted data:** Wine reads the `.lnk`, the name and the icon inside the guest, as untrusted data, never in Kazo.
  - **Entries:** the Start entry is required and the desktop shortcut optional.
  - **The library:** shows added programs, plus the programs found in your bottles, marked "pas dans Kazo".
  - **Removal:** deleting the program, its bottle or BoxedWine removes its entries.
- **B** Added programs only, browsing Kazo files only.
- **C** A, plus BoxedWine offers the new programs after each installation, as the real Boxedwine does.

Reason: Kazo's Files never see inside a bottle (J:162), and a hostile `.lnk` stays in the guest.

**71. Technical choices (one bundle, accept all).**
1. Os-x86's entry points count as one app for its data and bottles.
2. Os-x86 classifies DOS, Windows, console or graphical from the bytes it reads for the hash.
3. "Pas de paramètres utilisateur" means no settings window. Os-x86's per-user state changes through `x86` and Os-x86.Interface, and the rules page stays.
4. BoxedWine's network settings cover only the VM's network card and relay. They never grant anything.
5. BoxedWine is a window app that is not single-instance: one instance per running program, and the library is an instance without a program. A crash ends that program only.
6. The VMs run in Os-x86's own worker, shared by every front end. Closing BoxedWine never stops a VM.
7. After the sender ADR, BoxedWine accepts a file path only from an OS-stamped request. A shortcut or a plain launch carries only a library number.
8. `boxedwine file.exe` behaves like a double-click and returns 0 when its window closes. The full exit code comes only from `./file.exe` or `x86`. 60 A's precedence protects only `x86`.
9. BoxedWine's `network.real.access` gets admin approval at its first launch, not at install (43 changes).
10. The dependency ADR (12) covers a personal install that requires a system-only Feature.
11. A new image is offered by BoxedWine, and by Os-x86 at the next terminal launch. The Wine image is downloaded at the first Windows program, whichever way it was launched.
12. BoxedWine keeps its library, recent files and settings in its own per-user browser storage, through its own JS module, never in `~/.config`.
13. Os-x86's agent registers Wine in the guest's binfmt_misc.

**72. (C36) Linux subsystems in the terminal.** This no longer depends on an open question.
- **A (Rec.)**
  - `x86 list|start|stop|shell <name>|run <name> -- <cmd>`, and `x86` alone opens a shell in your default Linux.
  - The shell goes through Os-x86's agent (a real pty), with the serial console as a fallback.
  - A "Terminal" button sits next to each Linux subsystem, in whichever app lists them (Q63). The Terminal component already exists (RD/reponse-40/ui-theme.md:146-148).
- **B** Only wsl-style short options.
- **C** Serial console only.

**73. (C37) Clipboard.**
- **A (Rec.)**
  - Text in both directions through the agent, only while a guest window has focus.
  - One guest clipboard per running VM.
  - Capped by the app that shows the view: BoxedWine and Os-x86's windows declare `clipboard.read` and `clipboard.write`.
  - A small amendment to ADR 0065 retries the write to the real computer at the next gesture.
  - Images and files later.
- **B** Continuous sync.
- **C** Text, images and files in v1.
- **D** No clipboard in v1.

**74. (C38) Sound.**
- **A (Rec.)**
  - SB16, one stream per running VM, mixed by Os-x86 (an AudioWorklet fed through shared memory, with a fallback through messages).
  - A mute button in every view, which mutes the whole VM.
  - Volume per subsystem, set through `x86` and BoxedWine.
  - Sound is a device of the profile, off by default if milestone 0 finds it too costly.
- **B** One stream per program.
- **C** No sound in v1.
- **D** Kazo SDK sound API first.

**75. (C39) Mouse and keyboard.**
- **A (Rec.)**
  - Program windows: absolute mouse, no capture.
  - VM screens: a click captures mouse and keyboard, with the escape combination Ctrl + right Alt, and a Kazo ADR "a window may hold the keyboard".
  - The guest keyboard layout is a per-subsystem value set through `x86` and BoxedWine, detected automatically in Chromium. It is not an Os-x86 setting.
- **B** The same, without the Kazo ADR.
- **C** Capture everywhere.

**Questions that should wait:**
- **C33 (creating a subsystem):** wait. It depends on Q63 (which GUI creates subsystems) and Q66 ("nouvelle VM" as a new subsystem). The draft at RD/reponse-40/ui-theme.md:101 puts creation "dans l'app Sous-systèmes", which conflicts with 51 D under Q63 A. 59 C replaces its VM-limit part.
- **The second Windows front end and the export of a single bottle (J:228):** wait for Q65.

CONS's list of decisions to rewrite is otherwise sound. Correct it as follows:
- **55 A double-click console:** it follows Q67, not CONS's "Kazo Terminal".
- **60 A:** add E2.
- **D13:** "code de sortie rendu" holds only for the binfmt and `x86` routes (E1).

**UNVERIFIED:**
- The contents of Alpine's wine package (I checked only its APKBUILD).
- The v86 issue #1451 timings (WF c4), which I did not re-run.
- Whether the window manager accepts icons that are not package assets.
- Whether a Feature can provide a component whose type comes from Os-x86.Interface.
- The `navigator.deviceMemory` limits in each browser.
- Whether child windows close with their owner: I took this from KF (KA/0234:83-99) and did not re-read it.