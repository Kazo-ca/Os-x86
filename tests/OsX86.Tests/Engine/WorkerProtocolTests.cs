using System.Text;
using System.Text.Json;
using OsX86.Engine;
using OsX86.Engine.V86;

namespace OsX86.Tests.Engine;

/// <summary>The JSON of the worker protocol, as the worker (<c>v86-worker.js</c>) reads and writes it.</summary>
[Trait("Category", "unit")]
public sealed class WorkerProtocolTests
{
    [Fact]
    public void The_start_command_carries_the_options_in_camel_case()
    {
        var options = new EngineStartOptions
        {
            KernelPath = "osx86-test/vmlinuz-virt",
            InitrdPath = "osx86-test/initramfs-osx86",
            KernelCommandLine = "console=ttyS0 quiet",
            MemoryMiB = 64,
            VgaMemoryMiB = 1,
            VirtioConsole = false,
        };

        using JsonDocument document = JsonDocument.Parse(WorkerProtocol.Start(options));
        JsonElement root = document.RootElement;
        Assert.Equal("start", root.GetProperty("type").GetString());
        JsonElement sent = root.GetProperty("options");
        Assert.Equal("osx86-test/vmlinuz-virt", sent.GetProperty("kernelPath").GetString());
        Assert.Equal("osx86-test/initramfs-osx86", sent.GetProperty("initrdPath").GetString());
        Assert.Equal("console=ttyS0 quiet", sent.GetProperty("cmdline").GetString());
        Assert.Equal(64, sent.GetProperty("memoryMiB").GetInt32());
        Assert.Equal(1, sent.GetProperty("vgaMemoryMiB").GetInt32());
        Assert.False(sent.GetProperty("virtioConsole").GetBoolean());
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("query-state")]
    public void Simple_commands_are_a_type_only(string type)
    {
        string json = type switch
        {
            "stop" => WorkerProtocol.Stop(),
            "pause" => WorkerProtocol.Pause(),
            "resume" => WorkerProtocol.Resume(),
            _ => WorkerProtocol.QueryState(),
        };
        Assert.Equal($"{{\"type\":\"{type}\"}}", json);
    }

    [Fact]
    public void The_write_command_encodes_the_bytes_in_base64()
    {
        string json = WorkerProtocol.Write(ConsoleId.Virtio(1), Encoding.UTF8.GetBytes("ping\n"));
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("write", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("virtio1", document.RootElement.GetProperty("channel").GetString());
        Assert.Equal("ping\n", Encoding.UTF8.GetString(Convert.FromBase64String(document.RootElement.GetProperty("data").GetString()!)));
    }

    [Fact]
    public void Messages_of_every_type_parse()
    {
        WorkerMessage state = WorkerProtocol.Parse("""{"type":"state","state":"running","reason":null}""");
        Assert.Equal("state", state.Type);
        Assert.Equal("running", state.State);
        Assert.Null(state.Reason);

        WorkerMessage error = WorkerProtocol.Parse("""{"type":"error","code":"image-not-found","message":"no kernel","fatal":true}""");
        Assert.Equal("image-not-found", error.Code);
        Assert.Equal("no kernel", error.Message);
        Assert.True(error.Fatal);

        WorkerMessage console = WorkerProtocol.Parse("""{"type":"console","channel":"serial0","data":"aGk="}""");
        Assert.Equal("serial0", console.Channel);
        Assert.Equal("aGk=", console.Data);

        WorkerMessage screen = WorkerProtocol.Parse("""{"type":"screen-size","cols":80,"rows":25,"bpp":0}""");
        Assert.Equal((80, 25, 0), (screen.Cols, screen.Rows, screen.Bpp));

        WorkerMessage ev = WorkerProtocol.Parse("""{"type":"event","name":"emulator-started"}""");
        Assert.Equal("emulator-started", ev.Name);

        Assert.Equal("ready", WorkerProtocol.Parse("""{"type":"ready","unknown":1}""").Type);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"state\":\"running\"}")]
    [InlineData("{\"type\":\"\"}")]
    [InlineData("null")]
    public void A_message_that_is_not_of_the_protocol_is_a_protocol_error(string json)
    {
        var error = Assert.Throws<EngineException>(() => WorkerProtocol.Parse(json));
        Assert.Equal(EngineErrorCodes.Protocol, error.Code);
    }
}
