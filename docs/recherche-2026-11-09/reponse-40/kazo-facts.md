**Kazo facts for splitting Os-x86 (backend Feature) from BoxedWine (front end that receives executions)**

Clone: `<scratchpad>/repos/kazo-os`. Code paths are relative to `wasm2/KazoOs/` in that clone. Paths that start with `doc/` are relative to `doc/en/wasm/`.

**0. Repo status**
- FACT: `git fetch origin` reports main up to date. `origin/main` is still cd89ee0 (2026-11-08 22:06 -0500). No newer commits on main touch AppManifest, FileHandlers, the shell, intents or the terminal, and no Feature or binfmt code exists.
- FACT: issue #419 is still OPEN: 0 comments, last updated 2026-11-07.
- FACT: open PRs #428, #429 and #430 do not touch the listed areas.
  - #430 rewrites `Platform/Kazo.Os.Platform.Core/Apps/Packages/AppPackageApiCheck.cs`. It switches to exact parameter-type matching (ADR 0242). This sits next to Q36: the embedded Os-x86.Interface copies are API-checked by this code.
  - #429 takes ADR 0241 and #430 takes ADR 0242. #430's description says the next free number is 0243. The earlier research said "next free 0241"; that is outdated once these merge.
- FACT, relevant to Q47:
  - Repo `Kazo-ca/Os-App-BoxedWine` exists and holds only a GPL-2.0 `LICENSE`. Its description is "BoxedWine implementation for HackerOs" and it was last pushed 2026-10-28 (gh api).
  - Kazo's memory note still says BoxedWine follows the wrapped project's GPL-2.0 (`doc/memory/decisions-not-built.md:44-51`). This note will be stale after 47 B.

**a) File handlers**
- FACT: declaration
  - `FileHandlerManifest(MediaType?, Extensions, Actions, Priority=0)` (`Shared/Kazo.Os.App.Abstractions/AppManifest.cs:218-228`).
  - Only Window apps may declare file handlers (`AppManifestValidator.cs:316-319`). A handler needs a media type or an extension, and Priority must be ≥ 0 (`AppManifestValidator.cs:339-353`).
- FACT: matching uses no magic bytes and no content sniffing.
  - Matching is by extension (case-insensitive) or by exact media type (`Platform/Kazo.Os.Platform.Core/Intents/FileAssociationResolver.cs:386-393`).
  - The media type is only what the caller passes, "never auto-detected from content" (`Shared/Kazo.Os.Simulation.Abstractions/Gateways/AppGatewayContracts.cs:519-521`).
  - Files passes no media type for files (`Apps/System/Kazo.Os.Apps.FileExplorer/ExplorerFileView.Operations.cs:62`), so in practice `.exe` matches by extension only.
  - A grep finds no MZ/PE/ELF code at all. The only `.exe` mapping is an icon (`Apps/System/Kazo.Os.Apps.FileExplorer/FileControl/Icons/DefaultShellIconProvider.cs:25`).
- FACT: `Priority` is never used to resolve a handler.
  - It is only validated and mapped (`AppManifestValidator.cs:346`, `Definition/AppDefinitionMapper.cs:64`). Neither the resolver nor Files reads it (grep).
- FACT: when two apps both claim `.exe`, resolution goes in this order (`FileAssociationResolver.cs:180-216`). The first app to claim it does not win.
  1. The explicit target.
  2. The user's default in `~/.config/kazoos/file-associations.json`.
  3. The admin's default in `/etc/kazoos/file-associations.json`.
  4. The only enabled candidate.
  5. Otherwise `ChooserRequired` with every candidate.
- FACT: what each surface does with `ChooserRequired`.
  - Files shows the OS "Open with" dialog. "Always use" writes the user's default (`ExplorerFileView.Operations.cs:57-83, 92-118, 134-154`).
  - "Choose another app" can offer every window app (`ExplorerFileView.Operations.cs:91`), so a user can pick an app that does not declare `.exe`.
  - In the terminal, `xdg-open` has no chooser: it exits 4 and lists the candidates (`Apps/Commands/Kazo.Os.Commands/XdgOpen/XdgOpenCommand.cs:84-93`; `doc/framework/app-intents.md:171-172`).
- FACT: an app can set the user's default.
  - It calls `KazoApps.SetDefaultHandlerAsync(target, appId)` (`Shared/Kazo.Os.AppSdk/Apps/KazoApps.cs:70-88`). It needs only `file-associations.read` and `filesystem.user-home.write`, both normal (`Platform/Kazo.Os.Platform.Core/Execution/AppCatalogGateway.cs:146-151`; `AppCapabilities.cs:203, 220`).
  - The target app must be an enabled window app that declares that target (`FileAssociationResolver.cs:302-316`).
- INFERENCE: no user confirmation exists on that path. An app with those two capabilities can silently make itself, or another app, the user's `.exe` default.
- FACT: system layer
  - The built-in system-layer content has no `.exe` entry (`Platform/Kazo.Os.Platform.Core/Intents/FileAssociationSettingsDocuments.cs:36-46`).
  - `file-associations.write` (dangerous, `AppCapabilities.cs:221-224`) is declared by Settings, but no gateway code checks it (grep).
  - UNVERIFIED: the exact code path Settings uses to write the system layer.
- FACT: shell routes to a handler.
  - `./x.exe` never looks up file handlers (`Platform/Kazo.Os.Platform.Core/Shell/ShellInterpreter.Scripts.cs:26-128`).
  - The shell reaches a handler only through:
    - `xdg-open x.exe`: an OpenFile intent. The handler starts without the shell's streams and `xdg-open` returns 0 at once.
    - Typing the window app's short name or full id (`boxedwine x.exe`) (`ShellInterpreter.SimpleCommands.cs:177-178, 259-310`). The window gets its arguments, the shell's directory and environment, and its stdin/stdout/stderr through KazoConsole (`AppLifecycleOrchestrator.cs:548-559`). The shell waits for the window to close, Ctrl+C kills the window and returns 130, and the exit status is 0 (no window exit-code API).
- FACT: single-instance caveat. If the window app is single-instance and already running, the shell returns 0 at once (`ShellInterpreter.SimpleCommands.cs:282-285`). The arguments reach the running instance as a LaunchApp activation, with no streams and no sender (`AppLifecycleOrchestrator.cs:324-337`; `AppIntents.cs:204-213`).
- FACT: what the handler receives is the VFS path as a plain string argument, never a handle.
  - OpenFile launches it with `[intent.Path.Value]` (`AppIntentDispatcher.cs:241-242`). "Open with" uses `LaunchAsync(appId, [fullPath])` (`ExplorerFileView.Operations.cs:125`).
  - The handler then reads the file with its own capabilities. Compare the context-menu launcher's note (`FileContextMenuLauncher.cs:13-18`).

**b) `shell.context-menu`**
- FACT: who may register.
  - Window or Service apps only (`AppManifestValidator.cs:465-476`).
  - They need `shell.context-menu`, which is normal (`AppCapabilities.cs:182, 281`).
- FACT: entries are static.
  - They are written into the manifest at build, or recorded once at install and each update. They are copied to `/etc/kazoos/context-menus.d/<appId>.json` (root, 0644).
  - No app code runs to draw a menu (`Shared/Kazo.Os.App.Abstractions/AppManifestContextMenus.cs:5-11`; `doc/dev/adr/0151-app-registered-file-menu-entries.md` §2-3).
  - The files are synced after every change of the installed state (`OS/Kazo.Os.Ecosystem.Host/EcosystemServiceCollectionExtensions.cs:1346-1363`).
  - Filters are scope (file, directory, background, desktop), types (extension, media type, `image/*`, `*`), selection (all, each, single) and screens (`AppManifestContextMenus.cs:41-79`). Submenus go at most 3 deep (`:129-130`). Users can hide entries (`FileContextMenuAdministration.cs:47-68`).
- FACT: what an entry launches.
  - `Target` is `self` or another app's id (`AppManifestContextMenus.cs:27-28`). A target other than self must declare the entry's intent (`AppManifestValidator.cs:510-518`; `FileContextMenuLauncher.cs:52-72`).
  - The launch uses the target's own rights. The sender is stamped `org.kazoos.shell` (`FileContextMenuLauncher.cs:8-22`).
  - The payload is `{"paths":[...],"scope":"File","entry":"<name>"}`, with one launch per item or one for all (`FileContextMenuLauncher.cs:83-103, 151-174`).
  - If the target does not declare the intent, it gets the arguments `["<intent>", "<path>", ...]` instead (`:100-102`).
- FACT: a Service can be the target and is started on demand by the click. Precedent: `SetWallpaperService` (`Apps/System/Kazo.Os.Apps.Settings/SetWallpaperService.cs:16-23, 74-87`; ADR 0218 §1).

**c) Terminal commands (Terminal-kind app)**
- FACT: the command API.
  - The entry point is `TerminalAppBase.ExecuteAsync(TerminalExecutionContext, CancellationToken) → ValueTask<int>`. The int is the exit status (`Shared/Kazo.Os.AppSdk/TerminalAppBase.cs:17-25`).
  - The context holds the arguments, `TextReader` stdin, `TextWriter` stdout and stderr (UTF-16 text, not bytes), the working directory, a read-only environment snapshot, an optional full-screen session, and an `IShellSession` only when it holds `shell.control` (`TerminalExecutionContext.cs:23-85`).
  - `ReadSecretLineAsync` gives no-echo input (`:96-119`).
- FACT: how it ends.
  - Ctrl+C cancels the line's token once and writes ^C. A second Ctrl+C does nothing (`Shared/Kazo.Os.AppSdk.Terminal/TerminalPanel.razor:359-363, 1132-1139`).
  - In full-screen mode every key, Ctrl+C included, goes to the command (`TerminalPanel.razor:355-358`).
  - Exit codes are 130 (interrupt), 137 (kill), 141 (broken pipe) and 152 (CPU limit) (`Platform/Kazo.Os.Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs:1126-1154, 1211-1221`).
- FACT: long runs are allowed.
  - There is no command timeout. The 30 s start timeout applies to services only (`AppLifecycleOrchestrator.cs:196-198, 1519-1536`).
  - The default CPU rlimit is unlimited (`doc/dev/adr/0125-process-resource-limits.md:107-117`).
  - A command lives for one shell line (`doc/framework/app-intents.md:106-107`).
- FACT: names and collisions.
  - Each Terminal app has one name plus aliases (`AppManifest.cs:251-260`).
  - On a collision, system apps win, then the first app in activation order. Activation order is topological by dependency, with ties broken by ordinal app id (`AppCatalog.cs:333-370` and `CreateActivationOrder`). So a dependency such as Os-x86 beats an app that depends on it.
  - The loser gets a `catalog.command.duplicate` warning in syslog, not an install refusal. It is still runnable by its full app id (`ShellCommandResolver.cs:43-56`).
- FACT: other apps calling a command.
  - Another app can run a command with streams and get its exit code only through `KazoShell` or the embedded `IKazoTerminal` (`RunCommandAsync` gives the status and text). Both need `shell.host`, which is dangerous (`Shared/Kazo.Os.AppSdk/Shell/KazoShell.cs:6-10`; `IKazoShell.cs:76-95`; `Components/IKazoTerminal.cs:9-12, 58-68, 125-130`; `AppCapabilities.cs:274-276`).
  - `ExecuteCommandIntent` (buffered stdout, stderr and exit code) exists in the dispatcher (`AppIntentDispatcher.cs:247-335`). It is not on the SDK `IAppIntentGateway` (`AppGatewayContracts.cs:500-587`).
- FACT/INFERENCE: forwarding a stream to another app or a worker.
  - No OS API hands a stream to another app.
  - INFERENCE: it is possible only in-process. All apps share the default AssemblyLoadContext (`OS/Kazo.Os.Ecosystem.Host/InstalledAppsBoot.cs:300-316`), and topic payloads are delivered by object reference on the main thread when the CLR types are identical (`Platform/Kazo.Os.Platform.Core/Events/InMemoryTopicMessageBus.cs:121-133`). This is cooperative and breaks for worker subscribers, which get JSON (ADR 0124).
  - A command may inject `IJSRuntime` (`Tools/Kazo.Os.Tools.ManifestGenerator/SdkBoundaryInspector.cs:6-8, 74-80`). INFERENCE: it can therefore pump its streams to its own JS worker.

**d) Cross-app calls today**
- FACT: `LaunchAsync`.
  - `IAppIntentGateway.LaunchAsync(appId, args)` needs `apps.launch` (normal). Launching yourself is exempt (`AppIntentDispatcher.cs:209-219`).
  - It returns only `AppIntentLaunchResult(Outcome, ErrorCode)` (`AppGatewayContracts.cs:423`; `Platform/Kazo.Os.Platform.Core/Execution/AppIntentGateway.cs:21-47`).
  - The launched app is not a child of the caller (no ParentPid, `AppIntentDispatcher.cs:218`). It runs with its own rights, and a plain launch carries no sender id.
- FACT: app X can open Y's window with arguments through `LaunchAsync`. If Y is single-instance and running, the arguments arrive through `IntentReceived` (`AppLifecycleOrchestrator.cs:324-337, 432-439`).
- FACT: intents are one-way.
  - `SendIntentAsync` takes a JSON object of at most 64 KiB (`AppIntents.cs:47-48`; `AppGatewayContracts.cs:569-586`).
  - The sender id is OS-stamped (`AppIntentDispatcher.cs:581-583`). The result is only an outcome and candidates (`AppGatewayContracts.cs:487-490`).
  - A not-yet-running Service target is started by the intent (SetWallpaperService precedent above; `ServiceAppBase.cs:225`).
  - Intent ids are not tied to the declaring app's domain (`AppManifestValidator.cs:772-791`). Any app may declare Os-x86's or BoxedWine's intent id, which forces a chooser unless the sender passes `targetAppId`.
- FACT: topic bus.
  - Publishing is limited to the owner's namespace or to a shared-channel policy. The publisher app id is stamped by the bus (`InMemoryTopicMessageBus.cs:238-272`; `TopicMessagingContracts.cs:133-137`).
  - Anyone may subscribe to a topic not covered by a registered shared channel (`InMemoryTopicMessageBus.cs:274-280`).
  - Shared-channel roots are first-come, with no namespace check (`:221-234`). INFERENCE: another app could register `app/<osx86>/...` first.
  - Per-PID topics give unicast-like addressing by convention only (`doc/dev/adr/0040-declared-topic-permissions.md:57-61`).
- FACT: service control (start, stop, status) is allowed for the same assembly or with `services.manage`, which is dangerous (`AppGatewayContracts.cs:612-675`; `AppIntentDispatcher.cs:620-660`).
- FACT: a Service has no UI API (`ServiceAppBase.cs:33-226`). To show UI it can:
  - launch its companion Window app, which is another app id and needs `apps.launch`; or
  - post a notification whose action launches any app with arguments (`NotificationContracts.cs:78-99`). Other action ids do nothing (`Platform/Kazo.Os.Platform.Blazor/Shell/NotificationActionRunner.cs:12-14`).
- FACT: only one session exists at a time (`doc/dev/adr/0201-switch-user-stays-greyed.md`, Decision). Singleton lookup is by app id only (`Platform/Kazo.Os.Platform.Core/Processes/InMemoryProcessManager.cs:396-405`).

**e) Request/response between apps**
- FACT: no OS request/response primitive exists.
  - Every gateway result is a status. A Terminal target launched through `LaunchAsync` runs to the end because the orchestrator awaits it (`AppLifecycleOrchestrator.cs:565-568`). Its exit code and output are computed (`:1150-1154`), then dropped by the gateway (`AppIntentGateway.cs:34-46`).
  - The only answer-carrying paths are `KazoShell` and `IKazoTerminal` (dangerous `shell.host`), and in-process calls into a shared assembly (cooperative, ambient identity can be forged).
- INFERENCE: an "Os-x86 answers BoxedWine" exchange today has to be built by the apps. Two shapes:
  - An intent request (sender stamped by the OS) plus a reply topic. The reply topic is readable by any app unless a shared channel requiring a capability guards it.
  - A typed in-process Os-x86.Interface call.
- Under D19 and D21 the network prompt is answered by the user to Os-x86, so BoxedWine needs no reply for that prompt.

**f) binfmt hook**
- FACT: no binfmt or MZ/PE/ELF classification exists anywhere (grep).
  - `./file` is refused with 126 and the message "cannot execute binary file". The x-bit check runs first (`ShellInterpreter.Scripts.cs:57-66`), then the whole file is read as text (`:68`), then the NUL check fails (`:78-83`).
  - The exact message is `Platform/Kazo.Os.Platform.Core/Resources/Strings.resx:474-476`.
- FACT: insertion points.
  - The site is between `Scripts.cs:66` and `:68`. Reuse the shebang route `LaunchCommandAsync(resolution with {Arguments=[path, ..args]})` (`:93-116`, `ShellInterpreter.SimpleCommands.cs:188-231`). That route wires the streams, parent PID, Ctrl+C token, elevation and exit code.
  - The shebang route resolves Terminal-kind apps only (`ShellCommandResolver.cs:36-59, 101-106`). A Window target would need the `LaunchApplicationAsync` route (`SimpleCommands.cs:259-310`): exit code 0, and Ctrl+C kills the window.
  - `setup.exe` typed without `./` falls through to "command not found" (`SimpleCommands.cs:151-184`). That is a second possible hook.

**g) "Owned or protected on behalf of another app"**
- FACT: no generic notion exists. The closest:
  - `~/.config/<appId>/` needs only `filesystem.private.*` for its owner, but any app with `filesystem.user-home.write` can write it (`Platform/Kazo.Os.Platform.Core/FileSystem/FileSystemService.cs:759-786`).
  - `/etc/<appId>/` is readable by its owner and writable only with `system.write` plus administrator authority (`:770-776`).
  - The keyring is the only store the OS keys by app id. Other apps' entries need `keyring.read-all`, which is dangerous (`Platform/Kazo.Os.Platform.Core/Keyring/AppKeyringGateway.cs:39-72`).
  - An app can kill its own descendants without `process.manage` (`AppProcessGateway.cs:148-170`).
  - Topic namespaces and shared channels have owners (see d).
  - Admins can lock settings keys (`Shared/Kazo.Os.AppSdk/Settings/KazoSettings.cs:9`).
  - `ProtectedEntry` is about mount roots only.
- FACT: files belong to a user, never to an app. Installed apps cannot be boot services, so they never run as `kazoos-svc` (`AppLifecycleOrchestrator.cs:801-827`; `AppIntentDispatcher.cs:647-656`).
- INFERENCE:
  - "Created by BoxedWine, not modifiable by others" must be enforced by Os-x86's own state.
  - That record is trustworthy only if the creator id comes from an OS-stamped channel (intent `SenderAppId`, topic `PublisherAppId`) or from the future verified-caller ADR (D32).
  - It is enforced against guest code. Against other .NET apps it is cooperative only. That includes OPFS (D48), which shares the OS origin.

**Implications of the 40 override**
- INFERENCE: to receive `.exe` from all three surfaces, BoxedWine needs at least two app ids today (one kind per manifest, `AppManifestValidator.cs:295-319`):
  - a Window app for FileHandlers or context-menu entries;
  - a Terminal command for binfmt, console I/O and a real exit code.
- INFERENCE: this conflicts with two accepted answers.
  - Q38 A says only Features register binfmt handlers.
  - Q37 A gives multi-entry-point identities to Features only.
  - Either BoxedWine becomes a Feature, or the binfmt and multi-entry ADRs must allow ordinary apps.