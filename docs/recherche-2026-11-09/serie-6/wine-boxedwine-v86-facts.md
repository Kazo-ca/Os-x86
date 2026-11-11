# Wine, Boxedwine and v86 facts checked for the series-5 model

Sources were read on 2026-11-09:
- Boxedwine at github.com/danoon2/Boxedwine, commit 9905baa (2026-11-09).
- Wine at the wine-11.0 tag (commit db11d0fe, 2026-01-13), read through the github.com/wine-mirror/wine mirror.
- v86 at commit 315b2b0 (2026-10-30), which is still master today.
- Linux at commit 7475ea8b5861.
- Alpine package pages, branch v3.24, x86.

Paths below are relative to those repositories. Local copies are in `<scratchpad>\bw`, `...\scratchpad\wine` and `...\scratchpad\v86`.

## a) Boxedwine's interface

**a1. There are two generations of interface.** [FACT]
- **The older one** uses the ImGui library and is in `source/ui`. Its last change is 9ebae42 (2026-10-03). Its model is containers holding shortcuts.
- **The newer ones** are native: WPF (`project/msvc/BoxedwineUI`), Swift on Mac and Python on Linux. Their last changes are dated 2026-11-08 and 2026-11-09. Their model is apps, each with its own private environment. The word "container" does not appear in them (searched `Dialogs.cs`, `MainWindow.Actions.cs`, the WPF README and `library.py`).

**a2. What a container is in the older interface.** [FACT]
- A container is a folder holding:
  - `container.ini`, with `Name`, `FileSystemZipName` and `Mount1..N` (`source/ui/data/boxedContainer.cpp:25-55,125-136`);
  - `root/`, a writable layer over a read-only "file system" ZIP (Tiny Core Linux plus one Wine version). Launching adds the ZIP and sets `root` (`:204-219`);
  - the Wine prefix, at `root/home/username/.wine` (`:389`);
  - `apps/*.ini` and `automation/` (`source/ui/data/globalSettings.cpp:369-379`).
- The help text calls a container "a virtual file system" and adds that "Each app can be installed into its own container" (`source/ui/utils/boxedTranslation.cpp:199-200`). The install screen recommends a new container per app (`:55-56,115-116`).
- **Settings per container** (`source/ui/controls/containersView.cpp`):
  - name (`:27`);
  - "File System", which is the Wine version. Changing it deletes `root/opt/wine` (`:35-46`; label at `boxedTranslation.cpp:249`);
  - storage location (`:48-53`);
  - Windows version, written to the prefix registry (`:57`; `boxedContainer.cpp:675-812`);
  - DirectDraw GDI and renderer (`:63-78`);
  - video memory, in debug builds only (`:81`);
  - mouse warp (`:87-95`);
  - one mount folder, which maps a drive letter to a host folder (`:97-114`; the data format allows several, `boxedContainer.cpp:35-49`);
  - "Run App" for Wine's built-in programs (`:116-137`; list at `boxedContainer.cpp:237-333`);
  - Components (`:139-161`), Tiny Core packages (`:163-173`) and winetricks fonts and DLLs (`:175-203`). All three download content.
- **Settings per app (shortcut)** are stored in `apps/N.ini`: Name, Cmd, Path, Args, Icon, Resolution, BPP, Fullscreen, VSync, Scale, CpuAffinity (host CPU count), PollRate, OpenGL, DXVK, mouse options and uid/euid (`source/ui/data/boxedApp.cpp:23-122`; `containersView.cpp:286-460`).
- **Not offered:** guest RAM, disk size or network settings. I searched both interfaces for "network", "socket" and "internet" and found none.

**a3. Installing and adding a shortcut in the older interface.** [FACT]
- There are four install types: run a setup program, copy a folder, mount a folder, or create a blank container (`source/ui/controls/installView.cpp:22-25,128-155`).
- The file picker accepts only `*.exe` (`:139-144`).
- The container choice is "Create New Container (Recommended)" or an existing container. A new container gets a name, a file system and a Windows version (`:166-190`).
- After setup, a "Create Shortcut" picker lists the new `.exe` files it finds (`:337-340`; `boxedTranslation.cpp:203-210`). Files under `C:\windows` are skipped (`boxedContainer.cpp:376-440`).
- The "New Shortcut: Choose App" button lists the container's `.exe` files plus Wine's built-in programs (`containersView.cpp:207-229`).
- **`.lnk` files are not imported.** The Start-menu `.lnk` scan is commented out (`boxedContainer.cpp:223`), and the scanning function itself carries a "TODO: parse link" note (`:442-472`). An app that is a link is launched as `wine C:\windows\command\start.exe <link>` (`boxedApp.cpp:216-218`).

**a4. Installing and adding a shortcut in the newer interfaces.** [FACT]
- **One app is one private environment:** `Applications/<UUID>/root` (WPF `README.md:216-217`). Identical Wine ZIPs are shared, stored as `WinePackages/<sha256>.zip`. The Wine package is pinned per app.
- **"Add to Library"** offers "App or installer", "Collection" or "Restore a backup" (`project/msvc/BoxedwineUI/Dialogs.cs:106-113`).
  - The file filter accepts `*.zip;*.exe;*.msi;*.iso;*.cue;*.json`, or a folder (`:184`). There is no `.lnk`.
  - After that comes a program chooser that lists the `.exe` files on drive C (`Core/LibraryRepository.cs:104-116`). Setup and uninstall programs are flagged as maintenance tools by name (`Core/Models.cs:139`; `Dialogs.cs:258-259,289`).
  - "Run another program" accepts an outside EXE or MSI "using this app's Windows files" (`Dialogs.cs:289,296`).
- **Fields per app** (`Core/Models.cs:29-68`): Name, Executable, Installer, `LastOpened`, Resolution, FullScreen, Arguments, Boxedwine arguments, CD-ROM images, WinePackage, WindowsVersion, renderer and OpenGL settings, and `CustomIconPNG`. The custom icon must be a PNG of at most 4 MB (`LibraryRepository.cs:92`).
- **Window and taskbar icon.** "Running apps use their library icon ... in the Windows title bar, Alt+Tab and taskbar. Each library app has a separate taskbar identity" (WPF `README.md:243-246`). On Linux, each app gets a hidden `.desktop` entry that supplies its icon and identity under Wayland (`project/linux/ui/README.md:247-252`; `boxedwine/desktop.py:45-57`, `NoDisplay` at `:55`). This is close to answer 57 (the window icon changes and the taskbar button follows).

**a5. How recent apps and settings are stored.** [FACT]
- **Newer interfaces:** everything is in `library.json` (`LibraryRepository.cs:38,48`; `library.py:188-199`).
  - Default location: `%LOCALAPPDATA%\Boxedwine` on Windows (WPF `README.md:187-188`) and `~/.local/share/boxedwine` on Linux (Linux `README.md:124-125,237-238`).
  - Writes are atomic, and a lock stops two launchers from opening the same library (`LibraryRepository.cs:18`; WPF `README.md:223-224`).
  - Recent apps come from `LastOpened`: "Recently Opened" on Mac (`LibraryView.swift:77`) and "Library, recent apps, search" on Linux (Linux `README.md:259`).
- **Older interface:** a global `boxedwine.ini` holds DataFolder, Theme, DefaultResolution, scale, OpenGL and window position (`globalSettings.cpp:94-128,189-204`). There is no list of recent items; the only "recent" in `source/ui` is unrelated help text (`boxedTranslation.cpp:252`).
- **Web build:** settings come from URL parameters: `root`, `overlay`, `app`, `p`, `args`, `resolution`, `cpu`, and `network` through a WebSocket gateway (`project/emscripten/boxedwine-shell.js:162-164,290-343`). Storage is in IndexedDB, one store per game key (`:202-221`).

**a6. Features with no precedent in Boxedwine.**
- [FACT] Boxedwine never creates visible OS shortcuts for apps; on Linux its `.desktop` entries are hidden.
- [UNVERIFIED] I found no `.lnk` import and no badge drawn over icons. A code search for IShellLink and CreateShortcut found only Boxedwine's own NSIS installer, so absence is likely but not proven for the Mac code.
- [FACT] Boxedwine strips `winemenubuilder.exe.so` from the Wine builds it ships (`tools/buildWine/build_wine.py:868-903`).
- [INFERENCE] In Wine 11, winemenubuilder is a Windows-format (PE) program, so that removal may do nothing there. Whether Boxedwine's Wine 11 still ships winemenubuilder is UNVERIFIED.

**a7. Vocabulary mapping.** [INFERENCE]

| Boxedwine | Ours |
|---|---|
| Container (prefix plus writable layer over a read-only base, plus Windows version and renderer) | bouteille (bottle), with its own guest user and network (20, 22) |
| File System (Tiny Core + Wine ZIP) | image d'Os-x86 with Wine pinned (35, 44) |
| App / Shortcut (`apps/N.ini`) | an entry in BoxedWine's library |
| Mount Folder | the bottle's visible folder (22) |
| Newer "app" with a private root | one bottle per program (20) |
| `LastOpened` | BoxedWine's recent files (51 D) |
| `CustomIconPNG` and taskbar identity | the SDK change for window icons (57) |

Boxedwine gives no model for the machine, disk or network screens the user wants in BoxedWine (51 D): those are VM-level here.

## b) Wine 11 winemenubuilder

**b1. When it runs.** [FACT]
- Saving a `.lnk` through `IPersistFile::Save` starts `winemenubuilder.exe -w "<lnk>"` as a detached process (`dlls/shell32/shelllink.c:285-341,368`).
- winemenubuilder ignores links that are not under Desktop or Start Menu (`programs/winemenubuilder/winemenubuilder.c:2230-2240`).
- `winemenubuilder.exe -a -r` is also registered under RunServices (`loader/wine.inf.in:1102`), which wineboot runs (`programs/wineboot/wineboot.c:1896-1900`).
- [INFERENCE] A `.lnk` that an installer copies as a plain file never goes through `IPersistFile::Save`, so it never triggers winemenubuilder.

**b2. What it writes inside the guest.** [FACT]
- **Start-menu entries:** `$XDG_DATA_HOME`, or by default `$HOME/.local/share`, then `applications/wine/<Start Menu path>.desktop`, plus a `.menu` file in `~/.config/menus/applications-merged` (`winemenubuilder.c:1340-1392,1405-1425,2821-2846`).
- **Desktop shortcuts:** a `.desktop` file in the prefix's Desktop folder, marked executable with `chmod +x` (`:1258-1263,1294-1298,2823`). shell32 links that folder to `XDG_DESKTOP_DIR` or `$HOME/Desktop` (`dlls/shell32/shellpath.c:2774-2822`).
- **Icons:** one PNG per icon size, at `~/.local/share/icons/hicolor/<W>x<H>/apps/<CRC16>_<name>.<index>.png` (`:1045-1060,1080-1135`).
- **Entry keys:** Name, `Exec=[env "WINEPREFIX=..."] wine "<path>" "<args>"`, Type, StartupNotify, Comment, Path, Icon and StartupWMClass (`:1268-1292`).
- **File associations (`-a`):** it writes MIME packages and then runs `update-mime-database` and `update-desktop-database` (`:2615-2650`).
  - It never registers `.bat`, `.com`, `.exe`, `.msi`, `.url` or `file:`, "to evade malware created file type associations" (`:1953-1967`). This is relevant to point 61.10.
  - Associations alone can be switched off with `HKCU\Software\Wine\FileOpenAssociations\Enable` (`:1794-1800,2849-2864`).

**b3. Turning it off.** [FACT]
- `WINEDLLOVERRIDES=winemenubuilder.exe=d` works. In Wine 11, a main program whose load order is "disabled" ends with `STATUS_DLL_NOT_FOUND` (`dlls/ntdll/unix/loader.c:1485,1514-1521`).
- This was broken in Wine 6.5 and 6.6 and fixed in 6.7 (bugs.winehq.org bug 50956; commit f1e614027b).
- Alpine v3.24 x86 ships the program in the `wine` package, at `/usr/lib/wine/i386-windows/winemenubuilder.exe` (pkgs.alpinelinux.org contents search).

**b4. Could BoxedWine use these files to propose programs to add?** [INFERENCE] Possible, but the risks are:
- The files land in the bottle user's guest home, outside the prefix.
- Name, Exec and Icon come from untrusted `.lnk` data, so BoxedWine must never run an Exec line.
- winemenubuilder only sees shortcuts saved through `IShellLink`.
- It encodes PNGs inside the slow VM.

A safer route:
1. Keep winemenubuilder disabled for normal launches.
2. List the `.lnk` files in the two Start Menu folders, under AppData\Roaming and under ProgramData (`shellpath.c:1197-1201,1293-1297`).
3. For an icon, run `winemenubuilder -t <unix .lnk path> <out.png>` without the override. That command loads the `.lnk` and writes its icon as a PNG (`winemenubuilder.c:2701-2783,2909-2917`).

**b5. Tools to read a `.lnk`.**
- [FACT] Alpine v3.24 x86 has no package whose name contains "lnk".
- [FACT] Inside the guest, Wine reads `.lnk` files itself (shell32 `IShellLink`, `winemenubuilder -t`, and `start.exe`, as Boxedwine uses it).
- [FACT] On the .NET side, the NuGet package "Lnk" (EricZimmerman) is MIT, targets .NET Standard 2.0, and its latest version 2026.10.11 was updated on 2026-10-29 (nuget.org).
- [UNVERIFIED] Whether "Lnk" works under Blazor WebAssembly.

**b6. Extracting an icon from a `.exe`.**
- [FACT] Alpine v3.24 x86 has `icoutils` 0.32.3-r4 (community repository, GPL-3.0-or-later), with `/usr/bin/wrestool`, `/usr/bin/icotool`, `extresso` and `genresscript`.
- [UNVERIFIED] The usual command line (`wrestool -x -t 14`, then `icotool -x`) was not run here.
- [FACT] Boxedwine's Linux interface reads PE and NE icon resources on the host and "Never execute[s] or map[s] the Windows program" (`project/linux/ui/boxedwine/icons.py:1-25`).
- [INFERENCE] That file is GPL, so its code cannot be copied into BoxedWine, which is MIT. The host-side approach can still be reused.

## c) v86

**c1. Disks.** [FACT]
- IDE primary master is `hda`. Primary slave is `hdb`, used only when `hda` is set. Secondary master is always the CD-ROM (empty if none). Secondary slave is unused (`src/cpu.js:1218-1228`).
- Two floppies: `fda` and `fdb`.
- Virtio devices are only console, net and balloon (`src/virtio_*.js`; `Readme.md:19,27-30`). There is no virtio-blk, and no SCSI, AHCI or NVMe.
- Only the CD and floppies can be swapped while running (`src/browser/starter.js:946-1070`). Hard disks are fixed when the VM is built.

**c2. The 9p filesystem.** [FACT]
- **One virtio-9p device per VM**, with tag `host9p` and three possible backends (`cpu.js:1241-1252`; `lib/9p.js:181-197,896-907,973-990`):
  - **`fs9p`, the built-in one:** an in-memory JavaScript filesystem over an optional read-only HTTP base. Written data stays in JavaScript memory and goes into save states (`lib/filesystem.js:97-116`). The only storage backends are memory and the HTTP server (`src/browser/filestorage.js:36,89`). Its attach step ignores `aname` and always returns the root (`lib/9p.js:754-766`).
  - **`handle9p`:** your own 9p server written in JavaScript. It has no cache, no `create_file` or `read_file`, and you must keep it in sync with save states yourself (`docs/filesystem.md:48-67`).
  - **`proxy_url`:** a 9p server over WebSocket. It must be remounted after a restore (`docs/filesystem.md:81-82`).
- `aname` can select a subtree, but only with a server that supports it (`docs/filesystem.md:20`).
- The maintainer closed the request for OPFS support in 2024-05 (issue #1053).

**c3. Bottles stored outside a VM disk.** [INFERENCE] This is possible, through an Os-x86 9p server (`handle9p` or proxy) backed by OPFS.
- Each bottle would be mounted by `aname` or by a bind mount, and a VM can mount a new bottle while running. That suits "open in an existing VM" (59 C), which a fixed `hdb` cannot do.
- Conditions:
  - only one VM uses a bottle at a time (see d);
  - each bottle keeps the same guest uid in every VM. wineserver refuses a prefix it does not own (`server/request.c:636-639`), and 9p stores uids (`lib/9p.js:544-560`);
  - the 9p server's data stays consistent with VM save states (decisions 34 and 48).

**c4. Speed with many small files.**
- [FACT] v86 issue #1451 (open, 2025-11-18): reading 2,500 small files took 1.92 s on the in-memory 9p filesystem against 0.16 s on the root filesystem. The time goes to `yield_callback` and worker `postMessage`.
- [FACT] v86's 9p server starts at msize 8192 and adopts the guest's msize (`lib/9p.js:196,741-750`). Linux's default msize is 128 KiB plus a header (`include/net/9p/client.h:23`), and the virtio transport allows up to `PAGE_SIZE*125` (`net/9p/trans_virtio.c:812`).
- [FACT] By default Linux 9p uses no cache, and the "loose" cache is described as "non-coherent" (docs.kernel.org/filesystems/9p.html).
- [UNVERIFIED] The number of files in a Wine 11 win32 prefix. Earlier research notes that wineboot copies the built-in DLLs into each prefix (`docs/recherche-2026-11-09/critique.md:45`).
- [INFERENCE] Wine starts and installs will be noticeably slower on 9p. Mitigations:
  - keep the read-only base on a disk;
  - put only each bottle's changes on 9p;
  - use `cache=loose` when only one VM uses the bottle;
  - run the 9p server in the same worker as the VM.

**c5. Several VMs in one page.**
- [FACT] `examples/two_instances.html` runs two VMs in one page, linked by serial port and network (`Readme.md:176`). `examples/worker.js` runs a VM inside a Web Worker with 32 MB of RAM.
- [FACT] Each VM creates its own WebAssembly instance and memory (`starter.js:112-179`).
  - Guest RAM is `memory_size`: 64 MB by default (`cpu.js:1001-1004`), at most 2^31 − 128 KiB (`cpu.js:969-985`).
  - VGA memory is 8 MB by default (`cpu.js:1193`).
  - There is one virtual CPU (`Readme.md:13`).
- [FACT] The emulation loop runs on whichever thread created the VM (`src/main.js:100-160`).
- [INFERENCE] Two VMs on the main thread share one core; one worker per VM gives one core each. At the 512 MB per Wine VM suggested earlier (`docs/recherche-2026-11-09/wine-i686.md:145`), two VMs need more than 1.1 GB, plus any 9p data held in JavaScript memory.
- [UNVERIFIED] The browser's memory limit per tab, and any v86 guidance on running VMs at the same time.

## d) One wineserver per prefix

**d1. On one machine, the rule holds.** [FACT]
- The wineserver's folder is `/tmp/.wine-<uid>/server-<dev>-<ino>`, named from the prefix's device and inode (`server/request.c:597-672`).
- It holds a lock file with an fcntl write lock. A second wineserver exits with status 2 (`:674-695,763-810`).
- The man page says that Wine processes sharing a wineserver share "registry, shared memory and kernel objects" (`server/wineserver.man.in`, ENVIRONMENT and FILES sections).

**d2. Two VMs on the same prefix: nothing stops it.** [FACT] The lock and socket live in each VM's own `/tmp`, not in the prefix (`request.c:647-655`). Both VMs therefore start their own wineserver.

Consequences [INFERENCE, based on the code]:
- **Lost registry changes.** Each wineserver reads the registry hives only at startup (`server/registry.c:1873-1946`). It rewrites whole hive files every 30 s and at exit (`:125,2056-2143`). The last writer wins, so installs and settings get lost.
- **No file-sharing checks between VMs.** Windows file-sharing modes are checked only inside one wineserver (`server/fd.c:1618-1657`), so each VM can open the same file "exclusively".
- **Locks depend on the 9p server.** Byte-range locks become fcntl calls on the 9p file (`fd.c:1283`). They hold only if the 9p server shares locks between both VMs. The built-in server keeps them per VM (`lib/9p.js:450-493`).
- **Stale reads.** With `cache=loose`, each VM can read outdated data.
- **With the built-in `fs9p`,** each VM gets its own copy of the files (`starter.js:503`), so the "same" prefix splits into two copies that drift apart.

**d3. What this means for 59 C.** [INFERENCE] A bottle must belong to exactly one running VM at a time. Os-x86 has to enforce that itself; Wine will not detect the conflict.