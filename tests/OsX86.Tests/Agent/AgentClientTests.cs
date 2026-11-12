using System.Text.Json;
using OsX86.Agent;
using OsX86.Engine;
using OsX86.Engine.V86;
using OsX86.Tests.Engine;

namespace OsX86.Tests.Agent;

/// <summary><see cref="AgentClient"/> against a simulated agent: connection, pairing by id, timeouts, events, operations, errors, closing.</summary>
[Trait("Category", "unit")]
public sealed class AgentClientTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);
    private static readonly AgentClientOptions Fast = new()
    {
        RequestTimeout = TimeSpan.FromMilliseconds(250),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectProbeInterval = TimeSpan.FromMilliseconds(40),
        LongOperationMargin = TimeSpan.FromMilliseconds(250),
        ConsoleCloseFallback = TimeSpan.FromMilliseconds(50),
    };

    private static async Task<AgentClient> ConnectedAsync(SimulatedAgentChannel channel, AgentClientOptions? options = null)
    {
        var client = new AgentClient(channel, options ?? Fast);
        await client.ConnectAsync(None);
        return client;
    }

    private static async Task<List<AgentEvent>> TakeAsync(IAsyncEnumerable<AgentEvent> events, int count)
    {
        var taken = new List<AgentEvent>();
        using var timeout = new CancellationTokenSource(Soon);
        await foreach (AgentEvent agentEvent in events.WithCancellation(timeout.Token))
        {
            taken.Add(agentEvent);
            if (taken.Count == count)
            {
                break;
            }
        }

        return taken;
    }

    [Fact]
    public async Task Connect_waits_for_the_agent_to_answer_a_ping_and_keeps_its_status()
    {
        var channel = new SimulatedAgentChannel();
        await using var client = new AgentClient(channel, Fast);
        Assert.False(client.IsConnected);
        Assert.Null(client.Status);

        AgentStatus status = await client.ConnectAsync(None);

        Assert.Equal(new AgentStatus(1, TimeSpan.FromSeconds(12), 180, 0, 0, true), status);
        Assert.Same(status, client.Status);
        Assert.True(client.IsConnected);
        Assert.Equal(1, channel.Request("ping").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Connect_asks_again_while_the_guest_is_still_booting()
    {
        var channel = new SimulatedAgentChannel { IgnoreFirstRequests = 3 };
        await using var client = new AgentClient(channel, Fast);

        await client.ConnectAsync(None);

        long[] ids = channel.Requests("ping").Select(r => r.GetProperty("id").GetInt64()).ToArray();
        Assert.Equal([1, 2, 3, 4], ids);
    }

    [Fact]
    public async Task Connect_fails_with_not_ready_when_no_agent_answers()
    {
        var channel = new SimulatedAgentChannel { Silent = true };
        await using var client = new AgentClient(channel, Fast with { ConnectTimeout = TimeSpan.FromMilliseconds(200) });

        AgentTimeoutException error = await Assert.ThrowsAsync<AgentTimeoutException>(() => client.ConnectAsync(None));

        Assert.Equal(AgentErrorCodes.NotReady, error.Code);
        Assert.Equal("connect", error.Operation);
        Assert.False(client.IsConnected);
        Assert.True(channel.Requests("ping").Count >= 2);
    }

    [Fact]
    public async Task Connect_refuses_another_version_of_the_protocol()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("ping", _ => """{"ok":true,"version":2}""");
        await using var client = new AgentClient(channel, Fast);

        AgentProtocolException error = await Assert.ThrowsAsync<AgentProtocolException>(() => client.ConnectAsync(None));

        Assert.Equal(AgentErrorCodes.UnsupportedVersion, error.Code);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Connect_is_called_once_and_before_anything_else()
    {
        var channel = new SimulatedAgentChannel();
        await using var client = new AgentClient(channel, Fast);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(None));
        await client.ConnectAsync(None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync(None));
        Assert.Throws<ArgumentNullException>(() => new AgentClient(null!));
    }

    [Fact]
    public async Task Replies_are_paired_by_id_whatever_their_order()
    {
        var channel = new SimulatedAgentChannel();
        channel.Ignore("list-processes");
        channel.Ignore("mounts");
        await using AgentClient client = await ConnectedAsync(channel);

        Task<IReadOnlyList<ProcessInfo>> processes = client.ListProcessesAsync(None);
        Task<IReadOnlyList<MountInfo>> mounts = client.MountsAsync();
        long processesId = channel.Request("list-processes").GetProperty("id").GetInt64();
        long mountsId = channel.Request("mounts").GetProperty("id").GetInt64();
        Assert.NotEqual(processesId, mountsId);

        // The second request is answered first.
        channel.Reply(mountsId, """{"ok":true,"mounts":[{"path":"/tmp","type":"tmpfs","device":"tmpfs","state":"ok","entries":1,"blocks":2,"blockSize":4096,"freeBlocks":1}]}""");
        channel.Reply(processesId, """{"ok":true,"processes":[{"pid":1,"comm":"init","cmd":"/init","state":"S","cgroup":"/"}]}""");

        Assert.Equal("/tmp", Assert.Single(await mounts.WaitAsync(Soon)).Path);
        Assert.Equal(1, Assert.Single(await processes.WaitAsync(Soon)).ProcessId);
    }

    [Fact]
    public async Task A_request_without_reply_times_out_and_a_late_reply_is_ignored()
    {
        var channel = new SimulatedAgentChannel();
        channel.Ignore("list-processes");
        await using AgentClient client = await ConnectedAsync(channel);
        IAsyncEnumerable<AgentEvent> events = client.ReadEventsAsync(None);

        AgentTimeoutException error = await Assert.ThrowsAsync<AgentTimeoutException>(() => client.ListProcessesAsync(None));

        Assert.Equal(AgentErrorCodes.Timeout, error.Code);
        Assert.Equal("list-processes", error.Operation);
        Assert.Equal(Fast.RequestTimeout, error.Timeout);
        channel.Reply(channel.Request("list-processes").GetProperty("id").GetInt64(), """{"ok":true,"processes":[]}""");
        // The client still works, and the late reply was not reported as a fault: the next event is the marker.
        Assert.Equal(1, (await client.PingAsync(None)).ProtocolVersion);
        channel.SendLine("""{"event":"open-url","uid":1,"pid":2,"prefix":null,"url":"marker"}""");
        Assert.Equal("marker", Assert.IsType<OpenUrlEvent>(Assert.Single(await TakeAsync(events, 1))).Url);
    }

    [Fact]
    public async Task An_error_reply_becomes_an_agent_exception_with_its_code()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("launch", _ => """{"ok":false,"error":"unknown-prefix","message":"no guest user osx86-nobody"}""");
        await using AgentClient client = await ConnectedAsync(channel);

        AgentException error = await Assert.ThrowsAsync<AgentException>(() => client.LaunchAsync(new LaunchOptions { Prefix = "nobody", Program = "sleep" }, None));

        Assert.Equal(AgentErrorCodes.UnknownPrefix, error.Code);
        Assert.Equal("no guest user osx86-nobody", error.Message);
    }

    [Fact]
    public async Task A_cancelled_request_is_abandoned()
    {
        var channel = new SimulatedAgentChannel();
        channel.Ignore("list-processes");
        await using AgentClient client = await ConnectedAsync(channel);
        using var cancel = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListProcessesAsync(cancel.Token));

        Assert.Equal(1, (await client.PingAsync(None)).ProtocolVersion);
    }

    [Fact]
    public async Task A_channel_that_cannot_write_fails_the_request()
    {
        var channel = new SimulatedAgentChannel();
        await using AgentClient client = await ConnectedAsync(channel);
        channel.WriteException = new EngineStateException(EngineOperation.Stop, EngineState.Stopped, "The engine is stopped.");

        await Assert.ThrowsAsync<EngineStateException>(() => client.PingAsync(None));

        channel.WriteException = null;
        Assert.Equal(1, (await client.PingAsync(None)).ProtocolVersion);
    }

    [Fact]
    public async Task Launch_sends_the_program_and_follows_its_exit()
    {
        var channel = new SimulatedAgentChannel();
        // The exit event arrives right behind the reply: the launch must already be registered.
        channel.On("launch", _ => """{"ok":true,"launch":"test/3","pid":451,"uid":1001,"ms":2.5,"cgroup":"/sys/fs/cgroup/osx86/test/3"}""",
            """{"event":"exit","launch":"test/3","pid":451,"code":4,"signal":0,"remaining":0}""");
        await using AgentClient client = await ConnectedAsync(channel);
        IAsyncEnumerable<AgentEvent> events = client.ReadEventsAsync(None);

        LaunchResult launched = await client.LaunchAsync(new LaunchOptions
        {
            Prefix = "test",
            Program = "wine",
            Arguments = ["notepad.exe"],
            Environment = new Dictionary<string, string> { ["WINEDEBUG"] = "-all" },
            WorkingDirectory = "/home/osx86-test",
            MaxProcesses = 64,
            Log = true,
        }, None);

        Assert.Equal(new LaunchId("test", 3), launched.Id);
        Assert.Equal(451, launched.ProcessId);
        Assert.Equal(1001, launched.UserId);
        Assert.Equal(new ProcessExit(4, 0), await launched.Exited.WaitAsync(Soon));
        Assert.Equal(new LaunchExitedEvent(new LaunchId("test", 3), new ProcessExit(4, 0), 0), Assert.Single(await TakeAsync(events, 1)));
        JsonElement request = channel.Request("launch");
        Assert.Equal("wine", request.GetProperty("program").GetString());
        Assert.Equal("notepad.exe", request.GetProperty("args")[0].GetString());
        Assert.Equal("-all", request.GetProperty("env").GetProperty("WINEDEBUG").GetString());
        Assert.Equal("/home/osx86-test", request.GetProperty("cwd").GetString());
        Assert.Equal(64, request.GetProperty("nproc").GetInt32());
        Assert.True(request.GetProperty("log").GetBoolean());
    }

    [Fact]
    public async Task A_launch_reply_without_a_launch_identifier_is_a_protocol_error()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("launch", _ => """{"ok":true,"pid":451}""");
        await using AgentClient client = await ConnectedAsync(channel);

        AgentProtocolException error = await Assert.ThrowsAsync<AgentProtocolException>(() => client.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "sleep" }, None));

        Assert.Equal(AgentErrorCodes.Protocol, error.Code);
        Assert.False(channel.Request("launch").TryGetProperty("args", out _));
    }

    [Fact]
    public async Task Kill_names_the_launch_and_reports_whether_it_was_forced()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("kill", _ => """{"ok":true,"forced":true,"ms":512.5}""");
        await using AgentClient client = await ConnectedAsync(channel);

        KillResult killed = await client.KillAsync(new LaunchId("test", 3), TimeSpan.FromMilliseconds(500), 2);
        await client.KillAsync(new LaunchId("test", 4));

        Assert.Equal(new KillResult(true, TimeSpan.FromMilliseconds(512.5)), killed);
        IReadOnlyList<JsonElement> requests = channel.Requests("kill");
        Assert.Equal("test/3", requests[0].GetProperty("launch").GetString());
        Assert.Equal(500, requests[0].GetProperty("graceMs").GetInt32());
        Assert.Equal(2, requests[0].GetProperty("signal").GetInt32());
        Assert.False(requests[0].TryGetProperty("scope", out _));
        Assert.False(requests[1].TryGetProperty("graceMs", out _));
        Assert.False(requests[1].TryGetProperty("signal", out _));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.KillAsync(new LaunchId("test", 3), TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task A_kill_waits_for_its_grace_period_plus_the_margin()
    {
        var channel = new SimulatedAgentChannel();
        channel.Ignore("kill");
        await using AgentClient client = await ConnectedAsync(channel);

        AgentTimeoutException error = await Assert.ThrowsAsync<AgentTimeoutException>(() => client.KillAsync(new LaunchId("test", 3), TimeSpan.FromMilliseconds(100)));

        Assert.Equal(TimeSpan.FromMilliseconds(350), error.Timeout);
    }

    [Fact]
    public async Task The_administrative_kills_send_their_scope()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("kill", request => $$"""{"ok":true,"signal":{{(request.TryGetProperty("signal", out JsonElement s) ? s.GetInt32() : 9)}},"count":2,"ms":1,"target":"{{request.GetProperty("scope").GetString()}}"}""");
        await using AgentClient client = await ConnectedAsync(channel);

        Assert.Equal(new SignalResult(2, 15, "pid"), await client.SignalProcessAsync(40));
        Assert.Equal(new SignalResult(2, 9, "group"), await client.SignalProcessGroupAsync(40, 9, allowRoot: true));
        Assert.Equal(new SignalResult(2, 1, "prefix"), await client.SignalPrefixAsync("test", 1));
        Assert.Equal(new SignalResult(2, 9, "cgroup"), await client.KillCgroupAsync("test", 3));
        await client.KillCgroupAsync("test");

        IReadOnlyList<JsonElement> requests = channel.Requests("kill");
        Assert.Equal(40, requests[0].GetProperty("pid").GetInt32());
        Assert.False(requests[0].TryGetProperty("allowRoot", out _));
        Assert.True(requests[1].GetProperty("allowRoot").GetBoolean());
        Assert.Equal("test", requests[2].GetProperty("prefix").GetString());
        Assert.Equal(3, requests[3].GetProperty("seq").GetInt32());
        Assert.False(requests[4].TryGetProperty("seq", out _));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SignalPrefixAsync("Bad Prefix"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.KillCgroupAsync(string.Empty));
    }

    [Fact]
    public async Task Exec_in_prefix_returns_the_output_and_the_exit_code()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("exec-in-prefix", _ => """{"ok":true,"code":0,"signal":0,"timedOut":false,"ms":99.5,"truncated":false,"output":"1001\n"}""");
        await using AgentClient client = await ConnectedAsync(channel);

        ExecResult result = await client.ExecInPrefixAsync(new ExecOptions
        {
            Prefix = "test",
            Program = "id",
            Arguments = ["-u"],
            Timeout = TimeSpan.FromSeconds(2),
            MaxOutputBytes = 1024,
        }, None);

        Assert.Equal("1001\n", result.Output);
        Assert.Equal(new ProcessExit(0, 0), result.Exit);
        JsonElement request = channel.Request("exec-in-prefix");
        Assert.Equal(2000, request.GetProperty("timeoutMs").GetInt32());
        Assert.Equal(1024, request.GetProperty("maxOutput").GetInt32());
        Assert.Equal("-u", request.GetProperty("args")[0].GetString());
    }

    [Fact]
    public async Task Mounts_and_windows_map_their_lists_and_the_missing_x_server_is_an_error()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("mounts", _ => """{"ok":true,"timedOut":false,"ms":3,"mounts":[{"path":"/home","type":"tmpfs","device":"tmpfs","state":"ok","entries":2,"blocks":10,"blockSize":4096,"freeBlocks":9}]}""");
        channel.On("list-windows", _ => """{"ok":false,"error":"no-x-server","message":"this image has no X server: windows cannot be listed"}""");
        await using AgentClient client = await ConnectedAsync(channel);

        Assert.Equal("/home", Assert.Single(await client.MountsAsync(TimeSpan.FromSeconds(1))).Path);
        Assert.Equal(1000, channel.Request("mounts").GetProperty("timeoutMs").GetInt32());
        Assert.Equal(AgentErrorCodes.NoXServer, (await Assert.ThrowsAsync<AgentException>(() => client.ListWindowsAsync(None))).Code);

        channel.On("list-windows", _ => """{"ok":true,"windows":[{"id":"0x1","pid":7,"launch":"test/1","title":"Notepad"}]}""");
        Assert.Equal(new WindowInfo("0x1", 7, new LaunchId("test", 1), "Notepad"), Assert.Single(await client.ListWindowsAsync(None)));
    }

    [Fact]
    public async Task What_cannot_reach_the_guest_intact_is_refused_before_anything_is_sent()
    {
        var channel = new SimulatedAgentChannel();
        await using AgentClient client = await ConnectedAsync(channel);
        int sent = channel.Sent.Count;

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.LaunchAsync(null!, None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.LaunchAsync(new LaunchOptions { Prefix = "Test", Program = "sleep" }, None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.LaunchAsync(new LaunchOptions { Prefix = "test", Program = string.Empty }, None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "sleep", Arguments = ["1\0; rm"] }, None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "sleep", Arguments = [null!] }, None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.LaunchAsync(
            new LaunchOptions { Prefix = "test", Program = "sleep", Environment = new Dictionary<string, string> { ["A=B"] = "c" } }, None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.LaunchAsync(
            new LaunchOptions { Prefix = "test", Program = "sleep", Environment = new Dictionary<string, string> { ["A"] = "c\0d" } }, None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExecInPrefixAsync(new ExecOptions { Prefix = "test", Program = "ls", WorkingDirectory = "/tmp\0" }, None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ExecInPrefixAsync(null!, None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ExecInPrefixAsync(new ExecOptions { Prefix = "test", Program = "ls", Timeout = TimeSpan.FromSeconds(-1) }, None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test", Columns = 0 }, None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test", Rows = 1001 }, None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenConsoleAsync(null!, None));

        Assert.Equal(sent, channel.Sent.Count);
    }

    [Fact]
    public async Task Every_event_reader_sees_the_events_from_its_call_on()
    {
        var channel = new SimulatedAgentChannel();
        // Written before anyone connected: the ready event of the agent's start.
        channel.SendLine("""{"event":"ready","version":1,"pid":97,"cgroups":true,"port":"/dev/hvc0","consolePort":"/dev/hvc1"}""");
        await using var client = new AgentClient(channel, Fast);
        IAsyncEnumerable<AgentEvent> first = client.ReadEventsAsync(None);
        await client.ConnectAsync(None);
        Assert.Equal(new AgentReadyEvent(1, 97, true), Assert.Single(await TakeAsync(first, 1)));

        IAsyncEnumerable<AgentEvent> second = client.ReadEventsAsync(None);
        IAsyncEnumerable<AgentEvent> third = client.ReadEventsAsync(None);
        channel.SendLine("""{"event":"open-url","uid":1001,"pid":60,"prefix":"test","url":"https://example.test/"}""");
        Assert.Equal(new OpenUrlEvent("test", 1001, 60, "https://example.test/"), Assert.Single(await TakeAsync(second, 1)));
        Assert.Equal(new OpenUrlEvent("test", 1001, 60, "https://example.test/"), Assert.Single(await TakeAsync(third, 1)));
    }

    [Fact]
    public async Task What_does_not_follow_the_protocol_is_reported_as_a_fault_and_does_not_stop_the_client()
    {
        var channel = new SimulatedAgentChannel();
        await using AgentClient client = await ConnectedAsync(channel);
        IAsyncEnumerable<AgentEvent> events = client.ReadEventsAsync(None);

        channel.SendLine("half a line of something");
        channel.SendLine(string.Empty);
        channel.SendLine("""{"id":null,"ok":false,"error":"invalid-json","message":"the request is not a JSON object"}""");
        channel.SendLine("""{"ok":false}""");
        channel.SendLine("""{"event":"exit","launch":"nonsense"}""");
        channel.SendLine("""{"event":"from-the-future"}""");

        List<AgentEvent> faults = await TakeAsync(events, 5);
        Assert.All(faults, f => Assert.IsType<AgentFaultEvent>(f));
        Assert.Equal([AgentErrorCodes.Protocol, AgentErrorCodes.InvalidJson, AgentErrorCodes.Protocol, AgentErrorCodes.Protocol, AgentErrorCodes.Protocol],
            faults.Cast<AgentFaultEvent>().Select(f => f.Code));
        Assert.Equal(1, (await client.PingAsync(None)).ProtocolVersion);
    }

    [Fact]
    public async Task Disposing_fails_what_is_pending_and_ends_the_readers()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("launch", _ => """{"ok":true,"launch":"test/1","pid":10,"uid":1001}""");
        channel.Ignore("list-processes");
        var client = await ConnectedAsync(channel, Fast with { RequestTimeout = Soon });
        LaunchResult launched = await client.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "sleep" }, None);
        IAsyncEnumerable<AgentEvent> events = client.ReadEventsAsync(None);
        Task<IReadOnlyList<ProcessInfo>> pending = client.ListProcessesAsync(None);

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal(AgentErrorCodes.Closed, (await Assert.ThrowsAsync<AgentClosedException>(() => pending)).Code);
        await Assert.ThrowsAsync<AgentClosedException>(() => launched.Exited);
        Assert.Empty(await TakeAsync(events, 1));
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<AgentClosedException>(() => client.PingAsync(None));
        await Assert.ThrowsAsync<AgentClosedException>(() => client.ConnectAsync(None));
        Assert.Empty(await TakeAsync(client.ReadEventsAsync(None), 1));
    }

    [Fact]
    public async Task The_end_of_the_channel_closes_the_client()
    {
        var channel = new SimulatedAgentChannel();
        channel.Ignore("list-processes");
        await using AgentClient client = await ConnectedAsync(channel, Fast with { RequestTimeout = Soon });
        Task<IReadOnlyList<ProcessInfo>> pending = client.ListProcessesAsync(None);

        channel.Complete();

        await Assert.ThrowsAsync<AgentClosedException>(() => pending);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task A_reader_that_stops_listening_is_forgotten()
    {
        var channel = new SimulatedAgentChannel();
        await using AgentClient client = await ConnectedAsync(channel);
        using var cancel = new CancellationTokenSource();
        IAsyncEnumerator<AgentEvent> reader = client.ReadEventsAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        ValueTask<bool> next = reader.MoveNextAsync();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await next);
        await reader.DisposeAsync();
        channel.SendLine("""{"event":"open-url","uid":1,"pid":2,"prefix":null,"url":"after"}""");
        Assert.Equal(1, (await client.PingAsync(None)).ProtocolVersion);
    }

    [Fact]
    public async Task The_engine_channel_uses_the_first_two_virtio_console_ports()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await engine.StartAsync(new EngineStartOptions { KernelPath = "osx86-test/vmlinuz-virt", InitrdPath = "osx86-test/initramfs-osx86" }, None);
        EngineAgentChannel channel = EngineAgentChannel.For(engine);
        IAsyncEnumerable<string> lines = channel.ReadLinesAsync(None);
        IAsyncEnumerable<ReadOnlyMemory<byte>> bytes = channel.ReadConsoleBytesAsync(None);

        // The simulated worker echoes what it receives on the same port.
        await channel.WriteLineAsync("""{"id":1,"op":"ping"}""", None);
        await channel.WriteConsoleBytesAsync(new byte[] { 0xAE, 1, 0, 1, 0, 0 }, None);

        string[] channels = worker.Posted.Select(json => JsonDocument.Parse(json).RootElement).Where(r => r.GetProperty("type").GetString() == "write")
            .Select(r => r.GetProperty("channel").GetString()!).ToArray();
        Assert.Equal(["virtio0", "virtio1"], channels);
        await foreach (string line in lines)
        {
            Assert.Equal("""{"id":1,"op":"ping"}""", line);
            break;
        }

        await foreach (ReadOnlyMemory<byte> chunk in bytes)
        {
            Assert.Equal(6, chunk.Length);
            break;
        }

        Assert.Equal(ConsoleId.Virtio(0), EngineAgentChannel.ProtocolPort);
        Assert.Equal(ConsoleId.Virtio(1), EngineAgentChannel.ConsolePort);
        Assert.Throws<ArgumentNullException>(() => EngineAgentChannel.For(null!));
        Assert.Throws<ArgumentNullException>(() => new EngineAgentChannel(null!, engine.GetConsole(ConsoleId.Virtio(1))));
        Assert.Throws<ArgumentNullException>(() => new EngineAgentChannel(engine.GetConsole(ConsoleId.Virtio(0)), null!));
    }
}
