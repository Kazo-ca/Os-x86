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
    private static readonly AgentClientOptions Fast = new()
    {
        RequestTimeout = TimeSpan.FromMilliseconds(250),
        ConnectProbeInterval = TimeSpan.FromMilliseconds(40),
        ConsoleCloseFallback = TimeSpan.FromMilliseconds(50),
    };

    private static byte[] Closed(int console, int code, int signal) =>
    [
        0xAE, 0x02, (byte)(console >> 8), (byte)console, 0x00, 0x08,
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
        channel.Ignore("open-console");
        await using var client = new AgentClient(channel, Fast with { RequestTimeout = Soon });
        await client.ConnectAsync(None);
        Task<ConsoleHandle> opening = client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, None);
        long id = channel.Request("open-console").GetProperty("id").GetInt64();

        // The prompt is on the console port before the reply is on the protocol port.
        channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "first "u8));
        channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "second"u8));
        await client.PingAsync(None);
        channel.Reply(id, Opened);
        ConsoleHandle console = await opening.WaitAsync(Soon);
        channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, " third"u8));

        Assert.Equal("first second third", await ReadTextAsync(console, 18));
    }

    [Fact]
    public async Task A_console_that_ended_before_its_reply_is_already_over_when_it_is_returned()
    {
        var channel = new SimulatedAgentChannel();
        channel.Ignore("open-console");
        await using var client = new AgentClient(channel, Fast with { RequestTimeout = Soon });
        await client.ConnectAsync(None);
        Task<ConsoleHandle> opening = client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test", Program = "true" }, None);
        long id = channel.Request("open-console").GetProperty("id").GetInt64();

        channel.SendConsoleBytes(Closed(5, 0, 0));
        await client.PingAsync(None);
        channel.Reply(id, Opened);

        ConsoleHandle console = await opening.WaitAsync(Soon);
        Assert.Equal(new ProcessExit(0, 0), await console.Exited.WaitAsync(Soon));
    }

    [Fact]
    public async Task Frames_of_consoles_nobody_opened_are_bounded()
    {
        var channel = new SimulatedAgentChannel();
        channel.Ignore("open-console");
        await using var client = new AgentClient(channel, Fast with { RequestTimeout = Soon });
        await client.ConnectAsync(None);

        // More unknown consoles than the client keeps, each with more bytes than it keeps.
        byte[] big = new byte[4096];
        for (int console = 100; console < 140; console++)
        {
            channel.SendConsoleBytes(ConsoleFrame.EncodeData(console, "x"u8));
        }

        for (int index = 0; index < 80; index++)
        {
            channel.SendConsoleBytes(ConsoleFrame.EncodeData(139, big));
        }

        channel.SendConsoleBytes(new byte[] { 0xAE, 0x63, 0x00, 0x8B, 0x00, 0x01, 0x00 });
        channel.SendConsoleBytes(ConsoleFrame.EncodeData(5, "mine"u8));
        Task<ConsoleHandle> opening = client.OpenConsoleAsync(new ConsoleOptions { Prefix = "test" }, None);
        await client.PingAsync(None);
        channel.Reply(channel.Request("open-console").GetProperty("id").GetInt64(), Opened);

        ConsoleHandle mine = await opening.WaitAsync(Soon);
        Assert.Equal("mine", await ReadTextAsync(mine, 4));
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
            Assert.Equal([0xAE, 0x01, 0x00, 0x05, 0x00, 0x01, 0x03], channel.ConsoleWritten[1]);
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
            channel.SendConsoleBytes(new byte[] { 0xAE, 0x02, 0x00, 0x05, 0x00, 0x00 });

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
    }
}
