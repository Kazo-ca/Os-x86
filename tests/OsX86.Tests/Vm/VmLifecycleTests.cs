using System.Text;
using OsX86.Agent;
using OsX86.Engine;
using OsX86.Interface;
using OsX86.Profiles;
using OsX86.Tests.Agent;
using OsX86.Vm;
using static OsX86.Tests.Vm.VmTestRig;

namespace OsX86.Tests.Vm;

/// <summary>The lifecycle of a VM (166 A, 207 A, 212 A) on a simulated engine and agent.</summary>
[Trait("Category", "unit")]
public sealed class VmLifecycleTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_first_application_starts_the_vm_and_connects_the_agent()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync();
        var states = new List<VmState>();
        rig.Manager.InstanceStateChanged += (_, e) => states.Add(e.Data.Current);
        Assert.Equal(VmState.Stopped, vm.State);
        Assert.Null(vm.HasAgent);

        using VmApplication application = await vm.AcquireAsync("shell", None);

        Assert.Equal(VmState.Running, vm.State);
        Assert.Equal([VmState.Starting, VmState.Running], states);
        Assert.True(vm.HasAgent);
        Assert.Equal(1, vm.Applications);
        FakeEngine engine = Assert.Single(rig.Engines.Created);
        Assert.Equal(128, engine.StartedWith!.MemoryMiB);
        Assert.Equal("osx86-test/vmlinuz-virt", engine.StartedWith.KernelPath);
        Assert.True(engine.StartedWith.VirtioConsole);
        Assert.Same(engine, vm.Engine);
        Assert.Equal("shell", application.Reason);
    }

    [Fact]
    public async Task An_early_ping_echoed_by_the_guest_terminal_is_retried()
    {
        await using var rig = new VmTestRig();
        int pings = 0;
        rig.Engines.Configure = engine => engine.Agent!.On("ping", _ =>
            Interlocked.Increment(ref pings) <= 2
                ? "\"op\":\"ping\"}" // the request itself, echoed by the tty before the agent opened hvc0
                : """{"ok":true,"version":1,"uptime":3,"rssKb":170,"launches":0,"consoles":0,"cgroups":true}""");
        VmInstance vm = await rig.CreateAsync();

        using VmApplication application = await vm.AcquireAsync("shell", None);

        Assert.True(vm.HasAgent);
        Assert.True(pings >= 3);
    }

    [Fact]
    public async Task An_agent_of_another_protocol_version_fails_the_start()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => engine.Agent!.On("ping", _ => """{"ok":true,"version":2,"uptime":3,"rssKb":170,"launches":0,"consoles":0,"cgroups":true}""");
        VmInstance vm = await rig.CreateAsync();

        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await vm.AcquireAsync("shell", None));

        Assert.Equal(X86ErrorCodes.StartFailed, error.Code);
        Assert.Equal(VmState.Stopped, vm.State);
    }

    [Fact]
    public async Task The_vm_stops_after_the_grace_once_its_last_application_closes()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync();
        VmApplication first = await vm.AcquireAsync("shell", None);
        VmApplication second = await vm.AcquireAsync("program", None);

        first.Dispose();
        Assert.Equal(1, vm.Applications);
        Assert.Equal(0, rig.Time.PendingTimers); // another application holds it: no grace yet

        second.Dispose();
        second.Dispose(); // idempotent
        Assert.True(second.IsReleased);
        Assert.Equal(0, vm.Applications);
        rig.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(VmState.Running, vm.State);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => vm.State == VmState.Stopped, "the stop after the grace");
        FakeEngine engine = rig.Engines.Last;
        Assert.True(engine.Disposed);
        Assert.Equal(1, engine.Stops);
        Assert.Null(vm.Engine);
        Assert.Empty(rig.BackgroundErrors);
    }

    [Fact]
    public async Task An_application_that_comes_back_during_the_grace_keeps_the_vm()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync();
        (await vm.AcquireAsync("shell", None)).Dispose();
        Assert.Equal(1, rig.Time.PendingTimers);

        using VmApplication again = await vm.AcquireAsync("shell", None);
        Assert.Equal(0, rig.Time.PendingTimers);
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(VmState.Running, vm.State);
        Assert.Single(rig.Engines.Created);
    }

    [Fact]
    public async Task A_zero_grace_stops_at_once()
    {
        await using var rig = new VmTestRig(FastOptions with { StopGrace = TimeSpan.Zero });
        VmInstance vm = await rig.CreateAsync();
        (await vm.AcquireAsync("shell", None)).Dispose();
        await WaitUntilAsync(() => vm.State == VmState.Stopped, "the immediate stop");
    }

    [Fact]
    public async Task A_vm_started_by_hand_stays_up_until_it_is_stopped()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync();
        await vm.HoldAsync(None);
        await vm.HoldAsync(None); // counted once
        Assert.True(vm.IsHeld);
        Assert.Equal(1, vm.Applications);

        (await vm.AcquireAsync("shell", None)).Dispose();
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(VmState.Running, vm.State);

        await vm.StopAsync(force: false, None);
        Assert.Equal(VmState.Stopped, vm.State);
        Assert.False(vm.IsHeld);
        await vm.StopAsync(force: false, None); // stopped: a no-op
    }

    [Fact]
    public async Task A_stop_without_force_is_refused_while_applications_hold_the_vm_and_force_closes_them()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptConsole(engine);
        VmInstance vm = await rig.CreateAsync();
        IVmConsole console = await vm.OpenConsoleAsync(new VmConsoleOptions(), None);

        X86Exception busy = await Assert.ThrowsAsync<X86Exception>(async () => await vm.StopAsync(force: false, None));
        Assert.Equal(X86ErrorCodes.VmBusy, busy.Code);
        Assert.Equal(VmState.Running, vm.State);

        await vm.StopAsync(force: true, None);
        Assert.Equal(VmState.Stopped, vm.State);
        Assert.Equal(0, vm.Applications);
        Assert.Empty(vm.Children);
        X86Exception closed = await Assert.ThrowsAsync<X86Exception>(() => console.Exited);
        Assert.Equal(X86ErrorCodes.ConsoleClosed, closed.Code);
        await console.DisposeAsync(); // releasing an application of a run that ended changes nothing
        Assert.Equal(0, vm.Applications);
    }

    [Fact]
    public async Task An_agent_console_is_a_child_of_the_vm_with_its_exit_code_and_releases_the_vm()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptConsole(engine, number: 7);
        VmInstance vm = await rig.CreateAsync();

        IVmConsole console = await vm.OpenConsoleAsync(new VmConsoleOptions { Columns = 100, Rows = 30 }, None);
        Assert.False(console.IsSerial);
        Assert.Equal(vm.Id, console.VmId);
        VmChild child = Assert.Single(vm.Children);
        Assert.Equal(LaunchId.Parse("test/7"), child.Launch);
        Assert.Equal(VmChildKind.Console, child.Kind);
        var request = rig.Engines.Last.Agent!.Request("open-console");
        Assert.Equal("test", request.GetProperty("prefix").GetString());
        Assert.Equal(100, request.GetProperty("cols").GetInt32());

        await console.WriteAsync("ls\n"u8.ToArray(), None);
        await console.SendInterruptAsync(None);
        Assert.Contains(rig.Engines.Last.Agent!.ConsoleWritten, bytes => bytes.Length == 9 && bytes[^1] == 0x03);

        rig.Engines.Last.Agent!.SendConsoleBytes(ConsoleFrame.EncodeData(7, "bye\r\n"u8));
        rig.Engines.Last.Agent!.SendConsoleBytes(Closed(7, 0, 2));
        Assert.Equal(130, await console.Exited.WaitAsync(Soon)); // SIGINT: 128 + 2
        await WaitUntilAsync(() => vm.Children.Count == 0, "the child removed at its exit");
        await console.DisposeAsync();
        Assert.Equal(0, vm.Applications);
        Assert.Empty(rig.Engines.Last.Agent!.Requests("close-console")); // already ended: nothing to close
    }

    [Fact]
    public async Task Disposing_a_running_console_closes_it_in_the_guest()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptConsole(engine);
        VmInstance vm = await rig.CreateAsync();
        IVmConsole console = await vm.OpenConsoleAsync(new VmConsoleOptions { Prefix = "other" }, None);
        await console.ResizeAsync(90, 20, None);
        Assert.Equal(90, rig.Engines.Last.Agent!.Request("console-resize").GetProperty("cols").GetInt32());

        await console.DisposeAsync();

        Assert.Equal(129, await console.Exited.WaitAsync(Soon)); // SIGHUP from close-console
        Assert.Equal(100, rig.Engines.Last.Agent!.Request("close-console").GetProperty("graceMs").GetInt32());
        Assert.Equal("other", rig.Engines.Last.Agent!.Request("open-console").GetProperty("prefix").GetString());
        Assert.Equal(0, vm.Applications);
    }

    [Fact]
    public async Task A_console_needs_a_prefix_when_the_profile_has_none_and_no_serial_port()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync("base", BuiltInProfiles.LinuxBase with { SerialConsole = false });
        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await vm.OpenConsoleAsync(new VmConsoleOptions(), None));
        Assert.Equal(X86ErrorCodes.NoPrefix, error.Code);
        Assert.Equal(0, vm.Applications); // the failed console released the VM
        Assert.Equal(1, rig.Time.PendingTimers);
    }

    [Fact]
    public async Task An_agent_error_on_open_console_is_reported_with_its_code()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => engine.Agent!.On("open-console", _ => """{"ok":false,"error":"unknown-prefix","message":"no guest user osx86-nobody"}""");
        VmInstance vm = await rig.CreateAsync();
        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await vm.OpenConsoleAsync(new VmConsoleOptions { Prefix = "nobody" }, None));
        Assert.Equal(X86ErrorCodes.AgentError, error.Code);
        Assert.Contains("unknown-prefix", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_image_without_agent_runs_without_children_and_offers_its_serial_console()
    {
        await using var rig = new VmTestRig(withAgent: false);
        VmInstance vm = await rig.CreateAsync("plain", NoAgentSerial);

        IVmConsole console = await vm.OpenConsoleAsync(new VmConsoleOptions(), None);

        Assert.False(vm.HasAgent);
        Assert.True(console.IsSerial);
        Assert.Empty(vm.Children);
        FakeEngine engine = rig.Engines.Last;
        await console.WriteAsync("uname\n"u8.ToArray(), None);
        await console.SendInterruptAsync(None);
        await console.ResizeAsync(100, 40, None); // no-op on a serial port
        Assert.Equal(["uname\n", "\u0003"], engine.SerialWritten.Select(b => Encoding.UTF8.GetString(b)));
        engine.SendSerial("osx86# ");
        await using (IAsyncEnumerator<ReadOnlyMemory<byte>> output = console.ReadOutputAsync(None).GetAsyncEnumerator())
        {
            Assert.True(await output.MoveNextAsync());
            Assert.Equal("osx86# ", Encoding.UTF8.GetString(output.Current.Span));
        }

        await console.DisposeAsync();
        Assert.Equal(0, await console.Exited);
        await Assert.ThrowsAsync<X86Exception>(async () => await console.WriteAsync("x"u8.ToArray(), None));
        Assert.Equal(0, vm.Applications);

        X86Exception noLaunch = await Assert.ThrowsAsync<X86Exception>(async () => await vm.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "true" }, None));
        Assert.Equal(X86ErrorCodes.NoConsole, noLaunch.Code);
        await Assert.ThrowsAsync<X86Exception>(async () => await vm.PingAgentAsync(None));
    }

    [Fact]
    public async Task An_image_without_agent_and_without_serial_port_has_no_console()
    {
        await using var rig = new VmTestRig(withAgent: false);
        VmInstance vm = await rig.CreateAsync("dark", NoSerial);
        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await vm.OpenConsoleAsync(new VmConsoleOptions(), None));
        Assert.Equal(X86ErrorCodes.NoConsole, error.Code);
        Assert.Equal(VmState.Running, vm.State); // the VM is up, without children (212 A)
    }

    [Fact]
    public async Task A_serial_console_fails_when_the_vm_stops_under_it()
    {
        await using var rig = new VmTestRig(withAgent: false);
        VmInstance vm = await rig.CreateAsync("plain", NoAgentSerial);
        IVmConsole console = await vm.OpenConsoleAsync(new VmConsoleOptions(), None);
        await vm.StopAsync(force: true, None);
        Assert.Equal(X86ErrorCodes.ConsoleClosed, (await Assert.ThrowsAsync<X86Exception>(() => console.Exited)).Code);
    }

    [Fact]
    public async Task Each_launch_is_a_child_registered_at_launch_and_removed_at_exit()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => engine.Agent!.On("launch", _ => """{"ok":true,"launch":"test/1","pid":448,"uid":1001,"ms":4.2,"cgroup":"/sys/fs/cgroup/osx86/test/1"}""");
        VmInstance vm = await rig.CreateAsync();

        (LaunchResult launch, VmApplication application) = await vm.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "sleep", Arguments = ["30"] }, None);

        Assert.Equal(LaunchId.Parse("test/1"), launch.Id);
        Assert.Equal(VmChildKind.Program, Assert.Single(vm.Children).Kind);
        Assert.Equal(1, vm.Applications);
        rig.Engines.Last.Agent!.SendLine("""{"event":"exit","launch":"test/1","pid":448,"code":0,"signal":0,"remaining":0}""");
        Assert.Equal(new ProcessExit(0, 0), await launch.Exited.WaitAsync(Soon));
        // IsReleased turns true just before the count goes down: wait for the count itself.
        await WaitUntilAsync(() => vm.Children.Count == 0 && application.IsReleased && vm.Applications == 0, "the launch removed and released at its exit");
    }

    [Fact]
    public async Task A_refused_launch_releases_the_vm()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => engine.Agent!.On("launch", _ => """{"ok":false,"error":"program-not-found","message":"no such program"}""");
        VmInstance vm = await rig.CreateAsync();
        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await vm.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "/nope" }, None));
        Assert.Equal(X86ErrorCodes.AgentError, error.Code);
        Assert.Equal(0, vm.Applications);
        Assert.Empty(vm.Children);
    }

    [Fact]
    public async Task A_restarted_agent_forgets_the_children()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptConsole(engine);
        VmInstance vm = await rig.CreateAsync();
        IVmConsole console = await vm.OpenConsoleAsync(new VmConsoleOptions(), None);
        Assert.Single(vm.Children);

        rig.Engines.Last.Agent!.SendLine("""{"event":"ready","version":1,"pid":88,"cgroups":true,"port":"/dev/hvc0","consolePort":"/dev/hvc1"}""");

        await WaitUntilAsync(() => vm.Children.Count == 0, "the children forgotten");
        Assert.Equal(X86ErrorCodes.ConsoleClosed, (await Assert.ThrowsAsync<X86Exception>(() => console.Exited.WaitAsync(Soon))).Code);
        await console.DisposeAsync();
    }

    [Fact]
    public async Task A_guest_that_dies_stops_the_vm()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync();
        using VmApplication application = await vm.AcquireAsync("shell", None);

        rig.Engines.Last.Crash();

        await WaitUntilAsync(() => vm.State == VmState.Stopped, "the stop after the crash");
        Assert.Equal(0, vm.Applications);
        Assert.True(rig.Engines.Last.Disposed);
    }

    [Fact]
    public async Task A_failed_boot_leaves_the_vm_stopped_with_a_typed_error()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => engine.StartException = new EngineException(EngineErrorCodes.ImageNotFound, "no kernel");
        VmInstance vm = await rig.CreateAsync();

        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await vm.AcquireAsync("shell", None));

        Assert.Equal(X86ErrorCodes.StartFailed, error.Code);
        Assert.Contains("image-not-found", error.Message, StringComparison.Ordinal);
        Assert.Equal(VmState.Stopped, vm.State);
        Assert.Equal(0, vm.Applications);
        Assert.True(rig.Engines.Last.Disposed);
    }

    [Fact]
    public async Task A_teardown_that_fails_still_leaves_the_vm_stopped_and_keeps_the_start_error()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => engine.StopException = new InvalidOperationException("bridge broken");
        VmInstance vm = await rig.CreateAsync();
        using (await vm.AcquireAsync("shell", None))
        {
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await vm.StopAsync(force: true, None));
        Assert.Equal(VmState.Stopped, vm.State); // never left Stopping: the budget no longer counts it
        Assert.True(rig.Engines.Last.Disposed);

        rig.Engines.Configure = engine =>
        {
            engine.StartException = new EngineException(EngineErrorCodes.V86InitFailed, "no cpu");
            engine.StopException = new InvalidOperationException("bridge broken");
        };
        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await vm.AcquireAsync("shell", None));
        Assert.Equal(X86ErrorCodes.StartFailed, error.Code);
        Assert.Contains("v86-init-failed", error.Message, StringComparison.Ordinal);
        Assert.Equal(VmState.Stopped, vm.State);
        Assert.Contains(rig.BackgroundErrors, e => e is InvalidOperationException);
    }

    [Fact]
    public async Task A_removed_vm_cannot_be_started_again_through_an_old_instance()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync();
        await vm.HoldAsync(None);

        await rig.Manager.RemoveAsync(User, vm.Id, force: true, None);

        Assert.Equal(VmState.Stopped, vm.State);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await vm.AcquireAsync("shell", None))).Code);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await vm.HoldAsync(None))).Code);
        Assert.Single(rig.Engines.Created);
    }

    [Fact]
    public async Task A_guid_copied_into_another_users_registry_never_reaches_the_owners_vm()
    {
        await using var rig = new VmTestRig();
        VmInstance mine = await rig.CreateAsync("mine");
        await mine.HoldAsync(None);

        // The other user edits their own registry file and puts the GUID of this user's VM in it.
        string json = (await rig.Store.ReadAsync(User, None))!;
        await rig.Store.WriteAsync(OtherUser, json.Replace(User.ToString("D"), OtherUser.ToString("D"), StringComparison.Ordinal), None);
        VmInstance theirs = await rig.Manager.GetInstanceAsync(OtherUser, mine.Id, None);

        Assert.NotSame(mine, theirs);
        Assert.Equal(OtherUser, theirs.OwnerId);
        Assert.Equal(VmState.Stopped, theirs.State);
        Assert.Equal(VmState.Stopped, rig.Manager.StateOf(OtherUser, mine.Id));
        Assert.Equal(VmState.Running, rig.Manager.StateOf(User, mine.Id));
        Assert.Equal(User, mine.OwnerId);
        Assert.Equal("mine", mine.Record.Name);
    }

    [Fact]
    public async Task A_host_without_engine_or_without_the_image_refuses_the_start()
    {
        await using var rig = new VmTestRig();
        VmInstance missing = await rig.CreateAsync("missing", BuiltInProfiles.AlpineTest with { ImageVersion = "9.9.9" });
        Assert.Equal(X86ErrorCodes.ImageUnavailable, (await Assert.ThrowsAsync<X86Exception>(async () => await missing.AcquireAsync("shell", None))).Code);
        Assert.Empty(rig.Engines.Created);

        var store = new InMemoryVmRegistryStore();
        await using var manager = new VmManager(store, UnavailableEngineFactory.Instance, rig.Images, FixedHostMemory.Unknown, FastOptions);
        VmSummary vm = await manager.CreateAsync(User, "x", BuiltInProfiles.AlpineTest, None);
        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await manager.HoldAsync(User, vm.Id, None));
        Assert.Equal(X86ErrorCodes.EngineUnavailable, error.Code);
        Assert.Equal(VmState.Stopped, manager.StateOf(User, vm.Id));
    }

    [Fact]
    public async Task Applications_that_arrive_during_the_boot_share_one_engine()
    {
        await using var rig = new VmTestRig();
        var gate = new TaskCompletionSource();
        rig.Engines.Configure = engine => engine.BootGate = gate;
        VmInstance vm = await rig.CreateAsync();

        Task<VmApplication> first = vm.AcquireAsync("shell", None).AsTask();
        Task<VmApplication> second = vm.AcquireAsync("program", None).AsTask();
        await WaitUntilAsync(() => vm.State == VmState.Starting, "the boot");
        Assert.False(second.IsCompleted);
        gate.SetResult();

        using VmApplication a = await first.WaitAsync(Soon);
        using VmApplication b = await second.WaitAsync(Soon);
        Assert.Single(rig.Engines.Created);
        Assert.Equal(2, vm.Applications);
    }

    [Fact]
    public async Task The_watchdog_pings_the_agent_and_declares_it_lost_after_the_misses()
    {
        await using var rig = new VmTestRig(FastOptions with { AgentWatchdogInterval = TimeSpan.FromSeconds(20), AgentWatchdogTimeout = TimeSpan.FromSeconds(10), AgentWatchdogMisses = 2 });
        rig.Engines.Configure = engine => ScriptConsole(engine);
        var lost = new List<VmAgentLostEventArgs>();
        rig.Manager.AgentLost += (_, e) => lost.Add(e.Data);
        VmInstance vm = await rig.CreateAsync();
        using VmApplication application = await vm.AcquireAsync("shell", None);
        SimulatedAgentChannel agent = rig.Engines.Last.Agent!;
        int pings = agent.Requests("ping").Count;

        rig.Time.Advance(TimeSpan.FromSeconds(20));
        await WaitUntilAsync(() => vm.LastPingRoundTrip is not null, "an answered ping");
        Assert.Equal(pings + 1, agent.Requests("ping").Count);
        Assert.False(vm.IsAgentLost);

        agent.Silent = true;
        for (int miss = 0; miss < 2; miss++)
        {
            int before = agent.Requests("ping").Count;
            await WaitUntilAsync(() => rig.Time.PendingTimers > 0, "the next watchdog delay");
            rig.Time.Advance(TimeSpan.FromSeconds(20));
            await WaitUntilAsync(() => agent.Requests("ping").Count > before, "the next ping");
            await WaitUntilAsync(() => rig.Time.PendingTimers > 0, "the ping deadline");
            rig.Time.Advance(TimeSpan.FromSeconds(10));
        }

        await WaitUntilAsync(() => vm.IsAgentLost, "the agent declared lost");
        VmAgentLostEventArgs e = Assert.Single(lost);
        Assert.Equal(vm.Id, e.VmId);
        Assert.Equal(2, e.MissedPings);
        Assert.Equal(VmState.Running, vm.State); // the VM is not stopped for it: the user decides
        Assert.Equal(X86ErrorCodes.AgentLost, (await Assert.ThrowsAsync<X86Exception>(async () => await vm.OpenConsoleAsync(new VmConsoleOptions(), None))).Code);
        Assert.Equal(X86ErrorCodes.AgentLost, (await Assert.ThrowsAsync<X86Exception>(async () => await vm.LaunchAsync(new LaunchOptions { Prefix = "test", Program = "true" }, None))).Code);
    }

    [Fact]
    public async Task A_ping_on_demand_measures_the_round_trip()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync();
        using VmApplication application = await vm.AcquireAsync("shell", None);
        TimeSpan elapsed = await vm.PingAgentAsync(None);
        Assert.Equal(elapsed, vm.LastPingRoundTrip);
    }

    [Fact]
    public async Task The_options_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmManagerOptions { StopGrace = TimeSpan.FromSeconds(-1) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmManagerOptions { AgentWatchdogInterval = TimeSpan.FromSeconds(-1) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmManagerOptions { AgentWatchdogTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmManagerOptions { AgentWatchdogMisses = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmManagerOptions { MemoryLimitMiB = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmManagerOptions { ConsoleCloseGrace = TimeSpan.FromSeconds(-1) }.Validate());
        new VmManagerOptions().Validate();
        await Task.CompletedTask;
    }
}
