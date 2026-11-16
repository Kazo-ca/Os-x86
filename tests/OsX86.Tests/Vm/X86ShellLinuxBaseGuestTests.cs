using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Storage;
using OsX86.Tests.Guest;
using OsX86.Vm;
using Xunit.Abstractions;

namespace OsX86.Tests.Vm;

/// <summary>
/// The <c>guest</c> test of work item 6b: <c>linux-base</c> is the default image. In a real browser,
/// with the production engine, image store and VM manager (only the JS interop is replaced by
/// Playwright), the <c>x86</c> command does what a user does:
/// <list type="number">
/// <item><c>x86 vm create base</c> without profile takes linux-base;</item>
/// <item>the first <c>x86 shell</c> downloads the pack from a local origin (the catalog gives its
/// address and expected hash), verifies it into the OPFS, boots the VM on its own write layer and
/// opens the serial console (linux-base has no guest user for an agent shell yet): a command
/// answers, a file is written on the disk;</item>
/// <item>after <c>x86 vm stop</c>, a second <c>x86 shell</c> downloads nothing and finds the file;</item>
/// <item><c>x86 vm remove</c> deletes the layer: a new VM from the same image does not have the file.</item>
/// </list>
/// Both shells are timed. Its name holds <c>LinuxBaseGuestTests</c>: the guest job of
/// <c>images.yml</c>, which builds the packs, runs it with them. Skipped when the pack is not built (<c>python -I images/linux-base/build-linux-base.py</c>)
/// or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
[Collection(X86CommandCollection.Name)]
public sealed class X86ShellLinuxBaseGuestTests(ITestOutputHelper output)
{
    private const string Marker = "/var/lib/osx86-6b-marker";
    private static readonly TimeSpan Boot = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(60);
    private static readonly CancellationToken None = CancellationToken.None;

    [SkippableFact]
    public async Task Linux_base_is_downloaded_booted_and_keeps_its_disk_until_the_vm_is_removed()
    {
        string pack = PackedGuestSession.RequirePack("linux-base", "images/linux-base/build-linux-base.py");
        string root = Images.PythonTools.RepositoryRoot;
        byte[] manifestBytes = await File.ReadAllBytesAsync(Path.Combine(pack, "manifest.json"));
        ImageManifest manifest = ImageManifest.Parse(Encoding.UTF8.GetString(manifestBytes));
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));

        await using var server = new StaticFileServer(
        [
            ("/engine/", Path.Combine(root, "src", "OsX86", "wwwroot", "engine")),
            ("/v86/", Path.Combine(root, "src", "OsX86", "wwwroot", "v86")),
            ("/storage/", Path.Combine(root, "src", "OsX86", "wwwroot", "storage")),
            ("/origin/linux-base/", pack),
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

        // The local origin: the catalog names the address of the manifest and the hash it must have.
        var catalog = new ImageCatalog([new KnownImage(manifest.Image, manifest.Version, $"{server.BaseUrl}origin/linux-base/manifest.json", sha256)]);
        var store = new ImageStore(new PlaywrightStorageWorkerChannel(page, hub));
        var options = new VmManagerOptions { StopGrace = TimeSpan.FromSeconds(3), AgentWatchdogInterval = TimeSpan.FromSeconds(5) };
        await using var manager = new VmManager(new InMemoryVmRegistryStore(), new PlaywrightEngineFactory(page, hub), new StoreBootImageSource(store, catalog), new FixedHostMemory(8L * 1024 * 1024 * 1024), options);
        var services = new X86CommandServices(manager, new X86CommandTests.FakeFiles());
        X86Command.ServicesFactory = () => services;
        try
        {
            // 1. Created without profile: linux-base.
            string created = await RunAsync("vm", "create", "base");
            Assert.Contains("from the profile linux-base", created, StringComparison.Ordinal);
            VmRecord vm = await manager.ResolveAsync(VmTestRig.User, "base", None);
            Assert.Equal(manifest.Version, vm.Profile.ImageVersion);

            // 2. First shell: download, boot, serial console; a command answers; a file is written on the disk.
            await using (var shell = await ShellAsync())
            {
                TimeSpan first = await shell.AnswerAsync("echo vm-$((6*7))", "vm-42", Boot);
                output.WriteLine($"first x86 shell to an answer (download and boot included): {first.TotalSeconds:0.0} s");
                await shell.AnswerAsync($"mount -o remount,rw / && printf kept > {Marker} && sync && mount -o remount,ro / && echo W-$((40+2))", "W-42", Soon);
                Assert.Equal(0, await shell.CloseAsync());
                string[] notes = shell.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                output.WriteLine(shell.Errors);
                Assert.StartsWith("x86: linux-base 0.1.0 is not on this machine yet", notes[0], StringComparison.Ordinal);
                Assert.Contains(notes, line => line.StartsWith("x86: linux-base 0.1.0 downloaded and verified", StringComparison.Ordinal));
                Assert.Equal(X86Command.SerialNotice, notes[^1]);
            }

            Assert.Contains((await store.ListAsync(None)), image => image.Image == "linux-base" && image.Complete);

            // 3. Stopped, then a second shell: no download, the file is there.
            Assert.Contains("Stopped base.", await RunAsync("vm", "stop", "base"), StringComparison.Ordinal);
            Assert.Equal(VmState.Stopped, manager.StateOf(VmTestRig.User, vm.Id));
            await using (var shell = await ShellAsync())
            {
                TimeSpan second = await shell.AnswerAsync("echo vm-$((6*7))", "vm-42", Boot);
                output.WriteLine($"second x86 shell to an answer (image already there, boot included): {second.TotalSeconds:0.0} s");
                Assert.Equal("kept", await shell.ReadMarkerAsync());
                Assert.Equal(0, await shell.CloseAsync());
                Assert.Equal(X86Command.SerialNotice, shell.Errors.Trim());
            }

            // 4. Removed: its layer goes with it; a new VM of the same image starts from the image.
            Assert.Contains("Removed base", await RunAsync("vm", "remove", "base", "--force"), StringComparison.Ordinal);
            Assert.Empty(await store.ListLayersAsync(vm.EffectiveLayerSet, None));
            await RunAsync("vm", "create", "again");
            await using (var shell = await ShellAsync())
            {
                await shell.AnswerAsync("echo vm-$((6*7))", "vm-42", Boot);
                Assert.Equal("none", await shell.ReadMarkerAsync());
                Assert.Equal(0, await shell.CloseAsync());
            }

            Assert.Contains("Removed again", await RunAsync("vm", "remove", "again", "--force"), StringComparison.Ordinal);
        }
        finally
        {
            X86Command.ServicesFactory = static () => X86Runtime.Services;
            await store.DisposeAsync();
        }
    }

    private async Task<string> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await new X86Command().ExecuteAsync(TestAppContext.Terminal(args, stdout, stderr), None);
        output.WriteLine($"x86 {string.Join(' ', args)} -> {code}: {stdout}{stderr}".TrimEnd());
        Assert.Equal(0, code);
        return stdout.ToString();
    }

    private Task<Shell> ShellAsync()
    {
        var input = new QueueTextReader();
        var terminal = new LiveWriter();
        var errors = new LiveWriter();
        long start = Stopwatch.GetTimestamp();
        Task<int> command = new X86Command().ExecuteAsync(TestAppContext.Terminal(["shell"], terminal, errors, input), None).AsTask();
        return Task.FromResult(new Shell(input, terminal, errors, command, start, output));
    }

    /// <summary>An <c>x86 shell</c> running on the serial console of the VM.</summary>
    private sealed class Shell(QueueTextReader input, LiveWriter terminal, LiveWriter errors, Task<int> command, long start, ITestOutputHelper output) : IAsyncDisposable
    {
        public string Errors => errors.Text.Replace("\r\n", "\n", StringComparison.Ordinal);

        /// <summary>
        /// Sends a command line until its answer shows, and returns the time since the shell was
        /// started: the serial shell of the guest may start after the VM is up, and what is typed
        /// before it reads the port is lost, so the line is sent again every few seconds.
        /// </summary>
        public async Task<TimeSpan> AnswerAsync(string line, string answer, TimeSpan timeout)
        {
            long sent = Stopwatch.GetTimestamp();
            int from = terminal.Text.Length;
            while (true)
            {
                if (command.IsCompleted)
                {
                    Assert.Fail($"x86 shell ended ({(command.IsCompletedSuccessfully ? command.Result.ToString() : command.Exception?.Message)}) before {answer}; errors: {errors.Text}; output: {terminal.Text}");
                }

                if (errors.Text.Contains(X86Command.SerialNotice, StringComparison.Ordinal))
                {
                    input.Add(line);
                    if (await terminal.WaitForAsync(answer, TimeSpan.FromSeconds(5), from))
                    {
                        return Stopwatch.GetElapsedTime(start);
                    }
                }
                else
                {
                    await Task.Delay(100);
                }

                Assert.True(Stopwatch.GetElapsedTime(sent) < timeout, $"timed out waiting for {answer}; errors: {errors.Text}; output: {terminal.Text}");
            }
        }

        public async Task<string> ReadMarkerAsync()
        {
            int from = terminal.Text.Length;
            await AnswerAsync($"echo R=$(cat {Marker} 2>/dev/null || echo none)=E", "=E", Soon);
            string text = terminal.Text[from..];
            int end = text.LastIndexOf("=E", StringComparison.Ordinal);
            int begin = text.LastIndexOf("R=", end, StringComparison.Ordinal);
            return text[(begin + 2)..end];
        }

        public async Task<int> CloseAsync()
        {
            input.Add(X86Command.CloseLine);
            int code = await command.WaitAsync(Soon);
            output.WriteLine(terminal.Text);
            return code;
        }

        public async ValueTask DisposeAsync()
        {
            if (!command.IsCompleted)
            {
                input.Add(X86Command.CloseLine);
                input.End();
                try
                {
                    await command.WaitAsync(Soon);
                }
                catch (Exception exception) when (exception is TimeoutException or X86Exception)
                {
                    output.WriteLine($"x86 shell did not close: {exception.Message}");
                }
            }
        }
    }

    /// <summary>A standard output the test reads while the command writes it.</summary>
    private sealed class LiveWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _lock = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    return _text.ToString();
                }
            }
        }

        public override void Write(char value)
        {
            lock (_lock)
            {
                _text.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_lock)
            {
                _text.Append(value);
            }
        }

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _text.Append(buffer.Span);
            }

            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _text.Append(buffer.Span).Append('\n');
            }

            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>Whether <paramref name="needle"/> shows after <paramref name="from"/> within <paramref name="timeout"/>.</summary>
        public async Task<bool> WaitForAsync(string needle, TimeSpan timeout, int from)
        {
            long start = Stopwatch.GetTimestamp();
            while (Text.IndexOf(needle, Math.Min(from, Text.Length), StringComparison.Ordinal) < 0)
            {
                if (Stopwatch.GetElapsedTime(start) >= timeout)
                {
                    return false;
                }

                await Task.Delay(50);
            }

            return true;
        }
    }
}
