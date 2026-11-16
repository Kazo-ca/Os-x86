using System.Text.Json;
using OsX86.Engine.V86;

namespace OsX86.Tests.Engine;

/// <summary>
/// An <see cref="IEngineWorkerChannel"/> that plays the worker: records every command, answers
/// like <c>v86-worker.js</c> would (state reports, console loopback), and can be told to fail or
/// to stay silent so the tests can drive the engine through every path.
/// </summary>
internal sealed class SimulatedWorkerChannel : IEngineWorkerChannel
{
    private Func<string, ValueTask>? _onMessage;
    private string _state = "stopped";

    public List<string> Posted { get; } = [];

    public List<object> CanvasCalls { get; } = [];

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>When set, the worker does not answer commands; tests send the messages themselves.</summary>
    public bool Silent { get; set; }

    /// <summary>When set, a start fails with this error (fatal) after the <c>starting</c> report.</summary>
    public (string Code, string Message)? FailStart { get; set; }

    /// <summary>When set, pause and resume are refused with a non-fatal invalid-state error.</summary>
    public bool RefusePauseAndResume { get; set; }

    /// <summary>When set, <see cref="StartAsync"/> throws.</summary>
    public Exception? StartException { get; set; }

    /// <summary>When set, <see cref="PostAsync"/> throws before recording anything.</summary>
    public Exception? PostException { get; set; }

    /// <summary>How long <see cref="StartAsync"/> takes.</summary>
    public TimeSpan StartDelay { get; set; }

    /// <summary>How many times <see cref="StartAsync"/> was called.</summary>
    public int StartCalls { get; private set; }

    public string State => _state;

    public async ValueTask StartAsync(Func<string, ValueTask> onMessage, CancellationToken cancellationToken)
    {
        StartCalls++;
        if (StartException is not null)
        {
            throw StartException;
        }

        if (StartDelay > TimeSpan.Zero)
        {
            await Task.Delay(StartDelay, cancellationToken);
        }

        Started = true;
        _onMessage = onMessage;
    }

    public async ValueTask PostAsync(string json, CancellationToken cancellationToken)
    {
        if (PostException is not null)
        {
            throw PostException;
        }

        Posted.Add(json);
        if (Silent)
        {
            return;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        string type = document.RootElement.GetProperty("type").GetString()!;
        switch (type)
        {
            case "start":
                await ReportStateAsync("starting");
                if (FailStart is { } failure)
                {
                    await SendAsync(new { type = "error", code = failure.Code, message = failure.Message, fatal = true });
                    await ReportStateAsync("stopped", "start-failed");
                }
                else
                {
                    await SendAsync(new { type = "event", name = "emulator-ready" });
                    await ReportStateAsync("running");
                }

                break;
            case "stop":
                if (_state == "stopped")
                {
                    await ReportStateAsync("stopped", "already-stopped");
                }
                else
                {
                    await ReportStateAsync("stopping");
                    await ReportStateAsync("stopped", "stopped");
                }

                break;
            case "pause":
                if (RefusePauseAndResume)
                {
                    await SendAsync(new { type = "error", code = "invalid-state", message = "cannot pause", fatal = false });
                }
                else
                {
                    await ReportStateAsync("paused");
                }

                break;
            case "resume":
                if (RefusePauseAndResume)
                {
                    await SendAsync(new { type = "error", code = "invalid-state", message = "cannot resume", fatal = false });
                }
                else
                {
                    await ReportStateAsync("running");
                }

                break;
            case "query-state":
                await ReportStateAsync(_state, "query");
                break;
            case "read-screen":
                await SendAsync(new { type = "screen", cols = 3, rows = 2, lines = new[] { "C:\\", "ok " }, attributes = Convert.ToBase64String([7, 7, 7, 0x1E, 0x1E, 0x4F]), cursor = new { row = 1, col = 2, visible = true }, blink = true, graphical = false, source = "vga" });
                break;
            case "query-disks":
                await SendAsync(new
                {
                    type = "disks",
                    disks = new Dictionary<string, object>
                    {
                        ["hda"] = new { bytes = 4096, reads = 2, bytesRead = 1024, writes = 1, bytesWritten = 512, compressedBytesRead = 300, layer = new { mode = "persistent", blocksWritten = 1, layerBytes = 8192 } },
                        ["hdb"] = new { bytes = 8192, reads = 0, bytesRead = 0, writes = 0, bytesWritten = 0, compressedBytesRead = 0 },
                    },
                    cache = new { cacheBytes = 1024, cacheMaxBytes = 2048, cacheFrames = 1, hits = 3, misses = 1, evictions = 0 },
                });
                break;
            case "write":
                // Loopback: the guest echoes what it receives on the same channel.
                await SendAsync(new
                {
                    type = "console",
                    channel = document.RootElement.GetProperty("channel").GetString(),
                    data = document.RootElement.GetProperty("data").GetString(),
                });
                break;
        }
    }

    public ValueTask AttachCanvasAsync(object canvas, CancellationToken cancellationToken)
    {
        CanvasCalls.Add(canvas);
        return ValueTask.CompletedTask;
    }

    public ValueTask DetachCanvasAsync(CancellationToken cancellationToken)
    {
        CanvasCalls.Add("detach");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends a raw message to the engine, as the worker would.</summary>
    public ValueTask SendRawAsync(string json) => _onMessage?.Invoke(json) ?? ValueTask.CompletedTask;

    /// <summary>Sends an object as JSON to the engine.</summary>
    public ValueTask SendAsync(object message) => SendRawAsync(JsonSerializer.Serialize(message));

    /// <summary>Reports a state, remembering it for the next query.</summary>
    public ValueTask ReportStateAsync(string state, string? reason = null)
    {
        _state = state;
        return SendAsync(new { type = "state", state, reason });
    }

    /// <summary>The type of every command posted so far.</summary>
    public IReadOnlyList<string> PostedTypes => Posted
        .Select(json => JsonDocument.Parse(json).RootElement.GetProperty("type").GetString()!)
        .ToList();
}
