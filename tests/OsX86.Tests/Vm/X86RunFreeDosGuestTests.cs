using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Kazo.Os.AppSdk;
using Microsoft.Playwright;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Storage;
using OsX86.Tests.Dos;
using OsX86.Tests.Guest;
using OsX86.Vm;
using Xunit.Abstractions;

namespace OsX86.Tests.Vm;

/// <summary>
/// The <c>guest</c> test of work item 11, on the FreeDOS image in a real browser with the production
/// engine, image store and VM manager (only the JS interop and Kazo's Terminal are replaced: Playwright
/// and a simulated full screen). The <c>x86</c> command does what a user does:
/// <list type="number">
/// <item><c>x86 detect</c> names the test program a DOS one, run by FreeDOS;</item>
/// <item><c>x86 run osx86key.com a1 b2</c> makes the DOS VM, boots it with the program disk, shows the
/// program's screen with its colours and cursor; the keys typed (a letter, a capital, Tab, Alt+X and F1
/// by the Escape chord, an arrow) reach it, Ctrl+C reaches it and ends it with code 3; the VM stops at
/// once (166 A); the program's last screen stays in the output;</item>
/// <item>a second run in the same VM takes twenty keys one at a time (the delay per refresh is
/// measured on them) and ends by Enter with code 42;</item>
/// <item><c>x86 run osx86gfx.com</c> switches to the mode 13h: the message of 217 A, code 6, the VM stopped.</item>
/// </list>
/// The times and the refresh measures are printed. Its name holds <c>FreeDosGuestTests</c>: the guest
/// job of <c>images.yml</c> runs it with the FreeDOS pack and its test programs. Skipped when they are
/// not built (<c>python -I images/freedos/build-freedos.py</c>) or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
[Collection(X86CommandCollection.Name)]
public sealed class X86RunFreeDosGuestTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Boot = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(60);
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Dictionary<string, string> Statistics = new() { [X86Command.DosStatisticsVariable] = "1" };

    [SkippableFact]
    public async Task X86_run_shows_a_dos_program_in_the_terminal_takes_its_keys_and_stops_its_vm()
    {
        string pack = PackedGuestSession.RequirePack("freedos", "images/freedos/build-freedos.py");
        string programs = Path.Combine(Path.GetDirectoryName(pack)!, "programs");
        string keyProgram = Path.Combine(programs, "OSX86KEY.COM");
        string graphicsProgram = Path.Combine(programs, "OSX86GFX.COM");
        Skip.IfNot((File.Exists(keyProgram) && File.Exists(graphicsProgram)) || Environment.GetEnvironmentVariable("OSX86_REQUIRE_PACKS") == "1", $"{programs} is not built");
        string root = Images.PythonTools.RepositoryRoot;
        byte[] manifestBytes = await File.ReadAllBytesAsync(Path.Combine(pack, "manifest.json"));
        ImageManifest manifest = ImageManifest.Parse(Encoding.UTF8.GetString(manifestBytes));
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));

        await using var server = new StaticFileServer(
        [
            ("/engine/", Path.Combine(root, "src", "OsX86", "wwwroot", "engine")),
            ("/v86/", Path.Combine(root, "src", "OsX86", "wwwroot", "v86")),
            ("/storage/", Path.Combine(root, "src", "OsX86", "wwwroot", "storage")),
            ("/origin/freedos/", pack),
            ("/page/", Path.Combine(root, "tests", "OsX86.Tests", "Vm")),
        ]);
        server.Start();
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await GuestBrowser.LaunchAsync(playwright);
        await using IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        page.Console += (_, message) => output.WriteLine($"[browser {message.Type}] {message.Text}");
        page.PageError += (_, error) => output.WriteLine($"[browser error] {error}");
        PlaywrightWorkerHub hub = await PlaywrightWorkerHub.AttachAsync(page);
        await page.GotoAsync($"{server.BaseUrl}page/vm.html?host=/engine/v86-host.js&storage=/storage/storage-host.js");
        await page.WaitForFunctionAsync("() => typeof window.vm === 'object'");

        var catalog = new ImageCatalog([new KnownImage(manifest.Image, manifest.Version, $"{server.BaseUrl}origin/freedos/manifest.json", sha256)]);
        var store = new ImageStore(new PlaywrightStorageWorkerChannel(page, hub));
        // A long grace: a DOS VM must stop at once when its program ends, not after the grace.
        var options = new VmManagerOptions { StopGrace = TimeSpan.FromMinutes(10), AgentWatchdogInterval = TimeSpan.Zero };
        await using var manager = new VmManager(new InMemoryVmRegistryStore(), new PlaywrightEngineFactory(page, hub), new StoreBootImageSource(store, catalog), new FixedHostMemory(8L * 1024 * 1024 * 1024), options);
        var files = new X86CommandTests.FakeFiles();
        files.Binaries["osx86key.com"] = await File.ReadAllBytesAsync(keyProgram);
        files.Binaries["osx86gfx.com"] = await File.ReadAllBytesAsync(graphicsProgram);
        var services = new X86CommandServices(manager, files);
        X86Command.ServicesFactory = () => services;
        try
        {
            // 1. Detection.
            (int code, string text, _) = await RunAsync(["detect", "osx86key.com"], null, Soon);
            Assert.Equal(0, code);
            Assert.Contains(ProgramMimeTypes.DosExecutable, text, StringComparison.Ordinal);
            Assert.Contains("FreeDOS virtual machine", text, StringComparison.Ordinal);

            // 2. The program of keys: screen, colours, cursor, keys, Ctrl+C, exit code, stop.
            var clock = Stopwatch.StartNew();
            var screen = new FakeFullScreen();
            Task<(int, string, string)> run = RunAsync(["run", "osx86key.com", "a1", "b2"], screen, Boot);
            TerminalScreenFrame ready = await WaitForAsync(screen, "READY:", Boot);
            TimeSpan toReady = clock.Elapsed;
            Assert.StartsWith(VgaColors.Sgr(0x1E, true) + "OSX86 DOS TEST", ready.Lines[0], StringComparison.Ordinal);
            Assert.Contains("ARGS:[ a1 b2]", FakeFullScreen.Text(ready), StringComparison.Ordinal);
            Assert.True(ready.Cursor.Visible, "the DOS cursor is shown");
            Assert.Equal(3, ready.Cursor.Row);

            foreach (TerminalKeyEvent key in new[]
            {
                new TerminalKeyEvent(TerminalKey.Character, "a"),
                new TerminalKeyEvent(TerminalKey.Character, "B"),
                new TerminalKeyEvent(TerminalKey.Character, "\t"),
                new TerminalKeyEvent(TerminalKey.Escape),
                new TerminalKeyEvent(TerminalKey.Character, "x"),
                new TerminalKeyEvent(TerminalKey.Escape),
                new TerminalKeyEvent(TerminalKey.Character, "1"),
                new TerminalKeyEvent(TerminalKey.ArrowUp),
            })
            {
                screen.Type(key);
            }

            await WaitForAsync(screen, "K=1E61 K=3042 K=0F09 K=2D00 K=3B00 K=4800", Soon);
            screen.Type(new TerminalKeyEvent(TerminalKey.Character, "c", Control: true));
            (code, text, string errors) = await run.WaitAsync(Soon);
            TimeSpan first = clock.Elapsed;
            Assert.Equal(3, code);
            Assert.Contains("created the virtual machine dos", errors, StringComparison.Ordinal);
            Assert.Contains("ARGS:[ a1 b2]", text, StringComparison.Ordinal);
            Assert.Contains("K=4800  CTRL-C", text, StringComparison.Ordinal);
            Assert.Contains("x86: DOS screen:", errors, StringComparison.Ordinal);
            Assert.Equal(1, screen.Enters);
            Assert.False(screen.Active);
            Assert.Equal(VmState.Stopped, await DosStateAsync(manager));
            output.WriteLine($"first x86 run (download, install, FreeDOS boot to READY): {toReady.TotalSeconds:0.0} s; whole run {first.TotalSeconds:0.0} s");

            // 3. The same VM again: Enter ends the program with 42.
            clock.Restart();
            screen = new FakeFullScreen();
            run = RunAsync(["run", "osx86key.com"], screen, Boot);
            await WaitForAsync(screen, "READY:", Boot);
            TimeSpan again = clock.Elapsed;
            // Twenty keys one at a time, each waited for on the screen: the delay per refresh (E14).
            for (int index = 0; index < 20; index++)
            {
                screen.Type(new TerminalKeyEvent(TerminalKey.Character, ((char)('a' + index)).ToString()));
                await WaitForCountAsync(screen, "K=", index + 1, Soon);
            }

            screen.Type(new TerminalKeyEvent(TerminalKey.Enter));
            (code, text, errors) = await run.WaitAsync(Soon);
            Assert.Equal(42, code);
            Assert.Contains(" END", text, StringComparison.Ordinal);
            Assert.DoesNotContain("created the virtual machine", errors, StringComparison.Ordinal);
            Assert.Equal(VmState.Stopped, await DosStateAsync(manager));
            output.WriteLine($"second x86 run (image installed, FreeDOS boot to READY): {again.TotalSeconds:0.0} s");

            // 4. A program that switches to a graphical mode: message, code 6, VM stopped.
            clock.Restart();
            screen = new FakeFullScreen();
            (code, text, errors) = await RunAsync(["run", "osx86gfx.com"], screen, Boot);
            Assert.Equal(X86ExitCodes.Unavailable, code);
            Assert.Empty(text);
            Assert.Contains("switched to a graphical mode (320x", errors, StringComparison.Ordinal);
            Assert.Contains("BoxedWine", errors, StringComparison.Ordinal);
            Assert.False(screen.Active);
            Assert.Equal(VmState.Stopped, await DosStateAsync(manager));
            output.WriteLine($"x86 run of the graphical program to its stop: {clock.Elapsed.TotalSeconds:0.0} s");
        }
        finally
        {
            X86Command.ServicesFactory = static () => X86Runtime.Services;
            await store.DisposeAsync();
        }
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(string[] args, IFullScreenTerminalSession? screen, TimeSpan timeout)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await new X86Command().ExecuteAsync(TestAppContext.Terminal(args, stdout, stderr, fullScreen: screen, environment: Statistics), None).AsTask().WaitAsync(timeout);
        string text = stdout.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        string errors = stderr.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        output.WriteLine($"x86 {string.Join(' ', args)} -> {code}\n{text}{errors}".TrimEnd());
        return (code, text, errors);
    }

    private async Task<TerminalScreenFrame> WaitForAsync(FakeFullScreen screen, string text, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            TerminalScreenFrame? frame = screen.Frames.LastOrDefault(candidate => FakeFullScreen.Text(candidate).Contains(text, StringComparison.Ordinal));
            if (frame is not null)
            {
                return frame;
            }

            if (Stopwatch.GetElapsedTime(start) > timeout)
            {
                TerminalScreenFrame? last = screen.Frames.LastOrDefault();
                output.WriteLine(last is null ? "(no frame)" : FakeFullScreen.Text(last));
                Assert.Fail($"timed out waiting for '{text}' on the DOS screen");
            }

            await Task.Delay(20);
        }
    }

    private static async Task WaitForCountAsync(FakeFullScreen screen, string text, int count, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        while (screen.Frames.LastOrDefault() is not { } frame || CountOf(FakeFullScreen.Text(frame), text) < count)
        {
            Assert.True(Stopwatch.GetElapsedTime(start) < timeout, $"timed out waiting for {count} '{text}' on the DOS screen");
            await Task.Delay(2);
        }
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task<VmState> DosStateAsync(VmManager manager) =>
        (await manager.ListAsync(VmTestRig.User, None)).Single(vm => vm.Name == "dos").State;
}
