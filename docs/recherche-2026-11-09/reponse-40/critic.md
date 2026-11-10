# Critic report: the Os-x86 / BoxedWine split after answers 40 and 47 B

**Conventions**
- J = `docs/conception/journal-decisions.md`, which I read in full.
- MAP = `docs/recherche-2026-11-08/decisions.md`. Research files are in `docs/recherche-2026-11-09/`.
- Kazo code paths are relative to `wasm2/KazoOs/` in the clone. ADR paths are relative to `doc/en/wasm/dev/adr/`.
- `git fetch` on 2026-11-09 shows `origin/main` still at cd89ee0. The working tree is clean and nothing was pulled.

---

## 1. Claims that contradict the journal, the Kazo code, or another report

**1.1 Impact map: "the journal stores only the titles of Q36-Q48" is false for the current file.**
- J:170-184 has a summary row for each of 36-48.
- It includes answer 40 word for word (J:176) and 47 B (J:183).
- It also has the note on 41: "Propriétaire du sélecteur : à revoir avec la réponse 40" (J:177).
- What is really missing:
  - J:151 (the reading of 29 C: "Os-x86 garde … binfmt") is not marked as superseded.
  - The heading at J:186 still says "Reste à débattre après la série 3".

**1.2 Impact map Q60 A (BoxedWine's own chooser, "no trust is lost") reverses part of an accepted answer.**
- 41 A, as recommended and accepted, named "Os-x86's chooser".
- The user accepted it in the same reply as answer 40. J:177 only leaves the owner open.
- The claim "no trust is lost" is also wrong. In the trust split's laundering case (§2), BoxedWine holds the hash-to-bottle index. It can put a new hash into a bottle that has "always allow", and nobody is asked again. That defeats D20's "redemande si l'empreinte change" (J:132).

**1.3 Impact map Q50 A / Q60 A and trust split §2, §6 give opposite answers.**
- The disagreement is about who keeps the hash-to-bottle index and who makes the final bottle choice.
- Both reports agree that Os-x86 computes the hash.
- One fact weighs against keeping the index in BoxedWine's per-user data:
  - `~/.config/<appId>` is private only from the owner app's own side (`FileSystemService.cs:760-769`).
  - Any other app reaches it through `filesystem.user-home.write` (`:779-787`), which is Normal (`AppCapabilities.cs:203`).
- This must become one question (Q50).

**1.4 Opposite premises about where the bottles live.**
- Trust split §1(a), §5g and §6 say the bottles live in "Os-x86's managed subsystem (D24, J:136)".
- Impact map Q54 A and UI theme C33 make that subsystem "géré par BoxedWine".
- The two reports' uninstall analyses therefore rest on opposite premises.

**1.5 Who handles pure MZ (DOS) files.**
- Impact map recommends Q52 A: BoxedWine owns every MZ file and forwards DOS ones.
- Trust split §6 and UI theme X2 put pure MZ on Os-x86.
- Under Q52 A, a DOS `./game.exe` fails in the terminal when only Os-x86 is installed. That regresses D28 (FreeDOS "géré par Os-x86", "MZ seul → DOS", J:140) and D13 (J:64).
- The impact map's reason (avoid an "Open with" picker, ADR 0121) applies only to extension-based file handlers. A terminal binfmt can split on the e_lfanew signature without any picker.

**1.6 Impact map R3: one piece of evidence is overstated, and stronger evidence was missed.**
- The map cites "D33 D: the Feature Os-x86 has windows and commands (J:145)". J:145 only says a Feature "peut avoir" them. That describes the kind of app, not Os-x86.
- Only D11 gives Os-x86 its own UI (J:62), and it was a recommendation accepted without comment.
- Stronger evidence nobody used: in the same reply the user accepted 45 A and 48 A, which keep UI in Os-x86:
  - a refusal shown by Os-x86 at the next login, with a link to its rules page;
  - the notification "X attend ta réponse", which brings the Os-x86 permission window forward (J:181, J:184; that window must be an Os-x86 window app, J:105).

**1.7 Impact map D47: the GPL patch has the wrong line and the wrong component.**
- It cites the explorer timeout patch at `wine-display-console.md:136`. Line 136 is about bochs DRM; the patch is at `:65-67`.
- The patch changes Wine itself (`dlls/win32u/winstation.c`). So it would ship in Os-x86's guest-image Wine build (D35 "Wine 11.0 épinglé", J:147; the source-offer note at J:81), not in BoxedWine's MIT code.
- The licence issue belongs to the image package (D18, J:69), not to 47 B.

**1.8 Impact map 2b: "zero candidates UNVERIFIED" is now verified.**
- With no app declaring `.exe`, the resolver returns NoHandler (`Platform/Kazo.Os.Platform.Core/Intents/FileAssociationResolver.cs:212`).
- The intent fails with `intent.open-file.no-handler` (`AppIntentDispatcher.cs:221-242`).
- Files then shows "No application is registered to open this file." (`Apps/System/Kazo.Os.Apps.FileExplorer/ExplorerFileView.Operations.cs:77-78`; `Resources/Strings.resx:127`).
- So D13's "message et commande d'installation" (J:64) has no hook in Files either.

**1.9 Kazo facts: "Q37 A gives multi-entry-point identities to Features only".**
- J:173 does not say "only".
- One kind per ordinary app is a current Kazo fact (`AppManifestValidator.cs:295-321`; J:92), not a rule of 37 A.
- "BoxedWine must be a Feature" still holds, but because of 38 A's binfmt rule (J:174).

**1.10 Small line error in Kazo facts.**
- It cites the file-handler check at `AppManifestValidator.cs:316-319`.
- The check is at `:318-321`, with the error at `:320`. The impact map's `:318-321` is right.

**1.11 Command squatting: trust split 5b and Kazo facts are both right.**
- The precise rule:
  - System apps choose first, then the others in activation order (`AppCatalog.cs:343-372`, precedence at `:350`).
  - Activation order is Kahn's algorithm, which takes the smallest ready id each time (`:539-580`, `ready.Min` at `:573`).
- So a non-system squatter with no dependencies wins any shared name if its id sorts before BoxedWine's id, or before Os-x86's id.

**1.12 Trust split: "UNVERIFIED whether ~/.config/kazoos is protected" is partly resolved.**
- The seeder creates the folder with mode 0755 in the user's home (`FileSystemSeeder.cs:118`).
- Every path in the home outside the app's own `~/.config/<appId>` maps to user-home.read and user-home.write (`FileSystemService.cs:779-787`).
- I found no special rule. I did not check who owns the folder.

**1.13 UI theme C39: "no inter-app drag-and-drop API".**
- The SDK has typed drag payloads that carry `SourceAppId`: `VirtualFileDragPayload` and `VirtualFolderDragPayload` (`Shared/Kazo.Os.AppSdk/DragDrop/VirtualFileDragPayload.cs:1-20`).
- The file control uses them (`Apps/System/Kazo.Os.Apps.FileExplorer/FileControl/FileView.razor.cs:1330-1345`).
- Whether another app's window can receive such a drop is UNVERIFIED.

**1.14 Impact map 2c: the reading of "pas de root dans l'invité" (D35, J:147).**
- The map reads it as "BoxedWine cannot create network namespaces".
- The research behind it relies on "a small root agent in the guest" (`per-process-network.md:122`). So D35 means no root for launched programs and interface commands, while the Os-x86 agent is root.
- The conclusion stands (only Os-x86 can create identities). The spec should word D35 that way.

**1.15 One citation off by one.** Impact map Q57 cites `per-process-network.md:136`. The prompt-ownership sentence is at `:135`, as the trust split cites it.

---

## 2. Readings of answer 40 that were missed or dismissed too fast

**2.1 "La commande" can mean the order, not only the terminal command.**
- The impact map rejects R4 by reading "commande" as the terminal command.
- In French it also means "order" or "request". Read that way, BoxedWine receives every request to run a Windows program, including requests from other apps.
- That contradicts impact map Q62 A (other apps go to Os-x86 directly). Q52 below settles it.

**2.2 "Son interface" can mean the API (call it R5; no report considered it).**
- In this debate, "interface" has meant both:
  - the UI (D9 "BoxedWine n'est qu'une interface", J:60);
  - the API registered in the OS (D7, J:23; Os-x86.Interface, J:145).
- Literal R5: BoxedWine is the front that both apps and users talk to, with Os-x86 as the engine behind it.
- Against it: 33 D (apps reference "Os-x86" and Os-x86.Interface) and 29 C.
- Settle with Q52, option C.

**2.3 R3 is given too little weight on the wording, and too much on substance.**
- The user twice called BoxedWine Os-x86's interface and said "juste le backend". D11 was accepted silently.
- But 45 A and 48 A keep the prompt UI in Os-x86 (see 1.6).
- So a variant R3′ is still open: BoxedWine also hosts the Sous-systèmes app and the wsl-like command, and Os-x86 keeps only system dialogs, like Windows' UAC.
- Q49 should name each item and offer R1, R3′ and R3.

**2.4 R2 was rejected as "straining backend", but it is what the trust split actually recommends for identity, hash and prompts.**
- The user accepted 41 A (Os-x86's chooser) and 42 A ("identities the app created, or that the user chose through it") in the same reply as 40.
- Both answers assume Os-x86 owns identities and a choice surface the app cannot fake.
- The most consistent reading is a mix:
  - R1 for Wine logic and the entry points;
  - R2 for assigning programs to bottles.
- Q50 settles it.

**2.5 Minimal reading.**
- The answer only says who handles the .exe files. It does not reject Q40 A's four generic operations: launch a program, run a command, guest files, and isolation identities.
- Only two phrases of Q40 A are in question: "BoxedWine n'enregistre rien" and "l'appelant ne choisit jamais la bouteille".
- Confirm the four operations inside the option text of Q50 and Q55, rather than adopting the impact map's new list of nine primitives.

---

## 3. Ambiguities no report raised (to settle before an exhaustive spec)

**a) Unit of identity under 37 A: the Feature id or each child id?**
- Today everything per-app is keyed by the id of the app that runs:
  - `~/.config/<appId>` and `/etc/<appId>` (`FileSystemService.cs:760-777`);
  - grants (`AppIntentDispatcher.cs:205-206`);
  - the keyring, topic namespaces and command owners.
- If each child id is its own caller, problems follow:
  - A bottle created by BoxedWine's command counts as "created by another app" for BoxedWine's window. 42 A would then require "gérer les sous-systèmes" (dangerous) for BoxedWine's own bottles.
  - BoxedWine's private data is split between its entry points.
  - The D21 cap differs by entry point.

**b) Versioning between the two packages.**
- **Interface versions.** 36 A loads the newest compatible Os-x86.Interface. If BoxedWine was built against 1.3 and Os-x86 1.2 is installed, the 1.3 copy loads, but Os-x86 does not implement its new members.
- **New base image.** The base image is copy-on-write (J:132, J:136). A new base under existing bottles can break their prefixes. Who runs Wine's prefix update, and when?
- **Suspend states.** v86 refuses a state saved by another version (J:108). An Os-x86 update makes the Windows subsystem start cold.
- **Upper bound and rollback.** 44 A gives only a minimum image version. Rolling Os-x86 back below BoxedWine's range drops BoxedWine from the catalog (J:113).

**c) Uninstalling Os-x86 itself.**
- D12 warns and disables dependents (J:63).
- Nothing says what happens to per-user VM disks and bottles: kept, deleted, or exportable first.

**d) Several Windows front ends, and future engines.**
- D18 (J:69) foresees separate copyleft engine packages.
- A package around the real Boxedwine engine (GPL-2) would clash with the MIT front end named BoxedWine (47 B; `critique.md:139`).
- Nothing says whether a second front end sees BoxedWine's bottles or its library.

**e) What Os-x86 alone offers for Windows programs.**
- Can a user with only Os-x86 run `wine setup.exe` by hand in the Linux shell? That would use one shared identity, "Subsystem terminal session" (`per-process-network.md:134`).
- Is the Wine image downloaded when Os-x86 is installed, or at BoxedWine's first request? (This ties to C13-C14.)

**f) Wine Mono, Wine Gecko and winetricks.**
- Creating a prefix makes Wine offer to download Mono and Gecko.
- Unless the image ships them, that download triggers a network prompt under the new bottle before the user's program even runs.
- Whether Alpine 3.24 x86 packages them is UNVERIFIED. This belongs to 44 A's "stable toolset".

**g) Names.**
- Not yet decided:
  - BoxedWine's command (and whether it also takes the alias `wine`);
  - the name of the wsl-like command;
  - the child ids.
- The shell resolves a window app by the last segment of its id and gives up when two apps share that segment (`ShellCommandResolver.cs:69-82`). Child ids like `….library` would collide across Features.
- 48 A fixes the ids before first publication only for Os-x86.

**h) Shortcuts, Start menu and taskbar.** The research raised this in `critique.md:139`; no report has picked it up since.
- **Desktop shortcuts.** Kazo shortcuts are `.desktop` files with `Exec=<appId> [args]` and an optional `Icon` (`Shared/Kazo.Os.AppSdk/Files/KazoDesktopEntry.cs:7-29`; ADR `0152-desktop-icons.md:91-104`). BoxedWine can write one per program pointing at its window, but the icons must be stored in the VFS.
- **Start menu.** I found no API that adds a Start menu entry per program (grep only, UNVERIFIED).
- **Taskbar and `ps`.** A per-window title exists (`Shared/Kazo.Os.AppSdk.Blazor/WindowAppBase.WindowTitle.cs:70`). I found no per-window icon (UNVERIFIED). So every program likely shows BoxedWine's icon in the taskbar and BoxedWine's id in `ps`.
- **Security.** A `.desktop` launch carries plain arguments and no sender (`AppIntentDispatcher.cs:208-219`). A shortcut planted by any app with user-home.write can launch BoxedWine with arguments of its choice. The trust split's "accept only trusted senders" rule must decide whether the desktop counts as one; otherwise shortcuts break.

**i) BoxedWine's per-user and system data.**
- Per user: where do the library, labels, icons and any index live? `~/.config` can be written with a Normal capability (see 1.3).
- System: can an admin preinstall a program for every user? D24 has only per-user subsystems (J:136).
- Admin defaults in `/etc/<id>/` need system.write plus an administrator (`FileSystemService.cs:771-777`).

**j) Windows files that are not MZ.**
- A `.bat` or `.cmd` with the execute bit is text without `#!`, so Kazo's shell runs it as a shell script (`ShellInterpreter.Scripts.cs:24, :127`).
- `.com` has no magic number. `.msi`, `.lnk` and `.reg` are not MZ either.
- The binfmt ADR needs extension rules, as Linux binfmt_misc has. Otherwise these files work only by double-click.

**k) Bottle lifecycle.**
- Who deletes a bottle?
- When a bottle is deleted, what happens to its decisions, its shortcuts and its known-hash entry?
- Is there per-bottle export, or only D34's whole-disk export (J:146)?

**l) BoxedWine's own network use.**
- 43 A gives BoxedWine `network.real.access` so it can pass network access on to the programs it launches.
- Any downloads BoxedWine makes itself (icons, metadata) run on the Kazo side, under BoxedWine's own grant, and never show the Os-x86 prompt. Nothing says whether that is allowed in v1.

---

## 4. Questions for series 5 (blocking first)

**49. Where does BoxedWine stop? (blocking)**
- *A) Windows only.* BoxedWine owns:
  - the .exe entry points: terminal, double-click, "Ouvrir dans un environnement Windows…";
  - the library;
  - the windows and consoles of Windows programs.

  Os-x86 keeps:
  - the Sous-systèmes app and the wsl-like command;
  - ELF and DOS launches, and full VMs;
  - its system dialogs: the network prompt, the rules page, and the bottle picker (like UAC or a firewall dialog).
- B) A, plus BoxedWine hosts the Sous-systèmes app and the wsl-like command (R3′).
- C) All UI in BoxedWine, prompts included (this reverses 45 A and 48 A).
- **Recommended: A.**

**50. Who decides which bottle a file runs in? (blocking)**
- *A) Os-x86 decides:*
  - BoxedWine passes the file's Kazo path.
  - Os-x86 reads the file with the caller's rights and hashes those exact bytes.
  - It returns one of three things:
    - the bottle already tied to that hash, for the same user, subsystem and creator app;
    - a new bottle;
    - a bottle the user picks in Os-x86's picker, which shows the D41 warning.
  - BoxedWine builds the Wine prefix inside the bottle, and keeps names, icons and the library.
  - Writing an executable into a bottle that has "always allow", or running a command there, counts as a new program and asks again.
  - This matches 41 A as accepted.
- B) BoxedWine keeps the hash-to-bottle index and its own chooser.
- C) BoxedWine holds "gérer les sous-systèmes" and decides everything.
- **Recommended: A.**

**51. BoxedWine's form and its identity. (blocking)**
- *A) A Feature* (38 A):
  - installed system-wide by an admin, with a required dependency on Os-x86;
  - a command entry point (binfmt, consoles, exit code) and a multi-instance window entry point (.exe association, menu entry, library, program windows);
  - for ownership, permissions and private data, all of a Feature's entry points count as one app;
  - each launch is capped by the subset its entry point declares.
- B) A Feature whose entry points each count as a separate app.
- C) An ordinary app, so a user could install it without an admin (Kazo's default, `KazopkgCommand.cs:17-21`). This means relaxing 38 A for binfmt.
- **Recommended: A.**

**52. Another app wants to run a .exe. (blocking for the interface)**
- *A) It hands the file to BoxedWine,* as a double-click would:
  - A small Kazo ADR stamps the sender on launch and open-file deliveries.
  - The program is capped by both the sender's rights and BoxedWine's rights, and the prompt names both apps.
  - BoxedWine offers no interface in v1. Os-x86.Interface keeps its generic operations for ELF programs and scripts.
- B) It goes directly through Os-x86.Interface, and each app redoes the Wine work itself.
- C) BoxedWine publishes its own interface (allowed for Features by 38 A).
- **Recommended: A.**

**53. DOS and other Windows files. (blocking for the binfmt ADR)**
- *A) Split by format, with one owner per extension for double-click.*
  - Terminal:
    - binfmt reads the signature at e_lfanew;
    - pure MZ and `.com` go to Os-x86 (FreeDOS);
    - MZ+PE/NE goes to BoxedWine, along with `.bat`, `.cmd` and `.msi` through extension rules.
  - Double-click:
    - BoxedWine declares `.exe`, `.msi`, `.bat` and `.cmd`, and hands pure-MZ files to Os-x86's DOS operation;
    - Os-x86 declares `.com`.
- B) BoxedWine owns every MZ file and every Windows extension, and forwards DOS files.
- C) BoxedWine runs DOS itself.
- **Recommended: A.** DOS stays usable in the terminal with Os-x86 alone (D28).

**54. The "Programmes Windows" subsystem and uninstalls.**
- *A) Os-x86 owns the subsystem:*
  - It creates it at BoxedWine's first request, per user, tagged "géré pour BoxedWine". It shows read-only in the Sous-systèmes app.
  - Uninstalling BoxedWine keeps the bottles and decisions. They are marked "app désinstallée" and the user can delete them. Reinstalling BoxedWine re-attaches them.
  - Uninstalling Os-x86 keeps per-user VM data until an admin deletes it, after offering an export.
- B) BoxedWine owns the subsystem; uninstalling it deletes the bottles after a confirmation.
- C) Bottles go into the user's default Linux subsystem.
- **Recommended: A.**

**55. Program windows and consoles.**
- *A) One BoxedWine window per program,* embedding an Os-x86 "guest view" component:
  - The component handles pixels, input, focus, clipboard and mute, and runs with BoxedWine's rights.
  - Console programs go through BoxedWine's command over a generic terminal channel; BoxedWine's guest helper returns the full exit code.
  - The window title is the program's name. The taskbar shows BoxedWine's icon until Kazo allows per-window icons.
- B) Os-x86 opens the windows, and BoxedWine passes the title and icon.
- C) The interface gives raw primitives and BoxedWine draws its own view.
- **Recommended: A.**

**56. Versions between the two packages.**
- *A) Explicit minimums and controlled updates:*
  - BoxedWine requires an Os-x86 version at least as new as the Interface it was built with (checked when the package is built). It also requires a minimum image version (44 A).
  - Os-x86 never changes the base under existing bottles. A new image is a new version, which BoxedWine offers to the user; Wine's prefix update then runs in each bottle at its next start.
  - Suspend states made by an older Os-x86 are dropped, and the subsystem starts cold.
  - Rolling Os-x86 back below BoxedWine's minimum warns that BoxedWine will be disabled.
- B) Both packages always ship together under the same version number.
- C) Old bottles stay on their old image forever.
- **Recommended: A.**

**57. Installed programs in Kazo.**
- *A) Library and desktop shortcuts:*
  - BoxedWine's library lists the programs found in its bottles. Their names and icons are treated as untrusted input.
  - "Ajouter au bureau" writes a `.desktop` shortcut to BoxedWine's window, with the bottle and program as arguments; the icon is stored in BoxedWine's data.
  - No Start menu entry per program in v1.
  - Desktop shortcuts count as a trusted sender under Q52.
- B) A Kazo ADR in v1 for per-program Start menu entries and per-window icons.
- C) The library only.
- **Recommended: A.**

**58. Os-x86 installed without BoxedWine.**
- *A) A generic suggestion in Kazo:*
  - Packages declare in their Store listing the formats they handle.
  - In the terminal: the install command, a note that an admin must install it, and exit code 126.
  - In Files: a Store link instead of "No application is registered…".
  - The wsl-like command offers no .exe operation.
- B) Os-x86 registers a fallback handler that names BoxedWine.
- C) Keep today's messages.
- **Recommended: A.**

**59. Names.**
- *A) Fix every name before the first publication, with Kazo rules for conflicts:*
  - BoxedWine's Feature id, child ids (each with a unique last segment), C# namespace, and command `boxedwine`, with no `wine` alias so that `wine` keeps meaning the guest command;
  - the name of Os-x86's wsl-like command;
  - a Kazo ADR in which binfmt maps a magic number to an app id, Feature command names win over ordinary apps, and an admin decides between two Features.
- B) Same, plus the alias `wine`.
- C) Keep today's activation-order rule.
- **Recommended: A.**

**60. Technical choices (one bundle).**
- The network prompt, the decision store and the rules page stay in Os-x86. Decisions are stored in Os-x86's OPFS, not in `~/.config`.
- The interface has no "grant" operation: only read a bottle's status, revoke, and open the rules page, for the caller's own bottles.
- Guest commands take argv arrays and run as the bottle's unprivileged guest user, inside its network namespace, never as root.
- When BoxedWine's command or window dies, Os-x86 ends the guest program. "Until the program exits" decisions follow Os-x86's own launch record.
- No consent is ever typed in the terminal.
- Wine patches belong to the image package, with its source offer. BoxedWine (MIT) contains no Boxedwine code.
- Housekeeping:
  - update BoxedWine's repository licence and its "HackerOs" description;
  - update the Kazo memory note (`doc/memory/decisions-not-built.md:44-51`, cited by the Kazo-facts report; I did not check it);
  - mark J:151 as superseded and rename the heading at J:186.
- **Recommended: accept all.**

---

## 5. Not verified

- Whether another app's window can receive a `VirtualFileDragPayload` drop.
- Whether Kazo has a per-window icon or a per-program Start menu API (grep only).
- Who owns `~/.config/kazoos`.
- Whether `~/.config/<appId>` survives an uninstall.
- Whether an app id is bound to its publisher after an uninstall.
- Whether Alpine 3.24 x86 packages wine-mono, wine-gecko and winetricks.
- Whether a Feature child (which does not exist yet) can provide a component whose type comes from Os-x86.Interface.
- The Kazo memory note line range: I took it from the Kazo-facts report and did not open the file.