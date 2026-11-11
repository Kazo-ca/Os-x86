# UI questions C33, C36, C37, C38 and C39 after series 4 (with answers 40 and 47)

**Sources checked on 2026-11-09:**
- **Kazo:** `origin/main` is still cd89ee0. `git ls-remote` gives the same hash, so the clone was not changed.
- **v86:** master 315b2b0 (2026-10-30), read with `gh api`.
- **Alpine aports:** the 3.24-stable branch (linux-lts 6.18.62).
- **MDN browser-compat-data:** as of 2026-11-09.

Unless a path says otherwise, Kazo paths start at `wasm2/KazoOs/` and v86 paths are at 315b2b0.

---

## 0. Findings that affect all five questions

### X1. Answer 40 needs one generic "guest view" primitive

BoxedWine now hosts each Windows program's Kazo window (D26, journal:138). That window needs five things from Os-x86:
- the program's pixels;
- mouse and keyboard routing;
- focus;
- the clipboard;
- a mute control.

None of this needs Wine types (D29 C, journal:141). The unit is a **guest display surface**: one top-level window of the guest's X server, inside a rectangle of the VM screen. A Wine virtual desktop is one such window.

One possible design (it is not decided anywhere): a small guest window manager from Os-x86 places every X top-level window tagged with a surface id into a rectangle that Os-x86 allocates. The interface would stay generic. The same mechanism would also give one Kazo window per Linux GUI program.

Kazo can already carry this as an **embeddable component**:
- Any window app may register a component contract with a lower-case dotted name, except the contracts the platform provides itself (`Shared/Kazo.Os.App.Abstractions/AppManifestValidator.cs:412-455`).
- The embedded component runs in the **host's** process, with the host's capabilities (`doc/en/wasm/dev/adr/0063-embeddable-app-components.md:60-67`; `0156-replaceable-file-control-provider.md:93`). The D21 cap on BoxedWine (journal:133) would then be enforced by Kazo itself.
- **Caveat:** `KazoComponentContracts.cs:9-12` says a contract names an SDK interface. A controller type that lives in Os-x86.Interface only works if one copy of that assembly is loaded per page (D36). It probably needs a line in the Feature ADR (D38).

I propose an extra question on this (section 6).

### X2. Ambiguities and conflicts created by answer 40

- **How far does "backend" go?** "Os-x86 est juste le backend" could mean Os-x86 has no UI at all. In that case, every part I assign below to Os-x86's UI moves to BoxedWine or to another front end: the Sous-systèmes app, VM screens, capture mode, and the audio controls. This needs a split question.
- **binfmt (D38):** only a Feature may register a binfmt handler. If BoxedWine receives `.exe` executions, either BoxedWine becomes a Feature or D38 changes. The binfmt ADR also needs a second check after the magic bytes (PE/NE signature at `e_lfanew`): pure MZ goes to Os-x86 (DOS, D28, journal:140) and MZ+PE/NE goes to BoxedWine.
- **The caller (D21/D43):** with a double-click handled by BoxedWine, the caller becomes BoxedWine, not the user (journal:133). BoxedWine must therefore declare `clipboard.read`/`clipboard.write` (normal-risk capabilities, `Shared/Kazo.Os.App.Abstractions/AppCapabilities.cs:74-78, 239-240`). Otherwise Windows programs get no clipboard.
- **D24:** "géré par Os-x86" (journal:136) needs to become "a subsystem that Os-x86 manages on behalf of an app". That affects C33.

### X3. One running VM per session (D35) clashes with separate managed subsystems

D35 (journal:147; detail in `critique.md:212`) sets one running VM per session by default. But "Programmes Windows" (D24), the default Linux subsystem (D24) and FreeDOS (D28) are separate VMs. With the default cap, opening a `.exe` while Alpine runs must either stop Alpine or be refused. The C33 user experience has to show this, or the cap rule has to change.

### X4. One X server is shared by all bottles

Inside the guest, all bottles share one X clipboard. X11 also lets any client read the windows and keystrokes of other clients. This matches D22, which makes only the network per-program (journal:134). The documentation should say it.

### X5. v86's own input adapters must be turned off

v86's keyboard and mouse adapters listen on the whole `window` (`src/browser/keyboard.js:268-271`, `src/browser/mouse.js:92-100`). Inside Kazo they would steal input from every Kazo window. The `disable_keyboard`/`disable_mouse` options exist (`src/browser/starter.js:33-34, 284-290`). Os-x86 sends `keyboard-code` and `mouse-*` bus events for each view instead.

---

## 1. C33: creating a subsystem (decisions.md:419-426)

### What still needs deciding

Already decided:
- per-user subsystems (D11, journal:62);
- managed "Programmes Windows" subsystem (D24, journal:136);
- managed FreeDOS (D28, journal:140);
- full VM control (D15, journal:66);
- device profile at next boot (D34, journal:146);
- image downloaded separately (D35);
- image version declared to apps (D44).

Open:
- the creation path for users: ready profiles and/or an advanced mode;
- which profiles ship in v1;
- **new:** whether an app may ask Os-x86 for a managed subsystem it owns (BoxedWine's Windows programs);
- what happens at the D35 cap (X3).

The "Apporte ton ISO" profile depends on C18 (Windows ISO and licence), which is still open.

### Who does what

- **Os-x86:** everything. The image catalogue, creation in the Sous-systèmes app, the command, the interface.
- **BoxedWine:** only asks for its managed subsystem.

### What the interface must expose

- List the profiles/images, with image id and version (D44).
- Create a subsystem from a profile with overrides (RAM, disk, device profile from D34).
- **Ensure a managed subsystem owned by the calling app**, idempotent, shown as "géré par <app>" and not modifiable.
- Report a status/reason when the running-VM cap blocks a start.

Which permission covers creating one's own managed subsystem is not decided. D42 suggests "lancer des programmes".

### Facts (no fast-moving facts here)

- The image cannot be a package asset (`critique.md:238`).
- An Alpine x86 image is the natural base for both "Linux console" and "Programmes Windows", so both could share one download (UNVERIFIED as a plan; it belongs to C13-C14).

### Proposed question

**33. Comment crée-t-on un sous-système ?**
Contexte : tes sous-systèmes sont à toi (D11). Os-x86 crée seul les sous-systèmes gérés au premier besoin : Programmes Windows (D24), FreeDOS (D28), Linux par défaut. Avec la réponse 40, c'est BoxedWine qui reçoit les `.exe` : il doit pouvoir demander un sous-système géré pour lui. Par défaut, une seule VM tourne par session (D35).

*A) Des profils prêts, en un clic, dans l'app Sous-systèmes et par la commande (« Linux console », « FreeDOS », « Apporte ton ISO »), plus un mode avancé (mémoire, taille du disque, profil de périphériques de D34). Les sous-systèmes gérés se créent seuls, visibles mais non modifiables, avec le nom de l'app qui les a demandés (« géré par BoxedWine »). Si la limite de VM est atteinte, on te propose d'arrêter l'autre sous-système.
B) Profils prêts seulement, comme WSL ; pas de mode avancé en v1.
C) Assistant avancé seulement, comme VirtualBox.

Pourquoi A : les profils couvrent l'usage courant, le mode avancé ne fait que montrer ce que l'interface offre déjà (D15, D34), et BoxedWine reçoit son sous-système sans connaître Wine dans l'interface.

Dépendances : la séparation des rôles (X2) ; C18 pour « Apporte ton ISO » ; la limite de D35 (X3).

---

## 2. C36: Linux subsystems in the terminal (decisions.md:456-463)

### What still needs deciding

Already decided:
- a wsl-like command (D11);
- ELF files launched through binfmt (D13, journal:64);
- a Kazo raw-mode terminal ADR and the full guest exit code (D27, journal:139);
- DOS text mode in the terminal (D28).

Open:
- the shape of the command;
- the channel for the interactive shell;
- how a user reaches a subsystem's terminal from the menu.

The command name depends on the app id and namespace fixed by D48.

### Who does what

- **Os-x86:** Linux, DOS and the command.
- **BoxedWine** (after answer 40): Windows console programs.

### What the interface must expose

For BoxedWine: **run with a pty**. That means:
- a byte stream in and out;
- an initial size (Wine reads the size once, `wine-display-console.md:84-87`);
- an "interrupt" that does not end the program (Ctrl+C);
- the full exit code.

### Facts

- **Kazo terminal:**
  - It has no terminal profiles (grep found none).
  - The Terminal app declares no intents and takes no arguments (`Apps/System/Kazo.Os.Apps.Terminal/manifest.json`: `"intents":[]`).
  - It does provide the embeddable component `kazoos.terminal` with `IKazoTerminal.RunCommandAsync` (`Shared/Kazo.Os.AppSdk/Components/IKazoTerminal.cs:68`, options at `:90-100`).
  - A third-party app may embed it with `shell.host` (ADR 0063:128).
  - So the Sous-systèmes app can open a terminal already running the shell command, with no Kazo ADR.
- **v86 virtio-console** (`src/virtio_console.js:35, 146-148, 208-215`):
  - It still exposes 4 ports named `virtio-0`..`virtio-3`.
  - All of them are forced to console mode.
  - Host-to-guest data is dropped when the guest has posted no buffer (`//TODO: Buffer`).
  - This is unchanged since the 11-09 research (`wine-display-console.md:5-11`).
- **Kernel:** Alpine 3.24 `virt.x86.config:704` has `CONFIG_VIRTIO_CONSOLE=y`.
- **Distributions you bring yourself** have no Os-x86 agent (`critique.md:202-204`). The serial console still works, at one byte per guest write (`wine-display-console.md:12-15`).

### Proposed question

**36. Comment utilise-t-on un sous-système Linux dans le terminal ?**
Contexte : la commande à la manière de wsl (D11), le lancement des ELF (D13) et le mode brut du terminal (D27) sont décidés. Reste la forme de la commande, le canal du shell et l'accès par le menu. Le terminal de Kazo n'a pas de profils, mais une app peut y intégrer un terminal qui lance une commande.

*A) Une commande à sous-commandes (`<cmd> list`, `start`, `stop`, `shell <nom>`, `run <nom> -- <commande>`) ; `<cmd>` seule ouvre un shell dans le Linux par défaut, comme `wsl`. Le shell passe par l'agent d'Os-x86 (un vrai pty, plusieurs sessions) ; sans agent (distribution apportée), par la console série. Dans l'app Sous-systèmes, chaque sous-système Linux a un bouton « Terminal » qui ouvre un terminal Kazo déjà connecté.
B) Seulement les options courtes de wsl (`-l`, `-d`, `-e`, `--shutdown`).
C) Console série seulement (une session, plus lente), sans agent.

Pourquoi A : c'est le style des commandes de Kazo (`kazopkg install`), l'agent donne plusieurs shells rapides, la série reste un secours, et le bouton « Terminal » ne demande aucun ADR.

Dépendances : le nom de la commande (D48) ; le canal de l'agent (les correctifs de v86 de C6).

---

## 3. C37: clipboard (decisions.md:465-472)

### What still needs deciding

- the scope (text, images, files);
- the transport;
- **when** sync happens (continuous, or only while a guest window has focus);
- how the caller cap from D21/D22 applies;
- export to the real computer.

### Who does what

- **Os-x86:** the guest agent and the bridge to the X selection.
- **The view** (BoxedWine's windows, or Os-x86's VM screens): starts the sync on focus. If the view is Os-x86's embedded component (X1), it calls KazoClipboard with the host's rights, so BoxedWine's `clipboard.*` cap applies automatically.

### What the interface must expose

- set the guest clipboard text;
- an event when the guest's text changes;
- focus in/out of a surface.

### Facts

**v86 VMware backdoor clipboard:**
- It is text only, with legacy commands 6-9 (`src/vmware.js:11-14`).
- Maximum 64 KiB (`CLIP_MAX`, `:39`).
- The host stages data with `vmware-clipboard-host` (`:88-93`). The guest must **poll** `GETSELLENGTH` and gets no interrupt (`:251-258`). The guest pushes its text through `vmware-clipboard-guest` (`:270-299`). The encoding is not defined.
- Commit 132da7b was authored 2026-06-14 and merged 2026-08-30 (PR #1589). That explains the two different dates in `integration.md:34` and `wine-display-console.md:132`.
- From Linux user space it needs I/O-port privilege. That is fine for the agent (`critique.md:195`).

**Using the agent channel instead** (`wine-display-console.md:144`) gives UTF-8, no 64 KiB limit, and change events through the X server (design inference). For vdagent on a virtio-serial port, v86 still names ports `virtio-N` and forces console mode (`src/virtio_console.js:146-148`).

**Kazo clipboard:**
- The SDK exposes text and file lists only (`Shared/Kazo.Os.AppSdk/Clipboard/KazoClipboard.cs:34, 47, 66, 78`), plus a `Changed` event (`:25-29`).
- Internally it holds any MIME target and keeps nothing after logout (`Platform/Kazo.Os.Platform.Core/Clipboard/SystemClipboard.cs:9-16, 48`).
- An app write already has origin `Guest` and is mirrored to the host (`SystemClipboard.cs:94-96, 229-242`). **The "VM" origin proposed on 11-08 is not needed.** Images would need a Kazo ADR.

**To the real computer:**
- Kazo calls `writeText` and, if the browser refuses, only logs it (`Platform/Kazo.Os.Platform.Core/Clipboard/HostClipboardBridge.cs:14-21, 262-266`; `Infrastructure/Kazo.Os.Infrastructure.Browser/wwwroot/hostClipboard.js:141-151`).
- Per MDN browser-compat-data, `writeText` needs a user-gesture handler in Firefox and Safari. In Chrome (version 107 and later) it needs a gesture handler **or** the `clipboard-write` permission.
- A copy made in the guest reaches Kazo asynchronously, so it can miss that window (not measured).
- **Fix:** a small amendment to ADR 0065 that retries a pending host write at the next user gesture.

**From the real computer:**
- Host text enters Kazo only through a browser `paste` event. `readText` is never called (`hostClipboard.js:7-9, 52-88`).
- The view must therefore let the paste event happen on Ctrl+V and hold back the guest's Ctrl+V until the guest clipboard is updated.
- Whether `paste` fires on a focused non-editable canvas in every browser is UNVERIFIED. A hidden textarea is the usual workaround.

**Drag and drop:** grep found no inter-app drag-and-drop API in the SDK (UNVERIFIED beyond grep). A dropped file needs a Kazo ADR. For v1, files reach the guest as arguments (D22).

### Proposed question

**37. Jusqu'où va le presse-papiers entre les programmes de l'invité et Kazo ?**
Contexte : il y a un presse-papiers par sous-système, partagé par tous ses programmes (D22). Une app peut déjà écrire du texte dans celui de Kazo sans rien changer à Kazo. Vers l'ordinateur réel, Kazo n'écrit que pendant un geste : une copie faite dans l'invité arrive trop tard et peut se perdre. Le backdoor VMware de v86 donne du texte, 64 Kio au plus, à interroger en boucle ; l'agent d'Os-x86 fait mieux.

*A) En v1, du texte dans les deux sens par l'agent, seulement quand une fenêtre de l'invité a le focus : Kazo vers l'invité quand elle prend le focus, l'invité vers Kazo quand le programme copie. Plafonné par l'appelant : BoxedWine déclare `clipboard.read` et `clipboard.write`. Vers l'ordinateur réel, un petit ajout à l'ADR 0065 réessaie au geste suivant. Images et fichiers plus tard.
B) Texte synchronisé en continu, même sans focus (comme VirtualBox).
C) Texte, images et fichiers dès la v1.
D) Pas de presse-papiers en v1.

Pourquoi A : un programme en arrière-plan ne lit ni n'écrase ton presse-papiers, le texte marche sans ADR lourd, et le correctif de Kazo est petit.

Dépendances : X1 (la vue) ; X2 (BoxedWine appelant).

---

## 4. C38: sound (decisions.md:474-481)

### What still needs deciding

- sound in v1 or not;
- the guest device;
- one stream per subsystem or per program;
- who plays it, and how it survives stalls of the Kazo main thread;
- the controls.

### Who does what

- **Os-x86:** the emulated device, the stream, the AudioContext, and the mute/volume for each subsystem.
- **BoxedWine:** shows a speaker/mute button in its windows through the interface.

### What the interface must expose

- each subsystem's audio: mute, volume, and an "is playing" event;
- per-identity streams later.

### Facts

**v86 audio devices:**
- Only SB16 and the PC speaker exist, and both are always present (`src/cpu.js:1262-1265`). SB16 uses IRQ 5 and DMA 1/5 (`src/sb16.js:73-83`).
- **There is no HDA, AC97, USB, virtio-input or virtio-gpu** in `src/` at 315b2b0. The 11-08 map line "HDA pour Windows 7+ et Linux" (decisions.md:475; `integration.md:13`) does not apply to v86.
- The only GPU-side PR in progress is virtio-gpu 2D (#1665, open, 2026-11-05).

**Linux side:**
- Alpine 3.24 `linux-lts` **virt** x86 has `CONFIG_ISA=y` and `CONFIG_SND_SB16=m` (`virt.x86.config:78, 745`). The `lts.x86` flavour has neither.
- The image should use the virt kernel or a custom one.
- SB16 is also the card used by DOS games and Windows 9x.

**v86 player:**
- `SpeakerAdapter` needs `window.AudioContext`/`window.AudioWorklet` (`src/browser/speaker.js:28-39`), so it cannot run in the engine worker.
- It pulls data with bus messages (`dac-request-data`/`dac-send-data`, `:814-829, 877`). It uses no SharedArrayBuffer ring.
- **New:** issue #1660 (open, 2026-11-03) shows that a 150-300 ms stall of the main thread replays SB16 ring halves. PR #1661, which fixes it, is open.
- Kazo's main thread often stalls, so the player should be fed by a SharedArrayBuffer ring from the worker.
- Kazo can turn isolation **off** (`Shared/Kazo.Os.AppSdk/Diagnostics/KazoCrossOriginIsolation.cs:3-14, 42-56`), so a fallback using messages is required.

**Kazo:**
- There is no SDK audio API and no audio capability (grep; ADR 0204 §7, `doc/en/wasm/dev/adr/0204-os-event-sounds.md:103`).
- The `sound.config` volume only covers event sounds (`:68`).
- The OS sound card resumes its AudioContext at the first gesture (`Platform/Kazo.Os.Platform.Blazor/wwwroot/osSounds.js:7-9, 24-33`).

**Mixing:** Wine mixes all programs inside the guest, so the host gets one mixed stream per VM (inference). Per-program sound needs a separate stream for each bottle.

The CPU cost of SB16 plus ALSA plus Wine in v86 is UNVERIFIED (not measured).

### Proposed question

**38. Comment gère-t-on le son ?**
Contexte : v86 n'émule que la Sound Blaster 16 (pas de HDA ni d'AC97). Le noyau Alpine « virt » x86 a son pilote, et c'est aussi la carte de DOS et de Windows 9x. L'invité mélange tous les programmes : Kazo reçoit un seul flux par sous-système. Le SDK Kazo n'a aucune API son. Le lecteur de v86 dépend du fil principal, et un bogue ouvert de v86 rejoue du son quand ce fil bloque.

*A) En v1, la SB16, un flux par sous-système joué par Os-x86 : un AudioWorklet alimenté par le worker à travers une mémoire partagée (repli par messages si la page n'est pas isolée). Muet et volume par sous-système, offerts par l'interface pour que BoxedWine affiche un bouton muet (qui coupe tout le sous-système). Le son est un périphérique du profil (D34), coupé par défaut si la mesure du jalon 0 le juge trop coûteux. Une API son du SDK Kazo viendra plus tard (ADR).
B) Un flux par programme dès la v1 (son envoyé par l'agent), muet par fenêtre.
C) Pas de son en v1.
D) Créer d'abord l'API son du SDK Kazo.

Pourquoi A : la SB16 existe déjà et sert aussi à DOS, la mémoire partagée évite le bogue du fil principal, et rien ne bloque sur un ADR Kazo.

Dépendances : X2 (où vit l'interface son si Os-x86 n'a aucune UI) ; la mesure du jalon 0 (D25).

---

## 5. C39: mouse and keyboard (decisions.md:483-489)

### What still needs deciding

There are now two cases:
1. **Program windows** (BoxedWine, cut out of the VM screen, D26).
2. **Whole VM screens** (Os-x86: Linux desktop, DOS graphics, a full VM).

Also open: Kazo shortcuts during capture, and the guest's keyboard layout.

### What the interface or view must expose

- pointer position local to a surface (Os-x86 maps it into the VM screen);
- relative movement while the pointer is locked;
- buttons and wheel;
- physical key down/up;
- surface focus;
- capture-state events;
- a keyboard-layout setting for each subsystem.

### Facts

**v86 absolute pointer:**
- `mouse-absolute` takes `[x, y, width, height]` normalised over the **whole** guest screen (`src/vmware.js:107-118`). That fits slicing: Os-x86 adds the rectangle's offset.
- While the browser has pointer lock, v86 sends relative packets instead (`:32-36, 120-144`).
- The backdoor is always present (`src/cpu.js:1195-1196`).
- Merge dates: absolute pointer 2026-06-08 (authored 2026-04-11); pointer-lock handling 2026-06-08.
- **Linux driver:** `vmmouse.c` sends relative packets to the PS/2 device and absolute ones to the absolute device (`drivers/input/mouse/vmmouse.c:44, 158-165`). Alpine 3.24 `virt.x86.config:681` has `CONFIG_MOUSE_PS2_VMMOUSE=y`.
- DOS and Windows 9x without vmmouse need relative mode, which means pointer lock.

**Browsers:**
- Pointer Lock: Chrome 37+, Firefox 50+, Safari 10.1+, not Safari iOS. The `unadjustedMovement` option exists in Chrome 88 (not on Linux), Firefox 152 and Safari 18.4.
- `Keyboard.lock` and `getLayoutMap`: Chromium only (Chrome 68 and 69 respectively).
- Keyboard lock works only during full screen started by JavaScript, and a long Esc press exits it ([Chrome doc](https://developer.chrome.com/docs/capabilities/web-apis/keyboard-lock)).
- Chrome had announced a permission prompt for keyboard and pointer lock from version 131. The post now says *"We have decided not to launch"* it ([Chrome blog](https://developer.chrome.com/blog/keyboard-lock-pointer-lock-permission)). I did not test this in a live browser.

**Kazo:**
- `requestPointerLock` is used nowhere (grep).
- The window manager catches its bound key combinations in the capture phase, on `document`, **before any app** (`Platform/Kazo.Os.Platform.Blazor/Windows/WindowManagerOverlays.razor.js:2-10, 44-77, 118`).
- The fallback set is always active, for example Alt+Q to close, Alt+F7 to move, Alt+F8 to resize, Alt+Backquote to switch (`Platform/Kazo.Os.Platform.Core/Shell/WindowKeyBindings.cs:312-328`). The Linux set (Alt+Tab, Alt+F4, Super) applies only under keyboard lock (`:62-66`).
- The keyboard lock is taken whenever the page is in full screen (`WindowManagerOverlays.razor.js:94-109`).
- **No API lets an app hold the keyboard** (grep found none). So, in a captured DOS game, Alt+Q would close the VM window. Letting the guest have the keys needs a Kazo ADR (like Wayland's "keyboard shortcuts inhibit").

**Keyboard layout:**
- v86 sends physical scancodes from `event.code` (`src/browser/keyboard.js:134, 330-332`). Its "type text" helper assumes a US layout (`:125, 282-298`; v86 issue #1406).
- The guest keymap must therefore match the user's layout.
- Kazo has no keyboard-layout setting (grep). Os-x86 needs one, with detection through `getLayoutMap` in Chromium only.

**Mouse cursor:** with an absolute pointer, v86 hides the host cursor (commit 63136f0), so the guest draws it with lag. Showing Kazo's cursor and copying the guest cursor shape through the agent is an option for later (inference).

### Proposed question

**39. Comment partage-t-on la souris et le clavier ?**
Contexte : deux cas. Les fenêtres de programmes Windows (BoxedWine, D26) peuvent toujours utiliser une souris absolue (backdoor VMware de v86, pilote vmmouse d'Alpine). Les écrans de VM entiers (Os-x86 : bureau Linux, DOS, VM complète) ont souvent une souris relative et demandent une capture. Aujourd'hui, Kazo prend ses raccourcis avant toute app (Alt+Q ferme la fenêtre), et une app ne peut pas les lui demander. Le verrouillage du clavier n'existe que dans Chromium, en plein écran. v86 envoie des touches physiques : l'invité doit avoir la même disposition de clavier que toi.

*A) Fenêtres de programmes : souris absolue, aucune capture ; les raccourcis de Kazo gardent la priorité, le reste va au programme. Écrans de VM : un clic capture la souris et le clavier (pointeur verrouillé si la souris de l'invité est relative), avec une combinaison d'échappement affichée (Ctrl + Alt droite) ; pendant la capture, Kazo laisse passer ses raccourcis grâce à un ADR Kazo « une fenêtre peut tenir le clavier ». Disposition du clavier de l'invité : un réglage d'Os-x86, détecté automatiquement dans Chromium.
B) Pareil, sans ADR Kazo : même en capture, les raccourcis de Kazo gardent la priorité.
C) Capture partout, aussi dans les fenêtres de programmes.

Pourquoi A : une fenêtre de programme se comporte comme une fenêtre Kazo (Alt+Tab passe d'un programme à l'autre), et en capture, un raccourci de Kazo ne peut plus fermer ta VM par accident.

Dépendances : X1 (qui écrit la vue) ; X2 (si Os-x86 n'a aucune UI, la capture passe au front end).

---

## 6. Proposed extra question (from X1)

**X. Comment BoxedWine affiche-t-il un programme Windows ?**
Contexte : avec la réponse 40, BoxedWine tient la fenêtre de chaque programme. Il lui faut l'image, la souris et le clavier, le focus, le presse-papiers et le bouton muet, sans type propre à Wine (D29). Kazo sait déjà faire intégrer à une app un composant fourni par une autre app : c'est ainsi que le terminal s'intègre ailleurs. Le composant tourne avec les droits de l'app qui l'affiche.

*A) Os-x86 fournit un composant « vue de l'invité » que BoxedWine place dans chaque fenêtre. Il peint la surface depuis le worker, envoie la souris et le clavier, gère le focus, le presse-papiers et le bouton muet. Il agit avec les droits de BoxedWine, donc le plafond de D21 est appliqué par Kazo.
B) L'interface donne des primitives brutes (images de la surface, envoi des entrées, focus, presse-papiers, son), et BoxedWine écrit sa propre vue.

Pourquoi A : la partie difficile (peinture depuis le worker, coordonnées, ordre du coller, verrouillage du pointeur) existe une seule fois, elle sert aussi aux écrans de VM d'Os-x86, et le plafond vient de Kazo.

Dépendances : la règle des Features (D38) doit permettre à une Feature de fournir un contrat de composant dont le type vient d'Os-x86.Interface ; une seule copie de l'interface chargée par page (D36).

---

## 7. Not verified

- Whether `paste` fires on a focused non-editable canvas in every browser, and how long each browser keeps a gesture valid for `writeText`. No measurement of an asynchronous guest copy.
- The Chrome keyboard/pointer-lock permission prompt: only the blog notice says it did not launch. Not tested live.
- The CPU cost of SB16, snd-sb16 and Wine's audio driver inside v86. Live resize of Wine virtual desktops.
- Whether the Kazo component host accepts at run time a controller type from a non-SDK assembly. The validator accepts the contract name, but the type cast was not tested.
- Absence of an inter-app drag-and-drop API, of terminal profiles, of Terminal launch arguments and of a keyboard-layout setting in Kazo: based on grep and the manifest only.