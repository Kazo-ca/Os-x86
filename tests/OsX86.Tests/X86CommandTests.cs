using System.Text;
using OsX86.Agent;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Profiles;
using OsX86.Tests.Vm;
using OsX86.Vm;

namespace OsX86.Tests;

/// <summary>The <c>x86</c> command on a VM manager with simulated engines: parsing, output and exit code of each subcommand.</summary>
[Trait("Category", "unit")]
[Collection(X86CommandCollection.Name)]
public sealed class X86CommandTests
{
    private static readonly string Header = "ID".PadRight(38) + "KIND".PadRight(18) + "STATE".PadRight(10) + "MEMORY  " + "IMAGE".PadRight(22) + "NAME";

    internal sealed class FakeFiles : IX86Files
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Binaries { get; } = new(StringComparer.Ordinal);

        public ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken) => ValueTask.FromResult(Files.GetValueOrDefault(path));

        public ValueTask<byte[]?> ReadBytesAsync(string path, int maxBytes, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Binaries.TryGetValue(path, out byte[]? bytes) ? bytes[..Math.Min(bytes.Length, maxBytes)] : null);

        public ValueTask WriteTextAsync(string path, string text, CancellationToken cancellationToken)
        {
            Files[path] = text;
            return ValueTask.CompletedTask;
        }
    }

    internal static async Task<(int ExitCode, string Out, string Err)> RunAsync(VmTestRig rig, FakeFiles? files, string[] args, TextReader? stdin = null, string userId = TestAppContext.DefaultUserId, CancellationToken cancellationToken = default, CancellationToken process = default)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var services = new X86CommandServices(rig.Manager, files ?? new FakeFiles());
        X86Command.ServicesFactory = () => services;
        try
        {
            var command = new X86Command();
            int code = await command.ExecuteAsync(TestAppContext.Terminal(args, stdout, stderr, stdin, userId, process), cancellationToken);
            return (code, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            X86Command.ServicesFactory = static () => X86Runtime.Services;
        }
    }

    private static Task<(int ExitCode, string Out, string Err)> RunAsync(VmTestRig rig, params string[] args) => RunAsync(rig, null, args);

    [Fact]
    public async Task Version_and_help_need_no_user()
    {
        await using var rig = new VmTestRig();
        var (code, output, _) = await RunAsync(rig, null, ["--version"], userId: "not-a-guid");
        Assert.Equal(0, code);
        Assert.Equal($"Os-x86 {X86Service.FeatureVersion}\n", output);

        (code, output, _) = await RunAsync(rig, null, [], userId: "not-a-guid");
        Assert.Equal(0, code);
        Assert.StartsWith("Usage: x86 <command>\n", output, StringComparison.Ordinal);
        Assert.Contains("shell [--vm <vm>]", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("vm")]
    [InlineData("vm", "explode")]
    [InlineData("vm", "create")]
    [InlineData("vm", "create", "a", "b")]
    [InlineData("vm", "create", "a", "--size", "3")]
    [InlineData("vm", "create", "a", "--profile")]
    [InlineData("vm", "stop", "a", "--force=yes")]
    [InlineData("profile")]
    [InlineData("profile", "burn")]
    [InlineData("profile", "show")]
    [InlineData("shell", "extra")]
    [InlineData("shell", "--cols", "0")]
    [InlineData("shell", "--rows", "many")]
    public async Task A_wrong_command_line_fails_with_usage_on_stderr(params string[] args)
    {
        await using var rig = new VmTestRig();
        var (code, output, error) = await RunAsync(rig, args);
        Assert.Equal(X86ExitCodes.Usage, code);
        Assert.Equal(string.Empty, output);
        Assert.StartsWith("x86: ", error, StringComparison.Ordinal);
        Assert.Contains("Usage:", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_without_a_stable_identifier_is_refused()
    {
        await using var rig = new VmTestRig();
        var (code, _, error) = await RunAsync(rig, null, ["vm", "list"], userId: "user-1");
        Assert.Equal(X86ExitCodes.Failed, code);
        Assert.Contains("stable identifier", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vm_list_says_there_is_none_then_prints_a_stable_table()
    {
        await using var rig = new VmTestRig();
        var (code, output, _) = await RunAsync(rig, "vm", "list");
        Assert.Equal(0, code);
        Assert.Equal("No virtual machine.\n", output);

        VmInstance alpine = await rig.CreateAsync("Alpine");
        VmInstance linuxBase = await rig.CreateAsync("Base", BuiltInProfiles.LinuxBase);
        await rig.CreateAsync("Not mine", user: VmTestRig.OtherUser);
        await alpine.HoldAsync(CancellationToken.None);

        (code, output, _) = await RunAsync(rig, "vm", "list");
        Assert.Equal(0, code);
        Assert.Equal(
            Header + "\n"
            + $"{alpine.Id}  linux             running     128M  alpine-test 0.1.0     Alpine\n"
            + $"{linuxBase.Id}  linux             stopped     256M  linux-base 0.1.0      Base\n",
            output);
    }

    [Fact]
    public async Task Vm_create_uses_the_default_profile_a_named_one_or_a_profile_file()
    {
        await using var rig = new VmTestRig();
        var files = new FakeFiles();
        files.Files["mine.x86profile"] = (BuiltInProfiles.AlpineTest with { Name = "mine", MemoryMiB = 64 }).ToJson();

        // Work item 6b: without --profile, linux-base; the Alpine test image is a named profile.
        var (code, output, _) = await RunAsync(rig, files, ["vm", "create", "Base"]);
        Assert.Equal(0, code);
        VmRecord linuxBase = await rig.Manager.ResolveAsync(VmTestRig.User, "Base", CancellationToken.None);
        Assert.Equal($"Created Base ({linuxBase.Id}) from the profile linux-base.\n", output);
        Assert.Equal(BuiltInProfiles.LinuxBase, linuxBase.Profile);
        Assert.Equal(VmRecord.LayerSetOf(linuxBase.Id), linuxBase.LayerSet);

        (code, _, _) = await RunAsync(rig, files, ["vm", "create", "--profile", "alpine-test", "Alpine"]);
        Assert.Equal(0, code);
        Assert.Equal("alpine-test", (await rig.Manager.ResolveAsync(VmTestRig.User, "alpine", CancellationToken.None)).Profile.Image);

        (code, output, _) = await RunAsync(rig, files, ["vm", "create", "Small", "--profile=mine.x86profile"]);
        Assert.Equal(0, code);
        Assert.Contains("from the profile mine.", output, StringComparison.Ordinal);
        Assert.Equal(64, (await rig.Manager.ResolveAsync(VmTestRig.User, "Small", CancellationToken.None)).Profile.MemoryMiB);

        var (missing, _, error) = await RunAsync(rig, files, ["vm", "create", "X", "--profile", "nope"]);
        Assert.Equal(X86ExitCodes.NotFound, missing);
        Assert.Contains("No profile named 'nope'", error, StringComparison.Ordinal);
        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, files, ["vm", "create", "X", "--profile", "gone.x86profile"])).ExitCode);

        var (duplicate, _, taken) = await RunAsync(rig, files, ["vm", "create", "alpine"]);
        Assert.Equal(X86ExitCodes.Failed, duplicate);
        Assert.Contains("already exists", taken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vm_start_info_stop_rename_and_remove_act_on_the_registry()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync("Alpine");

        var (code, output, _) = await RunAsync(rig, "vm", "start", "alpine");
        Assert.Equal(0, code);
        Assert.Equal("Started Alpine; it keeps running until `x86 vm stop Alpine`.\n", output);

        (code, output, _) = await RunAsync(rig, "vm", "info", vm.Id.ToString());
        Assert.Equal(0, code);
        Assert.Equal(
            "Name:          Alpine\n"
            + $"ID:            {vm.Id}\n"
            + "Kind:          linux\n"
            + "State:         running (started by hand)\n"
            + "Image:         alpine-test 0.1.0\n"
            + "Memory:        128 MiB\n"
            + "Serial port:   declared\n"
            + "Prefix:        test\n"
            + "Created:       2026-11-12T10:00:00Z\n"
            + "Agent:         yes\n"
            + "Applications:  1\n"
            + "Budget:        128 of 512 MiB used by running virtual machines\n"
            + "Children:      0\n",
            output);

        (code, output, _) = await RunAsync(rig, "vm", "rename", "Alpine", "Shell box");
        Assert.Equal(0, code);
        Assert.Equal("Renamed Alpine to Shell box.\n", output);

        Assert.Equal(X86ExitCodes.Busy, (await RunAsync(rig, "vm", "remove", "Shell box")).ExitCode);
        (code, output, _) = await RunAsync(rig, "vm", "stop", "Shell box");
        Assert.Equal(0, code);
        Assert.Equal("Stopped Shell box.\n", output);
        Assert.Equal(VmState.Stopped, vm.State);

        (code, output, _) = await RunAsync(rig, "vm", "remove", "Shell box");
        Assert.Equal(0, code);
        Assert.Equal($"Removed Shell box ({vm.Id}).\n", output);
        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, "vm", "info", "Shell box")).ExitCode);
    }

    [Fact]
    public async Task Vm_stop_refuses_while_applications_hold_it_unless_forced()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync("Alpine");
        using VmApplication shell = await vm.AcquireAsync("shell", CancellationToken.None);

        var (code, _, error) = await RunAsync(rig, "vm", "stop", "Alpine");
        Assert.Equal(X86ExitCodes.Busy, code);
        Assert.Contains("--force", error, StringComparison.Ordinal);

        Assert.Equal(0, (await RunAsync(rig, "vm", "stop", "Alpine", "--force")).ExitCode);
        Assert.Equal(VmState.Stopped, vm.State);

        await vm.HoldAsync(CancellationToken.None);
        Assert.Equal(0, (await RunAsync(rig, "vm", "remove", "--force", "Alpine")).ExitCode);
        Assert.Empty(await rig.Manager.ListAsync(VmTestRig.User, CancellationToken.None));
    }

    [Fact]
    public async Task Vm_info_lists_the_children_and_an_unknown_agent_before_the_first_run()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => VmTestRig.ScriptConsole(engine, 5);
        VmInstance vm = await rig.CreateAsync("Alpine");
        var (_, before, _) = await RunAsync(rig, "vm", "info", "Alpine");
        Assert.Contains("Agent:         unknown until it runs\n", before, StringComparison.Ordinal);

        await using IVmConsole console = await vm.OpenConsoleAsync(new VmConsoleOptions(), CancellationToken.None);
        var (_, output, _) = await RunAsync(rig, "vm", "info", "Alpine");
        Assert.EndsWith("Children:      1\n  test/5  console\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_start_over_the_budget_exits_3_and_names_the_running_vms()
    {
        await using var rig = new VmTestRig(announcedMiB: 512); // budget: 256 MiB
        VmInstance first = await rig.CreateAsync("First");
        VmInstance second = await rig.CreateAsync("Second");
        await rig.CreateAsync("Third");
        await first.HoldAsync(CancellationToken.None);
        await second.HoldAsync(CancellationToken.None);

        var (code, _, error) = await RunAsync(rig, "vm", "start", "Third");

        Assert.Equal(X86ExitCodes.MemoryBudget, code);
        Assert.Contains("would exceed the budget of 256 MiB", error, StringComparison.Ordinal);
        Assert.Contains($"x86:   running First ({first.Id}), 128 MiB  (compatible: same kind and image)\n", error, StringComparison.Ordinal);
        Assert.Contains("x86 vm stop", error, StringComparison.Ordinal);
        Assert.Equal(2, rig.Engines.Created.Count);
    }

    [Fact]
    public async Task A_host_without_emulator_exits_6()
    {
        var store = new InMemoryVmRegistryStore();
        await using var rig = new VmTestRig();
        await using var manager = new VmManager(store, UnavailableEngineFactory.Instance, rig.Images, FixedHostMemory.Unknown, VmTestRig.FastOptions);
        await manager.CreateAsync(VmTestRig.User, "Alpine", BuiltInProfiles.AlpineTest, CancellationToken.None);
        var services = new X86CommandServices(manager, new FakeFiles());
        X86Command.ServicesFactory = () => services;
        try
        {
            var stderr = new StringWriter();
            int code = await new X86Command().ExecuteAsync(TestAppContext.Terminal(["vm", "start", "Alpine"], new StringWriter(), stderr), CancellationToken.None);
            Assert.Equal(X86ExitCodes.Unavailable, code);
            Assert.Contains("no emulator", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            X86Command.ServicesFactory = static () => X86Runtime.Services;
        }
    }

    [Fact]
    public async Task Profile_list_show_import_and_export()
    {
        await using var rig = new VmTestRig();
        var files = new FakeFiles();

        var (code, output, _) = await RunAsync(rig, files, ["profile", "list"]);
        Assert.Equal(0, code);
        Assert.Equal(
            "NAME                  KIND              IMAGE                 MEMORY  ORIGIN\n"
            + "alpine-test           linux             alpine-test 0.1.0       128M  built-in\n"
            + "linux-base            linux             linux-base 0.1.0        256M  built-in\n"
            + "wine                  windows-programs  wine 0.1.0              512M  built-in\n",
            output);

        (code, output, _) = await RunAsync(rig, files, ["profile", "show", "alpine-test"]);
        Assert.Equal(0, code);
        Assert.Equal(BuiltInProfiles.AlpineTest, VmProfile.Parse(output));

        (code, output, _) = await RunAsync(rig, files, ["profile", "export", "alpine-test", "/home/user-1/a"]);
        Assert.Equal(0, code);
        Assert.Equal("Wrote the profile alpine-test to /home/user-1/a.x86profile.\n", output);
        string exported = files.Files["/home/user-1/a.x86profile"];
        Assert.Equal(BuiltInProfiles.AlpineTest, VmProfile.Parse(exported));

        files.Files["work.x86profile"] = (BuiltInProfiles.AlpineTest with { Name = "work", MemoryMiB = 96 }).ToJson();
        (code, output, _) = await RunAsync(rig, files, ["profile", "import", "work.x86profile"]);
        Assert.Equal(0, code);
        Assert.Equal("Imported the profile work.\n", output);
        (_, output, _) = await RunAsync(rig, files, ["profile", "list"]);
        Assert.EndsWith("work                  linux             alpine-test 0.1.0        96M  imported\n", output, StringComparison.Ordinal);
        Assert.Equal(0, (await RunAsync(rig, files, ["vm", "create", "W", "--profile", "work"])).ExitCode);

        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, files, ["profile", "import", "absent.x86profile"])).ExitCode);
        files.Files["bad.x86profile"] = """{"format": 9, "name": "x"}""";
        var (bad, _, error) = await RunAsync(rig, files, ["profile", "import", "bad.x86profile"]);
        Assert.Equal(X86ExitCodes.Failed, bad);
        Assert.Contains("format 9", error, StringComparison.Ordinal);
        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, files, ["profile", "show", "nope"])).ExitCode);
    }

    [Fact]
    public async Task Shell_needs_one_vm_or_a_name()
    {
        await using var rig = new VmTestRig();
        var (code, _, error) = await RunAsync(rig, "shell");
        Assert.Equal(X86ExitCodes.NotFound, code);
        Assert.Contains("x86 vm create", error, StringComparison.Ordinal);

        await rig.CreateAsync("One");
        await rig.CreateAsync("Two");
        (code, _, error) = await RunAsync(rig, "shell");
        Assert.Equal(X86ExitCodes.Usage, code);
        Assert.Contains("--vm", error, StringComparison.Ordinal);
        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, "shell", "--vm", "Three")).ExitCode);
    }

    /// <summary>
    /// The shell of the guest answers each line; the terminal sees the output as text without the
    /// pty's echo or escape sequences; Ctrl+C (a <c>^C</c> line) reaches the guest; the end of input
    /// sends Ctrl+D, the shell's exit code is the command's, and the VM stops after the grace (166 A).
    /// </summary>
    [Fact]
    public async Task Shell_relays_a_guest_shell_line_by_line_and_returns_its_exit_code()
    {
        await using var rig = new VmTestRig();
        var received = new List<byte[]>();
        rig.Engines.Configure = engine =>
        {
            VmTestRig.ScriptConsole(engine, 3);
            SimulatedAgentFixtures.PlayShell(engine.Agent!, 3, received);
        };
        VmInstance vm = await rig.CreateAsync("Alpine");
        var input = new QueueTextReader();
        Task<(int, string, string)> run = RunAsync(rig, null, ["shell", "--cols", "100", "--rows", "30"], input);
        await VmTestRig.WaitUntilAsync(() => rig.Engines.Created.Count == 1 && rig.Engines.Last.Agent!.Requests("open-console").Count == 1, "the console");
        input.Add("echo hi");
        await VmTestRig.WaitUntilAsync(() => Received(received).Count == 1, "the first line");
        input.Add("^C");
        input.Add("exit 7");
        input.End();

        var (code, output, error) = await run.WaitAsync(VmTestRig.Soon);

        Assert.Equal(string.Empty, error);
        Assert.Equal(7, code);
        Assert.Equal("console$ hi\nconsole$ ^C\nconsole$ bye\n", output);
        Assert.Equal(["echo hi\r", "\u0003", "exit 7\r", "\u0004"], Received(received).Select(b => Encoding.UTF8.GetString(b)));
        Assert.Equal(100, rig.Engines.Last.Agent!.Request("open-console").GetProperty("cols").GetInt32());
        Assert.Equal(0, vm.Applications);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        await VmTestRig.WaitUntilAsync(() => vm.State == VmState.Stopped, "the stop after the grace");
    }

    [Fact]
    public async Task Shell_passes_the_commands_interruption_to_the_guest_and_tilde_dot_closes_it()
    {
        await using var rig = new VmTestRig();
        var received = new List<byte[]>();
        rig.Engines.Configure = engine =>
        {
            VmTestRig.ScriptConsole(engine, 3);
            SimulatedAgentFixtures.PlayShell(engine.Agent!, 3, received);
        };
        await rig.CreateAsync("Alpine");
        var input = new QueueTextReader();
        using var interrupt = new CancellationTokenSource();

        VmInstance vm = await rig.Manager.GetInstanceAsync(VmTestRig.User, (await rig.Manager.ResolveAsync(VmTestRig.User, "Alpine", CancellationToken.None)).Id, CancellationToken.None);
        Task<(int, string, string)> run = RunAsync(rig, null, ["shell", "--vm", "Alpine", "--prefix", "test"], input, cancellationToken: interrupt.Token);
        await VmTestRig.WaitUntilAsync(() => vm.Children.Count == 1, "the console");
        await interrupt.CancelAsync(); // Ctrl+C in the Kazo terminal
        await VmTestRig.WaitUntilAsync(() => Received(received).Any(b => b is [0x03]), "Ctrl+C in the guest");
        input.Add("~.");
        var (code, _, _) = await run.WaitAsync(VmTestRig.Soon);

        Assert.Equal(129, code); // the console was closed: SIGHUP
        Assert.Single(rig.Engines.Last.Agent!.Requests("close-console"));
    }

    [Fact]
    public async Task Shell_returns_when_the_guest_exits_even_if_reading_the_terminal_ignores_cancellation()
    {
        await using var rig = new VmTestRig();
        var received = new List<byte[]>();
        rig.Engines.Configure = engine =>
        {
            VmTestRig.ScriptConsole(engine, 3);
            SimulatedAgentFixtures.PlayShell(engine.Agent!, 3, received);
        };
        await rig.CreateAsync("Alpine");

        // "exit 3" is read, then the terminal blocks forever, deaf to cancellation, as a real stdin can.
        Task<(int, string, string)> run = RunAsync(rig, null, ["shell"], new StubbornReader("exit 3"));
        await VmTestRig.WaitUntilAsync(() => Received(received).Any(b => Encoding.UTF8.GetString(b) == "exit 3\r"), "the exit line");
        rig.Engines.Last.Agent!.SendConsoleBytes(VmTestRig.Closed(3, 3, 0)); // the guest shell ends by itself

        var (code, output, _) = await run.WaitAsync(VmTestRig.Soon);

        Assert.Equal(3, code);
        Assert.EndsWith("bye\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shell_closes_the_console_when_the_commands_process_is_stopped()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => VmTestRig.ScriptConsole(engine, 3);
        VmInstance vm = await rig.CreateAsync("Alpine");
        using var process = new CancellationTokenSource();
        Task<(int, string, string)> run = RunAsync(rig, null, ["shell"], new QueueTextReader(), process: process.Token);
        await VmTestRig.WaitUntilAsync(() => vm.Children.Count == 1, "the console");

        await process.CancelAsync(); // the tab is closed: the process ends, not just its command
        var (code, _, _) = await run.WaitAsync(VmTestRig.Soon);

        Assert.Equal(129, code);
        Assert.Single(rig.Engines.Last.Agent!.Requests("close-console"));
        Assert.Equal(0, vm.Applications);
    }

    [Fact]
    public async Task Shell_falls_back_to_the_serial_console_of_an_image_without_agent()
    {
        await using var rig = new VmTestRig(withAgent: false);
        rig.Engines.Configure = engine => engine.SendSerial("\u001b[1mosx86#\u001b[0m ");
        VmInstance vm = await rig.CreateAsync("Plain", VmTestRig.NoAgentSerial);
        var input = new QueueTextReader();
        Task<(int, string, string)> run = RunAsync(rig, null, ["shell"], input);
        await VmTestRig.WaitUntilAsync(() => vm.State == VmState.Running && vm.Applications == 1, "the serial console");
        input.Add("uname");
        await VmTestRig.WaitUntilAsync(() => rig.Engines.Last.SerialWritten.Count == 1, "the line on the serial port");
        input.End();

        var (code, output, _) = await run.WaitAsync(VmTestRig.Soon);

        Assert.Equal(0, code);
        Assert.Equal("osx86# ", output);
        Assert.Equal(["uname\r"], rig.Engines.Last.SerialWritten.Select(b => Encoding.UTF8.GetString(b)));
        Assert.Equal(0, vm.Applications);
    }

    [Fact]
    public async Task Shell_reports_a_vm_that_stops_under_it()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => VmTestRig.ScriptConsole(engine, 3);
        VmInstance vm = await rig.CreateAsync("Alpine");
        var input = new QueueTextReader();
        Task<(int, string, string)> run = RunAsync(rig, null, ["shell"], input);
        await VmTestRig.WaitUntilAsync(() => vm.Children.Count == 1, "the console");

        await vm.StopAsync(force: true, CancellationToken.None);
        var (code, _, error) = await run.WaitAsync(VmTestRig.Soon);

        Assert.Equal(X86ExitCodes.Failed, code);
        Assert.StartsWith("x86: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_exit_codes_follow_the_error_codes()
    {
        Assert.Equal(X86ExitCodes.NotFound, X86Command.ExitCodeOf(new X86Exception(X86ErrorCodes.VmNotFound, "x")));
        Assert.Equal(X86ExitCodes.Busy, X86Command.ExitCodeOf(new X86Exception(X86ErrorCodes.VmBusy, "x")));
        Assert.Equal(X86ExitCodes.Unavailable, X86Command.ExitCodeOf(new X86Exception(X86ErrorCodes.ImageUnavailable, "x")));
        Assert.Equal(X86ExitCodes.Unavailable, X86Command.ExitCodeOf(new X86Exception(X86ErrorCodes.NoConsole, "x")));
        Assert.Equal(X86ExitCodes.Failed, X86Command.ExitCodeOf(new X86Exception(X86ErrorCodes.AgentError, "x")));
        Assert.Equal(X86ExitCodes.MemoryBudget, X86Command.ExitCodeOf(new VmMemoryBudgetExceededException(1, 2, 3, [], [])));
        await Task.CompletedTask;
    }

    private static List<byte[]> Received(List<byte[]> received)
    {
        lock (received)
        {
            return [.. received];
        }
    }
}

/// <summary>The command tests share the static service factory of the command: they never run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class X86CommandCollection
{
    public const string Name = "x86 command";
}

/// <summary>Scripted guest programs for the simulated agent.</summary>
internal static class SimulatedAgentFixtures
{
    /// <summary>
    /// Plays a shell on console <paramref name="number"/>: an OSC title and a prompt at once; each
    /// line is echoed like a pty does (with CR LF); <c>echo X</c> prints X; Ctrl+C prints <c>^C</c>;
    /// <c>exit N</c> prints <c>bye</c> and the next Ctrl+D ends the console with N.
    /// </summary>
    public static void PlayShell(Tests.Agent.SimulatedAgentChannel agent, int number, List<byte[]> received)
    {
        int exitCode = 0;
        var reader = new ConsoleFrameReader();
        void Send(string text) => agent.SendConsoleBytes(ConsoleFrame.EncodeData(number, Encoding.UTF8.GetBytes(text)));
        agent.On("open-console", request =>
        {
            Send("\u001b]0;title\u0007console$ "); // before the reply: the client keeps the frames of a console it is opening
            return $$"""{"ok":true,"launch":"{{request.GetProperty("prefix").GetString()}}/{{number}}","console":{{number}},"pid":44,"uid":1001,"cols":80,"rows":25}""";
        });
        agent.ConsoleWriteHook = bytes =>
        {
            foreach (ConsoleFrame frame in reader.Push(bytes))
            {
                byte[] payload = frame.Payload.ToArray();
                lock (received)
                {
                    received.Add(payload);
                }

                string text = Encoding.UTF8.GetString(payload);
                if (text == "\u0003")
                {
                    Send("^C\r\nconsole$ ");
                }
                else if (text == "\u0004")
                {
                    agent.SendConsoleBytes(VmTestRig.Closed(number, exitCode, 0));
                }
                else if (text.StartsWith("echo ", StringComparison.Ordinal))
                {
                    Send(text.TrimEnd('\r') + "\r\n" + text[5..].TrimEnd('\r') + "\r\nconsole$ ");
                }
                else if (text.StartsWith("exit ", StringComparison.Ordinal))
                {
                    exitCode = int.Parse(text[5..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    Send(text.TrimEnd('\r') + "\r\nbye\r\n");
                }
            }
        };
    }
}

/// <summary>A standard input the test writes lines to while the command runs.</summary>
internal sealed class QueueTextReader : TextReader
{
    private readonly System.Threading.Channels.Channel<string?> _lines = System.Threading.Channels.Channel.CreateUnbounded<string?>();

    public void Add(string line) => _lines.Writer.TryWrite(line);

    public void End() => _lines.Writer.TryWrite(null);

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => await _lines.Reader.ReadAsync(cancellationToken);
}

/// <summary>A standard input that gives a few lines, then blocks forever without honouring cancellation.</summary>
internal sealed class StubbornReader(params string[] lines) : TextReader
{
    private int _next;

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        if (_next < lines.Length)
        {
            return lines[_next++];
        }

        await Task.Delay(Timeout.Infinite, CancellationToken.None);
        return null;
    }
}
