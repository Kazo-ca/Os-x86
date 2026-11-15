using System.Diagnostics;
using OsX86.Agent;
using OsX86.Interface;
using OsX86.Profiles;
using OsX86.Tests.Agent;
using OsX86.Vm;

namespace OsX86.Tests.Vm;

/// <summary>A VM manager on simulated engines and agents, a manual clock and an in-memory registry.</summary>
internal sealed class VmTestRig : IAsyncDisposable
{
    public static readonly Guid User = Guid.Parse("0000000000000000000000000000a001");
    public static readonly Guid OtherUser = Guid.Parse("0000000000000000000000000000b002");
    public static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    /// <summary>Short agent delays: an image without agent is detected in a fraction of a second.</summary>
    public static readonly AgentClientOptions FastAgent = new()
    {
        RequestTimeout = TimeSpan.FromSeconds(5),
        ConnectTimeout = TimeSpan.FromMilliseconds(300),
        ConnectProbeInterval = TimeSpan.FromMilliseconds(30),
        ConsoleCloseFallback = TimeSpan.FromMilliseconds(50),
    };

    public static readonly VmManagerOptions FastOptions = new()
    {
        StopGrace = TimeSpan.FromSeconds(5),
        AgentWatchdogInterval = TimeSpan.Zero,
        Agent = FastAgent,
        ConsoleCloseGrace = TimeSpan.FromMilliseconds(100),
    };

    public static readonly VmProfile NoAgentSerial = BuiltInProfiles.AlpineTest with { Name = "plain-serial" };

    public static readonly VmProfile NoSerial = BuiltInProfiles.AlpineTest with { Name = "no-serial", SerialConsole = false };

    public VmTestRig(VmManagerOptions? options = null, long? announcedMiB = 1024)
    {
        Images = new StaticBootImageSource()
            .Add("alpine-test", "0.1.0", new BootImage("osx86-test/vmlinuz-virt", "osx86-test/initramfs-osx86", "console=ttyS0"))
            .Add("linux-base", "0.1.0", new BootImage("osx86-test/vmlinuz-virt", "osx86-test/initramfs-osx86", "console=ttyS0"));
        Manager = new VmManager(Store, Engines, Images, new FixedHostMemory(announcedMiB * 1024 * 1024), options ?? FastOptions, Time);
        Manager.BackgroundError += (_, e) => BackgroundErrors.Add(e.Data);
    }

    public ManualTimeProvider Time { get; } = new();

    public InMemoryVmRegistryStore Store { get; } = new();

    public FakeEngineFactory Engines { get; } = new();

    public StaticBootImageSource Images { get; }

    public VmManager Manager { get; }

    public List<Exception> BackgroundErrors { get; } = [];

    public async Task<VmInstance> CreateAsync(string name = "alpine", VmProfile? profile = null, Guid? user = null)
    {
        Guid owner = user ?? User;
        VmSummary vm = await Manager.CreateAsync(owner, name, profile ?? BuiltInProfiles.AlpineTest, CancellationToken.None);
        return await Manager.GetInstanceAsync(owner, vm.Id, CancellationToken.None);
    }

    /// <summary>Scripts the agent of an engine to open console <paramref name="number"/> and to close it with SIGHUP.</summary>
    public static void ScriptConsole(FakeEngine engine, int number = 3, string prefix = "test")
    {
        SimulatedAgentChannel agent = engine.Agent!;
        agent.On("open-console", request => $$"""{"ok":true,"launch":"{{request.GetProperty("prefix").GetString()}}/{{number}}","console":{{number}},"pid":44,"uid":1001,"cols":80,"rows":25}""");
        agent.On("console-resize", request => $$"""{"ok":true,"console":{{number}},"cols":{{request.GetProperty("cols").GetInt32()}},"rows":{{request.GetProperty("rows").GetInt32()}}}""");
        agent.On(
            "close-console",
            _ => $$"""{"ok":true,"console":{{number}},"keptCount":0,"kept":null}""",
            $$"""{"event":"console-closed","launch":"{{prefix}}/{{number}}","console":{{number}},"pid":44,"code":-1,"signal":1,"remaining":0}""");
    }

    /// <summary>The frame that ends console <paramref name="console"/> with a code or a signal.</summary>
    public static byte[] Closed(int console, int code, int signal) =>
    [
        0xAE, 0x02, (byte)(console >> 24), (byte)(console >> 16), (byte)(console >> 8), (byte)console, 0x00, 0x08,
        (byte)(code >> 24), (byte)(code >> 16), (byte)(code >> 8), (byte)code,
        (byte)(signal >> 24), (byte)(signal >> 16), (byte)(signal >> 8), (byte)signal,
    ];

    /// <summary>Waits, in real time, for a condition that background work makes true.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(start) < Soon, $"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync() => await Manager.DisposeAsync();
}
