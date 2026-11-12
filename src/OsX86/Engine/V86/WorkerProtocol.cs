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
    /// <summary>OPFS path of the kernel.</summary>
    public required string KernelPath { get; init; }

    /// <summary>OPFS path of the initramfs.</summary>
    public required string InitrdPath { get; init; }

    /// <summary>Kernel command line.</summary>
    public required string Cmdline { get; init; }

    /// <summary>Guest memory in MiB.</summary>
    public int MemoryMiB { get; init; }

    /// <summary>VGA memory in MiB.</summary>
    public int VgaMemoryMiB { get; init; }

    /// <summary>Whether the virtio-console device is declared.</summary>
    public bool VirtioConsole { get; init; }

    /// <summary>Builds the command options from the public options.</summary>
    public static StartCommandOptions From(EngineStartOptions options) => new()
    {
        KernelPath = options.KernelPath,
        InitrdPath = options.InitrdPath,
        Cmdline = options.KernelCommandLine,
        MemoryMiB = options.MemoryMiB,
        VgaMemoryMiB = options.VgaMemoryMiB,
        VirtioConsole = options.VirtioConsole,
    };
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
    }

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
