using System.Text;
using System.Text.Json;
using OsX86.Agent;

namespace OsX86.Tests.Agent;

/// <summary>The consoles of <see cref="AgentClient"/> against a simulated agent: opening, the bytes both ways on the multiplexed port, Ctrl+C, the exit code, resizing, closing.</summary>
[Trait("Category", "unit")]
public sealed class AgentConsoleTests
{
    private const string Opened = """{"ok":true,"launch":"test/5","console":5,"pid":460,"uid":1001,"cols":80,"rows":25}""";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);
    private const string Sentinel = """{"ok":true,"launch":"test/4","console":4,"pid":450,"uid":1001,"cols":80,"rows":25}""";
    private static readonly AgentClientOptions Fast = new()
    {
        RequestTimeout = Soon,
        ConnectProbeInterval = TimeSpan.FromMilliseconds(40),
        ConsoleCloseFallback = TimeSpan.FromMilliseconds(50),
    };

    private static byte[] Closed(int console, int code, int signal) =>
    [
        0xAE, 0x02, (byte)(console >> 24), (byte)(console >> 16), (byte)(console >> 8), (byte)console, 0x00, 0x08,
        (byte)(code >> 24), (byte)(code >> 16), (byte)(code >> 8), (byte)code,
        (byte)(signal >> 24), (byte)(signal >> 16), (byte)(signal >> 8), (byte)signal,
    ];

    private static async Task<(AgentClient Client, ConsoleHandle Console)> OpenAsync(SimulatedAgentChannel channel)
    {
        channel.On("open-console", _ => Opened);
        var client = new AgentClient(channel, Fast);
        await client.ConnectAsync(None);
        ConsoleHandle console = await client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, None);
        return (client, console);
    }

    /// <summary>
    /// A client with console 4 open and the opening of console 5 left unanswered. Closing console 4
    /// from the test is how it knows that the reader of the console port has taken everything sent
    /// before: the frames of console 5 that came first are then really waiting for its reply.
    /// </summary>
    private static async Task<(AgentClient Client, ConsoleHandle Sentinel, Task<ConsoleHandle> Opening, long Id)> OpenBehindAsync(SimulatedAgentChannel channel, ConsoleOptions? options = null)
    {
        channel.On("open-console", _ => Sentinel);
        var client = new AgentClient(channel, Fast);
        await client.ConnectAsync(None);
        ConsoleHandle sentinel = await client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, None);
        channel.Ignore("open-console");
        Task<ConsoleHandle> opening = client.OpenConsoleAsync(options ?? new ConsoleOptions { Prefix = "test" }, None);
        long id = channel.Requests("open-console")[1].GetProperty("id").GetInt64();
        return (client, sentinel, opening, id);
    }

    private static async Task DrainConsolePortAsync(SimulatedAgentChannel channel, ConsoleHandle sentinel)
    {
        channel.SendConsoleBytes(Closed(sentinel.Number, 0, 0));
        await sentinel.Exited.WaitAsync(Soon);
    }

    private static async Task<string> ReadTextAsync(ConsoleHandle console, int bytes)
    {
        var text = new StringBuilder();
        using var timeout = new CancellationTokenSource(Soon);
        await foreach (ReadOnlyMemory<byte> chunk in console.ReadOutputAsync(timeout.Token))
        {
            text.Append(Encoding.UTF8.GetString(chunk.Span));
            if (text.Length >= bytes)
            {
                break;
            }
        }

        return text.ToString();
    }

    [Fact]
    public async Task Open_console_asks_for_a_terminal_and_returns_its_handle()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("open-console", _ => """{"ok":true,"launch":"test/5","console":5,"pid":460,"uid":1001,"cols":100,"rows":30}""");
        await using var client = new AgentClient(channel, Fast);
        await client.ConnectAsync(None);

        ConsoleHandle console = await client.OpenConsoleAsync(new ConsoleOptions
        {
            Prefix = "test",
            Program = "wine",
            Arguments = ["cmd"],
            Environment = new Dictionary<string, string> { ["TERM"] = "xterm" },
            WorkingDirectory = "/tmp",
            Columns = 100,
            Rows = 30,
        }, None);

        Assert.Equal(new LaunchId("test", 5), console.Launch);
        Assert.Equal(5, console.Number);
        Assert.Equal(460, console.ProcessId);
        Assert.Equal((100, 30), (console.Columns, console.Rows));
        Assert.False(console.IsClosed);
        JsonElement request = channel.Request("open-console");
        Assert.Equal("wine", request.GetProperty("program").GetString());
        Assert.Equal("cmd", request.GetProperty("args")[0].GetString());
        Assert.Equal(100, request.GetProperty("cols").GetInt32());
        Assert.Equal(30, request.GetProperty("rows").GetInt32());
        Assert.Equal("/tmp", request.GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task Without_a_program_the_arguments_are_not_sent()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, _) = await OpenAsync(channel);
        await using (client)
        {
            JsonElement request = channel.Request("open-console");
            Assert.False(request.TryGetProperty("program", out _));
            Assert.False(request.TryGetProperty("args", out _));
            Assert.Equal(80, request.GetProperty("cols").GetInt32());
        }
    }

    [Fact]
    public async Task The_output_of_a_console_comes_out_of_its_handle_in_order_until_the_closed_frame()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle console) = await OpenAsync(channel);
        await using (client)
        {
            byte[] stream = [.. ConsoleFrame.EncodeData(5, "console$ "u8), .. ConsoleFrame.EncodeData(6, "someone else"u8), .. ConsoleFrame.EncodeData(5, "42\r\n"u8), .. Closed(5, 7, 0)];
            // Cut anywhere: the reader of the port reassembles the frames.
            channel.SendConsoleBytes(stream.AsMemory(0, 11));
            channel.SendConsoleBytes(stream.AsMemory(11));

            Assert.Equal(new ProcessExit(7, 0), await console.Exited.WaitAsync(Soon));
            var text = new StringBuilder();
            await foreach (ReadOnlyMemory<byte> chunk in console.ReadOutputAsync(None))
            {
                text.Append(Encoding.UTF8.GetString(chunk.Span));
            }

            Assert.Equal("console$ 42\r\n", text.ToString());
            Assert.True(console.IsClosed);
        }
    }

    [Fact]
    public async Task Frames_that_arrive_before_the_reply_are_kept_for_the_console()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle sentinel, Task<ConsoleHandle> opening, long id) = await OpenBehindAsync(channel);
        await using (client)
        {
            // The prompt is on the console port before the reply is on the protocol port.
            channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "first "u8));
            channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "second"u8));
            await DrainConsolePortAsync(channel, sentinel);
            Assert.False(opening.IsCompleted);
            channel.Reply(id, Opened);
            ConsoleHandle console = await opening.WaitAsync(Soon);
            channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, " third"u8));

            Assert.Equal("first second third", await ReadTextAsync(console, 18));
        }
    }

    [Fact]
    public async Task A_console_that_ended_before_its_reply_is_already_over_when_it_is_returned()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle sentinel, Task<ConsoleHandle> opening, long id) =
            await OpenBehindAsync(channel, new ConsoleOptions { Prefix = "test", Program = "true" });
        await using (client)
        {
            channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "done"u8));
            channel.SendConsoleBytes(Closed(5, 0, 0));
            await DrainConsolePortAsync(channel, sentinel);
            channel.Reply(id, Opened);

            ConsoleHandle console = await opening.WaitAsync(Soon);
            Assert.Equal(new ProcessExit(0, 0), await console.Exited.WaitAsync(Soon));
            Assert.Equal("done", await ReadTextAsync(console, 4));
        }
    }

    [Fact]
    public async Task Frames_of_consoles_nobody_opened_are_bounded_but_the_end_of_a_console_is_never_lost()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle sentinel, Task<ConsoleHandle> opening, long id) = await OpenBehindAsync(channel);
        await using (client)
        {
            // Console 5 writes more than the client keeps for a console it does not know yet
            // (256 KiB), then ends: the bytes beyond the bound go, the end stays.
            byte[] big = new byte[4096];
            Array.Fill(big, (byte)'x');
            channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "head"u8));
            for (int index = 0; index < 80; index++)
            {
                channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, big));
            }

            channel.SendConsoleBytes(Closed(5, 9, 0));
            // A frame type this client does not know is skipped.
            channel.SendConsoleBytes(new byte[] { 0xAE, 0x63, 0x00, 0x00, 0x00, 0x8B, 0x00, 0x01, 0x00 });
            await DrainConsolePortAsync(channel, sentinel);
            channel.Reply(id, Opened);

            ConsoleHandle mine = await opening.WaitAsync(Soon);
            Assert.Equal(new ProcessExit(9, 0), await mine.Exited.WaitAsync(Soon));
            long received = 0;
            bool first = true;
            await foreach (ReadOnlyMemory<byte> chunk in mine.ReadOutputAsync(None))
            {
                if (first)
                {
                    Assert.Equal("head", Encoding.ASCII.GetString(chunk.Span));
                    first = false;
                }

                received += chunk.Length;
            }

            Assert.InRange(received, 4, 256 * 1024);
        }
    }

    [Fact]
    public async Task Late_frames_of_a_console_nobody_holds_are_dropped()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("open-console", _ => Sentinel);
        await using var client = new AgentClient(channel, Fast);
        await client.ConnectAsync(None);
        ConsoleHandle sentinel = await client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, None);

        // No console is being opened: these bytes belong to a console that is over.
        channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "stale"u8));
        await DrainConsolePortAsync(channel, sentinel);
        channel.On("open-console", _ => Opened);
        ConsoleHandle console = await client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, None);
        channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "fresh"u8));

        Assert.Equal("fresh", await ReadTextAsync(console, 5));
    }

    [Fact]
    public async Task A_console_number_the_agent_uses_twice_replaces_the_old_console()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle old) = await OpenAsync(channel);
        await using (client)
        {
            // An agent that restarted counts again from one: same number, another console.
            ConsoleHandle renewed = await client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, None);

            await Assert.ThrowsAsync<AgentClosedException>(() => old.Exited.WaitAsync(Soon));
            channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "new"u8));
            Assert.Equal("new", await ReadTextAsync(renewed, 3));
            Assert.Equal(1, (await client.PingAsync(None)).ProtocolVersion);
        }
    }

    [Fact]
    public async Task A_console_opened_for_a_caller_that_gave_up_is_closed_again()
    {
        var channel = new SimulatedAgentChannel();
        channel.Ignore("open-console");
        channel.On("close-console", _ => """{"ok":true,"console":5,"keptCount":0,"kept":null}""");
        await using var client = new AgentClient(channel, Fast);
        await client.ConnectAsync(None);
        using var cancel = new CancellationTokenSource();
        Task<ConsoleHandle> opening = client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, cancel.Token);
        long id = channel.Request("open-console").GetProperty("id").GetInt64();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);

        // The agent had opened it all the same: nobody holds the handle, so it is closed at once.
        channel.Reply(id, Opened);
        await client.PingAsync(None);

        JsonElement close = await WaitForRequestAsync(channel, "close-console");
        Assert.Equal(5, close.GetProperty("console").GetInt32());
        Assert.Equal(0, close.GetProperty("graceMs").GetInt32());
    }

    [Fact]
    public async Task A_restart_of_the_agent_ends_the_consoles_it_no_longer_knows()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle console) = await OpenAsync(channel);
        await using (client)
        {
            channel.SendLine("""{"event":"ready","version":1,"pid":300,"cgroups":true,"port":"/dev/hvc0","consolePort":"/dev/hvc1"}""");

            await Assert.ThrowsAsync<AgentClosedException>(() => console.Exited.WaitAsync(Soon));
            Assert.Equal(1, (await client.PingAsync(None)).ProtocolVersion);
        }
    }

    private static async Task<JsonElement> WaitForRequestAsync(SimulatedAgentChannel channel, string operation)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (channel.Requests(operation).Count == 0)
        {
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(start) < Soon, $"no {operation} request was sent");
            await Task.Delay(20);
        }

        return channel.Requests(operation)[0];
    }

    [Fact]
    public async Task What_is_written_reaches_the_guest_as_data_frames_and_ctrl_c_is_the_byte_3()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle console) = await OpenAsync(channel);
        await using (client)
        {
            await console.WriteAsync("ls é\n", None);
            await console.SendInterruptAsync(None);
            await console.WriteAsync(ReadOnlyMemory<byte>.Empty, None);
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await console.WriteAsync((string)null!, None));

            Assert.Equal(2, channel.ConsoleWritten.Count);
            ConsoleFrame typed = Assert.Single(new ConsoleFrameReader().Push(channel.ConsoleWritten[0]));
            Assert.Equal((ConsoleFrameType.Data, 5, "ls é\n"), (typed.Type, typed.Console, Encoding.UTF8.GetString(typed.Payload.Span)));
            Assert.Equal([0xAE, 0x01, 0x00, 0x00, 0x00, 0x05, 0x00, 0x01, 0x03], channel.ConsoleWritten[1]);
        }
    }

    [Fact]
    public async Task Resize_and_close_are_requests_on_the_protocol_port()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("console-resize", _ => """{"ok":true,"console":5,"cols":132,"rows":43}""");
        channel.On("close-console", _ => """{"ok":true,"console":5,"keptCount":2,"kept":"test/6"}""");
        (AgentClient client, ConsoleHandle console) = await OpenAsync(channel);
        await using (client)
        {
            await console.ResizeAsync(132, 43, None);
            Assert.Equal((132, 43), (console.Columns, console.Rows));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => console.ResizeAsync(0, 43, None));

            ConsoleCloseResult closing = await console.CloseAsync(TimeSpan.FromMilliseconds(750), [461, 462]);
            Assert.Equal(new ConsoleCloseResult(2, new LaunchId("test", 6)), closing);

            JsonElement resize = channel.Request("console-resize");
            Assert.Equal((5, 132, 43), (resize.GetProperty("console").GetInt32(), resize.GetProperty("cols").GetInt32(), resize.GetProperty("rows").GetInt32()));
            JsonElement close = channel.Request("close-console");
            Assert.Equal(5, close.GetProperty("console").GetInt32());
            Assert.Equal(750, close.GetProperty("graceMs").GetInt32());
            Assert.Equal([461, 462], close.GetProperty("keep").EnumerateArray().Select(p => p.GetInt32()));
        }
    }

    [Fact]
    public async Task Close_without_options_sends_only_the_console()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("close-console", _ => """{"ok":true,"console":5,"keptCount":0,"kept":null}""");
        (AgentClient client, ConsoleHandle console) = await OpenAsync(channel);
        await using (client)
        {
            Assert.Equal(new ConsoleCloseResult(0, null), await console.CloseAsync());
            JsonElement close = channel.Request("close-console");
            Assert.False(close.TryGetProperty("graceMs", out _));
            Assert.False(close.TryGetProperty("keep", out _));
        }
    }

    [Fact]
    public async Task A_closed_console_refuses_everything()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle console) = await OpenAsync(channel);
        await using (client)
        {
            channel.SendConsoleBytes(Closed(5, -1, 1));
            Assert.Equal(new ProcessExit(-1, 1), await console.Exited.WaitAsync(Soon));

            Assert.Equal(AgentErrorCodes.UnknownConsole, (await Assert.ThrowsAsync<AgentException>(async () => await console.WriteAsync("x", None))).Code);
            await Assert.ThrowsAsync<AgentException>(() => console.ResizeAsync(80, 25, None));
            await Assert.ThrowsAsync<AgentException>(() => console.CloseAsync());
            // Late bytes for a console that is over are dropped.
            channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "late"u8));
            await client.PingAsync(None);
        }
    }

    [Fact]
    public async Task The_console_closed_event_completes_a_console_whose_last_frame_never_came()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle console) = await OpenAsync(channel);
        await using (client)
        {
            channel.SendLine("""{"event":"console-closed","launch":"test/5","console":5,"pid":460,"code":3,"signal":0,"remaining":0}""");

            Assert.Equal(new ProcessExit(3, 0), await console.Exited.WaitAsync(Soon));
        }
    }

    [Fact]
    public async Task A_closed_frame_too_short_to_carry_a_code_still_ends_the_console()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle console) = await OpenAsync(channel);
        await using (client)
        {
            channel.SendConsoleBytes(new byte[] { 0xAE, 0x02, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00 });

            Assert.Equal(new ProcessExit(-1, 0), await console.Exited.WaitAsync(Soon));
        }
    }

    [Fact]
    public async Task Disposing_the_client_fails_its_consoles()
    {
        var channel = new SimulatedAgentChannel();
        (AgentClient client, ConsoleHandle console) = await OpenAsync(channel);

        await client.DisposeAsync();

        await Assert.ThrowsAsync<AgentClosedException>(() => console.Exited);
        Assert.True(console.IsClosed);
        await foreach (ReadOnlyMemory<byte> _ in console.ReadOutputAsync(None))
        {
            Assert.Fail("a closed console has no more output");
        }
    }

    [Fact]
    public async Task An_open_console_reply_without_launch_is_a_protocol_error()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("open-console", _ => """{"ok":true,"console":5}""");
        await using var client = new AgentClient(channel, Fast);
        await client.ConnectAsync(None);

        await Assert.ThrowsAsync<AgentProtocolException>(() => client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, None));
        channel.On("open-console", _ => """{"ok":true,"launch":"test/5","console":0}""");
        await Assert.ThrowsAsync<AgentProtocolException>(() => client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.OpenConsoleAsync(
            new ConsoleOptions { Prefix = "test", Environment = new Dictionary<string, string> { ["A"] = "b\0c" } }, None));
    }
}
