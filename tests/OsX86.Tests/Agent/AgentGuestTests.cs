using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using OsX86.Agent;
using OsX86.Tests.Guest;
using Xunit.Abstractions;

namespace OsX86.Tests.Agent;

/// <summary>
/// The <c>guest</c> test of work item 5: the Alpine test image with the guest agent boots in the
/// engine worker of a real browser and the C# <see cref="AgentClient"/> drives it: the checks of
/// trial E7 that need neither X nor Wine (ping, launch as the prefix user, process list, kill by
/// launch that leaves the others alone, mounts, exec-in-prefix, open-url, user consoles on ptys
/// with Ctrl+C and exit code, closing a console). Skipped with a message when the image is not
/// built with the agent (<c>python -I images/agent/build-agent.py</c> then
/// <c>python -I images/alpine-test/build-alpine-test.py</c>) or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
public sealed class AgentGuestTests(ITestOutputHelper output)
{
    private const int BootTimeoutMs = 180_000;
    private const int TestUserId = 1001;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(30);

    [SkippableFact]
    public async Task The_agent_of_the_alpine_image_is_driven_from_the_csharp_client()
    {
        string root = Images.PythonTools.RepositoryRoot;
        string imageDirectory = Path.Combine(root, "images", "build", "alpine-test");
        string manifestPath = Path.Combine(imageDirectory, "manifest.json");
        Skip.IfNot(File.Exists(manifestPath), $"Alpine test image not built ({manifestPath} missing): run `python -I images/agent/build-agent.py` then `python -I images/alpine-test/build-alpine-test.py`.");
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        bool hasAgent = manifest.RootElement.TryGetProperty("agent", out JsonElement agentEntry) && agentEntry.ValueKind == JsonValueKind.Object;
        Skip.IfNot(hasAgent, "The Alpine test image was built without the agent: run `python -I images/agent/build-agent.py` then `python -I images/alpine-test/build-alpine-test.py`.");

        await using var server = new StaticFileServer(
        [
            ("/engine/", Path.Combine(root, "src", "OsX86", "wwwroot", "engine")),
            ("/v86/", Path.Combine(root, "src", "OsX86", "wwwroot", "v86")),
            ("/image/", imageDirectory),
            ("/page/", Path.Combine(root, "tests", "OsX86.Tests", "Guest")),
        ]);
        server.Start();

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await GuestBrowser.LaunchAsync(playwright);
        await using IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        page.Console += (_, message) => output.WriteLine($"[browser {message.Type}] {message.Text}");
        page.PageError += (_, error) => output.WriteLine($"[browser error] {error}");
        await page.GotoAsync($"{server.BaseUrl}page/guest.html?host=/engine/v86-host.js&image=/image/");
        await page.WaitForFunctionAsync("() => typeof window.guest === 'object'");
        GuestAgentBridge bridge = await GuestAgentBridge.AttachAsync(page);

        try
        {
            await page.EvaluateAsync("async () => { await window.guest.boot({ bootTimeoutMs: " + BootTimeoutMs + " }); }");
            await using var client = new AgentClient(bridge.Channel);
            var events = new EventLog(client);

            AgentStatus status = await client.ConnectAsync(None);
            Assert.Equal(1, status.ProtocolVersion);
            Assert.True(status.HasCgroups, "the guest has no cgroup v2: launches would only be process groups");
            await MeasurePingAsync(client);

            await LaunchListAndKillAsync(client, events);
            await MountsExecAndOpenUrlAsync(client, events);
            await ConsolesAsync(client, events);
            await ErrorsAsync(client);

            Assert.DoesNotContain(events.Snapshot(), e => e is AgentFaultEvent);
            AgentStatus after = await client.PingAsync(None);
            Assert.Equal(0, after.Consoles);
            Assert.Equal("stopped", await page.EvaluateAsync<string>("() => window.guest.stop()"));
        }
        catch (Exception)
        {
            output.WriteLine("Serial transcript:");
            output.WriteLine(await page.EvaluateAsync<string>("() => window.guest.transcript()"));
            output.WriteLine("Agent log:");
            output.WriteLine(await TryReadAgentLogAsync(page));
            throw;
        }
        finally
        {
            bridge.Complete();
        }
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
        output.WriteLine($"ping round trip through the test bridge: median {times[5]:0.0} ms, min {times[0]:0.0}, max {times[^1]:0.0}");
    }

    /// <summary>The criterion of E7: A is stopped by its launch identifier, B and the VM are not touched.</summary>
    private async Task LaunchListAndKillAsync(AgentClient client, EventLog events)
    {
        // Binaries of the image, with arguments: never a shell line.
        LaunchResult a = await client.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "sleep", Arguments = ["300"] }, None);
        LaunchResult b = await client.LaunchAsync(new LaunchOptions { Prefix = "other", Program = "/bin/busybox", Arguments = ["sleep", "301"] }, None);
        // A launch that forks: busybox timeout keeps a child; the whole cgroup must go.
        LaunchResult tree = await client.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "timeout", Arguments = ["500", "sleep", "400"] }, None);
        Assert.Equal("test", a.Id.Prefix);
        Assert.Equal(TestUserId, a.UserId);
        Assert.NotEqual(a.Id, tree.Id);

        IReadOnlyList<ProcessInfo> processes = await client.ListProcessesAsync(None);
        ProcessInfo listedA = Assert.Single(processes, p => p.Launch == a.Id);
        Assert.Equal(a.ProcessId, listedA.ProcessId);
        Assert.Equal(TestUserId, listedA.UserId);
        Assert.Equal("test", listedA.Prefix);
        Assert.Equal("sleep 300", listedA.CommandLine);
        Assert.Equal($"/osx86/{a.Id}", listedA.Cgroup);
        Assert.Equal("other", Assert.Single(processes, p => p.Launch == b.Id).Prefix);
        Assert.Equal(2, processes.Count(p => p.Launch == tree.Id));
        Assert.Contains(processes, p => p.ProcessId == 1 && p.Prefix is null && p.Launch is null);

        long start = Stopwatch.GetTimestamp();
        KillResult killed = await client.KillAsync(a.Id);
        output.WriteLine($"kill by launch: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0.0} ms round trip, {killed.Duration.TotalMilliseconds:0.0} ms in the guest, forced={killed.Forced}");
        Assert.False(killed.Forced);
        ProcessExit exitA = await a.Exited.WaitAsync(Soon);
        Assert.Equal(15, exitA.Signal);
        await events.WaitAsync<LaunchExitedEvent>(e => e.Launch == a.Id, Soon);

        // No grace: cgroup.kill at once, for the parent and its child.
        KillResult forced = await client.KillAsync(tree.Id, TimeSpan.Zero);
        Assert.True(forced.Forced);
        Assert.Equal(9, (await tree.Exited.WaitAsync(Soon)).Signal);

        processes = await client.ListProcessesAsync(None);
        Assert.DoesNotContain(processes, p => p.Launch == a.Id || p.Launch == tree.Id || p.ProcessId == a.ProcessId);
        Assert.Single(processes, p => p.Launch == b.Id);
        AgentException gone = await Assert.ThrowsAsync<AgentException>(() => client.KillAsync(a.Id));
        Assert.Equal(AgentErrorCodes.UnknownLaunch, gone.Code);

        // A launch that ends by itself reports its exit code.
        LaunchResult quick = await client.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "false" }, None);
        Assert.Equal(new ProcessExit(1, 0), await quick.Exited.WaitAsync(Soon));

        await client.KillAsync(b.Id);
        Assert.True((await b.Exited.WaitAsync(Soon)).Signaled);
    }

    private static async Task MountsExecAndOpenUrlAsync(AgentClient client, EventLog events)
    {
        IReadOnlyList<MountInfo> mounts = await client.MountsAsync();
        MountInfo tmp = Assert.Single(mounts, m => m.Path == "/tmp");
        Assert.Equal("tmpfs", tmp.FileSystem);
        Assert.Equal(MountState.Ok, tmp.State);
        Assert.True(tmp.TotalBytes > 0);
        Assert.DoesNotContain(mounts, m => m.Path is "/proc" or "/sys" or "/sys/fs/cgroup");

        ExecResult id = await client.ExecInPrefixAsync(new ExecOptions { Prefix = "test", Program = "id", Arguments = ["-u"] }, None);
        Assert.Equal(new ProcessExit(0, 0), id.Exit);
        Assert.Equal($"{TestUserId}\n", id.Output);

        // A user of a prefix reaches neither the agent's port nor the other prefix (R134.5).
        ExecResult port = await client.ExecInPrefixAsync(new ExecOptions { Prefix = "test", Program = "cat", Arguments = ["/dev/hvc0"] }, None);
        Assert.NotEqual(0, port.Exit.Code);
        Assert.Contains("Permission denied", port.Output, StringComparison.Ordinal);
        ExecResult otherHome = await client.ExecInPrefixAsync(new ExecOptions { Prefix = "test", Program = "ls", Arguments = ["/home/osx86-other"] }, None);
        Assert.Contains("Permission denied", otherHome.Output, StringComparison.Ordinal);

        ExecResult slow = await client.ExecInPrefixAsync(new ExecOptions { Prefix = "test", Program = "sleep", Arguments = ["60"], Timeout = TimeSpan.FromMilliseconds(500) }, None);
        Assert.True(slow.TimedOut);
        Assert.Equal(9, slow.Exit.Signal);

        ExecResult url = await client.ExecInPrefixAsync(new ExecOptions { Prefix = "other", Program = "osx86-open-url", Arguments = ["https://example.test/doc?x=1"] }, None);
        Assert.Equal(0, url.Exit.Code);
        OpenUrlEvent opened = await events.WaitAsync<OpenUrlEvent>(_ => true, Soon);
        Assert.Equal("other", opened.Prefix);
        Assert.Equal("https://example.test/doc?x=1", opened.Url);
    }

    /// <summary>Two consoles at once on the multiplexed port: a shell answers on each pty, Ctrl+C is passed on, the exit code comes back, closing kills what is left.</summary>
    private async Task ConsolesAsync(AgentClient client, EventLog events)
    {
        var environment = new Dictionary<string, string> { ["PS1"] = "console$ " };
        long start = Stopwatch.GetTimestamp();
        ConsoleHandle first = await client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test", Environment = environment }, None);
        var firstOutput = new ConsoleTranscript(first);
        await firstOutput.WaitForAsync("console$ ", Soon);
        output.WriteLine($"open-console to the first prompt: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0.0} ms");
        ConsoleHandle second = await client.OpenConsoleAsync(new ConsoleOptions { Prefix = "other", Environment = environment, Columns = 100, Rows = 30 }, None);
        var secondOutput = new ConsoleTranscript(second);
        await secondOutput.WaitForAsync("console$ ", Soon);
        Assert.NotEqual(first.Number, second.Number);
        Assert.Equal(2, (await client.PingAsync(None)).Consoles);

        // Each shell answers on its own pty, as its own user, with the size it was opened with.
        await first.WriteAsync("echo one-$((6*7))-$(id -u)\n", None);
        await second.WriteAsync("echo two-$((6*8))-$(id -u); stty size\n", None);
        await firstOutput.WaitForAsync("one-42-1001", Soon);
        await secondOutput.WaitForAsync("two-48-1002", Soon);
        await secondOutput.WaitForAsync("30 100", Soon);
        Assert.DoesNotContain("two-48", firstOutput.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("one-42", secondOutput.Text, StringComparison.Ordinal);
        await second.ResizeAsync(90, 20, None);
        await second.WriteAsync("stty size\n", None);
        await secondOutput.WaitForAsync("20 90", Soon);

        // Ctrl+C: the byte 0x03 becomes SIGINT for the foreground program, the shell comes back.
        await first.WriteAsync("sleep 600\n", None);
        await Task.Delay(1000);
        start = Stopwatch.GetTimestamp();
        await first.SendInterruptAsync(None);
        await firstOutput.WaitForAsync("^C", Soon);
        await firstOutput.WaitForAsync("console$ ", Soon);
        output.WriteLine($"Ctrl+C to the next prompt: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0.0} ms");
        await first.WriteAsync("echo status-$?\n", None);
        await firstOutput.WaitForAsync("status-130", Soon);

        // The exit code comes on the console's own channel, after its last bytes.
        await first.WriteAsync("echo bye; exit 7\n", None);
        Assert.Equal(new ProcessExit(7, 0), await first.Exited.WaitAsync(Soon));
        await firstOutput.Completion.WaitAsync(Soon);
        Assert.Contains("bye", firstOutput.Text, StringComparison.Ordinal);
        ConsoleClosedEvent closed = await events.WaitAsync<ConsoleClosedEvent>(e => e.Console == first.Number, Soon);
        Assert.Equal(first.Launch, closed.Launch);
        await Assert.ThrowsAsync<AgentException>(async () => await first.WriteAsync("x", None));

        // Closing (208 A): a job left in the background dies with the console, the rest of the VM stays.
        LaunchResult bystander = await client.LaunchAsync(new LaunchOptions { Prefix = "other", Program = "sleep", Arguments = ["302"] }, None);
        await second.WriteAsync("sleep 700 & echo job-$((2+2))\n", None);
        await secondOutput.WaitForAsync("job-4", Soon);
        Assert.Contains(await client.ListProcessesAsync(None), p => p.Launch == second.Launch && p.CommandLine == "sleep 700");
        ConsoleCloseResult closing = await second.CloseAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, closing.KeptProcesses);
        Assert.True((await second.Exited.WaitAsync(Soon)).Signaled);
        await WaitUntilAsync(async () => !(await client.ListProcessesAsync(None)).Any(p => p.Launch == second.Launch), Soon);
        Assert.Single(await client.ListProcessesAsync(None), p => p.Launch == bystander.Id);
        await client.KillAsync(bystander.Id);
    }

    private static async Task ErrorsAsync(AgentClient client)
    {
        Assert.Equal(AgentErrorCodes.NoXServer, (await Assert.ThrowsAsync<AgentException>(() => client.ListWindowsAsync(None))).Code);
        Assert.Equal(AgentErrorCodes.UnknownPrefix,
            (await Assert.ThrowsAsync<AgentException>(() => client.LaunchAsync(new LaunchOptions { Prefix = "nobody", Program = "sleep" }, None))).Code);
        Assert.Equal(AgentErrorCodes.ProgramNotFound,
            (await Assert.ThrowsAsync<AgentException>(() => client.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "/no/such/program" }, None))).Code);
        Assert.Equal(AgentErrorCodes.RootProcess, (await Assert.ThrowsAsync<AgentException>(() => client.SignalProcessAsync(2, 0))).Code);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        while (!await condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(start) < timeout, "the condition did not become true in time");
            await Task.Delay(200);
        }
    }

    private static async Task<string> TryReadAgentLogAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string>("() => window.guest.sendAndWait('cat /run/osx86/agent.log; echo LOG-\"\"END', 'LOG-END\\r\\n', 10000).then(t => t.slice(-4000))");
        }
        catch (PlaywrightException exception)
        {
            return $"(not available: {exception.Message.Split('\n')[0]})";
        }
    }

    /// <summary>Collects the events of a client from its creation on, and waits for one.</summary>
    private sealed class EventLog
    {
        private readonly List<AgentEvent> _events = [];
        private readonly Lock _lock = new();

        public EventLog(AgentClient client)
        {
            IAsyncEnumerable<AgentEvent> stream = client.ReadEventsAsync(CancellationToken.None);
            _ = Task.Run(async () =>
            {
                await foreach (AgentEvent agentEvent in stream)
                {
                    lock (_lock)
                    {
                        _events.Add(agentEvent);
                    }
                }
            });
        }

        public IReadOnlyList<AgentEvent> Snapshot()
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }

        public async Task<T> WaitAsync<T>(Func<T, bool> predicate, TimeSpan timeout)
            where T : AgentEvent
        {
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                if (Snapshot().OfType<T>().FirstOrDefault(predicate) is { } found)
                {
                    return found;
                }

                Assert.True(Stopwatch.GetElapsedTime(start) < timeout, $"no {typeof(T).Name} in time; events: {string.Join(", ", Snapshot())}");
                await Task.Delay(50);
            }
        }
    }

    /// <summary>Accumulates the output of a console as text and waits for a piece of it.</summary>
    private sealed class ConsoleTranscript
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _lock = new();
        private int _consumed;

        public ConsoleTranscript(ConsoleHandle console)
        {
            IAsyncEnumerable<ReadOnlyMemory<byte>> stream = console.ReadOutputAsync(CancellationToken.None);
            Completion = Task.Run(async () =>
            {
                Decoder decoder = Encoding.UTF8.GetDecoder();
                await foreach (ReadOnlyMemory<byte> chunk in stream)
                {
                    char[] chars = new char[decoder.GetCharCount(chunk.Span, flush: false)];
                    int count = decoder.GetChars(chunk.Span, chars, flush: false);
                    lock (_lock)
                    {
                        _text.Append(chars, 0, count);
                    }
                }
            });
        }

        public Task Completion { get; }

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

        /// <summary>Waits for <paramref name="needle"/> after what the previous waits consumed (a prompt appears many times).</summary>
        public async Task WaitForAsync(string needle, TimeSpan timeout)
        {
            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                string text = Text;
                int found = text.IndexOf(needle, Math.Min(_consumed, text.Length), StringComparison.Ordinal);
                if (found >= 0)
                {
                    _consumed = found + needle.Length;
                    return;
                }

                Assert.True(Stopwatch.GetElapsedTime(start) < timeout, $"'{needle}' did not appear on the console; output so far: {text}");
                await Task.Delay(50);
            }
        }
    }
}
