# Consistency map of the series-5 answers (49-61)

**Sources.**
- J = `docs/conception/journal-decisions.md`
- S5 = `docs/conception/serie-5-envoyee.md`
- A5 = `docs/conception/serie-5-reponses.md`
- RD = `docs/recherche-2026-11-09/`
- K = `.../repos/kazo-os/wasm2/KazoOs/` and KA = `.../repos/kazo-os/doc/en/wasm/dev/adr/`. I checked again today: the clone is still at cd89ee0 (2026-11-08 22:06).
- Real Boxedwine = github.com/danoon2/Boxedwine, master, last pushed 2026-11-09.

**What the user wrote, as anchors:**
- 49 (A5:3): "Os-x86 génère le rendu, boxedWine l'affiche … comme le bureau et les fenêtres … Os-x86 comme des dll utilisé par BoxedWine."
- 51 D (A5:5): BoxedWine "reste une application qui utilise la feature Os-x86". It is "une façon simple d'utiliser Os-x86". "On pourrait tout faire par ligne de commande avec seulement Os-X86". Os-x86 has no user settings, no recent files, and no utility to configure disk, machine or network.
- 52 (A5:6): another app asks BoxedWine directly, or asks "le Shell", which goes through the extension-association registry.
- 53 (A5:7): "Quand le terminal utilise la fonctionalitée Os-x86, il s'occupe aussi d'afficher le rendu". The terminal must be modified.
- 55 (A5:9): "Lancé depuis le terminal: Il s'occupe aussi de rendre l'application si c'est une application console."
- 57 (A5:11) and 59 C (A5:13): see 1.1 (h) and (i).
- Everything else: "d'accord" (A5:15).

---

## 1. Readings

### 1.1 Most faithful reading (M): about 50% (my judgment)

The user's model matches Windows and WSL:
- Os-x86 is the subsystem: the engine plus a CLI like `wsl.exe`.
- The terminal plays the role of conhost.
- BoxedWine is a GUI front end, like Bottles over Wine or VirtualBox Manager over VBoxManage.
- "Le Shell" is ShellExecute over the association registry.

| Piece | Role under M | Grounds |
|---|---|---|
| **Os-x86** (Feature, admin, system-wide) | Engine: VMs, the image with Wine, bottles, network permissions with the prompt, rules page and bottle picker. It **renders** through the guest-view component. It owns the binfmt for every x86 format (pure MZ, MZ+PE/NE, ELF, plus extension rules for `.com`, `.bat`, `.cmd`, `.msi`) and the `x86` command, which does everything without BoxedWine. It has minimal windows (VM screen, plain program window) and the security windows. It has **no** preferences window, no recent files and no configuration utility. | 51 D; 38 A (J:174); security part of 49 A (S5:22); 50 A |
| **Kazo terminal** (modified) | Shows console programs and DOS text mode in its tab (raw mode) and hands the file to Os-x86. | Precisions of 53 and 55; D27 (J:139); D28 (J:140) |
| **BoxedWine** (ordinary Window app, required dependency on Os-x86, D7 J:21) | GUI: the `.exe`/`.msi`/`.bat`/`.cmd` association (double-click and the ShellExecute route), "ajouter une application à Kazo OS", library, recent files, user settings, disk/machine/network configuration. It **displays** the programs it launches by embedding Os-x86's view, and drives Wine settings with guest commands (29 C, J:141). | 49, 51 D, 52, 57 |
| **Other apps** | `LaunchAsync` or an intent to BoxedWine, or `OpenFileAsync` → association → the default `.exe` handler. The OS stamps the sender (ADR of 52 A). | 52 A (S5:58-62); RD/kazo-facts.md:26-31, 50-52 |

**(a) Terminal path.** Under M, `./setup.exe` goes to **Os-x86 directly**, not to BoxedWine. This reverses:
- my reading of 40 (J:190-191);
- the terminal items of 49 A (S5:20), 51 A (S5:49), 53 A ("un programme Windows va à BoxedWine", S5:71) and 55 A (S5:99).

Grounds:
- 38 A forbids a binfmt to an ordinary app (J:174). The user did not pick 51 C, which would have relaxed that rule (S5:52).
- The precisions of 53 and 55 put the terminal, not BoxedWine, in charge of showing what is launched from it.
- Kazo fact: `./x` never looks up file associations (K Platform/Kazo.Os.Platform.Core/Shell/ShellInterpreter.Scripts.cs:26-128). The association route (OpenFile, `xdg-open`) drops the shell's streams and returns 0 at once (RD/kazo-facts.md:46-47). A window app typed by name gets the streams but always exits with status 0 (RD/kazo-facts.md:48). So "terminal → association → BoxedWine" would lose the console and the exit code.

**(b) Os-x86 alone and Windows programs.** Under M, yes. This restores D7's "grâce à elle, l'OS exécute des .exe dans le terminal" (J:22). It contradicts bullets 2, 3 and 5 of 58 A (S5:139-142). The user accepted 58 A only through "pour le reste" (A5:15), while 51 D is explicit. The explicit wording should win, but Q62 confirms it.
- This does not force Wine types into Os-x86.Interface. Alpine 3.24's virt x86 kernel has `CONFIG_BINFMT_MISC=m` (github.com/alpinelinux/aports/blob/3.24-stable/main/linux-lts/virt.x86.config#L96). Debian ships `wine-binfmt`, "Register Wine as the interpreter for Windows executables" (packages.debian.org/stable/wine-binfmt, 10.0~repack-6).
- So a guest-side registration in Os-x86's image can make a generic "run this file in this bottle" call start Wine. Whether Alpine's own wine package registers binfmt_misc is UNVERIFIED.

**(c) Who shows a graphical program started from the terminal.**
- **Console program:** the terminal tab. Os-x86's command is attached to the shell's streams and needs the raw-mode ADR.
- **Graphical program:** the answer does not say. The precision of 55 limits the terminal to console programs ("si c'est une application console").
  - Without BoxedWine, only Os-x86 can show it; it keeps VM screens under 49 A (S5:21).
  - With BoxedWine, "BoxedWine l'affiche" would have the Feature call into an ordinary app. That is a reverse dependency, for example through an intent BoxedWine declares. Intent ids are not tied to the declaring app (RD/kazo-facts.md:105), so the call must name BoxedWine's app id.
- The same question applies to a DOS program that switches to VGA graphics. Q64 asks it.

**(d) What remains of Os-x86's own GUI.**
- 49 A kept "l'app Sous-systèmes" (S5:21; D11 J:62). 51 D says Os-x86 has no settings, no recent files and no configuration utility. Both hold only if the Sous-systèmes app shrinks to: list, start and stop, VM screen, delete, and the security pages (D23 J:135). Configuration would then go through `x86` and BoxedWine.
- The real Boxedwine UI configures containers:
  - container settings: name, location, Wine filesystem version, renderer, video memory, mouse warp, drive-letter mounts, programs;
  - an "add app" button;
  - per-app settings: resolution, color depth, full screen, CPU affinity, scale, OpenGL;
  - sources: `source/ui/controls/containersView.cpp:27-116, 207-232, 286-470`. A grep found no network setting.
- So "disque, machine, réseau" matches Os-x86's VM device profile (D34 J:146: RAM, network card on or off, sound, display, shares) better than Boxedwine's containers. That suggests BoxedWine configures VMs, at least the ones that run Windows programs. Whether it also configures Linux, DOS and full VMs is open (Q63).
- Under M, Os-x86 shows a Linux desktop or a full VM screen. Under A2 (1.2), BoxedWine does.
- "Pas de paramètres utilisateur" cannot mean "no per-user state". Os-x86 already keeps per user:
  - the default Linux subsystem (D24 J:136);
  - network decisions (D23);
  - "même sans session" (D45 J:181);
  - sudo per subsystem (61.3, S5:174);
  - device profiles (D34).
- These would change through `x86` and through Os-x86.Interface (driven by BoxedWine's GUI), never in an Os-x86 settings window.

**(e) Where the Wine logic lives.** Under M, the run recipe lives in the image, maintained by Os-x86. It would contain:
- a binfmt_misc entry in the guest;
- the launcher `.exe`, already in the image under 55 A (S5:101);
- the base prefix, Mono and Gecko (61.6, S5:177);
- the Wine patches (61.8, S5:179).

What follows:
- Os-x86.Interface stays generic (29 C).
- BoxedWine drives Wine settings (winecfg, the registry) with guest commands.
- Os-x86 creates the identity and chooses the bottle (50 A). The image's recipe creates the prefix at the first run in that identity.
- "Os-x86 comme des DLL" fits: Os-x86.Interface and the guest-view component run inside BoxedWine's process. An embedded component runs with the host app's rights (KA/0063:60-67, cited in RD/reponse-40/ui-theme.md:30).
- J:151's replacement note ("BoxedWine … porte la logique Wine") becomes wrong. Q65 asks.

**(f) The same `.exe` gets two bottles: problem and proposed fix.**
- 50 A reuses a bottle only for "même app qui a créé la bouteille" (S5:34).
- Under M, a terminal launch creates its bottle through Os-x86 with the user as caller (D21 J:133). A double-click creates it through BoxedWine, capped by the user and BoxedWine together (52 A, S5:61).
- So the same `setup.exe` gets two bottles, two network prompts and two installs. BoxedWine cannot see the terminal's bottle (61.12, S5:183).
- **Proposed fix:** key reuse on who started the launch, not on the app that relays it.
  - Launches the OS stamps as the user's share one bottle per (user, subsystem, hash), whichever door was used. These are: a command typed in a terminal the user opened, a double-click in Files or on the desktop, and a library shortcut without arguments (52 A, S5:58).
  - The relaying app gets "lancer des programmes" access to that bottle (42) for that launch and for its library.
  - A launch requested by another app keeps per-app bottles, as trust-split condition 6 wanted (RD/reponse-40/trust-split.md:75).
  - It requires 61.10 (changing the default handler needs your confirmation, S5:181), so an app cannot silently become the relay. Q67 asks.

**(g) BoxedWine as an ordinary app.**
- **One kind only:** Window. Only window apps may declare file handlers (K Shared/Kazo.Os.App.Abstractions/AppManifestValidator.cs:318-321). Context-menu entries are allowed for Window or Service apps (RD/kazo-facts.md:56).
- **No binfmt** (38 A) and no command entry point. The `boxedwine` name from 60 A (S5:162) survives only as the window app's short name:
  - the shell finds a window app by the last segment of its id or by its full id, and gives up when several apps share that segment (K Platform/Kazo.Os.Platform.Core/Shell/ShellCommandResolver.cs:61-82);
  - the exit status is always 0 (RD/kazo-facts.md:48);
  - the "Feature commands go first" rule of 60 A (S5:166) no longer protects it.
- **Installation:** any user can install it from a trusted source without an admin, or an admin installs it for everyone with `--system` (K Apps/Commands/Kazo.Os.Commands/Kazopkg/KazopkgCommand.cs:17-21). Its required dependency on Os-x86, a Feature only an admin installs, brings in an admin when Os-x86 is missing (D12 resolver).
- **Dangerous permissions:**
  - `network.real.access` (43 A, J:179). A non-system app gets a dangerous capability only **at launch**, from an admin `allow` line (RD/kazo-permissions-shell.md:10-13). The install approval writes no allow (:30-33). So "approbation admin à son installation" becomes "at its first interactive launch", and 39 A's install consent (J:175) no longer covers BoxedWine.
  - `shell.host`, only if BoxedWine embeds a terminal for a double-clicked console program (55 A, S5:100). The alternative is to open the modified Kazo Terminal. Today that app declares no intent and no file handler and is single-instance per user (K Apps/System/Kazo.Os.Apps.Terminal/manifest.json), so this rides on the terminal change the user already asked for.
  - "gérer les sous-systèmes" (32 A, J:144), as soon as its configuration utilities touch a subsystem it did not create. That includes the Programmes Windows subsystem Os-x86 owns (54 A, S5:83-84), unless a narrower rule is added (Q68).
- **Effects on 42, 50 and 61.12:**
  - 42 (J:178) becomes simpler for BoxedWine: one app id, so the child-id problem of RD/reponse-40/critic.md:135-143 disappears. It stays open for Os-x86's own entry points, because 51 D dropped the line of 51 A that covered Os-x86 too (S5:50).
  - 50 A: see (f).
  - 61.12 becomes: "the bottles of your launches are yours; an app sees only the bottles it relayed or that you gave it through the picker."

**(h) Several VMs (59 C).**
- **Two readings, not exclusive:**
  - (h1) a choice dialog when a launch meets a running VM;
  - (h2) running VMs are always visible to the user, with those actions.
- **When:** the sensible trigger is "the program's own VM is not running and another one is". When its VM already runs, it runs there with no question.
- **Which "VM existante" can host the program:**
  - a Windows program needs an image with Wine at or above the minimum version (44 A, J:180);
  - a DOS program needs FreeDOS (D28; Wine no longer runs DOS, J:80);
  - an ELF program needs a Linux of the same architecture;
  - the user's own Linux has no bottles (58 A bullet 4, S5:141), unless Os-x86 adds its Wine runtime to it.
- **Where bottles live:**
  - Today they are inside the managed subsystem's disk (D24, J:136).
  - v86 devices are fixed when the VM is built (J:109), so a bottle stored as its own disk cannot be attached to a running VM. It could only be reached through a share, as already assumed for D22 (RD/wine-display-console.md:144). How a Wine prefix behaves on such a share (speed, per-bottle guest users) is UNVERIFIED.
  - Wine runs one wineserver per (guest user, prefix) (RD/critique.md:38). A bottle must therefore never be open in two VMs at once, and Os-x86 must lock it.
  - "Créer une nouvelle VM" means a new instance with its own disk: one disk cannot be started twice with writes.
- **"Si les ressources le permettent":** Os-x86 decides.
  - WebAssembly memory comes back only when the worker ends, and each VM holds its guest RAM (RD/critique.md:210). A budget on the guest RAM of running VMs, with an admin cap, follows 35 A (RD/critique.md:212).
  - Browser memory hints are coarse and not available in every browser (UNVERIFIED today).
- **Who shows the dialog:**
  - BoxedWine, for its own launches: it is the GUI and reads the data from Os-x86.Interface.
  - An Os-x86 window for terminal launches, since 59 A said "jamais de question dans le terminal" (S5:151). 61.5 (S5:176) is about consent, but the same fake-question risk applies. Command options can answer in advance.
  - Os-x86 has no user settings (51 D), so only BoxedWine can remember a choice. In the terminal, options or an environment variable replace it.
- **Effects:** D24 becomes "one or more Programmes Windows subsystems". D35's "une VM en marche par session" (J:147) becomes a resource budget. The suspend and refusal bullets of 59 A (S5:150-153) go away. Whether "fermer" means stop or suspend is open.

**(i) Library, Start menu and icons (57).**
- **Kazo facts:**
  - The app icon is declared in the manifest, in `presentation.iconAssetPaths` (K Shared/Kazo.Os.App.Abstractions/AppManifestPresentation.cs:25-29).
  - The Start menu is built from the catalog's manifests (K Platform/Kazo.Os.Platform.Blazor/Shell/AppLauncher.razor.cs:352), plus per-user pins of app ids in `~/.config/kazoos/start-menu.json` (K Platform/Kazo.Os.Platform.Core/Shell/StartMenuPreferencesService.cs:7-10). There is no per-program entry.
  - The window manager has an internal `SetWindowIconCommand(WindowId, IconAssetPath)` (K Platform/Kazo.Os.Windowing.Abstractions/WindowRuntimeMessages.cs:118). It is only used to apply the manifest icon (K Platform/Kazo.Os.Platform.Blazor/Windows/WindowLaunchCoordinator.cs:119-125).
  - The SDK only offers `SetWindowTitleAsync`, which needs no capability (K Shared/Kazo.Os.AppSdk.Blazor/WindowAppBase.WindowTitle.cs:55-71).
  - The internal command takes a package asset path, so an icon extracted at run time needs a new form. Whether the window manager accepts such images is UNVERIFIED.
  - Desktop shortcuts are `.desktop` files with `Exec=<appId> [args]` and an optional `Icon` (K Shared/Kazo.Os.AppSdk/Files/KazoDesktopEntry.cs:7-29; KA/0152-desktop-icons.md:92-103). A shortcut whose app is uninstalled is greyed out (:102-103).
  - No Kazo code handles `.lnk` files (grep).
- **Library: added or discovered?** 57 A's text discovered programs (S5:125). The precision adds them explicitly, by browsing to an `.exe` or a `.lnk`. The real Boxedwine does both:
  - it scans the mounts for new `.exe` files (`source/ui/data/boxedContainer.cpp:222-230`);
  - it has a scan of the prefix's Start Menu `.lnk` files, currently commented out (:223, :442-449);
  - it then has an "add app" button (containersView.cpp:207-232).
- **Where the user browses:**
  - Installed programs and their `.lnk` files are inside bottles, which Kazo's Files can never see (J:162). Browsing there needs Os-x86's picker (S5:36). Picking a file there counts as "choisie par elle" (S5:39).
  - A portable `.exe` in the Kazo files stays where it is and is passed at launch (D22 J:134). 50 A forbids writing executables into bottles (S5:37).
- **Badge:**
  - The user asked BoxedWine to add its icon to shortcut PNGs.
  - A Windows program sets its window title (55 A, S5:102) and, after the ADR, its icon. It could imitate a Kazo window, including Os-x86's network prompt, which is an ordinary Os-x86 window (J:105).
  - A badge on window and taskbar icons protects only if Kazo draws it: an app can leave out a badge it draws itself.
  - `.desktop` files are plain files that any app with a normal permission can write (J:195-196), so for them only BoxedWine's own badge is possible.
- **Per user:** Start entries are per user, like bottles (61.11, S5:182). They must not be stored in `~/.config`, which another app can write with a normal permission (J:196).
- **Removal:** when the program or its bottle is deleted (54 A, S5:85), when BoxedWine is uninstalled, and when the bottle is deleted in Os-x86 while BoxedWine is absent.
- **Icon extraction:** reading icons out of PE resources means parsing untrusted data. It is safer in the guest, with a tool from the image, than in .NET code inside Kazo's page.

### 1.2 Alternative readings

- **A1 (about 20%).** BoxedWine still receives every Windows program, terminal included. This reads 52's "le shell passe par le registre" as describing the shell's own `./x`.
  - The terminal shows the console of what BoxedWine starts, and 58 A stays as written. "Tout faire en ligne de commande" then covers only generic subsystems and Wine run by hand.
  - Cost: a new shell route "header → association" that keeps the streams and the exit code. Kazo has no exit code for window apps (RD/kazo-facts.md:48) and no API to hand a stream to another app (:91).
- **A2 (about 20%).** BoxedWine is the GUI of all of Os-x86: Linux, DOS, full VMs, disks, machine, network, screens. Os-x86 keeps its CLI, the security windows and the view component, and the Sous-systèmes app goes away.
  - For it: "façon simple d'utiliser Os-x86" and the configuration list.
  - Against it: 49 A as accepted (S5:21) and the Wine-specific name (47 B).
- **A3 (about 10%).** In 55, "il" means BoxedWine: BoxedWine renders console programs launched from the terminal. This is A1 for console programs.

---

## 2. Earlier decisions whose text must change

"[Q62]" means the change depends on that series-6 answer.

- **D7 (J:16, J:22) and the reading of 40 (J:190-191):**
  - Remove "la décision 7 change (Os-x86 seul ne lance plus de .exe)".
  - Restore: "Os-x86 runs Windows, DOS and Linux programs from the terminal by itself; BoxedWine is an ordinary app that depends on it and adds a GUI."
  - Replace the "lecture retenue" with M. [Q62]
- **D9 (J:60):** no change in substance. "Os-x86 lance Wine" becomes literally true again for terminal launches.
- **D11 (J:62):** "app « Sous-systèmes »" becomes "app Sous-systèmes without settings or configuration (list, start/stop, screen, delete, security pages); configuration through `x86` and BoxedWine". [Q63]
- **D13 (J:64):**
  - "la fonctionnalité" means Os-x86 for every format, with extension rules.
  - "Graphique : une fenêtre Kazo" becomes "an Os-x86 window (launched from the terminal) or a BoxedWine window (launched by BoxedWine)".
  - The install message follows 58 A. [Q62, Q64]
- **D21 (J:133):**
  - Terminal: the user, as stamped by the OS.
  - Double-click or BoxedWine shortcut: the user and BoxedWine together.
  - Another app through BoxedWine or the Shell: that app and BoxedWine together.
- **D24 (J:136):**
  - "un sous-système … par usager" becomes "one or more" (59 C).
  - "créé au premier .exe" becomes "from the terminal or from BoxedWine".
  - "non modifiable" becomes "settings changed through `x86` and BoxedWine" (51 D). [Q63, Q69]
- **D26 (J:138):** add whose window: BoxedWine for its own launches, Os-x86 for terminal launches. [Q64]
- **D27 (J:139):** add "the terminal shows the console of programs launched from it (53, 55)". The modification is the binfmt ADR plus the raw-mode ADR. [Q66]
- **D29 C (J:141) and the note at J:151:** the note becomes "Wine's run recipe lives in Os-x86's image (guest binfmt_misc, launcher, base prefix); Os-x86.Interface stays generic; BoxedWine drives Wine settings with guest commands." [Q65]
- **D32 (J:144) and D42 (J:178):**
  - add bottles of launches the user started, shared between the doors [Q67];
  - add a rule for configuring the managed subsystem [Q68].
- **D33, D37, D38, D39 (J:145, J:173-175):**
  - BoxedWine is not a Feature.
  - 39's install consent covers only Os-x86.
  - Child ids (37) exist only for Os-x86.
  - Add that Os-x86's entry points count as one app for ownership. [Q75]
- **D35 (J:147):**
  - "une VM en marche par session" becomes "several VMs within a budget computed by Os-x86, with an admin cap".
  - "Un lancement = un processus Kazo" still holds if Q66 A.
- **D41 (J:177):** "Propriétaire du sélecteur : à revoir" becomes "Os-x86's picker (50 A); the terminal option is an option of `x86` and of the binfmt launch".
- **D43 (J:179):** "approbation admin à son installation" becomes "admin approval at the first interactive launch (Kazo's usual gate for a non-system app)".
- **49 A (S5:20-21):**
  - Remove "terminal" from BoxedWine's entry points.
  - "Consoles des programmes" applies only to the double-click.
  - Add the precision about rendering and display.
  - The Sous-systèmes app changes as in D11.
- **50 A (S5:34, S5:39):** replace "même app qui a créé la bouteille" with the rule on who started the launch [Q67]. "Choisies par elle" also covers "Ajouter une application".
- **51:** record D. BoxedWine is an ordinary Window app with a required dependency on Os-x86, no binfmt and no command entry point. 51 A's "les points d'entrée comptent comme une seule app" survives only for Os-x86 [Q75].
- **52 A (S5:58-62):**
  - Add the ShellExecute route (`OpenFileAsync` → association → default handler).
  - The sender-stamping ADR covers OpenFile and launches from apps.
  - A command an app runs in a terminal now reaches Os-x86 directly and counts as that app.
- **53 A (S5:71-74):**
  - "un programme Windows va à BoxedWine" becomes "goes to Os-x86 and is shown in the terminal".
  - In the terminal, ".bat, .cmd, .msi → BoxedWine" becomes "→ Os-x86".
  - "Avec Os-x86 seul … pas au double-clic d'un .exe" becomes "with Os-x86 alone, everything works in the terminal, Windows programs included; `.exe` does nothing on double-click (58 A)". [Q62]
- **54 A (S5:83-87):**
  - "créé à la première demande de BoxedWine, « géré pour BoxedWine »" becomes "created at the first request (terminal or BoxedWine), managed by Os-x86 for you".
  - Its settings can change [Q63, Q68].
  - Uninstalling BoxedWine leaves the bottles of your own launches usable from the terminal; only bottles started by apps are marked "app désinstallée" [Q67].
  - Add several VMs.
- **55 A (S5:97-103):**
  - "BoxedWine lit l'en-tête" becomes "Os-x86 classifies the file from the bytes it reads to compute the hash".
  - A graphical program launched from the terminal opens in an Os-x86 window [Q64].
  - A double-clicked console program opens in the Kazo Terminal instead of a terminal embedded in BoxedWine, so BoxedWine needs no `shell.host` [Q68].
  - The taskbar shows the program icon with a badge (57).
  - The rule that a Feature may provide the component stays, for Os-x86.
- **56 A (S5:111), rule 2:** Os-x86 also offers the new image, at the next terminal launch.
- **57 A (S5:125-129):**
  - Replace with the precision: "Ajouter une application à Kazo OS" (`.exe` or `.lnk`), a required Start-menu entry, an optional desktop shortcut, a Kazo ADR for per-window icons (the taskbar follows) and per-program Start entries, and the badge.
  - Delete "pas d'entrée … menu Démarrer en v1".
  - Keep "seulement un numéro de bibliothèque" (S5:126-127).
- **58 A (S5:139-142):**
  - Bullet 2 is deleted [Q62].
  - Bullet 3 depends on Q72.
  - Bullet 5 becomes "the image is downloaded at the first Windows program, from the terminal or BoxedWine".
  - Bullet 1 then applies to the double-click only.
- **59 (S5:150-153):** record C. The suspend and refusal bullets are replaced.
- **60 A (S5:162-166):**
  - `boxedwine` becomes the short name of BoxedWine's window app: no exit code, and the last segment of its id must be unique among window apps.
  - Feature precedence protects only `x86`.
  - The binfmt formats are tied to Os-x86's id.
- **61 A:**
  - 61.1 (S5:172): an ordinary app has no SDK API for browser storage (grep). It reaches it only through its own JS module, which is allowed (K Tools/Kazo.Os.Tools.ManifestGenerator/SdkBoundaryInspector.cs:5-9). BoxedWine separates users itself.
  - 61.4 (S5:175): "commande ou fenêtre de BoxedWine" becomes "BoxedWine's window, Os-x86's command or the terminal tab".
  - 61.12 (S5:183): depends on Q67.
- **Series-6 drafts that conflict with 51 D:**
  - RD/reponse-40/ui-theme.md:101 (C33 A: profiles and an advanced mode "dans l'app Sous-systèmes");
  - ui-theme.md:357 (C39 A: "un réglage d'Os-x86" for the keyboard layout).

**Unchanged:** D8, D10, D12, D14-D20, D22, D23, D25, D28, D30, D31, D34, D36, D44-D48. D36 changes only if Q66 B.

---

## 3. Series 6 questions (blocking first)

**62. Where does a Windows `./setup.exe` typed in the terminal go? (blocking)**
- **A)** To Os-x86 through its binfmt. The terminal shows console programs. BoxedWine is not involved, and Os-x86 alone runs Windows programs from the command line (D7 restored; 53 A and 58 A change).
- B) The shell reads the header and passes Windows programs to the default `.exe` app (BoxedWine) through the association registry. Os-x86 alone runs none (58 A kept).
- C) A when BoxedWine is absent, B when it is present.
- **Rec A.** It matches 51 D, the precisions of 53 and 55, and 38 A without making BoxedWine a Feature.

**63. What does BoxedWine's GUI cover, and what is left of Os-x86's? (blocking)**
- **A)** BoxedWine covers Windows programs and the VMs that run them: adding programs, library, recent files, per-program settings, bottles, and disk, machine and network of the Programmes Windows subsystem(s). Os-x86 keeps a thin Sous-systèmes app (list, start/stop, screens, security pages, no configuration) and its CLI for Linux, DOS and full VMs.
- B) BoxedWine is the GUI of all of Os-x86, Linux, DOS and full VMs included. The Sous-systèmes app disappears (11 and 49 A change).
- C) A now, B after v1.
- **Rec A.** It keeps 49 A and the Wine name while giving BoxedWine the configuration tools 51 D lists.

**64. Who shows a graphical program started from the terminal? (blocking)**
- **A)** A plain Os-x86 window (the same view, the program's name and icon), with or without BoxedWine. The terminal waits and gets the exit code.
- B) BoxedWine when installed (Os-x86 asks it by its app id), otherwise A.
- C) BoxedWine only; without it, a refusal with the install message.
- **Rec A.** The terminal path stays Os-x86 only, and a Feature never depends on an ordinary app.

**65. Where does the "run a Windows program" logic live? (blocking)**
- **A)** In the image: binfmt_misc in the guest, launcher, base prefix, maintained by Os-x86. The interface stays generic. BoxedWine changes Wine settings with guest commands.
- B) In Os-x86's .NET code.
- C) In BoxedWine only (needs 62 B).
- **Rec A.** The terminal and BoxedWine share one recipe, and no Wine type enters the interface (29 C).

**66. How does the terminal "use Os-x86"? (blocking for the ADR list)**
- **A)** Through the binfmt ADR and the raw-mode ADR: the shell hands the file to Os-x86's command, which runs in the tab and draws there.
- B) The Terminal app embeds Os-x86.Interface and calls it itself.
- C) Kazo's SDK defines a generic "x86 executor" contract that Os-x86 implements.
- **Rec A.** Kazo learns no product name (58 A), and each launch keeps its own Kazo process (35).

**67. The same program through two doors: one bottle or two? (blocking)**
- **A)** The bottles of launches you start yourself belong to you: same user, subsystem and hash means the same bottle, from the terminal, a double-click or a shortcut. The app that relayed your launch may act in that bottle (42). A launch requested by another app keeps its own bottles.
- B) Keep 50 A: terminal bottles and BoxedWine bottles stay separate; BoxedWine adopts one only through Os-x86's picker.
- C) The terminal asks BoxedWine to choose the bottle when it is installed.
- **Rec A.** One program gets one bottle for you, and apps still cannot reuse your approvals. It requires 61.10.

**68. BoxedWine as an ordinary app: installation and permissions.**
- **A)** A Window app, installed by any user or by an admin, with a required dependency on Os-x86.
  - `network.real.access`: admin approval at its first launch (43 changes).
  - No `shell.host`: a double-clicked console program opens in the Kazo Terminal.
  - Configuring the managed Programmes Windows subsystem falls under "lancer des programmes", except disk export and import and snapshots, which stay under "gérer les sous-systèmes".
- B) Same, but BoxedWine embeds its own terminal (`shell.host`, dangerous).
- C) Installed only by an admin, for everyone.
- **Rec A.** Least privilege, and the terminal change is already planned.

**69. Several VMs: when the choice appears and who shows it.**
- **A)** Only when the program's VM is not running and another VM is.
  - "VM existante" lists compatible running VMs.
  - "Nouvelle VM" starts a new instance and is greyed out when over the budget.
  - "Fermer" offers stop or suspend.
  - BoxedWine shows the choice for its own launches. An Os-x86 window shows it for terminal launches; the command waits, and options can answer in advance.
  - Running VMs are always listed in the Sous-systèmes app and in BoxedWine.
- B) Every time a VM is running.
- C) Os-x86 always shows the choice itself.
- **Rec A.** It asks only when there is a real choice and gives Os-x86 no settings window.

**70. Can a bottle open in another VM?**
- A) No: a bottle stays in the disk of its VM, and "VM existante" only applies to a new bottle.
- B) Yes: bottles are stored apart and opened through a share, locked to one VM at a time.
- **C)** A in v1, B after a measurement.
- **Rec C.** v86 cannot attach a disk to a running VM, and a Wine prefix on a share has not been measured.

**71. Who decides "si les ressources le permettent"?**
- **A)** Os-x86: the guest RAM of the running VMs plus the new one must stay within a per-page budget set by the admin (default taken from the browser's memory hint when it exists), plus a VM count per session.
- B) Try to start, and fall back if memory runs out.
- C) The admin's VM count only.
- **Rec A.** It is predictable, and wasm memory cannot be given back.

**72. What can `x86` do with Windows programs?**
- **A)** Everything BoxedWine does, by explicit commands: run a file, `--bottle` with the warning of 41, list and delete bottles, run a command or a shell in a named bottle, revoke decisions. Nothing is remembered between commands. A command run by an app counts as that app (52).
- B) Run files only (58 A bullet 3 kept).
- C) A without shells.
- **Rec A.** "Tout faire par ligne de commande avec seulement Os-x86" (51 D).

**73. The Kazo ADR for icons and the Start menu.**
- **A)** One generic ADR:
  - any window app can change its window icon at run time with no permission, like the title, and the taskbar follows;
  - any app can add per-user Start entries with an icon (new normal permission); an entry starts its app with an opaque number only;
  - Kazo itself draws the creating app's small icon at the bottom right of every icon set at run time (window, taskbar, Start entry);
  - entries are stored by the OS and removed at uninstall;
  - BoxedWine draws the same badge on desktop-shortcut PNGs.
- B) As written: BoxedWine draws the badge on shortcuts and Start entries; windows get no badge.
- C) B, plus BoxedWine draws the badge on window icons.
- **Rec A.** Otherwise a Windows program could copy a Kazo window, Os-x86's permission prompt included, and an app can omit a badge it draws itself.

**74. "Ajouter une application à Kazo OS".**
- **A)** The menu item opens Os-x86's picker on your bottles (installed programs and their `.lnk`) and on your Kazo files (a portable `.exe` stays where it is).
  - Picking an item in a bottle gives BoxedWine that bottle.
  - Name, arguments and icon are read inside the guest by a tool from the image, as untrusted data, then checked by BoxedWine.
  - The Start entry is required and the desktop shortcut optional.
  - The library shows added programs plus programs found in your bottles, marked "pas dans Kazo".
  - Deleting the program, its bottle or BoxedWine removes its entries.
- B) Added programs only.
- C) Kazo files only.
- **Rec A.** Kazo's Files cannot see inside bottles (J:162), and a hostile `.lnk` stays in the guest.

**75. Technical choices (one bundle, accept all).**
1. Os-x86's entry points count as one app for its data and bottles.
2. Os-x86 classifies DOS, Windows, console or graphical from the bytes it already reads for the hash.
3. "Pas de paramètres utilisateur" means no settings window. Os-x86's per-user state changes through `x86` and the interface: default Linux, sudo, device profile, keyboard layout.
4. A new image is offered by BoxedWine and by Os-x86 at the next terminal launch.
5. The Wine image is downloaded at the first Windows program, from either door.
6. `boxedwine file.exe` behaves like a double-click and returns no exit code. BoxedWine's id ends with a unique `.boxedwine`.
7. If BoxedWine's window, Os-x86's command or the terminal tab dies, Os-x86 stops the program.
8. BoxedWine keeps its library, recent files and settings in its own browser storage, per user, never in `~/.config`.

**Not blocking: the series-6 items already prepared (J:225), adapted to M.**
- **C33** (ui-theme.md:96-107): creation by profiles moves to `x86` and BoxedWine. 59 C replaces "la limite de VM".
- **C36** (ui-theme.md:159-168): unchanged. A "Terminal" button is not configuration, so the Sous-systèmes app can keep it.
- **C37** (ui-theme.md:224-234): the view is hosted by BoxedWine or by Os-x86's plain window. Both must declare `clipboard.read` and `clipboard.write`.
- **C38** (ui-theme.md:288-298): the mute button goes in both kinds of window. Volume is set through `x86` and BoxedWine.
- **C39** (ui-theme.md:354-363): the keyboard layout becomes a per-subsystem value set through `x86` and BoxedWine and detected automatically in Chromium, not an Os-x86 setting.

**UNVERIFIED:**
- whether Alpine's wine package registers binfmt_misc;
- how a Wine prefix behaves on a v86 share;
- the current limits of `navigator.deviceMemory`;
- whether the window manager accepts icons that are not package assets;
- whether a Feature child can provide a component typed from Os-x86.Interface;
- the absence of a network setting in the real Boxedwine (grep only).

Nothing was written to the repository. The Boxedwine source files I downloaded to check (1.1 d and i) are in `<scratchpad>/wf6/bw_containersView.cpp` and `<scratchpad>/wf6/bw_boxedContainer.cpp`.