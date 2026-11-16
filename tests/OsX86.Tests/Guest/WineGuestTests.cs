using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using OsX86.Agent;
using OsX86.Engine;
using OsX86.Tests.Agent;
using Xunit.Abstractions;

namespace OsX86.Tests.Guest;

/// <summary>
/// The <c>guest</c> test of work item 9 (decisions 152 A and 204 A): the wine image, published as
/// chunks and copied into the OPFS, boots in the engine worker with its ext4 root as <c>hda</c> (the
/// path of work item 6a). Three cold boots, each measured from the shell prompt of the serial console:
/// <list type="number">
/// <item>without the preloading (<c>osx86.preload=off</c>), the first program right after the prompt;</item>
/// <item>with the preloading of the hot files (the default), the first program right after the prompt;</item>
/// <item>with the preloading, the first program once the preloading is done.</item>
/// </list>
/// Each boot: a guest user <c>osx86-test</c> is added in memory from the root serial shell (prefixes
/// are work item 10), the template prefix is copied for it with <c>/usr/lib/osx86/wine/prefix-copy</c>,
/// the agent answers, the prefix's <c>wineserver</c> starts as a launch of its own, then
/// <c>exec-in-prefix</c> runs <c>wine cmd /c echo</c> and the console program
/// of the recipe (<c>osx86-hello.exe</c>, built from source with MinGW-w64), and their output and exit
/// codes are checked. The criterion of 204 A (first program less than 60 s after the prompt) is
/// printed, not asserted: when it is missed, decision 152 goes back to the maintainer with the numbers.
/// Skipped when the pack is not built (<c>python -I images/wine/build-wine.py</c>) or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
public sealed partial class WineGuestTests(ITestOutputHelper output)
{
    private const int BootTimeoutMs = 300_000;
    private const string PreloadOff = "osx86.preload=off";
    private const string PreloadDone = "OSX86-PRELOAD done";
    private const int CriterionSeconds = 60;
    private static readonly TimeSpan WineTimeout = TimeSpan.FromMinutes(8);
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly System.Text.StringBuilder _serial = new();

    [SkippableFact]
    public async Task Wine_runs_cmd_and_a_console_program_in_a_copy_of_the_template_prefix()
    {
        string pack = PackedGuestSession.RequirePack("wine", "images/wine/build-wine.py");
        await using PackedGuestSession session = await PackedGuestSession.OpenAsync(pack, output);
        GuestAgentBridge bridge = await GuestAgentBridge.AttachAsync(session.Page);
        try
        {
            long installMs = await session.InstallAsync();
            output.WriteLine($"wine: {session.Manifest.Size} bytes copied into the OPFS in {installMs} ms");
            EngineStartOptions preloading = ImageBoot.CreateStartOptions(session.Manifest);
            EngineStartOptions withoutPreloading = preloading with { KernelCommandLine = preloading.KernelCommandLine + " " + PreloadOff };
            Assert.Equal(512, preloading.MemoryMiB);

            var runs = new List<FirstRun>
            {
                await RunAsync(session, bridge, withoutPreloading, "cold boot, no preloading", waitForPreload: false),
                await RunAsync(session, bridge, preloading, "cold boot, preloading, program at once", waitForPreload: false),
                await RunAsync(session, bridge, preloading, "cold boot, preloading, program after it", waitForPreload: true),
            };

            output.WriteLine("summary (s after the prompt: prefix copied, wine cmd done, console program done; criterion of 204 A: program < 60 s):");
            foreach (FirstRun run in runs)
            {
                string verdict = run.ProgramDoneMs < CriterionSeconds * 1000 ? "met" : "MISSED";
                output.WriteLine($"  {run.What}: boot to prompt {Seconds(run.BootMs)}; copy {Seconds(run.CopyDoneMs)}, cmd {Seconds(run.CmdDoneMs)} "
                    + $"(wine cmd itself {Seconds(run.CmdMs)}), program {Seconds(run.ProgramDoneMs)} (itself {Seconds(run.ProgramMs)}); preload {run.Preload}; criterion {verdict}");
            }

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

    private async Task<FirstRun> RunAsync(PackedGuestSession session, GuestAgentBridge bridge, EngineStartOptions options, string what, bool waitForPreload)
    {
        var clock = Stopwatch.StartNew();
        _serial.Clear();
        long startMs = await session.StartAsync(options);
        await session.WaitForSerialAsync(session.Manifest.Boot!.ReadyMarker!, BootTimeoutMs);
        await session.WaitForSerialAsync(session.Manifest.Boot.Prompt!, 60_000);
        long bootMs = clock.ElapsedMilliseconds;
        output.WriteLine($"{what}: engine running after {startMs} ms, prompt after {bootMs} ms");
        if (waitForPreload)
        {
            await session.WaitForSerialAsync(PreloadDone, BootTimeoutMs);
            output.WriteLine($"{what}: preloading done {clock.ElapsedMilliseconds - bootMs} ms after the prompt");
        }

        string preloadText = await PreloadStateAsync(session);
        var sincePrompt = Stopwatch.StartNew();
        await AddPrefixUserAsync(session);
        long copyDoneMs = sincePrompt.ElapsedMilliseconds;
        var client = new AgentClient(bridge.Channel);
        AgentStatus status = await client.ConnectAsync(None);
        Assert.True(status.HasCgroups, "the wine image has no cgroup v2");
        // The wineserver of the prefix runs as a launch of its own (persistent, -p): a wineserver started
        // by an exec-in-prefix command belongs to that command's cgroup, which the agent ends 2 s after
        // the command exits (SIGHUP then cgroup.kill, 208 A); a second command that connects to it in
        // that window hangs (seen in this test: 480 s). Work item 10 decides how prefixes keep theirs.
        LaunchResult server = await client.LaunchAsync(new LaunchOptions
        {
            Prefix = "test",
            Program = "wineserver",
            Arguments = ["-f", "-p"],
            Environment = new Dictionary<string, string> { ["WINEDEBUG"] = "-all" },
        }, None);
        ExecResult cmd = await WineAsync(client, ["cmd", "/c", "echo", "osx86-wine-ok"]);
        long cmdDoneMs = sincePrompt.ElapsedMilliseconds;
        ExecResult program = await WineAsync(client, ["/usr/lib/osx86/wine/osx86-hello.exe", "first", "program"]);
        long programDoneMs = sincePrompt.ElapsedMilliseconds;
        output.WriteLine($"{what}: wine cmd {cmd.Exit} in {cmd.Duration.TotalMilliseconds:0} ms: {cmd.Output.Trim()}");
        output.WriteLine($"{what}: osx86-hello.exe {program.Exit} in {program.Duration.TotalMilliseconds:0} ms: {program.Output.Trim()}");
        Assert.False(cmd.TimedOut, "wine cmd timed out");
        Assert.Equal(new ProcessExit(0, 0), cmd.Exit);
        Assert.Contains("osx86-wine-ok", cmd.Output, StringComparison.Ordinal);
        Assert.False(program.TimedOut, "the console program timed out");
        Assert.Equal(new ProcessExit(7, 0), program.Exit);
        Assert.Matches(HelloLine(), program.Output);

        // The template's timestamp equals the modification time of wine.inf in the ext4 image:
        // otherwise Wine runs a full wineboot -u in the VM, which 152 A forbids.
        ExecResult timestamps = await client.ExecInPrefixAsync(new ExecOptions
        {
            Prefix = "test",
            Program = "/bin/sh",
            Arguments = ["-c", "cat $HOME/.wine/.update-timestamp; stat -c %Y /opt/wine-stable/share/wine/wine.inf"],
        }, None);
        string[] values = timestamps.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, values.Length);
        Assert.Equal(values[1], values[0]);

        if (preloadText == "running")
        {
            preloadText = await PreloadStateAsync(session);
        }

        await client.KillAsync(server.Id, TimeSpan.FromSeconds(2));
        await client.DisposeAsync();
        await session.StopAsync();
        return new FirstRun(what, bootMs, copyDoneMs, cmdDoneMs, (long)cmd.Duration.TotalMilliseconds, programDoneMs, (long)program.Duration.TotalMilliseconds, preloadText);
    }

    private async Task<string> PreloadStateAsync(PackedGuestSession session)
    {
        string transcript = _serial + await session.TranscriptAsync();
        if (transcript.Contains("OSX86-PRELOAD off", StringComparison.Ordinal))
        {
            return "off";
        }

        Match done = PreloadLine().Match(transcript);
        return done.Success ? $"{done.Groups[1].Value} files read between guest uptime {done.Groups[2].Value} and {done.Groups[3].Value} s" : "running";
    }

    private static Task<ExecResult> WineAsync(AgentClient client, IReadOnlyList<string> arguments) =>
        client.ExecInPrefixAsync(new ExecOptions
        {
            Prefix = "test",
            Program = "wine",
            Arguments = arguments,
            Environment = new Dictionary<string, string> { ["WINEDEBUG"] = "-all" },
            Timeout = WineTimeout,
        }, None);

    /// <summary>
    /// The guest user of the prefix "test", in memory, from the root serial shell (the wine image has no
    /// prefix user: work item 10 creates them), then its copy of the template prefix.
    /// </summary>
    private async Task AddPrefixUserAsync(PackedGuestSession session)
    {
        string[] commands =
        [
            "cp /etc/passwd /run/osx86-passwd && cp /etc/group /run/osx86-group",
            "echo 'osx86-test:x:1001:1001::/home/osx86-test:/bin/sh' >> /run/osx86-passwd && echo 'osx86-test:x:1001:' >> /run/osx86-group",
            "mount --bind /run/osx86-passwd /etc/passwd && mount --bind /run/osx86-group /etc/group",
            "mkdir -p /home/osx86-test && chown 1001:1001 /home/osx86-test && chmod 700 /home/osx86-test",
            "/usr/lib/osx86/wine/prefix-copy osx86-test && du -sk /home/osx86-test/.wine",
        ];
        foreach (string command in commands)
        {
            string transcript = await session.SerialAsync($"{command} && echo USERS-$((40+2))", "USERS-42", 120_000);
            Assert.Contains("USERS-42", transcript, StringComparison.Ordinal);
            // The serial transcript is kept here (the preloading reports on it) before it is cleared
            // for the next command (a waiter matches the whole transcript).
            _serial.Append(await session.TranscriptAsync());
            await session.Page.EvaluateAsync("() => window.guest.clearTranscripts()");
        }
    }

    private static string Seconds(long milliseconds) => (milliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private sealed record FirstRun(string What, long BootMs, long CopyDoneMs, long CmdDoneMs, long CmdMs, long ProgramDoneMs, long ProgramMs, string Preload);

    [GeneratedRegex(@"osx86-hello windows=C:\\windows user=osx86-test args=2 first program", RegexOptions.IgnoreCase)]
    private static partial Regex HelloLine();

    [GeneratedRegex(@"OSX86-PRELOAD done files=(\d+) start=([\d.]+) end=([\d.]+)")]
    private static partial Regex PreloadLine();
}
