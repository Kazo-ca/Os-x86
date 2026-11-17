using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Playwright;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Storage;
using OsX86.Tests.Guest;
using OsX86.Vm;
using Xunit.Abstractions;

namespace OsX86.Tests.Vm;

/// <summary>
/// The <c>guest</c> test of work item 10, on the wine image in a real browser with the production
/// engine, image store and VM manager (only the JS interop is replaced by Playwright). The
/// <c>x86</c> command does what a user does:
/// <list type="number">
/// <item><c>x86 detect</c> names the test program a 32-bit console PE;</item>
/// <item><c>x86 run osx86-hello.exe a b</c> makes the VM of Windows programs, boots it, makes the
/// default prefix in the guest, copies the program in, starts the prefix's Wine session, runs the
/// program on a pty: its line reaches the terminal and its exit code (7) is the command's;</item>
/// <item>a second <c>x86 run</c> (VM and session up) is faster, its code (3) is returned;</item>
/// <item>a line typed reaches the program (<c>--read</c>); Ctrl+C interrupts a program that waits;</item>
/// <item><c>x86 prefix create work</c>, a program in it runs as <c>osx86-work</c>; a shell of that
/// prefix cannot read the default prefix's files, nor the other way (144, 146);</item>
/// <item><c>x86 prefix list</c> and <c>delete</c>; after <c>x86 vm stop</c>, the default prefix and
/// a file written in it are still there at the next boot (the prefixes live on the VM's disk).</item>
/// </list>
/// The times are printed. Its name holds <c>WineGuestTests</c>: the guest-wine job of
/// <c>images.yml</c> runs it with the pack. Skipped when the pack is not built
/// (<c>python -I images/wine/build-wine.py</c>) or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
[Collection(X86CommandCollection.Name)]
public sealed class X86RunWineGuestTests(ITestOutputHelper output)
{
    private const string Program = "osx86-hello.exe";
    private static readonly TimeSpan Boot = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(90);
    private static readonly CancellationToken None = CancellationToken.None;

    [SkippableFact]
    public async Task X86_run_runs_a_console_program_in_a_prefix_and_returns_its_code()
    {
        string pack = PackedGuestSession.RequirePack("wine", "images/wine/build-wine.py");
        string exe = Path.Combine(Path.GetDirectoryName(pack)!, Program);
        Skip.IfNot(File.Exists(exe) || Environment.GetEnvironmentVariable("OSX86_REQUIRE_PACKS") == "1", $"{exe} is not built");
        string root = Images.PythonTools.RepositoryRoot;
        byte[] manifestBytes = await File.ReadAllBytesAsync(Path.Combine(pack, "manifest.json"));
        ImageManifest manifest = ImageManifest.Parse(Encoding.UTF8.GetString(manifestBytes));
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));

        await using var server = new StaticFileServer(
        [
            ("/engine/", Path.Combine(root, "src", "OsX86.Feature", "wwwroot", "engine")),
            ("/v86/", Path.Combine(root, "src", "OsX86.Feature", "wwwroot", "v86")),
            ("/storage/", Path.Combine(root, "src", "OsX86.Feature", "wwwroot", "storage")),
            ("/origin/wine/", pack),
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

        var catalog = new ImageCatalog([new KnownImage(manifest.Image, manifest.Version, $"{server.BaseUrl}origin/wine/manifest.json", sha256)]);
        var store = new ImageStore(new PlaywrightStorageWorkerChannel(page, hub));
        // A long grace: the runs below follow each other in the same run of the VM.
        var options = new VmManagerOptions { StopGrace = TimeSpan.FromSeconds(120), AgentWatchdogInterval = TimeSpan.FromSeconds(10) };
        await using var manager = new VmManager(new InMemoryVmRegistryStore(), new PlaywrightEngineFactory(page, hub), new StoreBootImageSource(store, catalog), new FixedHostMemory(8L * 1024 * 1024 * 1024), options);
        var files = new X86CommandTests.FakeFiles();
        files.Binaries[Program] = await File.ReadAllBytesAsync(exe);
        var services = new X86CommandServices(manager, files);
        X86Command.ServicesFactory = () => services;
        try
        {
            // 1. Detection.
            (int code, string text, _) = await RunAsync(["detect", Program], Soon);
            Assert.Equal(0, code);
            Assert.Contains(ProgramMimeTypes.PortableExecutable, text, StringComparison.Ordinal);
            Assert.Contains("PE32 console i386", text, StringComparison.Ordinal);

            // 2. First run: the VM is made and booted, the default prefix made, the session started.
            var clock = Stopwatch.StartNew();
            (code, text, string errors) = await RunAsync(["run", Program, "a", "b"], Boot);
            TimeSpan first = clock.Elapsed;
            Assert.Equal(7, code);
            Assert.Contains("created the virtual machine windows", errors, StringComparison.Ordinal);
            Assert.Contains(@"osx86-hello windows=C:\windows user=osx86-default args=2 a b", text, StringComparison.OrdinalIgnoreCase);
            VmRecord vm = await manager.ResolveAsync(VmTestRig.User, "windows", None);
            VmInstance instance = await manager.GetInstanceAsync(VmTestRig.User, vm.Id, None);
            output.WriteLine($"first x86 run (download, boot, prefix, Wine session, program): {first.TotalSeconds:0.0} s");

            // 3. Second run: VM and session are up.
            clock.Restart();
            (code, text, _) = await RunAsync(["run", Program, "--exit", "3"], Soon);
            TimeSpan second = clock.Elapsed;
            Assert.Equal(3, code);
            Assert.Contains("args=2 --exit 3", text, StringComparison.Ordinal);
            output.WriteLine($"second x86 run (VM and Wine session up): {second.TotalSeconds:0.0} s");
            Assert.True(second < first, "the second run is faster");

            // 4. Input reaches the program; Ctrl+C interrupts one that waits (216 A).
            await using (var reading = Live(["run", Program, "--read"]))
            {
                Assert.True(await reading.Output.WaitForAsync("osx86-hello reading", Soon, 0), $"no reading line: {reading.Errors.Text}");
                // Typed once, right after the program says it reads: before work item 10b this raced the
                // console of Wine, which sets the terminal raw only at its first read (a CR typed before
                // became LF, Ctrl+J for Wine, and the line never ended). The pty now starts raw.
                reading.Input.Add("bonjour");
                bool read = await reading.Output.WaitForAsync("osx86-hello read bonjour", Soon, 0);

                output.WriteLine($"--read: {reading.Output.Text}{reading.Errors.Text}");
                Assert.True(read, "the typed line did not reach the program");
                Assert.Equal(0, await reading.Command.WaitAsync(Soon));
                Assert.Contains("osx86-hello read bonjour", reading.Output.Text, StringComparison.Ordinal);
            }

            await using (var waiting = Live(["run", Program, "--wait"]))
            {
                Assert.True(await waiting.Output.WaitForAsync("osx86-hello waiting", Soon, 0), $"no waiting line: {waiting.Errors.Text}");
                clock.Restart();
                waiting.Input.Add(X86Command.InterruptLine);
                int interrupted = await waiting.Command.WaitAsync(Soon);
                output.WriteLine($"Ctrl+C to the end of a waiting program: {clock.Elapsed.TotalSeconds:0.0} s, exit code {interrupted}");
                Assert.NotEqual(0, interrupted);
            }

            // 5. A second prefix; isolation between prefixes (144, 146).
            (code, text, _) = await RunAsync(["prefix", "create", "work"], Soon);
            Assert.Equal(0, code);
            Assert.Contains("its guest user and files are ready", text, StringComparison.Ordinal);
            (code, text, _) = await RunAsync(["run", "--prefix", "work", Program], Soon);
            Assert.Equal(7, code);
            Assert.Contains("user=osx86-work", text, StringComparison.Ordinal);
            string fromWork = await ShellLinesAsync("work", "ls /home/osx86-default/.wine 2>&1 | head -1; echo ISO-$((1+1))", "ISO-2");
            Assert.Contains("Permission denied", fromWork, StringComparison.Ordinal);
            string fromDefault = await ShellLinesAsync("default", "cat /home/osx86-work/.wine/user.reg 2>&1 | head -1; echo kept > marker; echo ISO-$((2+1))", "ISO-3");
            Assert.Contains("Permission denied", fromDefault, StringComparison.Ordinal);

            // 6. List and delete.
            (code, text, _) = await RunAsync(["prefix", "list"], Soon);
            Assert.Equal(0, code);
            Assert.Matches(@"default\s+yes\s+yes", text);
            Assert.Matches(@"work\s+no\s+yes", text);
            (code, text, _) = await RunAsync(["prefix", "delete", "work"], Soon);
            Assert.Equal(0, code);
            Assert.Contains("its programs, guest user and files are gone", text, StringComparison.Ordinal);
            Assert.Null((await instance.ListGuestPrefixesAsync(None))!.FirstOrDefault(p => p.Prefix == "work"));

            // 7. The prefixes live on the VM's disk: after a stop, the default prefix and its file are there.
            (code, _, _) = await RunAsync(["vm", "stop", "windows", "--force"], Soon);
            Assert.Equal(0, code);
            clock.Restart();
            string again = await ShellLinesAsync("default", "cat marker; echo ISO-$((3+1))", "ISO-4", Boot);
            output.WriteLine($"x86 shell in the default prefix after a stop (boot included): {clock.Elapsed.TotalSeconds:0.0} s");
            Assert.Contains("kept", again, StringComparison.Ordinal);
            clock.Restart();
            (code, _, _) = await RunAsync(["run", Program], Boot);
            Assert.Equal(7, code);
            output.WriteLine($"x86 run after a stop and a shell (VM up, Wine session started): {clock.Elapsed.TotalSeconds:0.0} s");
            Assert.Equal(0, (await RunAsync(["vm", "stop", "windows", "--force"], Soon)).Code);
        }
        finally
        {
            X86Command.ServicesFactory = static () => X86Runtime.Services;
            await store.DisposeAsync();
        }
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(string[] args, TimeSpan timeout)
    {
        await using LiveCommand command = Live(args);
        command.Input.End();
        int code = await command.Command.WaitAsync(timeout);
        string text = command.Output.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        string errors = command.Errors.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        output.WriteLine($"x86 {string.Join(' ', args)} -> {code}\n{text}{errors}".TrimEnd());
        return (code, text, errors);
    }

    /// <summary>Runs one line in a shell of a prefix and returns what it printed up to <paramref name="end"/>.</summary>
    private async Task<string> ShellLinesAsync(string prefix, string line, string end, TimeSpan? timeout = null)
    {
        await using LiveCommand shell = Live(["shell", "--vm", "windows", "--prefix", prefix]);
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            shell.Input.Add(line);
            if (await shell.Output.WaitForAsync(end, TimeSpan.FromSeconds(5), 0))
            {
                break;
            }

            Assert.False(shell.Command.IsCompleted, $"x86 shell ended: {shell.Errors.Text}");
            Assert.True(Stopwatch.GetElapsedTime(start) < (timeout ?? Soon), $"timed out waiting for {end}: {shell.Output.Text}{shell.Errors.Text}");
        }

        shell.Input.Add("exit 0");
        Assert.Equal(0, await shell.Command.WaitAsync(Soon));
        output.WriteLine($"x86 shell --prefix {prefix}: {shell.Output.Text}");
        return shell.Output.Text;
    }

    private static LiveCommand Live(string[] args)
    {
        var input = new QueueTextReader();
        var stdout = new X86ShellLinuxBaseGuestTests.LiveWriter();
        var stderr = new X86ShellLinuxBaseGuestTests.LiveWriter();
        Task<int> command = new X86Command().ExecuteAsync(TestAppContext.Terminal(args, stdout, stderr, input), None).AsTask();
        return new LiveCommand(input, stdout, stderr, command);
    }

    /// <summary>An <c>x86</c> command running with a standard input the test writes to.</summary>
    private sealed record LiveCommand(QueueTextReader Input, X86ShellLinuxBaseGuestTests.LiveWriter Output, X86ShellLinuxBaseGuestTests.LiveWriter Errors, Task<int> Command) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (!Command.IsCompleted)
            {
                Input.Add(X86Command.CloseLine);
                Input.End();
                try
                {
                    await Command.WaitAsync(Soon);
                }
                catch (Exception exception) when (exception is TimeoutException or X86Exception)
                {
                    // Reported by the assertion that left it running.
                }
            }
        }
    }
}
