using OsX86.Agent;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Prefixes;
using OsX86.Profiles;
using OsX86.Tests.Agent;
using OsX86.Tests.Programs;
using OsX86.Tests.Vm;
using OsX86.Vm;
using static OsX86.Tests.X86CommandTests;

namespace OsX86.Tests;

/// <summary>The commands of work item 10: <c>x86 prefix</c>, <c>x86 detect</c>, <c>x86 run</c>, their output and their codes.</summary>
[Trait("Category", "unit")]
[Collection(X86CommandCollection.Name)]
public sealed class X86ProgramCommandTests
{
    private static void ScriptWine(FakeEngine engine, int console = 5)
    {
        SimulatedAgentChannel agent = engine.Agent!;
        agent.On("ping", _ => """{"ok":true,"version":2,"uptime":3,"rssKb":170,"launches":0,"consoles":0,"cgroups":true}""");
        agent.On("prefix-ensure", request => $$"""{"ok":true,"created":true,"prefix":"{{request.GetProperty("prefix").GetString()}}","uid":2000,"home":"/home/osx86-{{request.GetProperty("prefix").GetString()}}","ms":4100}""");
        agent.On("prefix-delete", _ => """{"ok":true,"deleted":true,"ms":80}""");
        agent.On("prefix-list", _ => """{"ok":true,"prefixes":[{"prefix":"default","uid":2000,"home":"/home/osx86-default"}],"admin":true}""");
        agent.On("put-file", _ => """{"ok":true,"size":1,"ms":3}""");
        agent.On("sync", _ => """{"ok":true,"timedOut":false,"ms":12}""");
        agent.On("kill", _ => """{"ok":true,"forced":false,"ms":5}""");
        agent.On("launch", request => $$"""{"ok":true,"launch":"{{request.GetProperty("prefix").GetString()}}/1","pid":90,"uid":2000,"cgroup":"/x","ms":4}""");
        agent.On("exec-in-prefix", request => request.GetProperty("program").GetString() == "stat"
            ? """{"ok":true,"code":1,"signal":0,"timedOut":false,"truncated":false,"output":"","ms":9}"""
            : """{"ok":true,"code":0,"signal":0,"timedOut":false,"truncated":false,"output":"","ms":3000}""");
        agent.On("open-console", request =>
        {
            // The program prints its line, then ends with code 7, as the wine image's test program does.
            agent.SendConsoleBytes(ConsoleFrame.EncodeData(console, "osx86-hello\u001b[1Cuser=osx86-default\r\n"u8));
            agent.SendConsoleBytes(VmTestRig.Closed(console, 7, 0));
            return $$"""{"ok":true,"launch":"{{request.GetProperty("prefix").GetString()}}/{{console}}","console":{{console}},"pid":44,"uid":2000,"cols":80,"rows":25}""";
        });
        agent.On("close-console", _ => $$"""{"ok":true,"console":{{console}},"keptCount":0,"kept":null}""");
    }

    private static VmTestRig WineRig()
    {
        var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptWine(engine);
        rig.Images.Add("wine", "0.1.0", new BootImage("k", "i", "console=ttyS0"));
        return rig;
    }

    [Fact]
    public async Task Detect_prints_the_media_type_the_detail_and_whether_x86_run_takes_it()
    {
        await using var rig = new VmTestRig();
        var files = new FakeFiles();
        files.Binaries["hello.exe"] = TestPrograms.Pe();
        files.Binaries["old.exe"] = TestPrograms.Dos();

        var (code, output, _) = await RunAsync(rig, files, ["detect", "hello.exe"]);
        Assert.Equal(0, code);
        Assert.Equal(
            "hello.exe: application/vnd.microsoft.portable-executable\n"
            + "  PE32 console i386 (by its header)\n"
            + "  x86 run: runs it in a virtual machine of Windows programs\n",
            output);

        (code, output, _) = await RunAsync(rig, files, ["detect", "old.exe"]);
        Assert.Equal(0, code);
        Assert.Contains("application/x-dosexec", output, StringComparison.Ordinal);
        Assert.Contains("x86 run: runs it in a FreeDOS virtual machine, its screen in the terminal", output, StringComparison.Ordinal);

        var (missing, _, error) = await RunAsync(rig, files, ["detect", "absent.exe"]);
        Assert.Equal(X86ExitCodes.NotFound, missing);
        Assert.Contains("no file absent.exe", error, StringComparison.Ordinal);
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, files, ["detect"])).ExitCode);
    }

    [Theory]
    [InlineData(ProgramDetectionSource.Extension, "by its extension")]
    [InlineData(ProgramDetectionSource.Conflict, "refused")]
    [InlineData(ProgramDetectionSource.None, "neither")]
    public void Detect_says_what_decided(ProgramDetectionSource source, string words)
    {
        var detection = new ProgramDetection(ProgramKind.Unknown, ProgramMimeTypes.Unknown, source, "x");

        Assert.Contains(words, X86Command.FormatDetection("f", detection).ElementAt(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_makes_the_vm_relays_the_program_and_returns_its_code()
    {
        await using VmTestRig rig = WineRig();
        var files = new FakeFiles();
        files.Binaries["hello.exe"] = TestPrograms.Pe();

        var (code, output, error) = await RunAsync(rig, files, ["run", "hello.exe", "--flag", "a b"]);

        Assert.Equal(7, code);
        Assert.Equal("osx86-hello user=osx86-default\n", output);
        Assert.StartsWith("x86: created the virtual machine windows (", error, StringComparison.Ordinal);
        string[] args = [.. rig.Engines.Last.Agent!.Request("open-console").GetProperty("args").EnumerateArray().Select(a => a.GetString()!)];
        Assert.Equal(["--flag", "a b"], args[1..]);

        // A second run takes the same VM and says nothing of it.
        (code, _, error) = await RunAsync(rig, files, ["run", "--vm", "windows", "--prefix", "default", "--cols=100", "hello.exe"]);
        Assert.Equal(7, code);
        Assert.Equal(string.Empty, error);
        Assert.Single(await rig.Manager.ListAsync(VmTestRig.User, CancellationToken.None));
    }

    [Fact]
    public async Task Run_refuses_what_it_cannot_run_a_missing_file_and_a_bad_line()
    {
        await using var rig = new VmTestRig();
        var files = new FakeFiles();
        files.Binaries["tool"] = TestPrograms.Elf();
        files.Binaries["hello.exe"] = TestPrograms.Pe();

        var (code, _, error) = await RunAsync(rig, files, ["run", "tool"]);
        Assert.Equal(X86ExitCodes.Unavailable, code);
        Assert.Contains("tool: this is a Linux program", error, StringComparison.Ordinal);

        (code, _, error) = await RunAsync(rig, files, ["run", "absent.exe"]);
        Assert.Equal(X86ExitCodes.NotFound, code);
        Assert.Contains("no file absent.exe", error, StringComparison.Ordinal);

        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, files, ["run"])).ExitCode);
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, files, ["run", "--bogus", "x", "hello.exe"])).ExitCode);
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, files, ["run", "--vm"])).ExitCode);
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, files, ["run", "--rows", "0", "hello.exe"])).ExitCode);
        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, files, ["run", "--vm", "nope", "hello.exe"])).ExitCode);
        Assert.Empty(rig.Engines.Created);
    }

    [Fact]
    public async Task Run_with_an_unknown_prefix_says_how_to_make_it()
    {
        await using VmTestRig rig = WineRig();
        await rig.CreateAsync("windows", BuiltInProfiles.Wine);
        var files = new FakeFiles();
        files.Binaries["hello.exe"] = TestPrograms.Pe();

        var (code, _, error) = await RunAsync(rig, files, ["run", "--prefix", "work", "hello.exe"]);

        Assert.Equal(X86ExitCodes.NotFound, code);
        Assert.Contains("x86 prefix create work --vm windows", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "p.exe" }, "p.exe", new string[0])]
    [InlineData(new[] { "--vm", "w", "p.exe", "--vm", "x" }, "p.exe", new[] { "--vm", "x" })]
    [InlineData(new[] { "--prefix=work", "--", "--odd.exe", "a" }, "--odd.exe", new[] { "a" })]
    public void Run_takes_options_before_the_program_only(string[] args, string program, string[] arguments)
    {
        Assert.True(X86Command.TryParseRun(args, out _, out string? parsed, out List<string> rest, out _));
        Assert.Equal(program, parsed);
        Assert.Equal(arguments, rest);
    }

    [Fact]
    public async Task Prefix_list_create_delete_and_export()
    {
        await using VmTestRig rig = WineRig();
        VmInstance vm = await rig.CreateAsync("windows", BuiltInProfiles.Wine);
        var files = new FakeFiles();

        var (code, output, _) = await RunAsync(rig, files, ["prefix", "create", "work"]);
        Assert.Equal(0, code);
        Assert.Equal("Created the prefix work in windows; its guest user and files are made when it is first used.\n", output);
        Assert.Equal(X86ExitCodes.Failed, (await RunAsync(rig, files, ["prefix", "create", "work"])).ExitCode);
        Assert.Equal(X86ExitCodes.Failed, (await RunAsync(rig, files, ["prefix", "create", "Not-Valid"])).ExitCode);

        (code, output, _) = await RunAsync(rig, files, ["prefix", "list"]);
        Assert.Equal(0, code);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith($"Prefixes of windows ({vm.Id})", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("PREFIX            DEFAULT  GUEST  SESSION  PROGRAMS  CREATED", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("default           yes      -      -        0         ", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("work              no       -      -        0         ", lines[3], StringComparison.Ordinal);

        await vm.HoldAsync(CancellationToken.None);
        (code, output, _) = await RunAsync(rig, files, ["prefix", "delete", "work", "--vm", "windows"]);
        Assert.Equal(0, code);
        Assert.Equal("Deleted the prefix work of windows; its programs, guest user and files are gone.\n", output);
        (code, output, _) = await RunAsync(rig, files, ["prefix", "create", "later"]);
        Assert.Contains("its guest user and files are ready", output, StringComparison.Ordinal);
        await vm.StopAsync(force: true, CancellationToken.None);
        (code, output, _) = await RunAsync(rig, files, ["prefix", "delete", "later"]);
        Assert.Equal(0, code);
        Assert.Contains("removed when the virtual machine next starts", output, StringComparison.Ordinal);
        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, files, ["prefix", "delete", "later"])).ExitCode);

        var (export, _, error) = await RunAsync(rig, files, ["prefix", "export", "default"]);
        Assert.Equal(X86ExitCodes.Unavailable, export);
        Assert.Equal(X86Command.ExportNotYet + "\n", error);
    }

    [Fact]
    public async Task Prefix_commands_need_a_vm_and_a_known_subcommand()
    {
        await using VmTestRig rig = WineRig();
        var files = new FakeFiles();

        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, files, ["prefix", "list"])).ExitCode);
        await rig.CreateAsync("a");
        await rig.CreateAsync("b");
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, files, ["prefix", "list"])).ExitCode); // several, none for Windows
        await rig.CreateAsync("w", BuiltInProfiles.Wine);
        Assert.Equal(0, (await RunAsync(rig, files, ["prefix", "list"])).ExitCode); // the only one for Windows programs
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, files, ["prefix"])).ExitCode);
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, files, ["prefix", "burn"])).ExitCode);
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, files, ["prefix", "create"])).ExitCode);
    }

    [Fact]
    public async Task Shell_with_a_prefix_needs_it_to_exist()
    {
        await using VmTestRig rig = WineRig();
        await rig.CreateAsync("windows", BuiltInProfiles.Wine);

        var (code, _, error) = await RunAsync(rig, new FakeFiles(), ["shell", "--prefix", "nope"]);

        Assert.Equal(X86ExitCodes.NotFound, code);
        Assert.Contains("x86 prefix create nope", error, StringComparison.Ordinal);
        Assert.Equal(PrefixId.Default, (await rig.Manager.Registry.ListAsync(VmTestRig.User, CancellationToken.None))[0].DefaultPrefix);
    }

    [Fact]
    public async Task The_usage_names_the_new_commands()
    {
        await using var rig = new VmTestRig();

        var (_, output, _) = await RunAsync(rig, null, ["--help"]);

        Assert.Contains("  run [--vm <vm>] [--prefix <p>] <program> [args...]", output, StringComparison.Ordinal);
        Assert.Contains("  detect <file>", output, StringComparison.Ordinal);
        Assert.Contains("  prefix list [--vm <vm>]", output, StringComparison.Ordinal);
    }
}
