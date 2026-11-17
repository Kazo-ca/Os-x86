using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OsX86.Agent;
using OsX86.Engine;
using OsX86.Tests.Agent;
using Xunit.Abstractions;

namespace OsX86.Tests.Guest;

/// <summary>
/// The <c>guest</c> test of work item 6a: linux-base, published as chunks and copied into the OPFS,
/// boots in the engine worker with its ext4 root as <c>hda</c> (frames decompressed on demand):
/// <list type="number">
/// <item>with a persistent layer: up to <c>OSX86-READY</c>, the agent answers <c>ping</c> through
/// <see cref="AgentClient"/>, <c>verify-console.sh</c> run from an agent console of an ordinary user
/// holds its 22 R134.5 checks, the disk is measured, and a file written in the guest goes to the layer;</item>
/// <item>with the same layer after a clean stop: the file is there;</item>
/// <item>discard: the file is not there (the image itself was never written), a new one is written;</item>
/// <item>discard again: that one is gone too.</item>
/// </list>
/// linux-base has no prefix user yet (work items 9 and 10): the test adds two, <c>osx86-test</c> and
/// <c>osx86-other</c>, from the root serial shell, in memory (bind mounts over <c>/etc/passwd</c> and
/// <c>/etc/group</c>). Skipped when the pack is not built (<c>python -I images/linux-base/build-linux-base.py</c>)
/// or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
public sealed partial class LinuxBaseGuestTests(ITestOutputHelper output)
{
    private const int BootTimeoutMs = 300_000;
    private const string LayerSet = "linux-base-guest-test";
    private const string MarkerFile = "/var/lib/osx86-6a-marker";
    private static readonly TimeSpan ConsoleTimeout = TimeSpan.FromMinutes(3);
    private static readonly CancellationToken None = CancellationToken.None;

    [SkippableFact]
    public async Task Linux_base_boots_from_the_opfs_with_its_agent_its_checks_and_its_write_layer()
    {
        string pack = PackedGuestSession.RequirePack("linux-base", "images/linux-base/build-linux-base.py");
        await using PackedGuestSession session = await PackedGuestSession.OpenAsync(pack, output);
        GuestAgentBridge bridge = await GuestAgentBridge.AttachAsync(session.Page);
        try
        {
            long installMs = await session.InstallAsync();
            output.WriteLine($"linux-base: {session.Manifest.Size} bytes copied into the OPFS in {installMs} ms");
            EngineStartOptions persistent = ImageBoot.CreateStartOptions(session.Manifest, new ImageBootOptions { LayerMode = EngineWriteLayerMode.Persistent, LayerSet = LayerSet });
            EngineStartOptions discard = ImageBoot.CreateStartOptions(session.Manifest);
            string layerPath = persistent.Disks["hda"].Layer.Path!;

            // 1. Persistent layer: ready marker, agent, the 22 checks, measures, a write.
            await BootAsync(session, persistent, "first boot, persistent layer");
            await using (var client = new AgentClient(bridge.Channel))
            {
                AgentStatus status = await client.ConnectAsync(None);
                Assert.Equal(3, status.ProtocolVersion);
                Assert.True(status.HasCgroups, "linux-base has no cgroup v2");
                await MeasurePingAsync(client);
                await AddPrefixUsersAsync(session);
                await RunVerifyConsoleAsync(client);
            }

            await MeasureDiskAsync(session);
            await WriteMarkerAsync(session, "persisted");
            await session.StopAsync();
            long layerBytes = await session.OpfsFileSizeAsync(layerPath);
            output.WriteLine($"layer after the first boot: {layerBytes} bytes at {layerPath}");
            Assert.True(layerBytes > 4096, "the layer holds no block after a write in the guest");
            Assert.True(await session.OpfsFileSizeAsync(layerPath + ".meta.json") > 0, "the layer has no metadata");

            // 2. The same layer after a clean stop: the write survived.
            await BootAsync(session, persistent, "second boot, same layer");
            Assert.Equal("persisted", await ReadMarkerAsync(session));
            await session.StopAsync();

            // 3. Discard: the image was never written; a new write lives in memory only.
            await BootAsync(session, discard, "third boot, discard");
            Assert.Equal("none", await ReadMarkerAsync(session));
            await WriteMarkerAsync(session, "discarded");
            Assert.Equal("discarded", await ReadMarkerAsync(session));
            await session.StopAsync();

            // 4. Discard again: gone.
            await BootAsync(session, discard, "fourth boot, discard");
            Assert.Equal("none", await ReadMarkerAsync(session));
            await session.StopAsync();
            Assert.Equal(layerBytes, await session.OpfsFileSizeAsync(layerPath));
            Assert.Equal(0, (await session.ErrorsAsync()).GetArrayLength());
        }
        catch (Exception)
        {
            await session.DumpAsync();
            throw;
        }
        finally
        {
            bridge.Complete();
        }
    }

    private async Task BootAsync(PackedGuestSession session, EngineStartOptions options, string what)
    {
        var clock = Stopwatch.StartNew();
        long startMs = await session.StartAsync(options);
        await session.WaitForSerialAsync(session.Manifest.Boot!.ReadyMarker!, BootTimeoutMs);
        long readyMs = clock.ElapsedMilliseconds;
        await session.WaitForSerialAsync(session.Manifest.Boot.Prompt!, 60_000);
        output.WriteLine($"{what}: engine running after {startMs} ms, OSX86-READY after {readyMs} ms, prompt after {clock.ElapsedMilliseconds} ms");
    }

    private async Task MeasurePingAsync(AgentClient client)
    {
        var times = new List<double>();
        for (int index = 0; index < 10; index++)
        {
            long start = Stopwatch.GetTimestamp();
            await client.PingAsync(None);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        times.Sort();
        AgentStatus status = await client.PingAsync(None);
        output.WriteLine($"agent ping: median {times[5]:0.0} ms (min {times[0]:0.0}, max {times[^1]:0.0}); agent RSS {status.ResidentKiB} KiB, guest up {status.Uptime.TotalSeconds:0} s");
    }

    /// <summary>Two prefix users in memory, from the root serial shell (linux-base has none until work items 9 and 10).</summary>
    private static async Task AddPrefixUsersAsync(PackedGuestSession session)
    {
        string[] commands =
        [
            "cp /etc/passwd /run/osx86-passwd && cp /etc/group /run/osx86-group",
            "echo 'osx86-test:x:1001:1001::/home/osx86-test:/bin/sh' >> /run/osx86-passwd && echo 'osx86-other:x:1002:1002::/home/osx86-other:/bin/sh' >> /run/osx86-passwd",
            "echo 'osx86-test:x:1001:' >> /run/osx86-group && echo 'osx86-other:x:1002:' >> /run/osx86-group",
            "mount --bind /run/osx86-passwd /etc/passwd && mount --bind /run/osx86-group /etc/group",
            "mkdir -p /home/osx86-test /home/osx86-other && chown 1001:1001 /home/osx86-test && chown 1002:1002 /home/osx86-other && chmod 700 /home/osx86-test /home/osx86-other",
        ];
        foreach (string command in commands)
        {
            string transcript = await session.SerialAsync($"{command} && echo USERS-$((40+2))", "USERS-42", 30_000);
            await session.Page.EvaluateAsync("() => window.guest.clearTranscripts()");
            Assert.Contains("USERS-42", transcript, StringComparison.Ordinal);
        }
    }

    /// <summary>verify-console.sh from a console of the user osx86-test, another user's process as its target.</summary>
    private async Task RunVerifyConsoleAsync(AgentClient client)
    {
        LaunchResult other = await client.LaunchAsync(new LaunchOptions { Prefix = "other", Program = "sleep", Arguments = ["600"] }, None);
        ProcessInfo agent = Assert.Single(await client.ListProcessesAsync(None), process => process.Name == "agent" && process.UserId == 0);
        ConsoleHandle console = await client.OpenConsoleAsync(new ConsoleOptions
        {
            Prefix = "test",
            Program = "/bin/sh",
            Arguments =
            [
                "/usr/lib/osx86/verify-console.sh",
                other.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "/home/osx86-other",
                agent.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ],
        }, None);
        var text = new StringBuilder();
        Decoder decoder = Encoding.UTF8.GetDecoder();
        using var timeout = new CancellationTokenSource(ConsoleTimeout);
        await foreach (ReadOnlyMemory<byte> chunk in console.ReadOutputAsync(timeout.Token))
        {
            char[] chars = new char[decoder.GetCharCount(chunk.Span, flush: false)];
            text.Append(chars, 0, decoder.GetChars(chunk.Span, chars, flush: false));
        }

        ProcessExit exit = await console.Exited.WaitAsync(ConsoleTimeout);
        string report = text.ToString().Replace("\r", string.Empty, StringComparison.Ordinal);
        output.WriteLine(report);
        string[] checks = [.. report.Split('\n').Where(line => line.StartsWith("RES check ", StringComparison.Ordinal) && !line.Contains(" INFO ", StringComparison.Ordinal))];
        Match done = VerifyDone().Match(report);
        Assert.True(done.Success, "verify-console.sh did not finish");
        Assert.Equal(22, checks.Length);
        Assert.DoesNotContain(checks, line => line.Contains(" LEAK ", StringComparison.Ordinal));
        Assert.Equal("22", done.Groups[1].Value);
        Assert.Equal("0", done.Groups[2].Value);
        Assert.Contains("uid=1001(osx86-test)", report, StringComparison.Ordinal);
        // The console is a pty of the user, in the cgroup of its launch: a check that runs a program with
        // 2>/dev/null only means something if the user can open /dev/null (0660 until work item 6a).
        Assert.Contains("tty=/dev/pts/", report, StringComparison.Ordinal);
        Assert.Contains("cgroup=0::/osx86/test/", report, StringComparison.Ordinal);
        Assert.Contains("RES check kill_agent HELD", report, StringComparison.Ordinal);
        Assert.DoesNotContain("no agent in this image", report, StringComparison.Ordinal);
        Assert.Equal(new ProcessExit(0, 0), exit);
        await client.KillAsync(other.Id, TimeSpan.Zero);
    }

    private async Task MeasureDiskAsync(PackedGuestSession session)
    {
        JsonElement disks = await session.QueryDisksAsync();
        JsonElement hda = disks.GetProperty("disks").GetProperty("hda");
        JsonElement cache = disks.GetProperty("cache");
        output.WriteLine($"hda after boot and checks: {hda.GetProperty("reads").GetInt64()} reads, {hda.GetProperty("bytesRead").GetInt64()} bytes read "
            + $"({hda.GetProperty("compressedBytesRead").GetInt64()} compressed bytes from image.bin), {hda.GetProperty("bytesWritten").GetInt64()} bytes written; "
            + $"frame cache {cache.GetProperty("cacheBytes").GetInt64()} of {cache.GetProperty("cacheMaxBytes").GetInt64()} bytes, {cache.GetProperty("hits").GetInt64()} hits, "
            + $"{cache.GetProperty("misses").GetInt64()} misses, {cache.GetProperty("evictions").GetInt64()} evictions; decoder {disks.GetProperty("decoder").GetRawText()}");
        JsonElement benchmark = await session.BenchmarkAsync(128, 4000);
        output.WriteLine($"hda read outside the guest: sequential {benchmark.GetProperty("sequential").GetRawText()}, random 4 KiB {benchmark.GetProperty("random").GetRawText()}");
        JsonElement after = (await session.QueryDisksAsync()).GetProperty("cache");
        output.WriteLine($"frame cache after the benchmark: {after.GetProperty("cacheBytes").GetInt64()} of {after.GetProperty("cacheMaxBytes").GetInt64()} bytes ({after.GetProperty("cacheFrames").GetInt32()} frames)");
        Assert.True(after.GetProperty("cacheBytes").GetInt64() <= after.GetProperty("cacheMaxBytes").GetInt64());
    }

    /// <summary>Writes the marker file on the read-only root (remounted read-write for the write), then syncs.</summary>
    private static async Task WriteMarkerAsync(PackedGuestSession session, string value)
    {
        string transcript = await session.SerialAsync(
            $"mount -o remount,rw / && printf %s {value} > {MarkerFile} && sync && mount -o remount,ro / && echo WRITE-$((40+2))", "WRITE-42", 60_000);
        Assert.Contains("WRITE-42", transcript, StringComparison.Ordinal);
        await session.Page.EvaluateAsync("() => window.guest.clearTranscripts()");
    }

    /// <summary>The content of the marker file, or <c>none</c>.</summary>
    private static async Task<string> ReadMarkerAsync(PackedGuestSession session)
    {
        string transcript = await session.SerialAsync($"echo R$((1+1))=$(cat {MarkerFile} 2>/dev/null || echo none)=E$((2+2))", "=E4", 30_000);
        await session.Page.EvaluateAsync("() => window.guest.clearTranscripts()");
        Match match = MarkerAnswer().Match(transcript);
        Assert.True(match.Success, $"no marker answer in: {transcript}");
        return match.Groups[1].Value;
    }

    [GeneratedRegex(@"RES verify-done (\d+) (\d+)")]
    private static partial Regex VerifyDone();

    [GeneratedRegex(@"R2=([a-z]+)=E4")]
    private static partial Regex MarkerAnswer();
}
