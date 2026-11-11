# Impact map: answer 40 ("BoxedWine receives the execution command") and answer 47 B

**Sources and abbreviations.** J = `docs/conception/journal-decisions.md`, read in full. MAP = `docs/recherche-2026-11-08/decisions.md`. Research files are in `docs/recherche-2026-11-09/`. Kazo paths are relative to the clone. `git fetch` on 2026-11-09 showed `origin/main` = cd89ee0, so there are no newer commits.

**UNVERIFIED: wording of series 4.** The journal stores only the titles of Q36-Q48 (J:168-180). The content of Q36-Q48 used below comes from the task summary.

---

## 1. Readings of the answer to 40

The user wrote "Os-x86 est juste le backend, et boxedWine son interface ... donc c'est BoxedWine qui s'occupe de recevoir la commande de l'execution."

- "Recevoir la commande" includes the terminal `./setup.exe`. In Q40 the terminal binfmt was the first item Os-x86 was to keep.
- The answer therefore takes at least the binfmt, the .exe association and the "Ouvrir dans un environnement Windows..." entry away from Os-x86.

### R1. BoxedWine is the front end for Windows programs and holds all Wine logic; Os-x86 is a generic backend (most likely, about 50%, judgment)

**BoxedWine owns:**
- every Windows entry point: MZ/PE/NE binfmt, .exe double-click, "Ouvrir dans un environnement Windows...", the bottle chooser;
- everything specific to Wine, done by guest commands (D29 C, J:141): Wine prefixes, the bottle index, virtual-desktop command lines, the launcher helper, the library of Windows programs.

**Os-x86 keeps:**
- VMs, the guest image with Wine pinned (44 A, D35 J:147), and generic isolation identities;
- console and display plumbing, and network enforcement;
- its own UI for Linux, DOS and VMs: the Sous-systèmes app and the wsl-like command (D11 J:62).

**Contradicts:**
- Q40 A;
- the review's reading of D29 C: "Os-x86 garde toute la logique Wine ... bouteilles, sous-système géré, une fenêtre par programme, lanceur, binfmt" (J:151).

**Reshapes:**
- D7: "grâce à elle, l'OS exécute des .exe dans le terminal" (J:22) now needs BoxedWine too;
- D13 (J:64), D20, D24, D26, D27, D28, D41, D43.

**Keeps:** D9 B (J:60), D11, D29 C, D33 D (J:145), 47 B.

**Why it is the most likely:**
- the user chose D29 C, "BoxedWine pilote Wine en envoyant des commandes dans l'invité";
- 47 B keeps a name specific to Wine;
- D33 D describes the Feature Os-x86 as having commands and windows, so Os-x86 keeps a UI.

### R2. Thin BoxedWine: it only receives and forwards (about 25%)

- BoxedWine registers the entry points and calls one generic "run this file" operation.
- Os-x86 still assigns bottles, hashes files, owns the managed subsystem, the launcher and the windows per program.
- This keeps J:151 except for the binfmt and changes the least.
- Weaknesses: it strains "Os-x86 est juste le backend". D29 C would then cover only settings (winecfg, reg), not launching.

### R3. BoxedWine is the whole UI of Os-x86 (about 15%)

- Os-x86 becomes a service plus Os-x86.Interface.
- BoxedWine hosts:
  - the Sous-systèmes app and the wsl-like command;
  - ELF and DOS launches, and VM management;
  - the permission prompts.

**Contradicts:**
- D11: one package with the Sous-systèmes app and the wsl-like command (J:62);
- D23: decisions revocable "dans l'app Sous-systèmes" (J:135);
- D24: "visible" in that app (J:136);
- D28: the FreeDOS subsystem is managed by Os-x86 (J:140);
- D33 D: the Feature Os-x86 has windows and commands (J:145);
- D7: WSL-like (WSL ships its own command);
- 47 B: a Wine name for a Linux, DOS and VM manager.

This reading takes "juste le backend" literally, but almost everything else the user said goes against it.

### R4. BoxedWine receives only graphical launches (about 10%)

- BoxedWine owns the double-click, the menu entry and the chooser; the terminal binfmt stays with Os-x86, as D7 J:22 says.
- Against it: "la commande de l'exécution" points at the terminal command.
- It would also create two front doors for the same .exe, with different bottle reuse.

**Windows only, or the whole UI?** Most likely Windows programs only (R1 or R2). Nothing in the answer moves Linux subsystems, ELF, DOS, VM management, the Sous-systèmes app or the wsl-like command. But "juste le backend" leaves R3 open, so the first question below asks it.

---

## 2. Every decision D7 to D48, under R1

### 2a. Who registers what under R1 (Kazo rules that apply)

| Item | Owner | Kazo rule |
|---|---|---|
| MZ (and PE/NE) binfmt | BoxedWine's Terminal entry point | The shell resolves only Terminal commands (ShellCommandResolver.cs:101-104). The binfmt hook goes between the x-bit check and the text read (ShellInterpreter.Scripts.cs:57-83; kazo-permissions-shell.md:54-56, 121). |
| ELF binfmt | Os-x86's Terminal entry point | Unchanged. |
| .exe association (double-click) | BoxedWine's Window entry point | Only window apps may declare file handlers (AppManifestValidator.cs:318-321). |
| "Ouvrir dans un environnement Windows..." | BoxedWine, Window or Service entry point | Needs `shell.context-menu` (normal) (AppManifestValidator.cs:472-480). An entry may target another app that declares the intent (context-menu.md:294). Menu entries are static, so the chooser opens behind one fixed entry (J:162). |
| Bottle chooser, terminal option | BoxedWine (Window, and its command) | 41 A had said "Os-x86's chooser". |
| Rule "only Features register a binfmt" (38 A) | BoxedWine must be a Feature | Admin installs it system-wide. It appears under "Fonctionnalités". It has a required dependency on the Feature Os-x86. Its whole permission set is shown once at install (37 A), including `network.real.access`, which is dangerous. That matches 43 A ("admin approval at its install"). Under 38 A it may also provide its own interface. |

### 2b. Os-x86 installed, BoxedWine not (the reverse cannot happen)

The reverse cannot happen because BoxedWine has a required dependency on Os-x86. If Os-x86 is missing, the catalog drops BoxedWine (AppCatalog.cs:437-451).

With Os-x86 alone:
- **`./setup.exe` in the terminal:** no MZ handler, so today's refusal: "cannot execute binary file", code 126 (ShellInterpreter.Scripts.cs:78-83). D13's "message et commande d'installation" (J:64) now has to name BoxedWine, and nothing in Kazo produces that message yet.
- **Double-click on an .exe:** no app declares `.exe`. ADR 0121 says "single candidate app, otherwise the picker" (doc/en/wasm/dev/adr/0121-user-file-associations.md:57). What happens with zero candidates is UNVERIFIED.
- **Menu and chooser:** neither exists.
- **Still working:** Linux subsystems, ELF, the Sous-systèmes app and the wsl-like command.
- **DOS:** works only if Os-x86 keeps a route to it (Q52).
- **Wine image:** never downloaded unless a user creates a Wine subsystem by hand.

### 2c. Generic primitives Os-x86.Interface must offer under R1 (no Wine types, D29 C)

1. **Subsystems.** Create one from an image id; it is tagged with the calling app as owner. Start, stop and suspend it. Read the image id, its version and the Wine version (44 A).
2. **Isolation identity.** Create, list and delete identities inside a subsystem:
   - each identity gets its own guest user, network namespace and source IP, and a copy-on-write home seeded from a template folder in the image;
   - it takes the origin file's Kazo path, which Os-x86 hashes itself (SHA-256, for the prompt and the re-ask rule of D20);
   - it carries a display label.
   - Why Os-x86 must do this: D35 rules out root in the guest (J:147), so BoxedWine cannot create network namespaces or users with guest commands. Enforcement must also stay on the host side (per-process-network.md:122-123, 129).
3. **Launch in an identity.** Takes argv, env, cwd, and the Kazo paths to share for this launch (D22, "son dossier et ses arguments"; a 9p share per launch, wine-display-console.md:144).
   - Console mode: a generic terminal channel (bytes, resize, interrupt), or pipes, or nothing.
   - Display mode: a display region of W×H.
   - Returns a handle: Unix exit status, kill, events.
4. **Run a command** without interaction: stdout, stderr, exit code (D29 C).
5. **Guest file access** inside an identity: list, read, write (42 A).
6. **Network decisions** of an identity: read and revoke only.
7. **Display.** A "guest display region" component, or a window opened on request (Q55).
8. **DOS.** "Run this file in the DOS subsystem", if BoxedWine forwards pure-MZ files (Q52).
9. **Caller identity.** Os-x86 needs the caller identity verified by the OS (D32; G5 at kazo-di-assemblies.md:142). It must know that the caller is BoxedWine and tie each launch to the caller's process.

### 2d. Decision by decision

- **D7** (J:16, 21-23). Changes. It assumed Os-x86 alone lets the OS run .exe in the terminal. Now that takes Os-x86 plus BoxedWine. Ask Q53 and Q49.
- **D8.** Unchanged. Each app declares its own licence; 47 B uses that.
- **D9 B** (J:60). Unchanged in substance. "Os-x86 lance Wine" now means: Os-x86 runs the VM and ships Wine in the image, and BoxedWine issues the wine commands. Only the wording needs updating.
- **D10.** Unchanged; already refined by 33, 35 and 36.
- **D11** (J:62). Unchanged under R1, broken under R3. Still ambiguous: what the Sous-systèmes app shows about the Windows subsystem (Q64).
- **D12** (J:63). The mechanism is unchanged. Uninstalling Os-x86 disables BoxedWine, so no .exe can run at all. Uninstalling BoxedWine is a new case (Q63).
- **D13** (J:64). Changes:
  - "passe le fichier à la fonctionnalité" now means MZ goes to BoxedWine and ELF goes to Os-x86;
  - a GUI .exe started from the terminal: BoxedWine's command opens a program window and waits for it (Q55);
  - a double-click on a console .exe: BoxedWine must open a terminal (detail);
  - the install message is Q53.
- **D14** (J:65). Partly changes. For every .exe, "l'app appelante" is now BoxedWine. Os-x86 keeps its own permission system (Q57, Q58).
- **D15** (J:66). Its scope changes: it must carry the primitives in 2c. The rule of Q40 A, "l'appelant ne choisit jamais la bouteille", no longer holds for the bottle (Q50).
- **D16, D17, D18.** Unchanged. Under R1, full Windows VMs stay with Os-x86; they would move under R3 or Q49 C.
- **D19** (J:131). Unchanged.
- **D20** (J:132). Changes:
  - bottle = BoxedWine's Wine prefix inside an Os-x86 identity;
  - decisions are keyed (user, subsystem, identity);
  - Os-x86 must hash the origin file itself.
  - Ask Q50 and Q60.
- **D21** (J:133). Conflict. "Caller = the user for a terminal or double-click launch" now meets "the app for an interface call", because BoxedWine relays every .exe launch (Q58).
- **D22** (J:134). Changes:
  - "un .exe voit son dossier et ses arguments" needs the per-launch share primitive, capped by BoxedWine's file grants. Those grants are normal: `filesystem.user-home.read` and `filesystem.user-home.write` (AppCapabilities.cs:202-203);
  - "chaque bouteille a son propre usager" becomes Os-x86's identity.
- **D23** (J:135). Ambiguous: who owns the prompt window, and where decisions are revoked (Q57, Q64).
- **D24** (J:136). Changes: "géré par Os-x86, créé au premier .exe", but the first .exe now reaches BoxedWine (Q54).
- **D25** (J:137). Unchanged. The spike tests the backend (VM plus Wine) and drives Wine by guest commands, which is the D29 C path anyway.
- **D26** (J:138). Changes. The Wine virtual-desktop command belongs to BoxedWine; tiling and slicing the VM screen belong to Os-x86. Who hosts the Kazo window is Q55.
- **D27** (J:139). Changes. The raw-mode terminal side belongs to BoxedWine's command. The launcher helper is a Windows .exe, so it is specific to Wine. Ask Q56 and Q61.
- **D28** (J:140). Changes: who dispatches pure MZ versus MZ+PE/NE (Q52). Wine does not run DOS (wine-i686.md:146).
- **D29 C** (J:141). Reinforced. The J:151 reading is overturned at least for the binfmt; Q50 settles the rest. The journal must record this.
- **D30.** Unchanged; superseded by 36 A.
- **D31** (J:143). Unchanged. BoxedWine must handle the `RestartRequired` status (39 A).
- **D32** (J:144). Ambiguous: which permission covers creating its own subsystem and identities (Q59). The verified caller identity becomes essential.
- **D33 D** (J:145). Extended: BoxedWine becomes a Feature too (Q51).
- **D34** (J:146). Mostly unchanged. Disk checkpoints of a subsystem owned by BoxedWine are controlled by the owner app (follows Q54).
- **D35** (J:147). Affected items:
  - "un fournisseur par contrat, Os-x86 au niveau système seulement" now also applies to BoxedWine;
  - "pas de root dans l'invité" forces the identity primitive;
  - ".exe sans bit d'exécution permis" becomes a flag on BoxedWine's format registration;
  - "un lancement = un processus Kazo" is now satisfied by BoxedWine's command or window instance;
  - "Wine 11.0 épinglé" stays in Os-x86's image.
- **D36.** Unchanged. BoxedWine carries its own copy of Os-x86.Interface and declares a required dependency.
- **D37.** Applies to BoxedWine. It needs at least two child entry points:
  - Terminal: binfmt handler and command (AppManifestValidator.cs:295-316);
  - Window: file handler, menu entry, chooser, library, and possibly the program windows (:318-321, :472-480).
- **D38.** Q51.
- **D39.** The binfmt registration must take effect at install, without a restart. Commands added at install already work at once (J:161, ADR 0214 §1.1-1.2). BoxedWine and Os-x86 install in one transaction.
- **D41.** Changes. The chooser and the menu entry move to BoxedWine, and so does the hash-to-bottle index (Q60).
- **D42.** Ambiguous. "Ou que l'usager a choisies par elle" relied on a trusted chooser in Os-x86. If BoxedWine's chooser lists only its own bottles, the "created by the caller" clause is enough (Q60).
- **D43.** Now covers every .exe launch, including terminal and double-click (Q58).
- **D44.** Reinforced: BoxedWine declares the minimum image version it needs. Where helpers live is Q61.
- **D45, D46.** Unchanged under R1, since ELF stays with Os-x86. Under R3, D46 would move to BoxedWine.
- **D47 B.**
  - BoxedWine moves from GPL-2 to MIT. Its repo held only a LICENSE file, and its description still said "HackerOs" (MAP:49, as of 2026-11-08). The repo's current state is UNVERIFIED.
  - The old rule "la licence suit le projet emballé" (MAP:49) no longer applies, because BoxedWine wraps nothing.
  - BoxedWine (MIT) must not copy Boxedwine's GPL patches, for example the explorer timeout patch; it must reimplement them (wine-display-console.md:136).
  - The name suggests the Boxedwine engine (critique.md:139); the user accepted that.
- **D48.** Unchanged if the prompts stay in Os-x86 (Q57 A). BoxedWine's Feature id, child ids and C# namespace must also be fixed before its first publication.

---

## 3. Questions to remove the ambiguity (blocking questions first)

### Blocking

**49. What is BoxedWine the interface of?**
- **A) Windows programs only.** BoxedWine owns MZ/PE/NE in the terminal, the .exe double-click, "Ouvrir dans un environnement Windows...", the chooser and the library. Os-x86 keeps the Sous-systèmes app, the wsl-like command, ELF, DOS and full VMs.
- B) The whole UI of Os-x86. Os-x86 is reduced to a service plus its interface.
- C) Windows programs plus full Windows VMs (ISO import, ReactOS).
- **Recommended: A.** It fits 47 B's Wine-specific name, D11 and D33 D.

**50. How is the Windows-program work split?**
- **A) BoxedWine owns everything specific to Wine.** It creates each bottle's prefix by guest commands, inside an identity that Os-x86 creates (guest user, network namespace, IP, copy-on-write home from a template in the image). It keeps the SHA-256-to-bottle index, the Wine command lines and the library. Os-x86.Interface offers only the generic primitives in 2c.
- B) BoxedWine only forwards "run this file". Os-x86 keeps bottles, hashing, the managed subsystem, the launcher and the windows (as J:151 assumed).
- C) Os-x86 also creates and indexes bottles as generic identities; BoxedWine owns the chooser, the library and the Wine settings.
- **Recommended: A.** It is "juste le backend" and D29 C taken literally: no Wine logic in Os-x86 beyond shipping Wine in its image.

**51. By 38 A, only Features register a binfmt. Is BoxedWine a Feature?**
- **A) Yes.** System-wide, installed by an admin, listed under "Fonctionnalités", required dependency on Os-x86; an app that wants it asks an admin.
- B) Relax 38 A so an ordinary app can register a binfmt.
- C) Os-x86 keeps the binfmt and forwards MZ files to BoxedWine.
- **Recommended: A.** It keeps 38 A intact, and 43 A already expects admin approval when BoxedWine is installed.

**52. Who owns MZ files, including DOS (D28)?**
- **A) BoxedWine owns them.** It owns the MZ format and the Windows file types (.exe, .com, maybe .msi), reads the header, and passes pure-MZ files to Os-x86's FreeDOS through a generic "run in the DOS subsystem" operation. Without BoxedWine, DOS works only through the wsl-like command. Note that .com files have no magic number.
- B) The binfmt ADR tests the signature at e_lfanew (0x3C). Os-x86 registers pure MZ, BoxedWine registers MZ+PE/NE, and BoxedWine still owns the double-click.
- C) BoxedWine owns DOS end to end.
- **Recommended: A.** One owner per extension avoids the "Open with" picker (ADR 0121:57) and binfmt priority rules.

**53. With Os-x86 but no BoxedWine (or with neither), what do `./setup.exe` and a double-click do (D13 "message et commande d'installation")?**
- **A) A generic suggestion in the binfmt ADR.** Packages declare in their Store listing the formats they handle. When no installed handler matches, the shell prints `kazopkg install <id>` and returns 126, and the file explorer offers the Store page. Whether the Store catalog works offline is UNVERIFIED.
- B) Os-x86 registers a fallback MZ handler that prints the suggestion (needs handler priorities).
- C) Keep the plain 126 (ShellInterpreter.Scripts.cs:78-83).
- D) Installing Os-x86 always installs BoxedWine.
- **Recommended: A.** It covers both cases and Kazo's core learns no product names.

### Interface design

**54. Who creates and owns the per-user "Programmes Windows" subsystem (D24)?**
- **A) BoxedWine.** It creates the subsystem at the first .exe from Os-x86's Wine image (44 A). Os-x86 tags it with BoxedWine as owner. The Sous-systèmes app shows it read-only, as "géré par BoxedWine".
- B) Os-x86 creates and owns it, and lends it to BoxedWine.
- C) Wine goes inside the user's default Linux subsystem.
- **Recommended: A.** It generalises D24 to "subsystems owned by an app", and 42 A already gives the creator app access.

**55. Who hosts the Kazo window of each graphical program (D26)?**
- **A) BoxedWine.** Each program gets a BoxedWine window instance that embeds an Os-x86 "guest display region" component (component model, kazo-permissions-shell.md:104-105, 113; the provider runs with the caller's gateways, kazo-di-assemblies.md:136).
- B) Os-x86 opens the window itself; BoxedWine passes the title and icon.
- C) One window per subsystem in v1.
- **Recommended: A.** The window belongs to the front end the user named, and the .exe file handler is a BoxedWine window anyway (AppManifestValidator.cs:318-321). Open point: whether a child window entry point of a Feature can provide a component is UNVERIFIED, because the Feature kind does not exist yet.

**56. Who bridges console programs to the Kazo terminal (D27)?**
- **A) BoxedWine's binfmt command.** It owns the raw-mode side and passes a generic terminal channel to Os-x86's launch operation. Os-x86's guest agent runs the pty. BoxedWine's launcher helper records the 32-bit Windows exit code, and BoxedWine returns it as the command's exit code. On a double-click of a console .exe, BoxedWine opens a terminal.
- B) Os-x86's terminal command bridges every console; BoxedWine starts it.
- **Recommended: A.** One Kazo process per launch (D35) and no Wine type in the interface.

**57. Who shows the network prompt for Windows programs and stores the decisions (D14, D19-D23, D45, D48)?**
- **A) Os-x86.** It keeps the window, the notification, the store, the admin rules and the no-session rule. BoxedWine can read and revoke a bottle's decisions, never grant them.
- B) BoxedWine shows the prompt; Os-x86 enforces and stores.
- C) BoxedWine shows and stores.
- **Recommended: A.** Enforcement sits in Os-x86's network adapter (per-process-network.md:122), and the capped caller must not answer its own prompts (per-process-network.md:136).

**58. Which cap applies to an .exe the user starts from the terminal or by double-click, now that BoxedWine relays it (D21 against 43 A)?**
- **A) BoxedWine's grants, intersected with the user's rights, for every .exe.**
- B) The user's rights, with the OS stamping the launch origin in the verified caller identity.
- **Recommended: A.** No new trust channel is needed. BoxedWine's file grants are normal (AppCapabilities.cs:202-203), and the admin approves its `network.real.access` at install.

**59. Which Os-x86 permission covers BoxedWine creating its subsystem and identities (D32, 42 A)?**
- **A) "Lancer des programmes" (normal)** covers subsystems and identities tagged with the calling app; "gérer les sous-systèmes" (dangerous) covers only the others.
- B) Creating any subsystem needs "gérer les sous-systèmes".
- **Recommended: A.** Least privilege.

**60. Bottle chooser and trusted choice (41 A, 42 A).**
- **A) BoxedWine's own chooser.** It lists only BoxedWine's bottles, and BoxedWine keeps the hash-to-bottle index. Os-x86 hashes the origin file when it creates an identity. Os-x86 keeps a trusted "choose an identity" picker only for apps that want an identity they did not create.
- B) BoxedWine always opens Os-x86's chooser.
- **Recommended: A.** BoxedWine's own choices are already covered by "created by the caller", so no trust is lost.

**61. Where do the Wine-specific guest pieces live?**
- **A) Split.** The image (Os-x86) ships Wine and a pre-built base prefix as part of the documented toolset (44 A). BoxedWine ships its own helpers, such as the launcher .exe, and writes them into bottles through the guest file operation.
- B) Everything in the image, maintained by Os-x86.
- C) BoxedWine installs its own scripts in the guest (Q40 C).
- **Recommended: A.** Os-x86 stays the backend, and the helpers are versioned with BoxedWine.

### Not blocking

**62. Can other apps run Windows programs?**
- **A) Through Os-x86 directly,** in identities they create (42 A, 44 A). BoxedWine offers no interface in v1.
- B) BoxedWine provides its own interface (allowed for Features by 38 A).
- C) Both.
- **Recommended: A.** Not needed yet. Caveat: a second Wine subsystem runs into D35's "une VM en marche par session".

**63. Uninstalling BoxedWine: what happens to the managed subsystem and its bottles?**
- **A) Kept.** Shown in Sous-systèmes as "propriétaire désinstallé", deletable by the user; reinstalling BoxedWine re-attaches them.
- B) Deleted after a confirmation.
- C) Handed over to the user as an ordinary subsystem.
- **Recommended: A.** No data loss.

**64. What does Os-x86's own UI show about Windows programs (D11, D23)?**
- **A) The Sous-systèmes app** shows the managed subsystem and its identities read-only, and lets the user revoke decisions. The wsl-like command has no .exe operation and points to BoxedWine.
- B) The wsl-like command keeps `run file.exe` and forwards it to BoxedWine.
- **Recommended: A.** One place for VM-level control, one for Windows programs.

**65. What happens when two Features claim the same binary format?**
- **A) One handler per format.** A second claim is refused at install unless the admin switches the handler (ADR 0156 model).
- B) The last one installed wins.
- C) Ask the user at each launch.
- **Recommended: A.** It mirrors D35's "un fournisseur par contrat".

---

**Journal maintenance.** Series 4 answers (36-48) are not yet recorded in J (J:166-180 lists only titles). The J:151 reading ("Os-x86 garde ... binfmt") must be marked as superseded by answer 40. The J:152 reading is confirmed by 36 A.