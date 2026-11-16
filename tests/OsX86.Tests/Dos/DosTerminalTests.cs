using Kazo.Os.AppSdk;
using OsX86.Dos;
using OsX86.Engine;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Tests.Programs;
using OsX86.Tests.Vm;
using OsX86.Vm;

namespace OsX86.Tests.Dos;

/// <summary>
/// <c>x86 run</c> of a DOS program on a simulated full screen of Kazo's Terminal (work item 11), and
/// the adapter of the 3.4.0 SDK (<see cref="KazoFullScreenTerminal"/>): colours, window, keys.
/// </summary>
[Trait("Category", "unit")]
[Collection(X86CommandCollection.Name)]
public sealed class DosTerminalTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(int ExitCode, string Out, string Err)> RunAsync(VmTestRig rig, X86CommandTests.FakeFiles files, string[] args, IFullScreenTerminalSession? fullScreen, IReadOnlyDictionary<string, string>? environment = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var services = new X86CommandServices(rig.Manager, files);
        X86Command.ServicesFactory = () => services;
        try
        {
            int code = await new X86Command().ExecuteAsync(TestAppContext.Terminal(args, stdout, stderr, fullScreen: fullScreen, environment: environment), None);
            return (code, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
        }
        finally
        {
            X86Command.ServicesFactory = static () => X86Runtime.Services;
        }
    }

    private static X86CommandTests.FakeFiles Files()
    {
        var files = new X86CommandTests.FakeFiles();
        files.Binaries["hello.com"] = [0xB4, 0x4C, 0xCD, 0x21];
        files.Binaries["old.exe"] = TestPrograms.Dos();
        return files;
    }

    private static EngineTextScreen ProgramScreen(string second, int cursorColumn)
    {
        string[] lines = [.. Enumerable.Repeat(new string(' ', 80), 25)];
        lines[0] = "OSX86 DOS TEST".PadRight(80);
        lines[1] = second.PadRight(80);
        byte[] attributes = new byte[80 * 25];
        attributes.AsSpan().Fill(0x07);
        attributes.AsSpan(0, 14).Fill(0x1E);
        return new EngineTextScreen(80, 25, lines, attributes, new EngineTextCursor(1, cursorColumn, true));
    }

    [Fact]
    public async Task X86_run_shows_a_dos_program_types_into_it_and_returns_its_code()
    {
        await using VmTestRig rig = DosRig();
        var screen = new FakeFullScreen();
        Task<(int, string, string)> run = RunAsync(rig, Files(), ["run", "hello.com"], screen, new Dictionary<string, string> { [X86Command.DosStatisticsVariable] = "1" });
        await VmTestRig.WaitUntilAsync(() => rig.Engines.Created.Count == 1 && rig.Engines.Last.State == EngineState.Running, "the DOS VM started");
        FakeEngine engine = rig.Engines.Last;
        await screen.WaitForAsync(frame => FakeFullScreen.Text(frame).Contains("starting the DOS virtual machine dos", StringComparison.Ordinal), "the starting screen");

        engine.Screen = ProgramScreen("READY", 5);
        engine.SendSerialLine("OSX86-START ");
        TerminalScreenFrame shown = await screen.WaitForAsync(frame => FakeFullScreen.Text(frame).StartsWith("OSX86 DOS TEST\nREADY", StringComparison.Ordinal), "the program's screen");
        Assert.StartsWith(VgaColors.Sgr(0x1E, false) + "OSX86 DOS TEST" + VgaColors.Sgr(0x07, false), shown.Lines[0], StringComparison.Ordinal);
        Assert.Equal(new TerminalCursor(5, 1, true), shown.Cursor);
        Assert.Equal(25, shown.Lines.Count);

        screen.Type(new TerminalKeyEvent(TerminalKey.Character, "a"));
        screen.Type(new TerminalKeyEvent(TerminalKey.Escape));
        screen.Type(new TerminalKeyEvent(TerminalKey.Character, "1"));
        screen.Type(new TerminalKeyEvent(TerminalKey.ArrowUp));
        screen.Type(new TerminalKeyEvent(TerminalKey.Character, "c", Control: true));
        byte[] expected = [0x1E, 0x9E, 0x3B, 0xBB, 0xE0, 0x48, 0xE0, 0xC8, 0x1D, 0x2E, 0xAE, 0x9D];
        await VmTestRig.WaitUntilAsync(() => engine.Scancodes.SequenceEqual(expected), "a, F1 by the chord, Up, Ctrl+C");
        engine.Screen = ProgramScreen("READY K=1E61 K=3B00 K=4800 CTRL-C", 0);
        await screen.WaitForAsync(frame => FakeFullScreen.Text(frame).Contains("CTRL-C", StringComparison.Ordinal), "the program's answer");
        engine.SendSerialLine("OSX86-EXIT 3 ");

        (int code, string output, string errors) = await run.WaitAsync(VmTestRig.Soon);
        Assert.Equal(3, code);
        Assert.Equal("OSX86 DOS TEST\nREADY K=1E61 K=3B00 K=4800 CTRL-C\n", output);
        Assert.Contains("created the virtual machine dos", errors, StringComparison.Ordinal);
        Assert.Contains("x86: DOS screen:", errors, StringComparison.Ordinal);
        Assert.Equal(1, screen.Enters);
        Assert.Equal(1, screen.Leaves);
        Assert.False(screen.Active);
        Assert.Equal(VmState.Stopped, await DosStateAsync(rig));
    }

    [Fact]
    public async Task A_dos_program_that_turns_graphical_is_stopped_with_its_vm()
    {
        await using VmTestRig rig = DosRig();
        var screen = new FakeFullScreen();
        Task<(int, string, string)> run = RunAsync(rig, Files(), ["run", "old.exe"], screen);
        await VmTestRig.WaitUntilAsync(() => rig.Engines.Created.Count == 1 && rig.Engines.Last.State == EngineState.Running, "the DOS VM started");
        FakeEngine engine = rig.Engines.Last;
        engine.SendSerialLine("OSX86-START");
        await screen.WaitForAsync(frame => frame.Lines.Count == 25 && FakeFullScreen.Text(frame).Trim().Length == 0, "the program's (blank) screen");

        engine.RaiseScreenSize(320, 200, 8);

        (int code, string output, string errors) = await run.WaitAsync(VmTestRig.Soon);
        Assert.Equal(X86ExitCodes.Unavailable, code);
        Assert.Empty(output);
        Assert.Contains("switched to a graphical mode (320x200, 8 bits per pixel)", errors, StringComparison.Ordinal);
        Assert.Contains("BoxedWine", errors, StringComparison.Ordinal);
        Assert.False(screen.Active);
        Assert.Equal(VmState.Stopped, await DosStateAsync(rig));
    }

    [Fact]
    public async Task A_vm_that_stops_under_the_program_is_reported()
    {
        await using VmTestRig rig = DosRig();
        var screen = new FakeFullScreen();
        Task<(int, string, string)> run = RunAsync(rig, Files(), ["run", "hello.com"], screen);
        await VmTestRig.WaitUntilAsync(() => rig.Engines.Created.Count == 1 && rig.Engines.Last.State == EngineState.Running, "the DOS VM started");
        rig.Engines.Last.SendSerialLine("OSX86-START");
        await screen.WaitForAsync(frame => frame.Lines.Count == 25, "the program's screen");

        rig.Engines.Last.Crash();

        (int code, _, string errors) = await run.WaitAsync(VmTestRig.Soon);
        Assert.Equal(X86ExitCodes.Failed, code);
        Assert.Contains("did not end normally", errors, StringComparison.Ordinal);
        Assert.False(screen.Active);
    }

    [Fact]
    public async Task A_dos_program_needs_the_full_screen_and_takes_no_console_options()
    {
        await using VmTestRig rig = DosRig();

        (int code, _, string errors) = await RunAsync(rig, Files(), ["run", "hello.com"], fullScreen: null);
        Assert.Equal(X86ExitCodes.Unavailable, code);
        Assert.Contains("full screen of Kazo's Terminal", errors, StringComparison.Ordinal);
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, Files(), ["run", "--prefix", "p", "hello.com"], new FakeFullScreen())).ExitCode);
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, Files(), ["run", "--cols", "100", "hello.com"], new FakeFullScreen())).ExitCode);
        Assert.Empty(rig.Engines.Created);
        Assert.Empty(await rig.Manager.ListAsync(VmTestRig.User, None));
    }

    [Fact]
    public async Task A_named_vm_that_is_not_dos_is_refused()
    {
        await using VmTestRig rig = DosRig();
        await rig.CreateAsync("alpine");

        (int code, _, string errors) = await RunAsync(rig, Files(), ["run", "--vm", "alpine", "hello.com"], new FakeFullScreen());

        Assert.Equal(X86ExitCodes.Unavailable, code);
        Assert.Contains("is not a DOS one", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_carries_the_colours_and_follows_the_cursor_in_a_small_terminal()
    {
        var terminal = new KazoFullScreenTerminal(new FakeFullScreen(), "label");
        EngineTextScreen top = ProgramScreen("READY", 2);

        TerminalScreenFrame whole = terminal.Frame(top, [], new TerminalViewport(80, 25));
        Assert.Equal(25, whole.Lines.Count);
        Assert.Equal("label", whole.AccessibleLabel);
        Assert.Equal(VgaColors.Sgr(0x1E, false) + "OSX86 DOS TEST" + VgaColors.Sgr(0x07, false), whole.Lines[0][..(whole.Lines[0].IndexOf(VgaColors.Sgr(0x07, false), StringComparison.Ordinal) + VgaColors.Sgr(0x07, false).Length)]);

        TerminalScreenFrame small = terminal.Frame(top, [], new TerminalViewport(80, 24));
        Assert.Equal(24, small.Lines.Count);
        Assert.Contains("OSX86 DOS TEST", small.Lines[0], StringComparison.Ordinal);
        Assert.Equal(new TerminalCursor(2, 1, true), small.Cursor);

        EngineTextScreen bottom = top with { Cursor = new EngineTextCursor(24, 3, true) };
        TerminalScreenFrame scrolled = terminal.Frame(bottom, [], new TerminalViewport(80, 24));
        Assert.Contains("READY", scrolled.Lines[0], StringComparison.Ordinal);
        Assert.Equal(new TerminalCursor(3, 23, true), scrolled.Cursor);

        Assert.False(terminal.Frame(top with { Cursor = null }, [], new TerminalViewport(80, 25)).Cursor.Visible);
        Assert.False(terminal.Frame(top with { Cursor = new EngineTextCursor(1, 79, true) }, [], new TerminalViewport(40, 25)).Cursor.Visible);
    }

    [Fact]
    public void Only_changed_rows_are_formatted_again()
    {
        var terminal = new KazoFullScreenTerminal(new FakeFullScreen(), "label");
        EngineTextScreen first = ProgramScreen("READY", 2);
        terminal.Frame(first, [.. Enumerable.Range(0, 25)], new TerminalViewport(80, 25));

        TerminalScreenFrame stale = terminal.Frame(ProgramScreen("CHANGED", 2), [], new TerminalViewport(80, 25));
        TerminalScreenFrame fresh = terminal.Frame(ProgramScreen("CHANGED", 2), [1], new TerminalViewport(80, 25));

        Assert.Contains("READY", stale.Lines[1], StringComparison.Ordinal);
        Assert.Contains("CHANGED", fresh.Lines[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x1E, false, 14, 1)]
    [InlineData(0x9E, false, 14, 9)]
    [InlineData(0x9E, true, 14, 1)]
    [InlineData(0x70, true, 0, 7)]
    public void Attributes_split_into_vga_colours(byte attribute, bool blink, int foreground, int background)
    {
        Assert.Equal((foreground, background), VgaColors.Split(attribute, blink));
    }

    [Fact]
    public void The_sgr_of_an_attribute_is_the_vga_palette_in_24_bits()
    {
        Assert.Equal("\u001b[38;2;255;255;85;48;2;0;0;170m", VgaColors.Sgr(0x1E, false));
        Assert.Equal("\u001b[38;2;170;170;170;48;2;0;0;0m", VgaColors.Sgr(0x07, true));
        var screen = new EngineTextScreen(3, 1, ["a\u0001b"], [0x07, 0x07, 0x70]);
        Assert.Equal(VgaColors.Sgr(0x07, false) + "a " + VgaColors.Sgr(0x70, false) + "b", VgaColors.FormatRow(screen, 0));
        Assert.Equal(string.Empty, VgaColors.FormatRow(screen, 4));
    }

    [Fact]
    public void The_keys_of_kazos_terminal_become_guest_keys()
    {
        Assert.Equal([GuestKey.Of('a')], KazoFullScreenTerminal.ToGuestKeys(new TerminalKeyEvent(TerminalKey.Character, "a")));
        Assert.Equal([GuestKey.Of('c', control: true)], KazoFullScreenTerminal.ToGuestKeys(new TerminalKeyEvent(TerminalKey.Character, "c", Control: true)));
        Assert.Equal([GuestKey.Of('x', alt: true)], KazoFullScreenTerminal.ToGuestKeys(new TerminalKeyEvent(TerminalKey.Character, "x", Alt: true)));
        Assert.Equal([new GuestKey(GuestKeyCode.Tab, Shift: true)], KazoFullScreenTerminal.ToGuestKeys(new TerminalKeyEvent(TerminalKey.Character, "\t", Shift: true)));
        Assert.Equal([GuestKey.Of('h'), GuestKey.Of('i')], KazoFullScreenTerminal.ToGuestKeys(new TerminalKeyEvent(TerminalKey.Character, "hié")));
        Assert.Empty(KazoFullScreenTerminal.ToGuestKeys(new TerminalKeyEvent(TerminalKey.Character, null)));
        Assert.Empty(KazoFullScreenTerminal.ToGuestKeys(new TerminalKeyEvent(TerminalKey.Resize)));
        foreach ((TerminalKey key, GuestKeyCode code) in new[]
        {
            (TerminalKey.Enter, GuestKeyCode.Enter), (TerminalKey.Backspace, GuestKeyCode.Backspace), (TerminalKey.Delete, GuestKeyCode.Delete),
            (TerminalKey.ArrowLeft, GuestKeyCode.Left), (TerminalKey.ArrowRight, GuestKeyCode.Right), (TerminalKey.ArrowUp, GuestKeyCode.Up),
            (TerminalKey.ArrowDown, GuestKeyCode.Down), (TerminalKey.Home, GuestKeyCode.Home), (TerminalKey.End, GuestKeyCode.End),
            (TerminalKey.PageUp, GuestKeyCode.PageUp), (TerminalKey.PageDown, GuestKeyCode.PageDown), (TerminalKey.Escape, GuestKeyCode.Escape),
        })
        {
            Assert.Equal([new GuestKey(code)], KazoFullScreenTerminal.ToGuestKeys(new TerminalKeyEvent(key)));
        }
    }

    [Fact]
    public void The_refresh_measures_give_median_p90_and_max()
    {
        var statistics = new DosRefreshStatistics();
        foreach (int value in Enumerable.Range(1, 10))
        {
            statistics.Record(value, value % 2 == 0 ? value : null, 1, value == 10 ? 50 : null);
        }

        Assert.Equal(10, statistics.Polls);
        Assert.Equal(5, statistics.Frames);
        Assert.Equal(10, statistics.RowsChanged);
        Assert.Equal("5.0/9.0/10.0", DosRefreshStatistics.Describe(statistics.Samples.Reads));
        Assert.Equal("-", DosRefreshStatistics.Describe([]));
        Assert.Contains("key to screen 50.0/50.0/50.0", statistics.Summary(), StringComparison.Ordinal);
        Assert.Equal(["a", string.Empty, "b"], X86Command.LastScreenLines(new EngineTextScreen(1, 4, ["a ", " ", "b", "  "])));
        Assert.Empty(X86Command.LastScreenLines(null));
    }

    private static VmTestRig DosRig() => DosRunTests.DosRig();

    private static async Task<VmState> DosStateAsync(VmTestRig rig) =>
        (await rig.Manager.ListAsync(VmTestRig.User, None)).Single(vm => vm.Name == "dos").State;
}
