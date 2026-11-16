using System.Text.Json;
using OsX86.Agent;
using OsX86.Interface;
using OsX86.Prefixes;
using OsX86.Profiles;
using OsX86.Tests.Agent;
using OsX86.Tests.Programs;
using OsX86.Vm;
using static OsX86.Tests.Vm.VmTestRig;

namespace OsX86.Tests.Vm;

/// <summary>
/// Work item 10 on simulated engines and agents: the prefixes of a VM (registry and guest), the
/// choice of the VM and the prefix, the copy of a program, the Wine session of a prefix, the
/// console of a program and its exit code, the sync before a stop.
/// </summary>
[Trait("Category", "unit")]
public sealed class ProgramRunTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly byte[] Hello = TestPrograms.Pe(length: 80 * 1024);

    /// <summary>A guest of protocol 2 with the wine image's operations, scripted to answer like the real one.</summary>
    private static void ScriptWineGuest(FakeEngine engine, bool copied = false, int console = 5)
    {
        SimulatedAgentChannel agent = engine.Agent!;
        agent.On("ping", _ => """{"ok":true,"version":2,"uptime":3,"rssKb":170,"launches":0,"consoles":0,"cgroups":true}""");
        agent.On("prefix-ensure", request => $$"""{"ok":true,"created":true,"prefix":"{{request.GetProperty("prefix").GetString()}}","uid":2000,"home":"/home/osx86-{{request.GetProperty("prefix").GetString()}}","ms":4100}""");
        agent.On("prefix-delete", _ => """{"ok":true,"deleted":true,"ms":80}""");
        agent.On("prefix-list", _ => """{"ok":true,"prefixes":[{"prefix":"default","uid":2000,"home":"/home/osx86-default"}],"admin":true}""");
        agent.On("put-file", request => $$"""{"ok":true,"size":{{request.GetProperty("data").GetString()!.Length}},"ms":3}""");
        agent.On("sync", _ => """{"ok":true,"timedOut":false,"ms":12}""");
        agent.On("kill", _ => """{"ok":true,"forced":false,"ms":5}""");
        agent.On("launch", request => $$"""{"ok":true,"launch":"{{request.GetProperty("prefix").GetString()}}/1","pid":90,"uid":2000,"cgroup":"/x","ms":4}""");
        agent.On("exec-in-prefix", request => request.GetProperty("program").GetString() == "stat"
            ? copied ? $$"""{"ok":true,"code":0,"signal":0,"timedOut":false,"truncated":false,"output":"{{Hello.Length}}\n","ms":9}""" : """{"ok":true,"code":1,"signal":0,"timedOut":false,"truncated":false,"output":"stat: cannot stat","ms":9}"""
            : """{"ok":true,"code":0,"signal":0,"timedOut":false,"truncated":false,"output":"","ms":3000}""");
        agent.On("open-console", request => $$"""{"ok":true,"launch":"{{request.GetProperty("prefix").GetString()}}/{{console}}","console":{{console}},"pid":44,"uid":2000,"cols":80,"rows":25}""");
        agent.On("close-console", _ => $$"""{"ok":true,"console":{{console}},"keptCount":0,"kept":null}""");
    }

    private static ProgramRunOptions Run(string name = "hello.exe", byte[]? content = null, string? prefix = null, Guid? vm = null, params string[] args) =>
        new() { FileName = name, Content = content ?? Hello, Prefix = prefix, VmId = vm, Arguments = args };

    [Fact]
    public async Task A_first_run_makes_the_windows_vm_the_default_prefix_copies_the_program_starts_the_session_and_returns_the_code()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptWineGuest(engine);
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));

        IVmConsole console = await rig.Manager.RunProgramAsync(User, Run(args: ["a", "b c"]), None);

        VmRecord vm = Assert.Single(await rig.Manager.Registry.ListAsync(User, None));
        Assert.Equal(VmManager.WindowsVmName, vm.Name);
        Assert.Equal(BuiltInProfiles.Wine, vm.Profile);
        Assert.Equal(PrefixId.Default, Assert.Single(vm.EffectivePrefixes).Id);
        SimulatedAgentChannel agent = rig.Engines.Last.Agent!;
        Assert.Equal("default", agent.Request("prefix-ensure").GetProperty("prefix").GetString());
        IReadOnlyList<JsonElement> puts = agent.Requests("put-file");
        Assert.Equal((Hello.Length + AgentClient.PutFileChunkBytes - 1) / AgentClient.PutFileChunkBytes, puts.Count);
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Hello))[..16];
        Assert.All(puts, put => Assert.Equal($".wine/drive_c/osx86/run/{hash}/hello.exe.part", put.GetProperty("path").GetString()));
        JsonElement moved = agent.Requests("exec-in-prefix").Single(r => r.GetProperty("program").GetString() == "mv");
        Assert.Equal(["-f", "--", $".wine/drive_c/osx86/run/{hash}/hello.exe.part", $".wine/drive_c/osx86/run/{hash}/hello.exe"], moved.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.True(puts[0].GetProperty("truncate").GetBoolean());
        Assert.Equal(AgentClient.PutFileChunkBytes, puts[1].GetProperty("offset").GetInt64());
        JsonElement session = agent.Request("launch");
        Assert.Equal(VmInstance.SessionProgram, session.GetProperty("program").GetString());
        Assert.Equal("start", session.GetProperty("args")[0].GetString());
        JsonElement open = agent.Request("open-console");
        Assert.Equal("wine", open.GetProperty("program").GetString());
        Assert.Equal([$"/home/osx86-default/.wine/drive_c/osx86/run/{hash}/hello.exe", "a", "b c"], open.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("-all", open.GetProperty("env").GetProperty("WINEDEBUG").GetString());
        Assert.Equal($"/home/osx86-default/.wine/drive_c/osx86/run/{hash}", open.GetProperty("cwd").GetString());
        VmInstance instance = await rig.Manager.GetInstanceAsync(User, vm.Id, None);
        Assert.Contains(instance.Children, child => child.Kind == VmChildKind.Session);
        Assert.Contains(instance.Children, child => child.Kind == VmChildKind.Program);
        Assert.True(instance.HasSession(PrefixId.Default));

        agent.SendConsoleBytes(OsX86.Agent.ConsoleFrame.EncodeData(5, "osx86-hello\r\n"u8));
        agent.SendConsoleBytes(Closed(5, 7, 0));
        Assert.Equal(7, await console.Exited.WaitAsync(Soon));
        await console.DisposeAsync();
    }

    [Fact]
    public async Task A_second_run_reuses_the_session_and_the_copy_and_the_session_ends_after_its_grace()
    {
        await using var rig = new VmTestRig(FastOptions with { PrefixSessionGrace = TimeSpan.FromSeconds(30), StopGrace = TimeSpan.FromMinutes(5) });
        bool copied = false;
        rig.Engines.Configure = engine => ScriptWineGuest(engine, copied: copied);
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));
        await rig.CreateAsync("w", BuiltInProfiles.Wine);

        IVmConsole first = await rig.Manager.RunProgramAsync(User, Run(), None);
        SimulatedAgentChannel agent = rig.Engines.Last.Agent!;
        agent.SendConsoleBytes(Closed(5, 0, 0));
        await first.Exited.WaitAsync(Soon);
        await first.DisposeAsync();
        ScriptWineGuest(rig.Engines.Last, copied: true, console: 6);

        IVmConsole second = await rig.Manager.RunProgramAsync(User, Run(), None);
        Assert.Single(agent.Requests("launch")); // the session is reused
        int pieces = (Hello.Length + AgentClient.PutFileChunkBytes - 1) / AgentClient.PutFileChunkBytes;
        Assert.Equal(pieces, agent.Requests("put-file").Count); // the same bytes are not copied again
        agent.SendConsoleBytes(Closed(6, 2, 0));
        Assert.Equal(2, await second.Exited.WaitAsync(Soon));
        await second.DisposeAsync();

        Assert.Empty(agent.Requests("kill"));
        rig.Time.Advance(TimeSpan.FromSeconds(31));
        await WaitUntilAsync(() => agent.Requests("kill").Count == 1, "the session killed after its grace");
        Assert.Equal("default/1", agent.Request("kill").GetProperty("launch").GetString());
    }

    [Fact]
    public async Task A_batch_file_runs_through_cmd()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptWineGuest(engine);
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));

        IVmConsole console = await rig.Manager.RunProgramAsync(User, Run("job.cmd", "@echo off\r\n"u8.ToArray(), args: ["x"]), None);

        JsonElement open = rig.Engines.Last.Agent!.Request("open-console");
        string[] args = [.. open.GetProperty("args").EnumerateArray().Select(a => a.GetString()!)];
        Assert.Equal(["cmd.exe", "/c"], args[..2]);
        Assert.EndsWith("/job.cmd", args[2], StringComparison.Ordinal);
        Assert.Equal("x", args[3]);
        await console.DisposeAsync();
    }

    [Fact]
    public async Task A_session_that_does_not_start_fails_the_run_and_releases_the_vm()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine =>
        {
            ScriptWineGuest(engine);
            engine.Agent!.On("exec-in-prefix", request => request.GetProperty("program").GetString() switch
            {
                "stat" => """{"ok":true,"code":1,"signal":0,"timedOut":false,"truncated":false,"output":"","ms":9}""",
                "mv" => """{"ok":true,"code":0,"signal":0,"timedOut":false,"truncated":false,"output":"","ms":9}""",
                _ => """{"ok":true,"code":1,"signal":0,"timedOut":false,"truncated":false,"output":"prefix-session: not ready","ms":9}""",
            });
        };
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));

        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunProgramAsync(User, Run(), None));

        Assert.Equal(X86ErrorCodes.StartFailed, error.Code);
        Assert.Contains("not ready", error.Message, StringComparison.Ordinal);
        VmInstance vm = (await rig.Manager.GetInstanceAsync(User, (await rig.Manager.ResolveAsync(User, "windows", None)).Id, None));
        Assert.Equal(0, vm.Applications);
        await WaitUntilAsync(() => rig.Engines.Last.Agent!.Requests("kill").Count == 1, "the failed session killed");
    }

    [Fact]
    public async Task A_session_that_dies_before_it_is_ready_fails_at_once_without_waiting_for_its_deadline()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine =>
        {
            ScriptWineGuest(engine, copied: true);
            SimulatedAgentChannel agent = engine.Agent!;
            agent.OnLines("launch", request =>
            [
                SimulatedAgentChannel.ReplyTo(request, """{"ok":true,"launch":"default/1","pid":90,"uid":2000,"cgroup":"/x","ms":4}"""),
                """{"event":"exit","launch":"default/1","pid":90,"code":1,"signal":0,"remaining":0}""",
            ]);
            agent.OnLines("exec-in-prefix", request => request.GetProperty("program").GetString() == "stat"
                ? [SimulatedAgentChannel.ReplyTo(request, $$"""{"ok":true,"code":0,"signal":0,"timedOut":false,"truncated":false,"output":"{{Hello.Length}}\n","ms":9}""")]
                : []); // the wait never answers
        };
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));

        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunProgramAsync(User, Run(), None).AsTask().WaitAsync(Soon));

        Assert.Equal(X86ErrorCodes.StartFailed, error.Code);
        Assert.Contains("ended with code 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_copy_that_cannot_be_put_in_place_fails_the_run()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine =>
        {
            ScriptWineGuest(engine);
            engine.Agent!.On("exec-in-prefix", request => request.GetProperty("program").GetString() == "mv"
                ? """{"ok":true,"code":1,"signal":0,"timedOut":false,"truncated":false,"output":"mv: cannot move","ms":9}"""
                : """{"ok":true,"code":1,"signal":0,"timedOut":false,"truncated":false,"output":"","ms":9}""");
        };
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));

        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunProgramAsync(User, Run(), None));

        Assert.Equal(X86ErrorCodes.AgentError, error.Code);
        Assert.Contains("mv: cannot move", error.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Engines.Last.Agent!.Requests("launch"));
    }

    [Fact]
    public async Task A_console_of_the_prefix_keeps_the_session_past_its_grace()
    {
        await using var rig = new VmTestRig(FastOptions with { PrefixSessionGrace = TimeSpan.FromSeconds(30), StopGrace = TimeSpan.FromMinutes(5) });
        rig.Engines.Configure = engine => ScriptWineGuest(engine);
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));
        VmInstance vm = await rig.CreateAsync("w", BuiltInProfiles.Wine);
        IVmConsole program = await rig.Manager.RunProgramAsync(User, Run(vm: vm.Id), None);
        SimulatedAgentChannel agent = rig.Engines.Last.Agent!;
        agent.SendConsoleBytes(Closed(5, 0, 0));
        await program.Exited.WaitAsync(Soon);
        await program.DisposeAsync();
        await WaitUntilAsync(() => vm.Children.All(child => child.Kind != VmChildKind.Program), "the program child gone");
        ScriptWineGuest(rig.Engines.Last, copied: true, console: 6);
        IVmConsole shell = await vm.OpenConsoleAsync(new VmConsoleOptions { Prefix = "default" }, None);

        rig.Time.Advance(TimeSpan.FromSeconds(31));
        await Task.Delay(100);
        Assert.Empty(agent.Requests("kill"));
        Assert.True(vm.HasSession(PrefixId.Default));

        agent.SendConsoleBytes(Closed(6, 0, 0));
        await shell.Exited.WaitAsync(Soon);
        await shell.DisposeAsync();
        await WaitUntilAsync(() => vm.Children.All(child => child.Kind == VmChildKind.Session), "the console child gone");
        rig.Time.Advance(TimeSpan.FromSeconds(31));
        await WaitUntilAsync(() => agent.Requests("kill").Count == 1, "the session killed once the console closed");
    }

    [Fact]
    public async Task An_agent_error_of_the_guest_becomes_an_agent_error()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine =>
        {
            ScriptWineGuest(engine);
            engine.Agent!.On("prefix-ensure", _ => """{"ok":false,"error":"prefix-failed","message":"prefix-admin ensure default failed: disk full"}""");
        };
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));

        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunProgramAsync(User, Run(), None));

        Assert.Equal(X86ErrorCodes.AgentError, error.Code);
        Assert.Contains("disk full", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_and_too_large_programs_are_refused_before_any_vm()
    {
        await using var rig = new VmTestRig();

        X86Exception dos = await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunProgramAsync(User, Run("old.exe", TestPrograms.Dos()), None));
        X86Exception big = await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunProgramAsync(User, Run(content: new byte[ProgramRunOptions.MaxContentBytes + 1]), None));

        Assert.Equal(X86ErrorCodes.ProgramNotSupported, dos.Code);
        Assert.Contains("work item 11", dos.Message, StringComparison.Ordinal);
        Assert.Equal(X86ErrorCodes.ProgramNotSupported, big.Code);
        Assert.Empty(await rig.Manager.ListAsync(User, None));
        Assert.Empty(rig.Engines.Created);
    }

    [Theory]
    [InlineData(ProgramKind.Dos, null, ProgramSubsystem.Unknown, false, "work item 11")]
    [InlineData(ProgramKind.Linux, 32, ProgramSubsystem.Unknown, false, "x86 shell")]
    [InlineData(ProgramKind.WindowsInstaller, null, ProgramSubsystem.Unknown, false, "work item 13")]
    [InlineData(ProgramKind.Windows, 32, ProgramSubsystem.Console, true, "library")]
    [InlineData(ProgramKind.Windows, 64, ProgramSubsystem.Console, false, "32-bit")]
    [InlineData(ProgramKind.Windows, 16, ProgramSubsystem.Unknown, false, "16-bit")]
    [InlineData(ProgramKind.Windows, 32, ProgramSubsystem.Windowed, false, "windowed")]
    [InlineData(ProgramKind.Windows, 32, ProgramSubsystem.Other, false, "not for the console")]
    [InlineData(ProgramKind.Unknown, null, ProgramSubsystem.Unknown, false, "not a program")]
    public void What_cannot_run_yet_says_why(ProgramKind kind, int? bits, ProgramSubsystem subsystem, bool library, string reason)
    {
        var detection = new ProgramDetection(kind, ProgramMimeTypes.Unknown, ProgramDetectionSource.Header, "x i386") { Bits = bits, Subsystem = subsystem, IsLibrary = library, IsComplete = true };

        Assert.Contains(reason, VmManager.WhyNotSupported(detection), StringComparison.Ordinal);
    }

    [Fact]
    public void A_console_program_for_i386_and_a_batch_file_can_run()
    {
        Assert.Null(VmManager.WhyNotSupported(OsX86.Programs.ProgramDetector.Detect(TestPrograms.Pe(), ".exe")));
        Assert.Null(VmManager.WhyNotSupported(OsX86.Programs.ProgramDetector.Detect(TestPrograms.Pe(dotNet: true), ".exe")));
        Assert.Null(VmManager.WhyNotSupported(OsX86.Programs.ProgramDetector.Detect("echo"u8.ToArray(), ".bat")));
        Assert.Contains("refused", VmManager.WhyNotSupported(OsX86.Programs.ProgramDetector.Detect(TestPrograms.Elf(), ".exe")), StringComparison.Ordinal);
        Assert.Contains("not for an x86", VmManager.WhyNotSupported(OsX86.Programs.ProgramDetector.Detect(TestPrograms.Pe(machine: 0xaa64), ".exe")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_vm_is_the_users_compatible_one_the_running_one_first_and_a_new_one_gets_a_free_name()
    {
        await using var rig = new VmTestRig();
        await rig.CreateAsync("windows");
        (VmRecord made, bool created) = await rig.Manager.ChooseVmAsync(User, VmKind.WindowsPrograms, None);
        Assert.True(created);
        Assert.Equal("windows-2", made.Name);

        (VmRecord again, bool createdAgain) = await rig.Manager.ChooseVmAsync(User, VmKind.WindowsPrograms, None);
        Assert.False(createdAgain);
        Assert.Equal(made.Id, again.Id);
        Assert.Equal(VmKind.Linux, (await rig.Manager.ChooseVmAsync(User, VmKind.Linux, None)).Vm.Profile.Kind);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.ChooseVmAsync(User, VmKind.Dos, None))).Code);
    }

    [Fact]
    public async Task A_prefix_is_the_named_one_or_the_default_one_recorded_on_first_use()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync("w", BuiltInProfiles.Wine);

        Assert.Equal(PrefixId.Default, await rig.Manager.ResolvePrefixAsync(User, vm.Id, null, None));
        Assert.Equal(PrefixId.Default, Assert.Single((await rig.Manager.Registry.FindAsync(User, vm.Id, None))!.EffectivePrefixes).Id);
        Assert.Equal(X86ErrorCodes.PrefixNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.ResolvePrefixAsync(User, vm.Id, "work", None))).Code);
        Assert.Equal(X86ErrorCodes.InvalidName, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.ResolvePrefixAsync(User, vm.Id, "Bad Name", None))).Code);
        Assert.False(await rig.Manager.CreatePrefixAsync(User, vm.Id, new PrefixId("work"), None)); // stopped: made at first use
        Assert.Equal("work", (await rig.Manager.ResolvePrefixAsync(User, vm.Id, "work", None)).Value);
        Assert.Equal(X86ErrorCodes.PrefixExists, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.CreatePrefixAsync(User, vm.Id, new PrefixId("work"), None))).Code);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.ResolvePrefixAsync(User, Guid.NewGuid(), null, None))).Code);

        VmInstance alpine = await rig.CreateAsync("a");
        Assert.Equal("test", (await rig.Manager.ResolvePrefixAsync(User, alpine.Id, null, None)).Value); // the profile's prefix is the default one
    }

    [Fact]
    public async Task A_prefix_deleted_while_stopped_is_deleted_in_the_guest_at_the_next_start()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptWineGuest(engine);
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));
        VmInstance vm = await rig.CreateAsync("w", BuiltInProfiles.Wine);
        await rig.Manager.CreatePrefixAsync(User, vm.Id, new PrefixId("work"), None);

        Assert.False(await rig.Manager.DeletePrefixAsync(User, vm.Id, new PrefixId("work"), force: false, None));
        VmRecord record = (await rig.Manager.Registry.FindAsync(User, vm.Id, None))!;
        Assert.Empty(record.EffectivePrefixes);
        Assert.Equal("work", Assert.Single(record.EffectivePendingPrefixDeletions).Value);
        Assert.Equal(X86ErrorCodes.PrefixNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.DeletePrefixAsync(User, vm.Id, new PrefixId("work"), false, None))).Code);

        IVmConsole console = await rig.Manager.RunProgramAsync(User, Run(vm: vm.Id), None);
        SimulatedAgentChannel agent = rig.Engines.Last.Agent!;
        Assert.Equal("work", agent.Request("prefix-delete").GetProperty("prefix").GetString());
        Assert.True(agent.Sent.ToList().FindIndex(line => line.Contains("prefix-delete", StringComparison.Ordinal)) < agent.Sent.ToList().FindIndex(line => line.Contains("prefix-ensure", StringComparison.Ordinal)));
        Assert.Empty((await rig.Manager.Registry.FindAsync(User, vm.Id, None))!.EffectivePendingPrefixDeletions);
        await console.DisposeAsync();
    }

    [Fact]
    public async Task A_prefix_of_a_running_vm_is_made_and_deleted_in_the_guest_at_once_and_its_programs_need_force()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptWineGuest(engine);
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));
        VmInstance vm = await rig.CreateAsync("w", BuiltInProfiles.Wine);
        IVmConsole console = await rig.Manager.RunProgramAsync(User, Run(vm: vm.Id), None);
        SimulatedAgentChannel agent = rig.Engines.Last.Agent!;

        Assert.True(await rig.Manager.CreatePrefixAsync(User, vm.Id, new PrefixId("work"), None));
        Assert.Contains(agent.Requests("prefix-ensure"), r => r.GetProperty("prefix").GetString() == "work");
        IReadOnlyList<PrefixState> states = await rig.Manager.ListPrefixesAsync(User, vm.Id, None);
        Assert.Equal(["default", "work"], states.Select(s => s.Prefix.Id.Value));
        Assert.True(states[0].IsDefault);
        Assert.True(states[0].InGuest);
        Assert.False(states[1].InGuest);
        Assert.True(states[0].HasSession);
        Assert.Equal(1, states[0].Programs);

        X86Exception busy = await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.DeletePrefixAsync(User, vm.Id, PrefixId.Default, force: false, None));
        Assert.Equal(X86ErrorCodes.VmBusy, busy.Code);
        Assert.True(await rig.Manager.DeletePrefixAsync(User, vm.Id, PrefixId.Default, force: true, None));
        Assert.Equal("default", agent.Request("prefix-delete").GetProperty("prefix").GetString());
        Assert.False(vm.HasSession(PrefixId.Default));
        Assert.True(await rig.Manager.DeletePrefixAsync(User, vm.Id, new PrefixId("work"), force: false, None));
        Assert.Empty((await rig.Manager.Registry.FindAsync(User, vm.Id, None))!.EffectivePendingPrefixDeletions);
        await console.DisposeAsync();
    }

    [Fact]
    public async Task A_stop_flushes_the_guest_disks_first()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptWineGuest(engine);
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));
        VmInstance vm = await rig.CreateAsync("w", BuiltInProfiles.Wine);
        await vm.HoldAsync(None);
        SimulatedAgentChannel agent = rig.Engines.Last.Agent!;

        await vm.StopAsync(force: false, None);

        Assert.Single(agent.Requests("sync"));
        Assert.Null(await vm.ListGuestPrefixesAsync(None));
        Assert.False(await vm.MaterializePrefixAsync(PrefixId.Default, None));
        Assert.False(await vm.DeleteGuestPrefixAsync(PrefixId.Default, None));
    }

    [Fact]
    public async Task An_agent_of_protocol_1_runs_with_the_users_its_image_ships()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine =>
        {
            ScriptConsole(engine, number: 4);
            engine.Agent!.On("exec-in-prefix", _ => """{"ok":true,"code":1,"signal":0,"timedOut":false,"truncated":false,"output":"","ms":9}""");
        };
        VmInstance vm = await rig.CreateAsync();

        IVmConsole console = await vm.OpenConsoleAsync(new VmConsoleOptions { Prefix = "test" }, None);

        Assert.Empty(rig.Engines.Last.Agent!.Requests("prefix-ensure"));
        PrefixState only = Assert.Single(await rig.Manager.ListPrefixesAsync(User, vm.Id, None));
        Assert.Null(only.InGuest); // an agent of protocol 1 cannot list its prefixes
        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunProgramAsync(User, Run(vm: vm.Id, prefix: "test"), None));
        Assert.Equal(X86ErrorCodes.AgentError, error.Code);
        await console.DisposeAsync();
        await vm.StopAsync(force: true, None);
        Assert.Empty(rig.Engines.Last.Agent!.Requests("sync"));
    }

    [Fact]
    public void A_file_name_is_made_safe_for_the_guest()
    {
        Assert.Equal("hello.exe", PreparedProgram.SafeFileName("/home/me/hello.exe"));
        Assert.Equal("my app.exe", PreparedProgram.SafeFileName(@"C:\dir\my app.exe"));
        Assert.Equal("a_b_.exe", PreparedProgram.SafeFileName("a$b'.exe"));
        Assert.Equal("hidden.exe", PreparedProgram.SafeFileName("..hidden.exe"));
        Assert.Equal("program", PreparedProgram.SafeFileName("dir/.."));
        string longName = PreparedProgram.SafeFileName(new string('x', 150) + ".exe");
        Assert.Equal(100, longName.Length);
        Assert.EndsWith(".exe", longName, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => PreparedProgram.SafeFileName(" "));
    }

    [Fact]
    public void A_prefix_identifier_is_checked_and_written_as_its_name()
    {
        Assert.Equal("osx86-work", new PrefixId("work").GuestUser);
        Assert.Equal("default", PrefixId.Default.ToString());
        Assert.Equal("default", default(PrefixId).Value);
        Assert.Throws<ArgumentException>(() => new PrefixId("Work"));
        Assert.False(PrefixId.TryParse("way-too-long-for-a-prefix", out _));
        Assert.False(PrefixId.TryParse(null, out _));
        var prefix = new Prefix(new PrefixId("a-1"), DateTimeOffset.UnixEpoch);
        string json = JsonSerializer.Serialize(new VmRecord { Id = Guid.NewGuid(), Name = "x", Created = DateTimeOffset.UnixEpoch, Profile = BuiltInProfiles.Wine, Prefixes = [prefix], PendingPrefixDeletions = [new PrefixId("old")] }, VmJsonContext.Indented.VmRecord);
        Assert.Contains("\"id\": \"a-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"old\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registry_keeps_prefixes_and_refuses_a_duplicate_or_an_invalid_one()
    {
        var store = new InMemoryVmRegistryStore();
        var registry = new VmRegistry(store);
        VmRecord vm = await registry.CreateAsync(User, "w", BuiltInProfiles.Wine, None);
        await registry.AddPrefixAsync(User, vm.Id, new PrefixId("work"), ifMissing: false, None);
        await registry.AddPrefixAsync(User, vm.Id, new PrefixId("work"), ifMissing: true, None);
        await registry.RenameAsync(User, vm.Id, "renamed", None);

        VmRecord read = (await new VmRegistry(store).FindAsync(User, vm.Id, None))!;
        Assert.Equal("work", Assert.Single(read.EffectivePrefixes).Id.Value);
        Assert.True(read.KnowsPrefix(PrefixId.Default));
        Assert.False(read.KnowsPrefix(new PrefixId("other")));

        string json = (await store.ReadAsync(User, None))!;
        string duplicated = json.Replace("\"id\": \"work\"", "\"id\": \"work\" }, { \"id\": \"work\", \"created\": \"2026-11-16T00:00:00+00:00\"", StringComparison.Ordinal);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => VmRegistryDocument.Parse(duplicated, User)).Code);
        string invalid = json.Replace("\"id\": \"work\"", "\"id\": \"Not Valid\"", StringComparison.Ordinal);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => VmRegistryDocument.Parse(invalid, User)).Code);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await registry.AddPrefixAsync(User, Guid.NewGuid(), PrefixId.Default, true, None))).Code);
    }

    [Fact]
    public void The_options_of_the_sessions_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (FastOptions with { PrefixSessionGrace = TimeSpan.FromSeconds(-1) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (FastOptions with { PrefixSessionStartTimeout = TimeSpan.Zero }).Validate());
        Assert.Equal(TimeSpan.FromSeconds(30), new VmManagerOptions().PrefixSessionGrace);
    }
}
