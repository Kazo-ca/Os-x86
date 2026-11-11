# Kazo facts for the series-5 model (BoxedWine as an ordinary app, Os-x86 as a Feature)

Base: the read-only Kazo clone at commit cd89ee0. In the citations, `K/` means `wasm2/KazoOs/` and `ADR/` means `doc/en/wasm/dev/adr/`.

## a) Start menu and `.desktop` shortcuts

**Start menu**
- FACT: The Start menu shows one entry per catalog manifest. It is built from `Catalog.Manifests.Values` (`K/Platform/Kazo.Os.Platform.Blazor/Shell/AppLauncher.razor.cs:349-356`) through `LauncherAppProjection.Create` (`K/Platform/Kazo.Os.Platform.Blazor/Shell/LauncherAppProjection.cs:28-43`).
- FACT: Only some apps are listed: window apps marked `Visible`, enabled, and supported on desktop (`LauncherAppProjection.cs:83-94`). Terminal, service and web apps never appear.
- FACT: The name is the manifest name in the session language (`AppLauncher.razor.cs:352`). The grouping comes from `presentation.category` (`LauncherAppProjection.cs:45-50`).
- FACT: The icon is chosen in this order: the icon pack's image for the app id, then the first `presentation.iconAssetPaths`, then the pack's category image, then a Lucide icon for the category (`ShellAppIcon.razor:5-8`; `ShellAppIcon.razor.cs:43-51`; `ADR/0232...md:71-79`).
- FACT: An icon must be a package asset of kind `image`, declared in `assets` with its SHA-256 (`K/Shared/Kazo.Os.App.Abstractions/AppManifestPresentation.cs:25-29`; `AppManifestValidator.cs:663-666, 682-688`; `AppManifestAssets.cs:10-12`). It is served under `_content/<assembly>/` (`ADR/0164...md:20-22`).
- FACT: A Start-menu launch never passes arguments: `HandleAppSelected(manifest) => LaunchAsync(manifest, [])` (`K/Platform/Kazo.Os.Platform.Blazor/Shell/DesktopShell.razor:383`).
- FACT: Some parts are per user:
  - Pins are stored per user in `~/.config/kazoos/start-menu.json`, as app ids only, at most 64 (`K/Platform/Kazo.Os.Platform.Core/Shell/StartMenuSettingsDocuments.cs:6-18`; `StartMenuPreferencesService.cs:7-16`).
  - The catalog itself is built per session: system apps first, then that user's personal apps (`ADR/0189...md:76-78, 144-145`; `K/OS/Kazo.Os.Ecosystem.Host/HostAppCatalog.cs:82-131`).
- FACT (based on a search for absence): No API or ADR lets an app add extra Start-menu entries with their own name, icon or arguments. The phrase `/usr/share/applications` appears only as an analogy in comments (`K/Shared/Kazo.Os.AppSdk/Apps/AppCatalogGatewayContracts.cs:9`; `KazoAppInfo.cs:8`).
- FACT: "Send to ▸ Desktop" in the Start menu creates a `.desktop` file (`AppLauncher.Menu.cs:90-91`; `ADR/0152...md:96-98`).
- INFERENCE: The ADR for answer 57 would need to add:
  1. A per-user entry record (display name, image, target app id, arguments) that the projection reads besides the manifests (`LauncherAppProjection.cs:39-43`).
  2. Launch with arguments (`DesktopShell.razor:383`).
  3. Pins of entries, not only app ids.
  4. Search over these entries.
  5. A way to draw an image that is not a package asset (`ShellAppIcon.razor.cs:43-51`).
  6. Who may create entries. If the entries live under `~`, any app with the normal `filesystem.user-home.write` can forge them, as it can `.desktop` files today.
  7. Cleanup or greying when the owning app is uninstalled.
  8. A link to the "who sent this" ADR of answer 52.

**`.desktop` files**
- FACT: The format is `[Desktop Entry]`, `Type=Application`, `Name`, `Exec=<appId> [args]`, and an optional `Icon` (`ADR/0152...md:91-104`; `ADR/0160...md:136-148`; `K/Shared/Kazo.Os.AppSdk/Files/KazoDesktopEntry.cs:7-29`). A file is read only up to 16 KiB (`KazoDesktopEntry.cs:38`).
- FACT: How `Exec` is resolved:
  - The first word must look like an app id (`KazoDesktopEntry.cs:63-67, 204-208`).
  - Field codes such as `%f` are dropped (`KazoDesktopEntry.cs:244-253`).
  - Opening the file checks that the app is in the user's catalog, then calls `Intents.LaunchAsync(entry.AppId, entry.Arguments)` (`K/Apps/System/Kazo.Os.Apps.FileExplorer/ExplorerFileView.Desktop.cs:460-475`).
  - A missing app is shown greyed and gives an error (`ExplorerFileView.Desktop.cs:318-321, 467-470`).
- FACT: The `Icon` field is read (`KazoDesktopEntry.cs:72-75`) and written (`KazoDesktopEntry.cs:87-90`) but **never displayed**. The file view draws `AppShortcutIcon(entry.AppId, app)`: the pack image, else the app's `IconUrl`, else the category icon (`ExplorerFileView.Desktop.cs:316-321`; `ExplorerFileView.IconPack.cs:27-39`).
  - So no meaning exists today for a VFS path, PNG only, or a size limit.
  - Every shortcut to BoxedWine would show BoxedWine's own icon.
- FACT: `CreateShortcutAsync` has no icon parameter (`KazoDesktopEntry.cs:107-123`).
- FACT: The renderer accepts "asset path or data URI" (`.../FileControl/Icons/ShellIconDescriptor.cs:16-17, 38-40`).
- FACT: Who may write: `~/Desktop` needs `filesystem.user-home.write` (`K/Platform/Kazo.Os.Platform.Core/FileSystem/FileSystemService.cs:776-787`), which is a Normal capability (`K/Shared/Kazo.Os.App.Abstractions/AppCapabilities.cs:203`). The shortcut is written with the calling app's rights (`KazoDesktopEntry.cs:95-98`).
- FACT: Kazo has no `.lnk` handling (searching `*.cs` for `.lnk` finds nothing). `.exe` has only an icon mapping (`.../Icons/DefaultShellIconProvider.cs:25`).
- INFERENCE: The badge that BoxedWine draws at the bottom-right of each icon would be BoxedWine's own image work, since Kazo has no image-composition API. The shortcut ADR must:
  - make `Icon=` meaningful (a VFS path to a PNG, with a size cap);
  - draw it, for example as a data URI;
  - add an icon parameter to `CreateShortcutAsync`.

## b) Window icon, title and taskbar

- FACT: A window's icon is set once, when the window is created, by `IconFor(manifest)`: the pack image, else the first declared icon (`K/Platform/Kazo.Os.Platform.Blazor/Windows/WindowLaunchCoordinator.cs:111-117, 285-302`; `ADR/0164...md:20-22`).
- FACT: The taskbar button reads `window.IconAssetPath` and `window.Title` from the window's state (`.../Shell/TaskbarAdapters.cs:46-51`; `K/Platform/Kazo.Os.Taskbar.Blazor/Taskbar.razor:29-49`). Previews and other views reuse the same snapshot (`ADR/0164...md:21-23`).
- FACT: Title at run time: `SetWindowTitleAsync` needs no capability and changes only this instance's window. The title is cleaned and capped at 256 characters (`K/Shared/Kazo.Os.AppSdk.Blazor/WindowAppBase.WindowTitle.cs:53-70`; `ADR/0117`).
- FACT: Icon at run time: **no app API exists**.
  - `IAppWindowGateway` has title, options, minimize/maximize/restore and move/resize, but no icon (`K/Shared/Kazo.Os.AppSdk.Blazor/IAppWindowGateway.cs:16-89`).
  - The window manager does have `SetWindowIconCommand` (`K/Platform/Kazo.Os.Windowing.Abstractions/WindowRuntimeMessages.cs:112-118`; `K/Platform/Kazo.Os.Windowing.Core/WindowRuntime.cs:114, 567-578`). It accepts any string URL.
  - Only the shell issues that command, when the icon pack changes, and it then reapplies the **manifest** icon to every open window of the app (`WindowLaunchCoordinator.cs:119-127`).
- INFERENCE: The window-icon ADR of answer 57 can expose `SetWindowIconAsync` on `IAppWindowGateway`, `WindowAppBase` and `KazoWindowBase`, mapped to the existing command; the taskbar then follows automatically. It must also:
  - change `RefreshWindowIcons` so an icon-pack change does not wipe a per-window icon;
  - say where the image comes from (package asset, or a VFS PNG turned into a data/blob URL).
- FACT: There are several windows per app in two ways:
  - **Separate instances.** Each launch starts a new instance unless `SingleInstancePerUser` is set (`K/Shared/Kazo.Os.App.Abstractions/AppManifest.cs:120-125`; `K/Platform/Kazo.Os.Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs:431-439`). Each instance has one window, found by its owner instance (`WindowLaunchCoordinator.cs:244-248`; `ADR/0234...md:29-34`).
  - **Child windows.** `ShowWindowAsync` and `ShowDialogAsync` belong to the same instance and close with the app or with their owner (`ADR/0234...md:83-99`; `WindowAppBase.ChildWindows.cs:12-37`). A child window gets its own title but takes its owner's icon (`.../Windows/AppWindowGatewayFactory.cs:99-102, 140-141`).
- FACT: The taskbar does **not group** by app: one button per window (`Taskbar.razor:29-50`; `ADR/0234...md:33-34`). `ShowInTaskbar=false` removes a window's button (`TaskbarAdapters.cs:35-38`; `ADR/0234...md:74`).

## c) Terminal app and commands

- FACT: Kazo has four app kinds: window, terminal, service and web (`K/Shared/Kazo.Os.App.Abstractions/AppKind.cs:12-28`). No "Feature" kind exists.
  - Command metadata is allowed only on terminal apps (`AppManifestValidator.cs:295-316`).
  - File handlers are allowed only on window apps (`AppManifestValidator.cs:318-321`).
  - Embeddable components are allowed only on window apps (`AppManifestValidator.cs:419-422`).
- FACT: A terminal command has no UI. `TerminalAppBase` has only `ExecuteAsync` (`K/Shared/Kazo.Os.AppSdk/TerminalAppBase.cs:8-25`).
  - Its context gives it streams, an optional `FullScreen` session, and `Shell` for `shell.control` (`TerminalExecutionContext.cs:53-85`).
  - Child windows are `protected` members of `WindowAppBase`/`KazoWindowBase`. So a command can open neither its own window nor a child window of the Terminal app (`WindowAppBase.ChildWindows.cs:21, 37`).
- FACT: A command can start a window app with `Intents.LaunchAsync` (needs the normal `apps.launch`).
  - That launch has no parent, no streams and no caller recorded (`K/Platform/Kazo.Os.Platform.Core/Intents/AppIntentDispatcher.cs:207-217`).
  - The result carries no process id (`K/Shared/Kazo.Os.Simulation.Abstractions/Gateways/AppGatewayContracts.cs:423`), so the command cannot wait for that window to close.
- FACT (important for 51 D and 60 A): The shell itself already runs **window apps as commands**.
  - When no command has the typed name, a window app whose id ends with that name runs with the shell's stdin, stdout and stderr (`K/Platform/Kazo.Os.Platform.Core/Shell/ShellInterpreter.SimpleCommands.cs:177-179, 252-277`; `ShellCommandResolver.cs:61-82`).
  - The shell waits for the window to close and returns its exit code; Ctrl+C sends `InterruptAsync` (`SimpleCommands.cs:285-295`).
  - Output written with `KazoConsole` appears in the terminal (`K/Shared/Kazo.Os.AppSdk/KazoConsole.cs:3-8`).
  - Limits: `fullScreen` is null for window apps (`SimpleCommands.cs:277`). A single-instance app is only focused and returns 0 (`SimpleCommands.cs:281-283`). Two apps with the same last id segment make the name fail (`ShellCommandResolver.cs:76-81`).
  - INFERENCE: An ordinary BoxedWine window app whose id ends in `.boxedwine` is therefore already runnable as `boxedwine …`, with its exit code, as long as it is not single-instance.
- FACT: The Terminal app:
  - id `org.kazoos.terminal`, a window app, single instance with tabs (`K/Apps/System/Kazo.Os.Apps.Terminal/TerminalApp.cs:45`; `manifest.json`);
  - capabilities: `apps.launch`, `filesystem.system.read`, `filesystem.user-home.*`, `notifications.post`, `process.list`, `process.manage`, `shell.host` (`manifest.json`);
  - its window holds only `TerminalTabs` (`TerminalWindow.razor:11`). The panel lives in the shared library `K/Shared/Kazo.Os.AppSdk.Terminal`.
- FACT: Embedding another app's component:
  - `KazoEmbeddedComponent<TController>` shows whatever an installed app registered for a contract (`K/Shared/Kazo.Os.AppSdk.Blazor/Components/KazoEmbeddedComponent.razor.cs:7-55`).
  - Contract names are free-form dotted words, except the contracts the platform provides itself (`AppManifestValidator.cs:436-448`).
  - The embedded component runs in the **host's** process with the **host's** capabilities (`ADR/0063...md:62-63`).
  - Nothing lets a running command ask its terminal to show a component. The only extra display path is `IFullScreenTerminalSession`, which is text only: lines of strings plus a cursor (`K/Shared/Kazo.Os.AppSdk/FullScreenTerminalContracts.cs:60-85`).
- INFERENCE: Answers 53 and 55 ("the terminal shows the rendering") mean a Kazo pull request to the Terminal app and the `Kazo.Os.AppSdk.Terminal` library, so a tab can host an Os-x86 guest view. That view would then run with the Terminal's rights, including `shell.host` and `process.manage`.
- FACT: Today's terminal input and output, which a "raw mode" would change:
  - Command output goes to xterm.js unchanged except `\n` → `\r\n`, so ANSI sequences work (`ADR/0061...md:50-67`).
  - Standard input is canonical, line by line only (`K/Shared/Kazo.Os.AppSdk.Terminal/TerminalInputReader.cs:6-12`).
  - The only input-mode flag is ECHO (`TerminalExecutionContext.cs:122-133`).
  - Ctrl+C cancels the command once and prints `^C` (`TerminalPanel.razor:1132-1139`).
  - The full-screen key set has no Tab, Insert or F1-F12 (`FullScreenTerminalContracts.cs:4-34`).
  - The streams are `TextReader`/`TextWriter` text (`TerminalExecutionContext.cs:59-66`).
  - The full-screen session is given only to terminal commands, and only when their stdout is the terminal (`SimpleCommands.cs:493`).
- FACT: Running a file path in the shell (`ShellInterpreter.Scripts.cs`):
  - `RunPathAsync` (line 26) checks the execute bit first (57-66), then reads the whole file as text (68), then refuses a file containing NUL (78-83).
  - The `#!` route resolves the interpreter as a terminal command only (`Resolver.Resolve`, 99) and runs `LaunchCommandAsync(... Arguments=[path, ..args])` (116).
- INFERENCE: A binfmt hook that points to a terminal entry point fits before line 57, since decision 35 allows `.exe` without the execute bit, and can reuse the line-116 route. A binfmt that points to a window entry point would reuse `LaunchApplicationAsync` instead.

## d) "Ask the Shell to execute it" (answer 52)

- FACT: To open a file by association, an app calls `Intents.OpenFileAsync` or `KazoApps.OpenFileAsync`; the user can type `xdg-open` (`AppGatewayContracts.cs:511-526`; `K/Shared/Kazo.Os.AppSdk/Apps/KazoApps.cs:152`; `K/Apps/Commands/Kazo.Os.Commands/XdgOpen/XdgOpenCommand.cs:11-21, 84`).
  - The handler is chosen by extension among window apps only (`AppIntentDispatcher.cs:221-240`; file handlers only on window apps, `AppManifestValidator.cs:318-321`).
  - The handler is launched with `[path]` and **no caller id** (`AppIntentDispatcher.cs:239-240`).
  - INFERENCE: This path reaches **BoxedWine** through the `.exe` association.
- FACT: To run a command line:
  - `ExecuteCommandIntent` is handled by the dispatcher and checks only `apps.launch` (`AppIntentDispatcher.cs:177, 247-266`). But no code outside tests ever builds one, and the SDK has no facade for it. Only the record exists (`K/Shared/Kazo.Os.App.Abstractions/AppIntents.cs:129`).
  - The SDK routes that run a command line are `KazoShell` and an embedded `IKazoTerminal`, and both need the dangerous `shell.host` (`K/Shared/Kazo.Os.AppSdk/Shell/KazoShell.cs:6-10, 40-43`; `AppCapabilities.cs:273-276`).
  - The hsh path route never looks at file associations (`ShellInterpreter.Scripts.cs:26-125`).
  - INFERENCE: A command line such as `./setup.exe` would reach the future binfmt handler (an **Os-x86** Feature entry point, since rule 38 says only Features register binfmt), not BoxedWine.
- INFERENCE: The two readings of "the Shell" in 52 lead to different receivers:
  - the desktop shell's association → BoxedWine;
  - hsh → binfmt → Os-x86.
  
  The sender ADR of 52 has to cover both, because neither route records the calling app today.

## e) Installing an ordinary app; dangerous capabilities; dependencies

- FACT: A user without admin rights can install an app for themselves from a Verified or Trusted source. An admin is needed only for a system install or an untrusted source (`ADR/0189...md:69-82`; `K/Platform/Kazo.Os.Platform.Core/Apps/Packages/AppPackageInstaller.cs:42-46`; `K/Platform/Kazo.Os.Platform.Core/Execution/AppManagementGateway.Packages.cs:129-160`).
  - The admin's block list (by app, category or capability, for example `network.real.access`) forces a system install (`ADR/0189...md:100-120`; `AppPackageInstaller.cs:554-567`).
  - The default list is empty (`AppPackageDefaults.cs:65-77`).
- FACT: Dangerous capabilities (`network.real.access`, `shell.host`) are **not** granted at install.
  - The install approval window shows them (`AppManagementGateway.Packages.cs:139-152`), but install writes no "allow" (no reference to `AppPermissionDecisions` in the install path).
  - At launch, `DangerousCapabilityGate.EnsureDecidedAsync` asks for admin credentials: an OS window from the desktop, or text mode in the terminal (`AppLifecycleOrchestrator.cs:469`; `ADR/0049...md:146-180`).
  - The "allow" goes to the machine file and so covers every user; a user's "deny" stays per user (`ADR/0049...md:114-144`).
  - Normal capabilities are granted at once at hot install (`doc/en/wasm/framework/capabilities.md:110-120`).
  - A capability declared by a provider app does not exist; an unknown one is refused (journal; `AppManifestValidator.cs:846-863`).
- FACT: Dependencies:
  - The installer does not resolve or check dependencies (no hit for "Dependenc" in `Apps/Packages/*` or in `AppManagementGateway*`).
  - At catalog build, a missing required dependency gives `catalog.dependency.missing` (`K/Platform/Kazo.Os.Platform.Core/AppCatalog.cs:487-503`).
  - The installed app is then skipped with a warning, while the other apps load (`HostAppCatalog.cs:99-128`).
  - INFERENCE: A user-installed BoxedWine that requires a system-only Os-x86 Feature that is absent installs successfully but stays invisible, with only a log line. The dependency ADR (decision 12) must cover user-scope installs that depend on a system-only Feature.

## f) Resource information (59 C: "if resources allow")

- FACT: ADR 0125 sets per-process rlimits (cpu, rss, nofile, nproc), inherited by child processes. Defaults: cpu and rss unlimited, nofile 1024/524288, nproc 4096 (`ADR/0125...md:29-48, 107-117`). Enforcement is strict in a worker and best effort on the main thread (`ADR/0125...md:50-63`).
- FACT: Kazo's memory numbers are not the real machine's:
  - `MemTotal` is a **simulated** hardware profile, 1 core and 2 GiB by default (`K/Platform/Kazo.Os.Platform.Core/Processes/SystemMemoryProbe.cs:6, 58-59`; `K/Shared/Kazo.Os.Simulation.Abstractions/Processes/ResourceSimulationContracts.cs:53-55`; `K/OS/Kazo.Os.Ecosystem.Host/HardwareServiceCollectionExtensions.cs:15-16, 31`).
  - Per-process RSS is an estimate that splits the .NET heap among processes (`ADR/0068...md:17-19, 37`).
  - The manifest `resources` weights in [0,1] are simulation inputs, not budgets (`AppManifestResources.cs:13-20`; `AppManifestValidator.cs:703-709`).
- FACT: OOM guard (ADR 0126 §5): Kazo tracks the size of `WebAssembly.Memory`, against the 4 GiB wasm32 ceiling or `jsHeapSizeLimit`, for the main runtime and each **Kazo** worker. It warns at 80% (`ADR/0126...md:82-93`).
- FACT: Browser APIs Kazo already uses:
  - `navigator.hardwareConcurrency`, as the worker ceiling (`K/Platform/Kazo.Os.Platform.WorkerHost/Host/WorkerPool.cs:19`; `K/Infrastructure/Kazo.Os.Infrastructure.Browser/wwwroot/workerHost.js:33-35`);
  - `performance.measureUserAgentSpecificMemory()` when cross-origin isolated; Kazo's own comment says the answer may take several seconds (`workerHost.js:120-132`);
  - `navigator.storage.estimate()` (`.../wwwroot/storageManager.js:4`).
  - Cross-origin isolation is on by default: `AuditPassed = true` (`K/Platform/Kazo.Os.Platform.Core/Isolation/CrossOriginIsolation.cs:56-60`; `ADR/0131...md:30-54, 70-87`).
  - Kazo does not use `navigator.deviceMemory` anywhere.
- FACT (web docs):
  - `navigator.deviceMemory` is rounded to a power of two, clamped to implementation-defined bounds, available only in secure contexts, non-standard and of limited support (https://developer.mozilla.org/en-US/docs/Web/API/Navigator/deviceMemory).
  - `measureUserAgentSpecificMemory` requires a secure, cross-origin isolated context, is experimental and of limited support (https://developer.mozilla.org/en-US/docs/Web/API/Performance/measureUserAgentSpecificMemory).
  - wasm32 memory is at most 65,536 pages × 64 KiB = 4 GiB, and the engine may clamp `maximum` (https://developer.mozilla.org/en-US/docs/WebAssembly/Reference/JavaScript_interface/Memory/Memory).
- UNVERIFIED:
  - the actual clamp values of `deviceMemory` per browser (the MDN compatibility table did not render);
  - whether v86 keeps guest RAM inside its own wasm memory (so each VM is capped below 4 GiB);
  - the per-tab memory limits of each browser.
- INFERENCE: No browser API gives free host RAM. Kazo's numbers ignore a v86 worker that Kazo's worker host does not own. So "fits" must be a heuristic of Os-x86's own, for example the sum of configured VM RAM against a budget, plus `deviceMemory` where available.

## g) Showing a choice (59) when the launch came from the terminal

- FACT: A service app has no UI. `IDialogService` acts "on behalf of a window app" (`K/Shared/Kazo.Os.AppSdk.Blazor/DialogContracts.cs:316-319`). Child windows are owner-modal at most (`ADR/0234...md:92-96`). Only OS overlays are modal for the whole system (`ADR/0049...md:182+`).
- FACT: Notifications:
  - Any app with the normal `notifications.post` can post one, for its own user only, with actions (`K/Platform/Kazo.Os.Platform.Core/Execution/AppNotificationGateway.cs:13-61`).
  - The queue is in memory (`.../Notifications/InMemoryNotificationQueue.cs:10`).
  - The shell acts only on its own registered actions or on `NotificationAction.LaunchApp(appId, label, args)`. App-defined action ids do nothing (`K/Platform/Kazo.Os.Platform.Blazor/Shell/NotificationActionRunner.cs:9-15, 30-51`; `K/Shared/Kazo.Os.Simulation.Abstractions/Notifications/NotificationContracts.cs:68-99`).
- FACT: What the terminal-side handler can do today:
  - write to the terminal and read a line from stdin (canonical; `TerminalInputReader.cs:6-12`), or use the text full-screen session;
  - launch an Os-x86 window entry point with arguments (`apps.launch`), but not wait for it (no process id, see c);
  - get an answer back through topic messaging (ADR 0040), `SendIntentAsync` (`AppGatewayContracts.cs:568-586`, 64 KiB), or shared in-process state.
- INFERENCE: The 59 C choice dialog (close another VM, reuse a running VM, create a new VM) is best shown as an Os-x86 window launched with an opaque request id, while the command waits on an Os-x86 channel for the answer. A notification with `LaunchApp` can serve as the fallback reminder.
- INFERENCE: Decision 61.5 forbids consent typed in the terminal. 59 is a resource choice, not a permission, so asking it in the terminal would need an explicit design decision.

## Effects on the series-5 answers

These follow from the facts above.
1. **51 D** (BoxedWine stays an ordinary app):
   - BoxedWine can hold only one kind, so a window app. Its `boxedwine` command (60 A) works through the window-by-short-name route, with streams and exit code (`SimpleCommands.cs:177-179, 252-310`), if it is not single-instance.
   - BoxedWine cannot register a binfmt (rule 38), so `./x.exe` in hsh reaches Os-x86, consistent with 53 and 55.
   - A user can install BoxedWine without an admin. Its dangerous capabilities still wait for admin approval at first launch.
2. **57**:
   - Three Kazo changes are needed: a window-icon API, Start-menu entries with icon and arguments, and a `.desktop` `Icon=` that is actually drawn.
   - Today `Icon=` is ignored, Start-menu launches carry no arguments, and taskbar buttons follow the window's icon state automatically.
3. **53 and 55** ("the terminal renders"): this needs a Kazo pull request to the Terminal app and its library. An embedded Os-x86 view would inherit the Terminal's rights, including `shell.host` and `process.manage`.