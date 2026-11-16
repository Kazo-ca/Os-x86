using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OsX86.Engine.V86;

/// <summary>A message posted by the worker (every field but <see cref="Type"/> depends on the type).</summary>
internal sealed class WorkerMessage
{
    /// <summary><c>ready</c>, <c>state</c>, <c>error</c>, <c>console</c>, <c>screen-size</c> or <c>event</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary><c>state</c>: the state name.</summary>
    public string? State { get; set; }

    /// <summary><c>state</c>: why, when the worker says.</summary>
    public string? Reason { get; set; }

    /// <summary><c>error</c>: one of <see cref="EngineErrorCodes"/>.</summary>
    public string? Code { get; set; }

    /// <summary><c>error</c>: the description.</summary>
    public string? Message { get; set; }

    /// <summary><c>error</c>: whether the engine stopped because of it.</summary>
    public bool Fatal { get; set; }

    /// <summary><c>console</c>: the channel name (<see cref="ConsoleId.ChannelName"/>).</summary>
    public string? Channel { get; set; }

    /// <summary><c>console</c>: the bytes, base64.</summary>
    public string? Data { get; set; }

    /// <summary><c>screen-size</c>: text columns or pixel width.</summary>
    public int Cols { get; set; }

    /// <summary><c>screen-size</c>: text rows or pixel height.</summary>
    public int Rows { get; set; }

    /// <summary><c>screen-size</c>: 0 in text mode.</summary>
    public int Bpp { get; set; }

    /// <summary><c>event</c>: the v86 event name.</summary>
    public string? Name { get; set; }

    /// <summary><c>screen</c>: the rows of the VGA text screen.</summary>
    public List<string>? Lines { get; set; }

    /// <summary><c>screen</c>: one VGA attribute byte per cell, row by row, base64.</summary>
    public string? Attributes { get; set; }

    /// <summary><c>screen</c>: the hardware cursor, null when the worker cannot read it.</summary>
    public CursorMessage? Cursor { get; set; }

    /// <summary><c>screen</c>: whether bit 7 of an attribute blinks (instead of brightening the background).</summary>
    public bool Blink { get; set; }

    /// <summary><c>screen</c>: whether the VGA is in a graphical mode (the text is then the last one, frozen).</summary>
    public bool Graphical { get; set; }

    /// <summary><c>screen</c>: <c>vga</c> (the VGA memory) or <c>events</c> (characters only).</summary>
    public string? Source { get; set; }

    /// <summary><c>disks</c>: the counters of each open disk.</summary>
    public Dictionary<string, DiskCountersMessage>? Disks { get; set; }

    /// <summary><c>disks</c>: the counters of the frame cache, null when no disk is open.</summary>
    public CacheCountersMessage? Cache { get; set; }
}

/// <summary>The cursor of the <c>screen</c> message.</summary>
internal sealed class CursorMessage
{
    public int Row { get; set; }

    public int Col { get; set; }

    public bool Visible { get; set; }
}

/// <summary>The <c>keys</c> command: PS/2 scancodes (set 1).</summary>
internal sealed class KeysCommand
{
    /// <summary>Always <c>keys</c>.</summary>
    public string Type { get; } = "keys";

    /// <summary>The scancodes, in order.</summary>
    public required int[] Codes { get; init; }
}

/// <summary>The <c>start</c> command.</summary>
internal sealed class StartCommand
{
    /// <summary>Always <c>start</c>.</summary>
    public string Type { get; } = "start";

    /// <summary>What to boot.</summary>
    public required StartCommandOptions Options { get; init; }
}

/// <summary>The options of <see cref="StartCommand"/>, as the worker reads them.</summary>
internal sealed class StartCommandOptions
{
    /// <summary><c>linux</c> or <c>dos</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>OPFS path of a kernel read whole (the form of work item 2).</summary>
    public string? KernelPath { get; init; }

    /// <summary>OPFS path of an initramfs read whole.</summary>
    public string? InitrdPath { get; init; }

    /// <summary>The kernel as a source.</summary>
    public SourceCommand? Kernel { get; init; }

    /// <summary>The initramfs as a source.</summary>
    public SourceCommand? Initrd { get; init; }

    /// <summary>The disks by name.</summary>
    public Dictionary<string, DiskCommand>? Disks { get; init; }

    /// <summary>Kernel command line.</summary>
    public required string Cmdline { get; init; }

    /// <summary>Guest memory in MiB.</summary>
    public int MemoryMiB { get; init; }

    /// <summary>VGA memory in MiB.</summary>
    public int VgaMemoryMiB { get; init; }

    /// <summary>The bound of the frame cache in MiB.</summary>
    public int CacheMiB { get; init; }

    /// <summary>Whether the virtio-console device is declared.</summary>
    public bool VirtioConsole { get; init; }

    /// <summary>Whether <c>keys</c> may reach the guest keyboard.</summary>
    public bool Keyboard { get; init; }

    /// <summary>Builds the command options from the public options.</summary>
    public static StartCommandOptions From(EngineStartOptions options) => new()
    {
        Kind = options.Kind == EngineBootKind.Dos ? "dos" : "linux",
        KernelPath = options.KernelPath,
        InitrdPath = options.InitrdPath,
        Kernel = SourceCommand.From(options.Kernel),
        Initrd = SourceCommand.From(options.Initrd),
        Disks = options.Disks.Count == 0 ? null : options.Disks.ToDictionary(static pair => pair.Key, static pair => DiskCommand.From(pair.Value), StringComparer.Ordinal),
        Cmdline = options.KernelCommandLine,
        MemoryMiB = options.MemoryMiB,
        VgaMemoryMiB = options.VgaMemoryMiB,
        CacheMiB = options.FrameCacheMiB,
        VirtioConsole = options.VirtioConsole,
        Keyboard = options.Keyboard,
    };
}

/// <summary>A source of the start command: <c>{path, offset?, size?, rawSize?, blockSize?}</c> or <c>{inline}</c>.</summary>
internal sealed class SourceCommand
{
    public string? Path { get; init; }

    /// <summary>The bytes of a disk sent whole, base64.</summary>
    public string? Inline { get; init; }

    public long? Offset { get; init; }

    public long? Size { get; init; }

    public long? RawSize { get; init; }

    public int? BlockSize { get; init; }

    public static SourceCommand? From(EngineFileSource? source) => source is null ? null : source.Content is { } content ? new SourceCommand
    {
        Inline = Convert.ToBase64String(content.Span),
    } : new SourceCommand
    {
        Path = source.Path,
        Offset = source.Offset,
        Size = source.Size,
        RawSize = source.RawSize,
        BlockSize = source.BlockSize,
    };
}

/// <summary>A disk of the start command: its source and its layer.</summary>
internal sealed class DiskCommand
{
    public required SourceCommand Source { get; init; }

    public required LayerCommand Layer { get; init; }

    public static DiskCommand From(EngineDiskOptions disk) => new()
    {
        Source = SourceCommand.From(disk.Source)!,
        Layer = new LayerCommand
        {
            Mode = disk.Layer.Mode == EngineWriteLayerMode.Persistent ? "persistent" : "discard",
            Path = disk.Layer.Path,
            Metadata = disk.Layer.MetadataJson,
        },
    };
}

/// <summary>The layer of a disk: <c>{mode:"discard"}</c> or <c>{mode:"persistent", path, metadata}</c>.</summary>
internal sealed class LayerCommand
{
    public required string Mode { get; init; }

    public string? Path { get; init; }

    public string? Metadata { get; init; }
}

/// <summary>The counters of one disk in the <c>disks</c> message.</summary>
internal sealed class DiskCountersMessage
{
    public long Bytes { get; set; }

    public long Reads { get; set; }

    public long BytesRead { get; set; }

    public long Writes { get; set; }

    public long BytesWritten { get; set; }

    public long CompressedBytesRead { get; set; }

    public LayerCountersMessage? Layer { get; set; }
}

/// <summary>The counters of a write layer in the <c>disks</c> message.</summary>
internal sealed class LayerCountersMessage
{
    public string? Mode { get; set; }

    public long BlocksWritten { get; set; }

    public long LayerBytes { get; set; }
}

/// <summary>The counters of the frame cache in the <c>disks</c> message.</summary>
internal sealed class CacheCountersMessage
{
    public long CacheBytes { get; set; }

    public long CacheMaxBytes { get; set; }

    public int CacheFrames { get; set; }

    public long Hits { get; set; }

    public long Misses { get; set; }

    public long Evictions { get; set; }
}

/// <summary>A command without arguments: <c>stop</c>, <c>pause</c>, <c>resume</c>, <c>query-state</c>.</summary>
internal sealed class SimpleCommand(string type)
{
    /// <summary>The command name.</summary>
    public string Type { get; } = type;
}

/// <summary>The <c>write</c> command: bytes for a console port.</summary>
internal sealed class WriteCommand
{
    /// <summary>Always <c>write</c>.</summary>
    public string Type { get; } = "write";

    /// <summary>The channel name (<see cref="ConsoleId.ChannelName"/>).</summary>
    public required string Channel { get; init; }

    /// <summary>The bytes, base64.</summary>
    public required string Data { get; init; }
}

/// <summary>Source-generated JSON for the protocol (the library is trimmed in Kazo OS).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkerMessage))]
[JsonSerializable(typeof(StartCommand))]
[JsonSerializable(typeof(SimpleCommand))]
[JsonSerializable(typeof(WriteCommand))]
[JsonSerializable(typeof(KeysCommand))]
internal sealed partial class WorkerJsonContext : JsonSerializerContext
{
}

/// <summary>Serializes the commands and parses the messages of the worker protocol.</summary>
internal static class WorkerProtocol
{
    /// <summary>The message types the worker posts.</summary>
    public static class Types
    {
        public const string Ready = "ready";
        public const string State = "state";
        public const string Error = "error";
        public const string Console = "console";
        public const string ScreenSize = "screen-size";
        public const string Event = "event";
        public const string Screen = "screen";
        public const string Disks = "disks";
        public const string Benchmark = "benchmark";
    }

    public static string ReadScreen() => Serialize(new SimpleCommand("read-screen"));

    public static string Keys(IReadOnlyList<byte> codes) => Serialize(new KeysCommand { Codes = [.. codes.Select(static code => (int)code)] }, WorkerJsonContext.Default.KeysCommand);

    public static string QueryDisks() => Serialize(new SimpleCommand("query-disks"));

    public static string Serialize(StartCommand command) => Serialize(command, WorkerJsonContext.Default.StartCommand);

    public static string Serialize(SimpleCommand command) => Serialize(command, WorkerJsonContext.Default.SimpleCommand);

    public static string Serialize(WriteCommand command) => Serialize(command, WorkerJsonContext.Default.WriteCommand);

    public static string Stop() => Serialize(new SimpleCommand("stop"));

    public static string Pause() => Serialize(new SimpleCommand("pause"));

    public static string Resume() => Serialize(new SimpleCommand("resume"));

    public static string QueryState() => Serialize(new SimpleCommand("query-state"));

    public static string Start(EngineStartOptions options) => Serialize(new StartCommand { Options = StartCommandOptions.From(options) });

    public static string Write(ConsoleId console, ReadOnlySpan<byte> data) =>
        Serialize(new WriteCommand { Channel = console.ChannelName, Data = Convert.ToBase64String(data) });

    /// <summary>Parses a message posted by the worker.</summary>
    /// <exception cref="EngineException">The text is not a message of the protocol (<see cref="EngineErrorCodes.Protocol"/>).</exception>
    public static WorkerMessage Parse(string json)
    {
        WorkerMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerMessage);
        }
        catch (JsonException exception)
        {
            throw new EngineException(EngineErrorCodes.Protocol, $"The worker sent a message that is not JSON: {exception.Message}", exception);
        }

        if (message is null || string.IsNullOrEmpty(message.Type))
        {
            throw new EngineException(EngineErrorCodes.Protocol, "The worker sent a message without a type.");
        }

        return message;
    }

    private static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => JsonSerializer.Serialize(value, typeInfo);
}
