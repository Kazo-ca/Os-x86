using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using OsX86.Agent;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Network;
using OsX86.Prefixes;
using OsX86.Profiles;
using OsX86.Storage;
using OsX86.Tests.Guest;
using OsX86.Tests.Vm;
using OsX86.Vm;
using Xunit.Abstractions;

namespace OsX86.Tests.Network;

/// <summary>
/// The <c>guest</c> test of work item 12, on the packed <c>linux-base</c> in a real browser with the
/// production engine, image store, VM manager and network (only the JS interop is replaced by
/// Playwright; the relay is <see cref="LocalTestRelay"/>, which answers in memory and never reaches
/// the Internet; the permission window is injected):
/// <list type="number">
/// <item>two VMs of a user reach each other on the private network (root of one to root of the other);</item>
/// <item>two prefixes of a VM have distinct addresses and do not see each other, nor the other VM;</item>
/// <item>a connection to a destination of the test relay opens the window: "allow" passes, "refuse"
/// blocks, the journal records both (the scenario of milestone 0);</item>
/// <item>the 8 confinement checks of trial E6 hold for a prefix's user, plus UDP 53, IPv6 and a
/// user outside any prefix; nothing reaches the real Internet;</item>
/// <item>the measures: throughput through the VM's namespace and through a prefix's (the cost of
/// the namespace), the guest memory the namespaces take.</item>
/// </list>
/// Its name holds <c>LinuxBaseGuestTests</c>: the guest job of <c>images.yml</c> runs it with the packs.
/// Skipped when the pack is not built or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
[Collection(X86CommandCollection.Name)]
public sealed partial class NetworkLinuxBaseGuestTests(ITestOutputHelper output)
{
    private const string Probe = "/usr/lib/osx86/osx86-netcheck";
    private const string Web = "203.0.113.9";
    private static readonly TimeSpan Boot = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(60);
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly PrefixId P1 = new("p1");
    private static readonly PrefixId P2 = new("p2");

    [SkippableFact]
    public async Task Two_vms_and_two_prefixes_on_the_private_network_with_decisions_limits_journal_and_confinement()
    {
        string pack = PackedGuestSession.RequirePack("linux-base", "images/linux-base/build-linux-base.py");
        string root = Images.PythonTools.RepositoryRoot;
        byte[] manifestBytes = await File.ReadAllBytesAsync(Path.Combine(pack, "manifest.json"));
        ImageManifest manifest = ImageManifest.Parse(Encoding.UTF8.GetString(manifestBytes));
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));

        await using var server = new StaticFileServer(
        [
            ("/engine/", Path.Combine(root, "src", "OsX86.Feature", "wwwroot", "engine")),
            ("/v86/", Path.Combine(root, "src", "OsX86.Feature", "wwwroot", "v86")),
            ("/storage/", Path.Combine(root, "src", "OsX86.Feature", "wwwroot", "storage")),
            ("/origin/linux-base/", pack),
            ("/page/", Path.Combine(root, "tests", "OsX86.Tests", "Vm")),
        ]);
        server.Start();
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await GuestBrowser.LaunchAsync(playwright);
        await using IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        page.PageError += (_, error) => output.WriteLine($"[browser error] {error}");
        PlaywrightWorkerHub hub = await PlaywrightWorkerHub.AttachAsync(page);
        await page.GotoAsync($"{server.BaseUrl}page/vm.html?host=/engine/v86-host.js&storage=/storage/storage-host.js");
        await page.WaitForFunctionAsync("() => typeof window.vm === 'object'");

        var catalog = new ImageCatalog([new KnownImage(manifest.Image, manifest.Version, $"{server.BaseUrl}origin/linux-base/manifest.json", sha256)]);
        var store = new ImageStore(new PlaywrightStorageWorkerChannel(page, hub));
        var relay = new LocalTestRelay().WithHttp(Web, 80);
        // The window: the VM itself and p1 are allowed this time, p2 is refused this time.
        var prompt = new InjectedPermissionPrompt(request => request.Identity.Prefix == P2 ? NetworkPermissionAnswer.DenyOnce : NetworkPermissionAnswer.AllowOnce);
        var network = new NetworkHub(new InMemoryNetworkStore(), relay, prompt);
        var options = new VmManagerOptions { StopGrace = TimeSpan.FromSeconds(3), AgentWatchdogInterval = TimeSpan.FromSeconds(10) };
        await using var manager = new VmManager(new InMemoryVmRegistryStore(), new PlaywrightEngineFactory(page, hub), new StoreBootImageSource(store, catalog), new FixedHostMemory(8L * 1024 * 1024 * 1024), options, network: network);
        manager.BackgroundError += (_, e) => output.WriteLine($"[background error] {e.Data}");
        Guid user = VmTestRig.User;
        try
        {
            VmInstance a = await StartAsync(manager, user, "a");
            VmInstance b = await StartAsync(manager, user, "b");
            Assert.Equal("10.86.0.2", a.NetworkAddress.ToString());
            Assert.Equal("10.86.0.3", b.NetworkAddress.ToString());
            await using RootShell rootA = await RootShell.OpenAsync(a, output);
            await using RootShell rootB = await RootShell.OpenAsync(b, output);
            string memoryBefore = await rootA.RunAsync("grep -E 'MemTotal|MemAvailable' /proc/meminfo");

            await VmsReachEachOtherAsync(rootA, rootB);
            await PrefixesAreApartAsync(manager, user, a, rootA);
            await DecisionsAndJournalAsync(manager, user, a, prompt, relay);
            await ConfinementHoldsAsync(a, rootA);
            await NoRealInternetAsync(manager, user, a, relay);
            await MeasureAsync(a, rootA, memoryBefore);
            await PrintJournalAsync(manager);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    private async Task<VmInstance> StartAsync(VmManager manager, Guid user, string name)
    {
        VmSummary vm = await manager.CreateAsync(user, name, BuiltInProfiles.LinuxBase, None);
        VmInstance instance = await manager.GetInstanceAsync(user, vm.Id, None);
        long start = Stopwatch.GetTimestamp();
        await instance.HoldAsync(None).AsTask().WaitAsync(Boot);
        output.WriteLine($"VM {name} up with its network in {Stopwatch.GetElapsedTime(start).TotalSeconds:0.0} s at {instance.NetworkAddress}");
        return instance;
    }

    /// <summary>1. The root of VM a reaches a listener of VM b over the switch, from a's own address.</summary>
    private async Task VmsReachEachOtherAsync(RootShell rootA, RootShell rootB)
    {
        await rootB.StartAsync($"{Probe} listen 7000");
        await Task.Delay(500);
        string answer = await rootA.RunAsync($"{Probe} connect 10.86.0.3 7000");
        Assert.Contains("first=osx86-netcheck pong from=10.86.0.2", answer, StringComparison.Ordinal);
        output.WriteLine($"VM to VM: {answer.Trim()}");
    }

    /// <summary>2. p1 and p2 of VM a have their own addresses and reach neither each other nor VM b.</summary>
    private async Task PrefixesAreApartAsync(VmManager manager, Guid user, VmInstance a, RootShell rootA)
    {
        await manager.CreatePrefixAsync(user, a.Id, P1, None);
        await manager.CreatePrefixAsync(user, a.Id, P2, None);
        string p1 = (await ExecAsync(a, P1, "ip", "-4", "-o", "addr", "show", "dev", "eth0")).Output;
        string p2 = (await ExecAsync(a, P2, "ip", "-4", "-o", "addr", "show", "dev", "eth0")).Output;
        Assert.Contains("inet 10.86.2.2/30", p1, StringComparison.Ordinal);
        Assert.Contains("inet 10.86.2.6/30", p2, StringComparison.Ordinal);
        Assert.Equal("10.86.2.2", a.NetworkAddressOf(P1).ToString());
        output.WriteLine($"p1: {p1.Trim()}\np2: {p2.Trim()}");

        Task<ExecResult> listener = ExecAsync(a, P1, Probe, "listen", "7001");
        await Task.Delay(1000);
        ExecResult fromP2 = await ExecAsync(a, P2, Probe, "connect", "10.86.2.2", "7001");
        Assert.Equal(7, fromP2.Exit.Code);
        ExecResult toOtherVm = await ExecAsync(a, P2, Probe, "connect", "10.86.0.3", "7000");
        Assert.Equal(7, toOtherVm.Exit.Code);
        ExecResult toOwnVm = await ExecAsync(a, P2, Probe, "connect", "10.86.0.2", "22");
        Assert.Equal(7, toOwnVm.Exit.Code);
        output.WriteLine($"p2 -> p1: {fromP2.Output.Trim()}; p2 -> VM b: {toOtherVm.Output.Trim()}; p2 -> VM a: {toOwnVm.Output.Trim()}");

        // The root of the VM still reaches p1 (its own namespace may): the listener ends.
        await rootA.RunAsync($"{Probe} connect 10.86.2.2 7001");
        Assert.Equal(0, (await listener.WaitAsync(Step)).Exit.Code);
    }

    /// <summary>3. The scenario of milestone 0: the window opens, allow passes, refuse blocks, the journal records it.</summary>
    private async Task DecisionsAndJournalAsync(VmManager manager, Guid user, VmInstance a, InjectedPermissionPrompt prompt, LocalTestRelay relay)
    {
        ExecResult allowed = await ExecAsync(a, P1, Probe, "get", Web, "80", "/milestone-0");
        Assert.Equal(0, allowed.Exit.Code);
        Assert.Contains("first=HTTP/1.0 200 OK", allowed.Output, StringComparison.Ordinal);
        Assert.Contains("local=10.86.2.2", allowed.Output, StringComparison.Ordinal);

        ExecResult refused = await ExecAsync(a, P2, Probe, "get", Web, "80", "/milestone-0");
        Assert.Equal(7, refused.Exit.Code);
        Assert.Contains(prompt.Requests, request => request.Identity == new FlowIdentity(a.Id, P1) && request.Host == Web && request.Port == 80);
        Assert.Contains(prompt.Requests, request => request.Identity == new FlowIdentity(a.Id, P2));
        Assert.DoesNotContain(relay.Requests, request => request.Identity.Prefix == P2);

        IReadOnlyList<NetworkJournalEntry> journal = await WaitForJournalAsync(manager.Network!.For(user), entries =>
            entries.Any(e => e.Prefix == P1 && e.Event == NetworkJournalEvent.Closed) && entries.Any(e => e.Prefix == P2 && e.Event == NetworkJournalEvent.Refused));
        Assert.Contains(journal, e => e.Prefix == P1 && e.Event == NetworkJournalEvent.Opened && e.Reason == NetworkJournalReasons.Prompt);
        Assert.Contains(journal, e => e.Prefix == P2 && e.Event == NetworkJournalEvent.Refused && e.Reason == NetworkJournalReasons.Prompt);
        output.WriteLine($"allow: {allowed.Output.Trim()}\nrefuse: {refused.Output.Trim()}");
    }

    /// <summary>4. The 8 checks of E6 as p2's user, plus UDP 53, IPv6 and a user outside any prefix.</summary>
    private async Task ConfinementHoldsAsync(VmInstance a, RootShell rootA)
    {
        // p1 leaves what a Wine server would: a 0700 directory of its own in /tmp.
        string uid1 = (await ExecAsync(a, P1, "id", "-u")).Output.Trim();
        Assert.Equal(0, (await ExecAsync(a, P1, "sh", "-c", $"mkdir -m 700 /tmp/.wine-{uid1} && echo socket > /tmp/.wine-{uid1}/server")).Exit.Code);

        var checks = new (string Name, string[] Command, Func<ExecResult, bool> Held)[]
        {
            ("unshare -n", ["unshare", "-n", "true"], r => r.Exit.Code != 0),
            ("nsenter to the VM's namespace", ["nsenter", "-t", "1", "-n", "true"], r => r.Exit.Code != 0),
            ("another prefix's server directory", ["ls", $"/tmp/.wine-{uid1}"], r => r.Exit.Code != 0 && r.Output.Contains("Permission denied", StringComparison.Ordinal)),
            ("another prefix's home", ["ls", "/home/osx86-p1"], r => r.Exit.Code != 0 && r.Output.Contains("Permission denied", StringComparison.Ordinal)),
            ("the serial console and the agent's port", ["sh", "-c", "cat /dev/ttyS0 </dev/null; cat /dev/hvc0 </dev/null"], r => r.Exit.Code != 0 && Regex.Count(r.Output, "Permission denied") == 2),
            ("a raw socket", ["/usr/lib/osx86/osx86-rawsock"], r => r.Exit.Code != 0),
            ("a setuid program", ["sh", "-c", "find / -xdev -type f -perm -4000 2>/dev/null"], r => r.Output.Trim().Length == 0),
            ("the home is nosuid", ["grep", " /home ", "/proc/mounts"], r => r.Output.Contains("nosuid", StringComparison.Ordinal)),
            ("DNS (UDP 53)", [Probe, "udp", "8.8.8.8", "53"], r => r.Exit.Code == 7),
            ("IPv6", [Probe, "ipv6"], r => r.Exit.Code == 7),
        };
        foreach ((string name, string[] command, Func<ExecResult, bool> held) in checks)
        {
            ExecResult result = await ExecAsync(a, P2, command[0], command[1..]);
            output.WriteLine($"confinement [{name}]: code {result.Exit.Code}, {result.Output.Trim().Replace('\n', ' ')}");
            Assert.True(held(result), $"the check '{name}' does not hold: code {result.Exit.Code}, output {result.Output}");
        }

        // A user of the VM outside any prefix namespace has no network at all (the root namespace's rules).
        string outside = await rootA.RunAsync($"setpriv --reuid=65534 --regid=65534 --clear-groups {Probe} get {Web} 80 /outside; echo rc=$?");
        Assert.Contains("rc=7", outside, StringComparison.Ordinal);
        output.WriteLine($"confinement [user outside a prefix]: {outside.Trim()}");
        string dns = await rootA.RunAsync($"{Probe} udp 10.86.0.1 53; echo rc=$?");
        Assert.Contains("rc=7", dns, StringComparison.Ordinal);
    }

    /// <summary>A destination the test relay does not serve fails there: the relay never leaves the page.</summary>
    private async Task NoRealInternetAsync(VmManager manager, Guid user, VmInstance a, LocalTestRelay relay)
    {
        await manager.Network!.For(user).Rules.AddAsync(new FlowIdentity(a.Id, P1), NetworkDestination.Any, NetworkRuleAction.Allow, session: false, None);
        ExecResult real = await ExecAsync(a, P1, Probe, "get", "1.1.1.1", "80", "/");
        Assert.Equal(7, real.Exit.Code);
        Assert.Contains(relay.Requests, request => request.Host == "1.1.1.1");
        IReadOnlyList<NetworkJournalEntry> journal = await WaitForJournalAsync(manager.Network.For(user), entries => entries.Any(e => e.Host == "1.1.1.1"));
        Assert.Contains(journal, e => e.Host == "1.1.1.1" && e.Reason == NetworkJournalReasons.Relay && e.Detail == RelayException.Unreachable);
    }

    /// <summary>5. Throughput through the VM's namespace and through a prefix's, and the guest memory.</summary>
    private async Task MeasureAsync(VmInstance a, RootShell rootA, string memoryBefore)
    {
        const string Blob = "/blob?mb=8";
        for (int pass = 1; pass <= 3; pass++) // the first pass warms the engine's code and caches
        {
            string direct = await rootA.RunAsync($"{Probe} get {Web} 80 '{Blob}'", TimeSpan.FromSeconds(180));
            ExecResult viaPrefix = await ExecAsync(a, P1, Probe, "get", Web, "80", Blob);
            output.WriteLine($"measure pass {pass}: VM namespace {Rate(direct)} MiB/s, prefix namespace {Rate(viaPrefix.Output)} MiB/s");
            Assert.Contains("bytes=8388", viaPrefix.Output, StringComparison.Ordinal);
        }

        string memoryAfter = await rootA.RunAsync("grep -E 'MemTotal|MemAvailable' /proc/meminfo; ip netns list");
        output.WriteLine($"guest memory before the prefixes: {memoryBefore.Trim().Replace('\n', ' ')}");
        output.WriteLine($"guest memory with two prefix namespaces: {memoryAfter.Trim().Replace('\n', ' ')}");
    }

    private async Task PrintJournalAsync(VmManager manager)
    {
        var services = new X86CommandServices(manager, new X86CommandTests.FakeFiles());
        X86Command.ServicesFactory = () => services;
        try
        {
            var stdout = new StringWriter();
            int code = await new X86Command().ExecuteAsync(TestAppContext.Terminal(["rules", "log", "--last", "40"], stdout, new StringWriter()), None);
            Assert.Equal(0, code);
            output.WriteLine("x86 rules log:\n" + stdout);
        }
        finally
        {
            X86Command.ServicesFactory = static () => X86Runtime.Services;
        }
    }

    private static string Rate(string text) => MibpsPattern().Match(text) is { Success: true } match ? match.Groups[1].Value : "?";

    private static async Task<IReadOnlyList<NetworkJournalEntry>> WaitForJournalAsync(UserNetwork network, Func<IReadOnlyList<NetworkJournalEntry>, bool> ready)
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            IReadOnlyList<NetworkJournalEntry> entries = await network.Journal.ReadAsync(None);
            if (ready(entries))
            {
                return entries;
            }

            Assert.True(Stopwatch.GetElapsedTime(start) < Step, "the journal did not record the flows");
            await Task.Delay(200);
        }
    }

    private static Task<ExecResult> ExecAsync(VmInstance vm, PrefixId prefix, string program, params string[] arguments) =>
        vm.ExecInPrefixAsync(prefix, program, arguments, TimeSpan.FromSeconds(120), None).WaitAsync(TimeSpan.FromSeconds(180));

    [GeneratedRegex(@"mibps=([0-9.]+)")]
    private static partial Regex MibpsPattern();

    /// <summary>The root shell on the serial console of a VM: one command at a time, its output up to a marker.</summary>
    private sealed class RootShell : IAsyncDisposable
    {
        private readonly IVmConsole _console;
        private readonly StringBuilder _text = new();
        private readonly Lock _lock = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly ITestOutputHelper _output;
        private int _count;

        private RootShell(IVmConsole console, ITestOutputHelper output)
        {
            _console = console;
            _output = output;
            _ = Task.Run(ReadAsync);
        }

        public static async Task<RootShell> OpenAsync(VmInstance vm, ITestOutputHelper output)
        {
            IVmConsole console = await vm.OpenConsoleAsync(new VmConsoleOptions(), None);
            Assert.True(console.IsSerial);
            var shell = new RootShell(console, output);
            await shell.RunAsync("true", Boot); // the serial shell starts after the agent: wait for it
            return shell;
        }

        /// <summary>Starts a command in the background.</summary>
        public Task StartAsync(string command) => WriteAsync($"({command}) >/dev/null 2>&1 &\n").AsTask();

        /// <summary>Runs a command and returns what it printed; sent again until the shell answers.</summary>
        public async Task<string> RunAsync(string command, TimeSpan? timeout = null)
        {
            int number = Interlocked.Increment(ref _count);
            string marker = $"@@END-{number}@@";
            string typed = $"{command}; echo @@E\"N\"D-{number}@@\n"; // what the terminal echoes is not the marker
            long start = Stopwatch.GetTimestamp();
            int from;
            lock (_lock)
            {
                from = _text.Length;
            }

            await WriteAsync(typed);
            long lastSend = Stopwatch.GetTimestamp();
            while (true)
            {
                string text;
                lock (_lock)
                {
                    text = _text.ToString(from, _text.Length - from);
                }

                int end = text.IndexOf(marker, StringComparison.Ordinal);
                if (end >= 0)
                {
                    int lineStart = text.IndexOf('\n', StringComparison.Ordinal);
                    return lineStart >= 0 && lineStart < end ? text[(lineStart + 1)..end] : text[..end];
                }

                TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
                Assert.True(elapsed < (timeout ?? Step), $"no answer to '{command}': {text}");
                if (number == 1 && Stopwatch.GetElapsedTime(lastSend) > TimeSpan.FromSeconds(5))
                {
                    await WriteAsync(typed); // the first command may come before the shell reads the port
                    lastSend = Stopwatch.GetTimestamp();
                }

                await Task.Delay(100);
            }
        }

        private ValueTask WriteAsync(string text) => _console.WriteAsync(Encoding.UTF8.GetBytes(text), None);

        private async Task ReadAsync()
        {
            try
            {
                await foreach (ReadOnlyMemory<byte> chunk in _console.ReadOutputAsync(_stop.Token))
                {
                    lock (_lock)
                    {
                        _text.Append(Encoding.UTF8.GetString(chunk.Span).Replace("\r", string.Empty, StringComparison.Ordinal));
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or X86Exception)
            {
                // Closed.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _console.DisposeAsync();
            }
            catch (X86Exception exception)
            {
                _output.WriteLine($"serial console: {exception.Message}");
            }
        }
    }
}
