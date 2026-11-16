using System.Text.Json;
using OsX86.Engine;
using OsX86.Engine.V86;

namespace OsX86.Tests.Engine;

/// <summary>The DOS way of the engine (work item 11): scancodes, the keyboard option, a disk sent with the start options, the screen with its attributes.</summary>
[Trait("Category", "unit")]
public sealed class TextModeEngineTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static EngineStartOptions DosOptions(bool keyboard) => new()
    {
        Kind = EngineBootKind.Dos,
        Disks = new Dictionary<string, EngineDiskOptions>
        {
            ["hda"] = new(EngineFileSource.Range("osx86/images/freedos/0.2.0/image.bin", 0, 4096), EngineWriteLayer.Discard),
            ["hdb"] = new(EngineFileSource.InMemory(new byte[] { 1, 2, 3 }), EngineWriteLayer.Discard),
        },
        MemoryMiB = 64,
        Keyboard = keyboard,
    };

    [Fact]
    public async Task Scancodes_are_posted_as_a_keys_command_while_the_engine_runs()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);

        await Assert.ThrowsAsync<EngineStateException>(async () => await engine.SendScancodesAsync([0x1E, 0x9E], None));
        await engine.StartAsync(DosOptions(keyboard: true), None);
        await engine.SendScancodesAsync([0x1E, 0x9E], None);
        await engine.SendScancodesAsync([], None);

        Assert.Equal(["start", "keys"], worker.PostedTypes);
        using JsonDocument keys = JsonDocument.Parse(worker.Posted[1]);
        Assert.Equal([0x1E, 0x9E], keys.RootElement.GetProperty("codes").EnumerateArray().Select(static code => code.GetInt32()));
    }

    [Fact]
    public void The_start_command_carries_the_keyboard_and_a_disk_sent_whole()
    {
        using JsonDocument command = JsonDocument.Parse(WorkerProtocol.Start(DosOptions(keyboard: true)));
        JsonElement options = command.RootElement.GetProperty("options");

        Assert.True(options.GetProperty("keyboard").GetBoolean());
        JsonElement hdb = options.GetProperty("disks").GetProperty("hdb").GetProperty("source");
        Assert.Equal("AQID", hdb.GetProperty("inline").GetString());
        Assert.False(hdb.TryGetProperty("path", out _));
        Assert.Equal("osx86/images/freedos/0.2.0/image.bin", options.GetProperty("disks").GetProperty("hda").GetProperty("source").GetProperty("path").GetString());
        using JsonDocument linux = JsonDocument.Parse(WorkerProtocol.Start(new EngineStartOptions { KernelPath = "a/k", InitrdPath = "a/i" }));
        Assert.False(linux.RootElement.GetProperty("options").GetProperty("keyboard").GetBoolean());
    }

    [Fact]
    public void A_disk_sent_whole_has_no_path_and_a_bounded_size()
    {
        EngineFileSource source = EngineFileSource.InMemory(new byte[10]);

        Assert.True(source.IsInMemory);
        source.Validate("hdb");
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineFileSource.InMemory(Array.Empty<byte>()).Validate("hdb"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EngineFileSource.InMemory(new byte[EngineFileSource.MaxInMemoryBytes + 1]).Validate("hdb"));
        Assert.Throws<ArgumentException>(() => (EngineFileSource.InMemory(new byte[1]) with { Offset = 0, Size = 1 }).Validate("hdb"));
        Assert.False(EngineFileSource.WholeFile("a/b").IsInMemory);
    }

    [Fact]
    public void A_screen_message_with_wrong_attributes_keeps_its_characters()
    {
        var message = new WorkerMessage { Type = "screen", Cols = 2, Rows = 1, Lines = ["ab"], Attributes = Convert.ToBase64String([7, 7, 7]) };

        EngineTextScreen screen = V86Engine.ToTextScreen(message);
        Assert.Null(screen.Attributes);
        Assert.Null(screen.Cursor);
        Assert.Equal("ab", screen.Text);
        Assert.Null(V86Engine.ToTextScreen(new WorkerMessage { Type = "screen", Cols = 1, Rows = 1, Lines = ["a"], Attributes = "%%" }).Attributes);
        Assert.Equal(new EngineTextCursor(0, 1, false), V86Engine.ToTextScreen(new WorkerMessage { Type = "screen", Cols = 2, Rows = 1, Lines = ["ab"], Cursor = new CursorMessage { Row = 0, Col = 1 }, Graphical = true }).Cursor);
    }
}
