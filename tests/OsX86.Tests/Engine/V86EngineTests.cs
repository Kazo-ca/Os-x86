using System.Text;
using OsX86.Engine;
using OsX86.Engine.V86;

namespace OsX86.Tests.Engine;

/// <summary><see cref="V86Engine"/> against a simulated worker: lifecycle, errors, consoles, display, disposal.</summary>
[Trait("Category", "unit")]
public sealed class V86EngineTests
{
    private static readonly EngineStartOptions Options = new()
    {
        KernelPath = "osx86-test/vmlinuz-virt",
        InitrdPath = "osx86-test/initramfs-osx86",
    };

    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Start_boots_through_starting_to_running_and_raises_the_changes()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        var changes = new List<(EngineState, EngineState)>();
        engine.StateChanged += (_, e) => changes.Add((e.Previous, e.Current));

        Assert.Equal(EngineState.Stopped, engine.State);
        await engine.StartAsync(Options, None);

        Assert.Equal(EngineState.Running, engine.State);
        Assert.Equal([(EngineState.Stopped, EngineState.Starting), (EngineState.Starting, EngineState.Running)], changes);
        Assert.Equal(["start"], worker.PostedTypes);
        Assert.True(worker.Started);
        Assert.Contains("\"kernelPath\":\"osx86-test/vmlinuz-virt\"", worker.Posted[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_validates_the_options_before_touching_the_worker()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await engine.StartAsync(null!, None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await engine.StartAsync(Options with { KernelPath = "/abs" }, None));
        Assert.False(worker.Started);
    }

    [Fact]
    public async Task Start_twice_is_a_state_error()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        var error = await Assert.ThrowsAsync<EngineStateException>(async () => await engine.StartAsync(Options, None));
        Assert.Equal(EngineOperation.Start, error.Operation);
        Assert.Equal(EngineState.Running, error.State);
        Assert.Single(worker.Posted);
    }

    [Fact]
    public async Task Pause_resume_and_stop_follow_the_lifecycle()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        var reasons = new List<string?>();
        engine.StateChanged += (_, e) => reasons.Add(e.Reason);

        await engine.StartAsync(Options, None);
        await engine.PauseAsync(None);
        Assert.Equal(EngineState.Paused, engine.State);
        await engine.ResumeAsync(None);
        Assert.Equal(EngineState.Running, engine.State);
        await engine.StopAsync(None);
        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.Equal(["start", "pause", "resume", "stop"], worker.PostedTypes);
        Assert.Equal("stopped", reasons[^1]);
    }

    [Fact]
    public async Task Stop_when_stopped_does_nothing()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await engine.StopAsync(None);
        Assert.Empty(worker.Posted);
        Assert.False(worker.Started);
    }

    [Theory]
    [InlineData(EngineOperation.Pause)]
    [InlineData(EngineOperation.Resume)]
    public async Task Pause_and_resume_need_the_right_state(EngineOperation operation)
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        var error = await Assert.ThrowsAsync<EngineStateException>(async () =>
        {
            if (operation == EngineOperation.Pause)
            {
                await engine.PauseAsync(None);
            }
            else
            {
                await engine.ResumeAsync(None);
            }
        });
        Assert.Equal(operation, error.Operation);
        Assert.Equal(EngineState.Stopped, error.State);
    }

    [Fact]
    public async Task A_fatal_error_during_start_fails_the_start_with_its_code()
    {
        var worker = new SimulatedWorkerChannel { FailStart = ("image-not-found", "osx86-test/vmlinuz-virt: NotFoundError") };
        await using var engine = new V86Engine(worker);
        EngineErrorEventArgs? reported = null;
        engine.ErrorReported += (_, e) => reported = e;

        var error = await Assert.ThrowsAsync<EngineException>(async () => await engine.StartAsync(Options, None));

        Assert.Equal(EngineErrorCodes.ImageNotFound, error.Code);
        Assert.Contains("NotFoundError", error.Message, StringComparison.Ordinal);
        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.NotNull(reported);
        Assert.True(reported.Fatal);
        Assert.Equal(reported.Code, engine.LastError?.Code);

        // The engine is usable again, and the error of the first start is forgotten.
        worker.FailStart = null;
        await engine.StartAsync(Options, None);
        Assert.Equal(EngineState.Running, engine.State);
        Assert.Null(engine.LastError);
    }

    [Fact]
    public async Task A_refused_pause_throws_and_leaves_the_engine_running()
    {
        var worker = new SimulatedWorkerChannel { RefusePauseAndResume = true };
        await using var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);

        var error = await Assert.ThrowsAsync<EngineException>(async () => await engine.PauseAsync(None));
        Assert.Equal(EngineErrorCodes.InvalidState, error.Code);
        Assert.Equal(EngineState.Running, engine.State);

        await engine.StopAsync(None);
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task A_refused_start_leaves_the_engine_stopped_and_raises_the_change()
    {
        var worker = new SimulatedWorkerChannel { Silent = true };
        await using var engine = new V86Engine(worker);
        var states = new List<EngineState>();
        engine.StateChanged += (_, e) => states.Add(e.Current);

        ValueTask start = engine.StartAsync(Options, None);
        await worker.SendAsync(new { type = "error", code = "invalid-state", message = "cannot start", fatal = false });

        var error = await Assert.ThrowsAsync<EngineException>(async () => await start);
        Assert.Equal(EngineErrorCodes.InvalidState, error.Code);
        Assert.Equal([EngineState.Starting, EngineState.Stopped], states);
    }

    [Fact]
    public async Task The_engine_stopping_on_its_own_is_reported_and_fails_a_pending_operation()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        worker.Silent = true;

        ValueTask pause = engine.PauseAsync(None);
        await worker.SendAsync(new { type = "error", code = "worker-error", message = "v86 crashed", fatal = true });
        await worker.ReportStateAsync("stopped", "emulator-stopped");

        var error = await Assert.ThrowsAsync<EngineException>(async () => await pause);
        Assert.Equal(EngineErrorCodes.WorkerError, error.Code);
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task A_stop_report_without_an_error_fails_a_pending_operation_with_the_stopped_code()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        worker.Silent = true;

        ValueTask start = engine.StartAsync(Options, None);
        await worker.ReportStateAsync("starting");
        await worker.ReportStateAsync("stopped", "start-failed");

        var error = await Assert.ThrowsAsync<EngineException>(async () => await start);
        Assert.Equal(EngineErrorCodes.Stopped, error.Code);
        Assert.Contains("start-failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_one_operation_at_a_time()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        worker.Silent = true;
        ValueTask pause = engine.PauseAsync(None);

        var error = await Assert.ThrowsAsync<EngineStateException>(async () => await engine.PauseAsync(None));
        Assert.Contains("in progress", error.Message, StringComparison.Ordinal);

        await worker.ReportStateAsync("paused");
        await pause;
        Assert.Equal(EngineState.Paused, engine.State);
    }

    [Fact]
    public async Task A_stop_preempts_a_pending_start()
    {
        var worker = new SimulatedWorkerChannel { Silent = true };
        await using var engine = new V86Engine(worker);
        ValueTask start = engine.StartAsync(Options, None);
        Assert.Equal(EngineState.Starting, engine.State);

        ValueTask stop = engine.StopAsync(None);
        Assert.Equal(EngineState.Stopping, engine.State);
        var error = await Assert.ThrowsAsync<EngineException>(async () => await start);
        Assert.Equal(EngineErrorCodes.Stopped, error.Code);

        await worker.ReportStateAsync("stopped", "stopped");
        await stop;
        Assert.Equal(["start", "stop"], worker.PostedTypes);
    }

    [Fact]
    public async Task A_second_stop_waits_for_the_first()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        worker.Silent = true;

        ValueTask first = engine.StopAsync(None);
        ValueTask second = engine.StopAsync(None);
        Assert.Equal(EngineState.Stopping, engine.State);
        await worker.ReportStateAsync("stopped", "stopped");
        await first;
        await second;
        Assert.Equal(["start", "stop"], worker.PostedTypes);
    }

    [Fact]
    public async Task A_stop_joins_a_stop_the_worker_started_on_its_own()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        worker.Silent = true;
        await worker.ReportStateAsync("stopping");

        ValueTask stop = engine.StopAsync(None);
        Assert.Equal(["start"], worker.PostedTypes); // nothing more is asked of the worker
        await worker.ReportStateAsync("stopped", "emulator-stopped");
        await stop;
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task A_cancelled_operation_releases_the_engine()
    {
        var worker = new SimulatedWorkerChannel { Silent = true };
        await using var engine = new V86Engine(worker);
        using var cancellation = new CancellationTokenSource();
        ValueTask start = engine.StartAsync(Options, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await start);

        // The command reached the worker: its late reports are still applied, and a new operation is possible.
        Assert.Equal(EngineState.Starting, engine.State);
        await worker.ReportStateAsync("running");
        Assert.Equal(EngineState.Running, engine.State);
        worker.Silent = false;
        await engine.StopAsync(None);
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task A_command_the_channel_cannot_post_leaves_the_engine_as_it_was()
    {
        var worker = new SimulatedWorkerChannel { PostException = new InvalidOperationException("JS gone") };
        await using var engine = new V86Engine(worker);
        var changes = new List<EngineState>();
        engine.StateChanged += (_, e) => changes.Add(e.Current);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.StartAsync(Options, None));

        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.Equal([EngineState.Starting, EngineState.Stopped], changes);
        worker.PostException = null;
        await engine.StartAsync(Options, None); // no operation is left pending
        Assert.Equal(EngineState.Running, engine.State);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_strand_the_pending_operation()
    {
        var worker = new SimulatedWorkerChannel { Silent = true };
        await using var engine = new V86Engine(worker);
        engine.StateChanged += (_, e) =>
        {
            if (e.Current == EngineState.Running)
            {
                throw new InvalidOperationException("subscriber bug");
            }
        };

        ValueTask start = engine.StartAsync(Options, None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await worker.ReportStateAsync("running"));
        await start.AsTask().WaitAsync(Soon);
        Assert.Equal(EngineState.Running, engine.State);
    }

    [Fact]
    public async Task Concurrent_first_calls_share_one_worker_start()
    {
        var worker = new SimulatedWorkerChannel { StartDelay = TimeSpan.FromMilliseconds(200) };
        await using var engine = new V86Engine(worker);
        Task attach = engine.AttachDisplayAsync(new object(), None).AsTask();
        Task start = engine.StartAsync(Options, None).AsTask();
        await Task.WhenAll(attach, start).WaitAsync(Soon);
        Assert.Equal(1, worker.StartCalls);
        Assert.Equal(EngineState.Running, engine.State);
    }

    [Fact]
    public async Task Consoles_fan_the_guest_bytes_out_to_raw_and_line_readers_and_write_through_the_worker()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        IEngineConsole serial = engine.GetConsole(ConsoleId.Serial0);
        Assert.Same(serial, engine.GetConsole(ConsoleId.Serial0));
        Assert.Equal(ConsoleId.Serial0, serial.Id);

        using var cancellation = new CancellationTokenSource();
        IAsyncEnumerable<ReadOnlyMemory<byte>> rawStream = serial.ReadRawAsync(cancellation.Token);
        IAsyncEnumerable<string> lineStream = serial.ReadLinesAsync(cancellation.Token);

        // The simulated guest echoes writes on the same channel.
        await serial.WriteLineAsync("echo hi", None);
        await serial.WriteAsync(Encoding.UTF8.GetBytes("hi\r\nosx86# "), None);
        await worker.SendAsync(new { type = "console", channel = "virtio1", data = Convert.ToBase64String("other port\n"u8.ToArray()) });
        Assert.Contains("\"channel\":\"serial0\"", worker.Posted[1], StringComparison.Ordinal);
        Assert.Equal("osx86# ", ((V86Console)serial).PendingText);

        var raw = new List<byte>();
        var lines = new List<string>();
        Task rawReader = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlyMemory<byte> chunk in rawStream)
                {
                    raw.AddRange(chunk.ToArray());
                }
            }
            catch (OperationCanceledException)
            {
                // The stream ends with its token, as every IAsyncEnumerable does.
            }
        });
        Task lineReader = Task.Run(async () =>
        {
            try
            {
                await foreach (string line in lineStream)
                {
                    lines.Add(line);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        await Task.Delay(100);
        cancellation.Cancel();
        await Task.WhenAll(rawReader, lineReader).WaitAsync(Soon);

        Assert.Equal("echo hi\nhi\r\nosx86# ", Encoding.UTF8.GetString(raw.ToArray()));
        Assert.Equal(["echo hi", "hi"], lines);
    }

    [Fact]
    public async Task Readers_are_registered_at_the_call_and_complete_on_dispose()
    {
        var worker = new SimulatedWorkerChannel();
        var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        IEngineConsole serial = engine.GetConsole(ConsoleId.Serial0);

        IAsyncEnumerable<string> lines = serial.ReadLinesAsync(None); // registered now, before the write
        await serial.WriteLineAsync("echo early", None);

        var received = new List<string>();
        Task reader = Task.Run(async () =>
        {
            await foreach (string line in lines)
            {
                received.Add(line);
            }
        });
        await engine.DisposeAsync(); // completes the readers
        await reader.WaitAsync(Soon);
        Assert.Equal(["echo early"], received);

        await foreach (string unused in serial.ReadLinesAsync(None))
        {
            Assert.Fail("a reader on a disposed engine must complete at once");
        }
    }

    [Fact]
    public async Task The_prompt_pending_when_the_engine_stops_becomes_a_line()
    {
        var worker = new SimulatedWorkerChannel();
        var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        IEngineConsole serial = engine.GetConsole(ConsoleId.Serial0);
        IAsyncEnumerable<string> lines = serial.ReadLinesAsync(None);
        await worker.SendAsync(new { type = "console", channel = "serial0", data = Convert.ToBase64String("done\r\nosx86# "u8.ToArray()) });
        await engine.StopAsync(None);
        await engine.DisposeAsync();

        var received = new List<string>();
        await foreach (string line in lines)
        {
            received.Add(line);
        }

        Assert.Equal(["done", "osx86# "], received);
    }

    [Fact]
    public async Task Writing_to_a_stopped_engine_is_a_state_error()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        IEngineConsole serial = engine.GetConsole(ConsoleId.Serial0);
        var error = await Assert.ThrowsAsync<EngineStateException>(async () => await serial.WriteLineAsync("ls", None));
        Assert.Equal(EngineOperation.Write, error.Operation);
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await serial.WriteLineAsync(null!, None));
        await serial.WriteAsync(ReadOnlyMemory<byte>.Empty, None); // nothing to send, nothing to refuse
        Assert.Empty(worker.Posted);
    }

    [Fact]
    public async Task An_unknown_console_port_is_refused()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        Assert.Throws<ArgumentException>(() => engine.GetConsole(ConsoleId.Virtio(4)));
    }

    [Fact]
    public async Task Protocol_errors_are_reported_without_stopping_the_engine_or_failing_an_operation()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        var errors = new List<EngineErrorEventArgs>();
        engine.ErrorReported += (_, e) => errors.Add(e);
        worker.Silent = true;
        ValueTask pause = engine.PauseAsync(None);

        await worker.SendRawAsync("not json");
        await worker.SendAsync(new { type = "state", state = "sideways" });
        await worker.SendAsync(new { type = "console", channel = "usb0", data = "aGk=" });
        await worker.SendAsync(new { type = "console", channel = "serial0", data = "***" });
        await worker.SendAsync(new { type = "screen-size", cols = 0, rows = 25, bpp = 0 });
        await worker.SendAsync(new { type = "what" });
        await worker.ReportStateAsync("starting"); // illegal from running: reported, then applied

        Assert.Equal(7, errors.Count);
        Assert.All(errors, e => Assert.Equal(EngineErrorCodes.Protocol, e.Code));
        Assert.All(errors, e => Assert.False(e.Fatal));
        Assert.Equal(EngineState.Starting, engine.State);
        Assert.False(pause.IsCompleted); // a garbage message does not abort the pending pause

        await worker.ReportStateAsync("paused");
        await pause;
    }

    [Fact]
    public async Task Screen_size_and_ready_are_surfaced()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        ScreenSizeChangedEventArgs? size = null;
        engine.ScreenSizeChanged += (_, e) => size = e;

        Assert.False(engine.WorkerReady);
        await worker.SendAsync(new { type = "ready" });
        await worker.SendAsync(new { type = "screen-size", cols = 80, rows = 25, bpp = 0 });

        Assert.True(engine.WorkerReady);
        Assert.NotNull(size);
        Assert.Equal((80, 25, true), (size.Columns, size.Rows, size.IsText));
    }

    [Fact]
    public async Task The_display_is_attached_and_detached_through_the_worker()
    {
        var worker = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(worker);
        var canvas = new object();
        await engine.AttachDisplayAsync(canvas, None);
        await engine.DetachDisplayAsync(None);
        Assert.Equal([canvas, "detach"], worker.CanvasCalls);
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await engine.AttachDisplayAsync(null!, None));
    }

    [Fact]
    public async Task A_worker_that_cannot_start_leaves_the_engine_stopped_and_retryable()
    {
        var worker = new SimulatedWorkerChannel { StartException = new InvalidOperationException("no JS runtime") };
        await using var engine = new V86Engine(worker);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.StartAsync(Options, None));
        Assert.Equal(EngineState.Stopped, engine.State);

        worker.StartException = null;
        await engine.StartAsync(Options, None);
        Assert.Equal(EngineState.Running, engine.State);
    }

    [Fact]
    public async Task Dispose_ends_the_worker_and_the_pending_operation()
    {
        var worker = new SimulatedWorkerChannel();
        var engine = new V86Engine(worker);
        await engine.StartAsync(Options, None);
        worker.Silent = true;
        ValueTask pause = engine.PauseAsync(None);
        var states = new List<EngineState>();
        engine.StateChanged += (_, e) => states.Add(e.Current);

        await engine.DisposeAsync();
        await engine.DisposeAsync(); // idempotent

        Assert.True(worker.Disposed);
        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.Equal([EngineState.Stopped], states);
        var error = await Assert.ThrowsAsync<EngineException>(async () => await pause);
        Assert.Equal(EngineErrorCodes.Disposed, error.Code);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await engine.StartAsync(Options, None));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await engine.StopAsync(None));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await engine.AttachDisplayAsync(new object(), None));
        await worker.SendAsync(new { type = "state", state = "running" }); // dropped after disposal
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task Dispose_without_a_started_worker_does_not_touch_it()
    {
        var worker = new SimulatedWorkerChannel();
        var engine = new V86Engine(worker);
        await engine.DisposeAsync();
        Assert.False(worker.Disposed);
        Assert.Throws<ArgumentNullException>(() => new V86Engine(null!));
    }
}
