using System.Text.Json;
using OsX86.Agent;

namespace OsX86.Tests.Agent;

/// <summary>The JSON of the agent protocol, as the guest agent (<c>images/agent/src</c>) reads and writes it, and the launch identifier.</summary>
[Trait("Category", "unit")]
public sealed class AgentProtocolTests
{
    [Fact]
    public void A_request_is_one_line_in_camel_case_without_the_unused_fields()
    {
        var request = new AgentRequest
        {
            Id = 7,
            Op = AgentProtocol.Operations.Launch,
            Prefix = "test",
            Program = "/usr/bin/wine",
            Args = ["notepad.exe", "a b"],
            Env = new Dictionary<string, string> { ["WINEDEBUG"] = "-all" },
            Cwd = "/home/osx86-test",
            Nproc = 128,
            Log = true,
        };

        string line = AgentProtocol.Serialize(request);
        Assert.DoesNotContain('\n', line);
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        Assert.Equal(7, root.GetProperty("id").GetInt64());
        Assert.Equal("launch", root.GetProperty("op").GetString());
        Assert.Equal("test", root.GetProperty("prefix").GetString());
        Assert.Equal(["notepad.exe", "a b"], root.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("-all", root.GetProperty("env").GetProperty("WINEDEBUG").GetString());
        Assert.Equal("/home/osx86-test", root.GetProperty("cwd").GetString());
        Assert.Equal(128, root.GetProperty("nproc").GetInt32());
        Assert.True(root.GetProperty("log").GetBoolean());
        Assert.Equal(9, root.EnumerateObject().Count());
    }

    [Fact]
    public void The_other_request_fields_use_the_names_the_agent_reads()
    {
        var request = new AgentRequest
        {
            Id = 1,
            Op = AgentProtocol.Operations.Kill,
            Launch = "test/3",
            Scope = "cgroup",
            Pid = 42,
            Signal = 9,
            GraceMs = 500,
            Seq = 3,
            AllowRoot = true,
            TimeoutMs = 1000,
            MaxOutput = 4096,
            Console = 5,
            Cols = 100,
            Rows = 30,
            Keep = [10, 11],
        };

        using JsonDocument document = JsonDocument.Parse(AgentProtocol.Serialize(request));
        string[] names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["id", "op", "launch", "scope", "pid", "signal", "graceMs", "seq", "allowRoot", "timeoutMs", "maxOutput", "console", "cols", "rows", "keep"], names);
    }

    [Fact]
    public void A_reply_and_an_event_are_parsed()
    {
        AgentMessage reply = AgentProtocol.Parse("""{"id":3,"ok":true,"launch":"test/4","pid":451,"uid":1001,"ms":1.5,"cgroup":"/sys/fs/cgroup/osx86/test/4"}""");
        Assert.Equal(3, reply.Id);
        Assert.True(reply.Ok);
        Assert.Null(reply.Event);
        Assert.Equal(new LaunchId("test", 4), AgentProtocol.RequireLaunch(reply.Launch));
        Assert.Equal(1.5, reply.Ms);

        AgentMessage error = AgentProtocol.Parse("""{"id":null,"ok":false,"error":"invalid-json","message":"the request is not a JSON object"}""");
        Assert.Null(error.Id);
        AgentException exception = AgentProtocol.ToException(error);
        Assert.Equal(AgentErrorCodes.InvalidJson, exception.Code);
        Assert.Equal("the request is not a JSON object", exception.Message);

        AgentMessage ready = AgentProtocol.Parse("""{"event":"ready","version":1,"pid":97,"cgroups":true,"port":"/dev/hvc0","consolePort":"/dev/hvc1"}""");
        Assert.Equal(new AgentReadyEvent(1, 97, true), AgentProtocol.ToEvent(ready));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"id":1}""")]
    [InlineData("""{"id":"one","ok":true}""")]
    [InlineData("[1,2]")]
    public void A_line_that_is_neither_a_reply_nor_an_event_is_a_protocol_error(string line)
    {
        AgentProtocolException exception = Assert.Throws<AgentProtocolException>(() => AgentProtocol.Parse(line));
        Assert.Equal(AgentErrorCodes.Protocol, exception.Code);
    }

    [Fact]
    public void An_error_reply_without_code_or_message_still_gives_an_exception()
    {
        AgentException exception = AgentProtocol.ToException(new AgentMessage { Id = 1, Ok = false });
        Assert.Equal(AgentErrorCodes.Protocol, exception.Code);
        Assert.NotEmpty(exception.Message);
    }

    [Fact]
    public void Events_map_to_their_types()
    {
        Assert.Equal(new LaunchExitedEvent(new LaunchId("b1", 2), new ProcessExit(-1, 15), 3),
            AgentProtocol.ToEvent(AgentProtocol.Parse("""{"event":"exit","launch":"b1/2","pid":10,"code":-1,"signal":15,"remaining":3}""")));
        Assert.Equal(new ConsoleClosedEvent(new LaunchId("b1", 9), 9, new ProcessExit(7, 0), 0),
            AgentProtocol.ToEvent(AgentProtocol.Parse("""{"event":"console-closed","launch":"b1/9","console":9,"pid":10,"code":7,"signal":0,"remaining":0}""")));
        Assert.Equal(new OpenUrlEvent("b1", 1001, 55, "https://example.test/"),
            AgentProtocol.ToEvent(AgentProtocol.Parse("""{"event":"open-url","uid":1001,"pid":55,"prefix":"b1","url":"https://example.test/"}""")));
        Assert.Equal(new OpenUrlEvent(null, 0, 1, string.Empty),
            AgentProtocol.ToEvent(AgentProtocol.Parse("""{"event":"open-url","uid":0,"pid":1,"prefix":null}""")));
        AgentFaultEvent unknown = Assert.IsType<AgentFaultEvent>(AgentProtocol.ToEvent(AgentProtocol.Parse("""{"event":"from-the-future"}""")));
        Assert.Equal(AgentErrorCodes.Protocol, unknown.Code);
        Assert.Throws<AgentProtocolException>(() => AgentProtocol.ToEvent(AgentProtocol.Parse("""{"event":"exit","launch":"no-sequence"}""")));
    }

    [Fact]
    public void The_lists_of_the_replies_map_to_the_public_types()
    {
        AgentMessage processes = AgentProtocol.Parse(
            """{"id":1,"ok":true,"processes":[{"pid":1,"ppid":0,"pgid":0,"uid":0,"state":"S","prefix":null,"launch":null,"comm":"init","cmd":"/bin/sh /init","cgroup":"/"},{"pid":40,"ppid":30,"pgid":40,"uid":1001,"state":"R","prefix":"test","launch":"test/2","comm":"sleep","cmd":"sleep 300","cgroup":"/osx86/test/2"}]}""");
        IReadOnlyList<ProcessInfo> list = AgentProtocol.ToProcesses(processes);
        Assert.Equal(new ProcessInfo(1, 0, 0, 0, "S", null, null, "init", "/bin/sh /init", "/"), list[0]);
        Assert.Equal(new ProcessInfo(40, 30, 40, 1001, "R", "test", new LaunchId("test", 2), "sleep", "sleep 300", "/osx86/test/2"), list[1]);
        Assert.Empty(AgentProtocol.ToProcesses(new AgentMessage()));

        AgentMessage mounts = AgentProtocol.Parse(
            """{"id":2,"ok":true,"timedOut":true,"ms":8000,"mounts":[{"path":"/tmp","type":"tmpfs","device":"tmpfs","state":"ok","statfsErrno":0,"readdirErrno":0,"entries":4,"blocks":100,"blockSize":4096,"freeBlocks":60,"ms":0.4,"error":""},{"path":"/mnt/share","type":"9p","device":"host9p","state":"error","statfsErrno":0,"readdirErrno":5,"entries":-1,"blocks":0,"blockSize":0,"freeBlocks":0,"ms":3.1,"error":"I/O error"},{"path":"/mnt/dead","type":"","device":"","state":"hung","statfsErrno":0,"readdirErrno":0,"entries":-1,"blocks":0,"blockSize":0,"freeBlocks":0,"ms":0,"error":"no answer before the deadline"},{"state":"from-the-future"}]}""");
        IReadOnlyList<MountInfo> mountList = AgentProtocol.ToMounts(mounts);
        Assert.Equal(new MountInfo("/tmp", "tmpfs", "tmpfs", MountState.Ok, 4, 409_600, 245_760, 0, string.Empty), mountList[0]);
        Assert.Equal(new MountInfo("/mnt/share", "9p", "host9p", MountState.Error, -1, 0, 0, 5, "I/O error"), mountList[1]);
        Assert.Equal(MountState.Hung, mountList[2].State);
        Assert.Equal(MountState.Error, mountList[3].State);
        Assert.Equal(string.Empty, mountList[3].Path);

        AgentMessage windows = AgentProtocol.Parse("""{"id":3,"ok":true,"windows":[{"id":"0x400001","pid":40,"launch":"test/2","title":"Untitled"},{}]}""");
        IReadOnlyList<WindowInfo> windowList = AgentProtocol.ToWindows(windows);
        Assert.Equal(new WindowInfo("0x400001", 40, new LaunchId("test", 2), "Untitled"), windowList[0]);
        Assert.Equal(new WindowInfo(string.Empty, 0, null, string.Empty), windowList[1]);

        ExecResult exec = AgentProtocol.ToExecResult(AgentProtocol.Parse("""{"id":4,"ok":true,"code":3,"signal":0,"timedOut":false,"ms":120.5,"truncated":true,"output":"a\nb"}"""));
        Assert.Equal(new ExecResult(new ProcessExit(3, 0), false, true, "a\nb", TimeSpan.FromMilliseconds(120.5)), exec);

        AgentStatus status = AgentProtocol.ToStatus(AgentProtocol.Parse("""{"id":5,"ok":true,"version":1,"uptime":90,"rssKb":176,"launches":2,"consoles":1,"cgroups":false}"""));
        Assert.Equal(new AgentStatus(1, TimeSpan.FromSeconds(90), 176, 2, 1, false), status);
    }

    [Theory]
    [InlineData("test/1", "test", 1)]
    [InlineData("b-2/2147483647", "b-2", 2147483647)]
    [InlineData("0123456789abcdef/12", "0123456789abcdef", 12)]
    public void A_launch_identifier_round_trips(string text, string prefix, int sequence)
    {
        Assert.True(LaunchId.TryParse(text, out LaunchId id));
        Assert.Equal(new LaunchId(prefix, sequence), id);
        Assert.Equal(text, id.ToString());
        Assert.Equal(id, LaunchId.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test")]
    [InlineData("/1")]
    [InlineData("test/")]
    [InlineData("test/0")]
    [InlineData("test/-1")]
    [InlineData("test/1x")]
    [InlineData("test/ 1")]
    [InlineData("Test/1")]
    [InlineData("te st/1")]
    [InlineData("-test/1")]
    [InlineData("0123456789abcdefg/1")]
    [InlineData("test/99999999999")]
    public void A_malformed_launch_identifier_is_refused(string? text)
    {
        Assert.False(LaunchId.TryParse(text, out _));
        if (text is not null)
        {
            Assert.Throws<FormatException>(() => LaunchId.Parse(text));
        }
    }

    [Fact]
    public void The_process_exit_says_whether_a_signal_ended_it()
    {
        Assert.True(new ProcessExit(-1, 9).Signaled);
        Assert.False(new ProcessExit(1, 0).Signaled);
    }

    [Fact]
    public void The_exceptions_carry_their_codes()
    {
        var timeout = new AgentTimeoutException(AgentErrorCodes.Timeout, "launch", TimeSpan.FromSeconds(1.5));
        Assert.Equal("launch", timeout.Operation);
        Assert.Equal(TimeSpan.FromSeconds(1.5), timeout.Timeout);
        Assert.Contains("1.5 s", timeout.Message, StringComparison.Ordinal);
        Assert.Equal(AgentErrorCodes.Closed, new AgentClosedException("gone").Code);
        var inner = new InvalidOperationException("inner");
        Assert.Same(inner, new AgentProtocolException(AgentErrorCodes.Protocol, "bad", inner).InnerException);
        Assert.Throws<ArgumentException>(() => new AgentException(string.Empty, "no code"));
    }
}
