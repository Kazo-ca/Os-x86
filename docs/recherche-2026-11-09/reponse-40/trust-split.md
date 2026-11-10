# BoxedWine / Os-x86 trust split: network prompts, bottle assignment and .exe entry points

The network decision must stay with Os-x86, even though BoxedWine now receives every .exe. Os-x86 should show the network prompt, store the answers, compute the SHA-256 and make the final call on which isolation identity (bottle) a file runs in. BoxedWine owns the entry points and the library UI. No app should be able to grant network through the interface.

## 0. Scope and threat model

- **Kazo paths.** All Kazo paths below are relative to the clone `.../scratchpad/repos/kazo-os` at commit `cd89ee0`. `git fetch` found no newer commit on `origin/main`.
- **Adversary 1: a malicious guest program** (.exe or ELF). This is the one D14 exists for. The boundary against it must be hard: enforced on the host side, at the guest's TCP SYN, with one network namespace and IP per identity (per-process-network.md:121-124).
- **Adversary 2: a Kazo app that uses only public SDK APIs with normal permissions.** This is the level that D32 (OS-verified caller identity) protects against.
- **Out of scope: a fully malicious .NET or JS app.** All apps share the page and the origin, and `KazoExecutionContext.Enter` is public (kazo-permissions-shell.md:34-37; critique.md:28-35). No split can stop such an app.
- **BoxedWine is trusted as an author but must not become a hole.** Everything it reads from the guest (shortcuts, registry, file names, console output) is untrusted input.

## 1. Who shows the prompt and stores the decisions

Facts that apply to every option:
- Only Os-x86 sees guest traffic: it holds the SYN in its own host adapter (per-process-network.md:121).
- The relay knows only Os-x86's app id (per-process-network.md:109; kazo-permissions-shell.md:84-87).
- The prompt must be an Os-x86 window app (journal-decisions.md:105).
- The research already says the window "belongs to Os-x86's feature service, not the calling app. That keeps a calling app from answering its own prompts" (per-process-network.md:135).

**(a) Os-x86 always shows the prompt and stores the decision.**
- **Silent self-grant:** not possible through the API, because no answer path exists outside Os-x86's window.
  - Remaining path 1: a front end can still give new code an existing allow, by running commands or writing files in an identity it created. Sections 2 and 3 limit this.
  - Remaining path 2: tampering with the decision store if it sits in the VFS (section 4).
- **ELF:** works, with the same mechanism and the 46 A key.
- **Third-party front end with only "lancer des programmes":** works. The prompt shows the OS-verified caller (D32) and the origin file and hash that Os-x86 computed.
- **BoxedWine uninstalled:** the bottles live in Os-x86's managed subsystem (D24, journal-decisions.md:136) and the decisions live in Os-x86's store.
  - Both survive and stay visible and revocable in the "Sous-systèmes" app (D23, journal-decisions.md:135).
  - The .exe entry points disappear. The D13 message "install the feature" (journal-decisions.md:64) now has to name BoxedWine.
- **Tension with the user's answer:** "Os-x86 est juste le backend" sits uneasily with Os-x86 owning a visible prompt window and rules page. The comparison is Windows Firewall and UAC: system dialogs, not app dialogs.

**(b) BoxedWine prompts for Windows programs, Os-x86 for ELF.**
- **Silent self-grant:** yes. Os-x86 holds the SYN and asks BoxedWine through a callback. Whatever BoxedWine answers is applied, so a bug or a bad update in BoxedWine grants network to every .exe. D14's "système de permissions propre à Os-x86" (journal-decisions.md:65) is gone.
- **New attack path, the answer token.** In (b), the answer comes back through an API, keyed by a request id.
  - Per 48 A, that id travels as an app argument, and app arguments are readable in /proc.
  - Reading /proc of other processes needs only `process.list`, which is normal (`AppCapabilities.cs:225`; kazo-permissions-shell.md:74).
  - Any app could therefore read the id and answer "allow". The id would become a bearer token unless Os-x86 also checks the answerer's verified identity.
  - In (a) the answer never leaves Os-x86's window, so the id is only a pointer.
- **Third-party front ends:** they still need Os-x86's prompt, because BoxedWine cannot sensibly decide about another app's program. That gives two prompt systems and two revocation places, against D23.
- **No session (45 A):** Os-x86 refuses the connection anyway, so it already needs its own path.
- **BoxedWine uninstalled:** decisions stored by BoxedWine disappear while the bottles remain in Os-x86. "Deny always" answers vanish. Whether `~/.config/<appId>` survives an uninstall: UNVERIFIED.

**(c) The front end that launched the program answers, through a callback.**
- Same as (b), generalized: any app holding only "lancer" (normal, no admin) can auto-allow the programs it launches.
- The D21 cap still applies: real network only if that app itself has `network.real.access`. But an admin approves that permission for the app's own traffic. Extending it silently to arbitrary downloaded guest code is exactly what D14 forbids.
- D14 would become only as strong as the least careful front end.
- ELF from the terminal would fall back to Os-x86, so there are again several stores.

**Verdict: (a).** BoxedWine may show read-only status (pending, allowed, denied) and a button that opens Os-x86's rules page.

## 2. Who assigns the bottle and computes the SHA-256

The underlying constraint:
- wineserver creates and connects every socket, with one wineserver per prefix. A bottle is therefore the smallest unit the kernel can enforce (per-process-network.md:42-53, 124; critique.md:37-45).
- `/proc` cmdline is chosen by the parent process and is not an identity. Identity must be what the host assigned at launch, plus a SHA-256 the host computed (per-process-network.md:113, 127).

**Attack paths if BoxedWine chooses the bottle or supplies the hash:**
- **Laundering.** The user once said "always allow" for the bottle created by `steam_setup.exe` (hash H1). Later BoxedWine runs `crack.exe` in that bottle, by design ("simple mode: one bottle") or by bug. There is no prompt, because D20's "re-ask if the hash changes" only ever looks at the bottle's origin.
- **Hash spoofing or TOCTOU.** BoxedWine passes the old hash, or the file changes between hashing and import.
- **Laundering through the generic operations.**
  - Under D29 C, BoxedWine drives Wine with guest commands (journal-decisions.md:141, 151).
  - "Execute a command" or "write a file" in an identity that has an allow decision equals "run arbitrary new code with that network decision".
  - 42 A makes this a normal permission inside identities the caller created.
- **Cross-bottle file reads.** Wine's Z: drive maps the whole guest root (critique.md:166). If the file API is not executed as the bottle's own guest user (D22, journal-decisions.md:134), it can read other bottles.
- **Guest root.** Guest root can change IPs or namespaces and impersonate an allowed identity (per-process-network.md:62-64; critique.md:192-199). So no interface command may ever run as root (D35, journal-decisions.md:147).
- **Confused deputy on Kazo files.** If Os-x86 reads a Kazo file with its own rights on a caller's request, a caller without `filesystem.user-home.read` can pull a file into a bottle and read it back through the guest file API. D22 already caps files by the caller.

**Is the proposed rule sound?** The rule: "decisions keyed by identity; the identity records its origin file and a hash computed by Os-x86; a new hash goes to a new identity unless the user picks an existing one". It is sound as the core, under seven conditions:

1. **Os-x86 hashes what it imports.** It reads the file once with the caller's file rights (verified snapshot, D32), hashes those exact bytes, and imports the same buffer. No hash ever comes from a front end.
2. **Os-x86 decides the identity.** A front end may only request an existing one.
3. **"The user picks" happens on a surface the front end cannot fake** (section 3). Otherwise it is the same as letting the front end choose.
4. **The rule also covers the side doors.** Interface file writes of executable content (MZ or ELF magic) and generic commands, when they target an identity with any allow decision, are the same act as "new origin into an existing identity". Otherwise laundering bypasses the rule. Changes made inside the guest (installers, self-updates) stay in the identity, as D20 intends (journal-decisions.md:132).
5. **Each identity records its creator front end** (verified app id). The prompt shows it, and its text says the decision covers "this program and everything run in its bottle, including by <front end>".
6. **Same-hash reuse (41 A) is limited to the same user, subsystem and creator.** Identical bytes started by another front end with different arguments can turn an approved interpreter-like .exe into arbitrary code with network.
7. **Hash change at the same path:** a new identity by default, and its prompt may mention "a previous version was allowed in bottle Y". Updating in place goes through the picker, with the D41 warning.

For ELF there are no bottles: each launch gets its own network namespace, and 46 A's key (user, subsystem, path, SHA-256) is consistent with this.

## 3. How Os-x86 knows "the user chose this identity through the app" (42 A)

**(iii) Trust the app's word.** Any app with "lancer" claims "the user chose bottle Steam", runs commands there and inherits its allow decisions. The 42 A split between normal and dangerous collapses. Reject.

**(i) BoxedWine holds "gérer les sous-systèmes" and keeps its own chooser; other apps use the Os-x86 picker.**
- Workable, and the same pattern as Android's system file picker versus a full-storage permission.
- Costs:
  - BoxedWine gets full VM control (snapshots, disks, the user's Linux subsystems), which is more than it needs.
  - The D41 warning then depends on BoxedWine's UI.
  - A BoxedWine bug means D14 is bypassed everywhere.

**(ii) Every front end, BoxedWine included, uses the Os-x86 picker (powerbox) for identities it did not create, and for any new origin entering an identity that has allow decisions.**
- The picker returns an opaque grant bound to (verified caller, identity, user), with a limited scope or lifetime.
- The picker shows the D41 warning itself.
- Cost for BoxedWine is small. Under answer 40, BoxedWine creates almost every bottle, so it needs no picker to:
  - list its bottles;
  - run programs already installed in them;
  - reuse a bottle for a same-hash file.
- The picker appears only for "Ouvrir dans un environnement Windows..." on an existing bottle and for its terminal option. In those cases BoxedWine opens Os-x86's picker instead of its own: one dialog either way.
- BoxedWine then needs only "lancer" plus what 43 A gives it.

**Recommendation: (ii).** Under it, 42 A's "chose through it" means "obtained through the Os-x86 picker the app opened". "Gérer" stays a rare admin tool.

## 4. Granting network decisions through the interface

- **No grant API at all**, whatever the permission. A grant API turns any front-end bug, or any guest data that reaches BoxedWine (section 5e), into silent network.
- **What the interface offers instead:**
  - read the decision status of the caller's own identities;
  - revoke (narrowing only);
  - "open the Os-x86 rules page on identity X".
- **Pre-set rules** stay as admin files in `/etc/<osx86AppId>/`. Writing there needs `filesystem.system.write` plus Administrator authority (`wasm2/KazoOs/Platform/Kazo.Os.Platform.Core/FileSystem/FileSystemService.cs:770-776`).
- **Hidden grant path: the user-layer store.**
  - An app's own `~/.config/<appId>/` is private to it. But another app's `~/.config/<x>/` resolves to `filesystem.user-home.write` (`FileSystemService.cs:760-786`), and that capability is Normal (`wasm2/KazoOs/Shared/Kazo.Os.App.Abstractions/AppCapabilities.cs:203`).
  - So a decision file in `~/.config/<osx86>/`, as suggested in kazo-permissions-shell.md:128, is a grant API in disguise for any app holding that normal capability.
  - Recommend keeping user decisions in Os-x86's own OPFS storage, as 48 A already does for suspend states.
  - Residual: OPFS is same-origin, so a malicious JS app can still reach it. That is out of scope.

## 5. Other holes created by moving the .exe entry points to BoxedWine

**a. Competing .exe handler.**
- Any window app may declare `.exe` (`wasm2/KazoOs/Shared/Kazo.Os.App.Abstractions/AppManifestValidator.cs:318-321`).
- With several candidates the OS shows a chooser, unless a user or admin default exists (`wasm2/KazoOs/Platform/Kazo.Os.Platform.Core/Intents/FileAssociationResolver.cs:22-28, 91-96`).
- The user default lives in `~/.config/kazoos/file-associations.json` (`wasm2/KazoOs/Platform/Kazo.Os.Platform.Core/Intents/UserFileAssociationStore.cs:37-40`). Going only by `ResolveCapability`, it is writable with the normal `filesystem.user-home.write`, so an app could silently become the default .exe opener. UNVERIFIED whether mode bits or an OS rule protect `~/.config/kazoos`.
- Os-x86 cannot stop this. Its defence is the prompt showing the verified front end ("opened by X", not just the file).

**b. Command-name squatting.**
- Command names go to system apps first, then by activation order (`wasm2/KazoOs/Platform/Kazo.Os.Platform.Core/AppCatalog.cs:120-126, 335-370`). Activation order is dependency order with ties broken by app id (`AppCatalog.cs:566-573`).
- An app with no dependencies and a lower id beats BoxedWine, which depends on Os-x86. The loser gets only a syslog warning (`AppCatalog.cs:363-368`).
- Fixes:
  - The binfmt registry maps magic numbers to an app id, never to a command name.
  - Conflicts between handlers go to an admin choice (the ADR 0156 model), not activation order.
  - Consider letting Feature command names win over non-Feature apps, as system apps already do.
- 38 A (only Features may register binfmt) means BoxedWine must itself be a Feature, which fits "installed by an admin".

**c. Lying about the caller (D21) and confused deputy.**
- Plain launches and file opens reach the target app with no sender:
  - `wasm2/KazoOs/Platform/Kazo.Os.Platform.Core/Intents/AppIntentDispatcher.cs:209-220` (launch);
  - `AppIntentDispatcher.cs:241-242` (open-file passes only the path);
  - `wasm2/KazoOs/Platform/Kazo.Os.Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs:324-329` (running instance).
- Other intents do carry an OS-stamped sender (`AppIntentDispatcher.cs:382, 421, 475, 505, 583`).
- **Attack 1:** any app holding `apps.launch` (Normal, `AppCapabilities.cs:212`) calls `LaunchAsync("boxedwine", ["/home/u/Downloads/evil.exe"])` or `OpenFileAsync` (`wasm2/KazoOs/Shared/Kazo.Os.Simulation.Abstractions/Gateways/AppGatewayContracts.cs:500-526`). BoxedWine cannot tell this from a user's double-click, and launches under its own cap, which includes the admin-approved `network.real.access` (43 A).
- **Attack 2:** the same app uses `ExecuteCommandIntent` to run `./evil.exe` in a non-interactive shell inside its own context (`AppIntentDispatcher.cs:252-300`). Commands started there run with their own rights (kazo-permissions-shell.md:80-83).
- Context-menu entries are stamped with sender `org.kazoos.shell`, but only when the target declares the intent; otherwise it gets plain arguments (`wasm2/KazoOs/Platform/Kazo.Os.Platform.Core/ContextMenus/FileContextMenuLauncher.cs:13-22, 100-102`).
- **Fixes:**
  - A small Kazo ADR to stamp the sender on open-file and launch deliveries, and an "interactive shell" flag on binfmt dispatch.
  - BoxedWine accepts only the shell, the explorer, its own UI and context-menu deliveries. Other apps must call Os-x86 directly under their own identity and cap.
- **D21 itself must be re-read:** for every BoxedWine-routed launch the caller is now BoxedWine, and "the user is the caller" (journal-decisions.md:133) remains true only for Os-x86's own "Sous-systèmes" app and wsl-like command.

**d. Console bridge.**
- BoxedWine's command now hosts the Kazo process and the stdio, so it sees all console input (passwords typed into console programs).
- Raw mode passes guest output to xterm.js unchanged except for newline translation (kazo-permissions-shell.md:58). A malicious console .exe can print a fake "Os-x86: allow network? [y/N]" line and capture the answer.
- Rule: never accept a consent typed in the terminal. Consent happens only in Os-x86's window (D23; 48 A).
- **Lifetime:** if BoxedWine's command crashes or is killed, the guest launch must die, and decisions scoped "until the program exits" must follow Os-x86's own launch record. Os-x86 can watch `ProcessStateChangedEvent` (kazo-permissions-shell.md:110).
- UNVERIFIED: whether Kazo's xterm.js honours OSC 52 (clipboard write) or OSC 8 (hyperlinks).

**e. Guest-to-front-end injection.**
- Bottle contents (`.lnk` files, registry, file names) are controlled by the guest program. If BoxedWine builds command strings from them, a planted shortcut can inject commands.
- **Interface rules:**
  - commands take argv arrays, not shell strings;
  - the identity is a typed parameter, never parsed from guest data;
  - Os-x86 runs each command as the identity's unprivileged guest user, inside its network namespace, never as root.
- With these, an injection stays inside its own bottle.

**f. The cap is computed only by Os-x86.**
- The relay checks Os-x86's grant and declared ports, not BoxedWine's (per-process-network.md:100-110).
- So Os-x86 must intersect the caller's `network.real.access` and ports itself, and re-check at every SYN. Kazo grants are evaluated live (kazo-permissions-shell.md:115), so a revocation takes effect.

**g. BoxedWine uninstalled.**
- Mark its bottles "orphaned": not launchable by others except through the picker. Decisions are kept.
- A reinstall under the same app id resumes ownership.
- Package signing is per source, not per app id (ADR 0214, doc/en/wasm/dev/adr/0214-hack-store-hot-add-signing-transactions.md:69-95). Whether another publisher could reuse a removed app id and inherit its bottles: UNVERIFIED.

**h. Who owns non-Windows binaries.**
- File handlers match the `.exe` extension, so BoxedWine also receives pure-MZ DOS files, which D28 sends to Os-x86's FreeDOS subsystem.
- Who owns the ELF binfmt is not settled by answer 40.

## 6. Recommended split

| Responsibility | Owner | Why |
|---|---|---|
| .exe entry points: MZ+NE/PE binfmt, `.exe` file handler, "Ouvrir dans un environnement Windows...", terminal command | BoxedWine (as a Feature, per 38 A) | User's answer 40 |
| ELF binfmt and pure-MZ DOS routing | Os-x86 (ask) | Not Wine; D28 |
| Verified caller identity and D21 cap, re-checked at every SYN | Os-x86 | The relay sees only Os-x86's app id |
| Reading the file, SHA-256 on the imported bytes | Os-x86 | No spoofing, no TOCTOU |
| Final identity decision: new, same-hash reuse within the same creator, or existing via picker | Os-x86 | Keeps D20's meaning |
| Library UI, running installed programs in its own bottles | BoxedWine | Its own bottles count as normal permission (42 A) |
| Choosing an existing bottle for a new file, with the D41 warning | Os-x86 picker, opened by BoxedWine | A surface the front end cannot fake |
| SYN hold, network namespace and IP per identity, RST, DNS | Os-x86 | Only host-side enforcement holds |
| Network prompt window | Os-x86 | A front end must never answer its own prompts |
| Decision store | Os-x86, in OPFS; admin rules in `/etc/<osx86>/` | `~/.config` is writable with a normal capability |
| Grant | Nobody, through the API | — |
| Read status, revoke, open rules page | Interface, own identities only | Narrowing only |
| Guest commands and guest files | Os-x86 executes them: argv only, per-identity guest user, no root | Isolation between bottles |
| Kazo process and stdio for a launch | BoxedWine | Answer 40 |
| Guest launch lifetime and "until exit" decisions | Os-x86 | No orphaned launches keeping network |
| Sender stamping on open-file and launch, interactive-shell flag | Kazo ADR | Stops confused-deputy launches through BoxedWine |

## 7. Questions worth asking the user

1. **Prompt and decision ownership:** (a) Os-x86 always, accepted as a "system dialog" even though Os-x86 is "just the backend" (recommended); (b) BoxedWine for Windows programs; (c) each front end through a callback.
2. **Who establishes "the user chose this bottle":** (ii) the Os-x86 picker for everyone, BoxedWine included, only when targeting a bottle it did not create or putting a new file into a bottle with allow decisions (recommended); (i) BoxedWine takes "gérer les sous-systèmes" and keeps its own chooser; (iii) trust the app.
3. **Same-hash reuse (41 A):** limited to the same creator front end (recommended), or global per user and subsystem.
4. **Grant API:** none; only revoke, read status and open the rules page (recommended).
5. **Decision store:** Os-x86's OPFS storage (recommended), or `~/.config/<osx86>/`, which apps with a normal capability can write.
6. **Kazo ADR for sender stamping,** plus a BoxedWine rule to refuse launches forwarded by other apps (recommended), or accept that any app with `apps.launch` can launch through BoxedWine's cap.
7. **ELF and pure-MZ DOS entry points:** stay with Os-x86 (recommended), or also go through BoxedWine.
8. **binfmt and command-name conflicts:** admin choice per magic number, plus precedence for Feature command names (recommended), or today's activation order.

## 8. UNVERIFIED

- Whether mode bits or an OS rule protect `~/.config/kazoos/` or other apps' `~/.config/<id>/` beyond `ResolveCapability`.
- Whether a Terminal command can read the `IsInteractive` flag of the shell session that launched it (`ShellSession.cs:37-60`).
- Whether Kazo's xterm.js handles OSC 52 or OSC 8.
- Whether app ids are bound to a publisher across an uninstall.
- Whether `~/.config/<appId>` survives an app uninstall.