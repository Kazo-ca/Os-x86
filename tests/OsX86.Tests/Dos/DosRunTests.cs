using System.Text.RegularExpressions;
using System.Threading.Channels;
using Kazo.Os.AppSdk;
using OsX86.Dos;
using OsX86.Engine;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Profiles;
using OsX86.Storage;
using OsX86.Tests.Programs;
using OsX86.Tests.Storage;
using OsX86.Tests.Vm;
using OsX86.Vm;

namespace OsX86.Tests.Dos;

/// <summary>
/// The DOS way of work item 11 on simulated engines: the choice of the DOS VM, its start with the
/// program disk and the keyboard and without agent, the markers of COM1, the graphical signal, the
/// stop at the end (166 A), and <c>x86 run</c> of a DOS program on a simulated full screen.
/// </summary>
[Trait("Category", "unit")]
[Collection(X86CommandCollection.Name)]
public sealed partial class DosRunTests
{
    private static readonly Guid User = VmTestRig.User;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly byte[] Program = [0xB4, 0x4C, 0xCD, 0x21]; // mov ah,4Ch / int 21h

    internal static VmTestRig DosRig()
    {
        var rig = new VmTestRig(withAgent: false);
        rig.Images.Add("freedos", "0.2.0", FreeDos());
        return rig;
    }

    internal static BootImage FreeDos()
    {
        Dictionary<string, object?> manifest = TestManifests.ValidObject();
        manifest["image"] = "freedos";
        manifest["version"] = "0.2.0";
        manifest["files"] = new Dictionary<string, object?> { ["disk"] = new Dictionary<string, object?> { ["offset"] = 0, ["size"] = TestManifests.Size, ["rawSize"] = 33554432, ["blockSize"] = 1048576 } };
        manifest["boot"] = new Dictionary<string, object?> { ["kind"] = "dos", ["disks"] = new Dictionary<string, object?> { ["hda"] = "disk" }, ["display"] = "vga-text", ["readyMarker"] = "OSX86-READY", ["memoryMiB"] = 64 };
        return new BootImage(ImageManifest.Parse(TestManifests.Json(manifest)));
    }

    private static DosRunOptions Run(string name = "hello.com", byte[]? content = null, Guid? vm = null, IReadOnlyList<string>? arguments = null) =>
        new() { FileName = name, Content = content ?? Program, VmId = vm, Arguments = arguments ?? [] };

    [Fact]
    public async Task The_dos_vm_is_made_from_the_dos_profile_and_a_running_one_is_never_chosen()
    {
        await using VmTestRig rig = DosRig();

        (VmRecord first, bool created) = await rig.Manager.ChooseDosVmAsync(User, None);
        Assert.True(created);
        Assert.Equal("dos", first.Name);
        Assert.Equal(BuiltInProfiles.Dos, first.Profile);
        (VmRecord again, created) = await rig.Manager.ChooseDosVmAsync(User, None);
        Assert.False(created);
        Assert.Equal(first.Id, again.Id);

        await using DosProgramSession session = await rig.Manager.RunDosProgramAsync(User, Run(vm: first.Id), None);
        (VmRecord second, created) = await rig.Manager.ChooseDosVmAsync(User, None);
        Assert.True(created);
        Assert.Equal("dos-2", second.Name);
    }

    [Fact]
    public async Task A_dos_run_starts_the_vm_with_its_program_disk_and_the_keyboard_and_no_agent()
    {
        await using VmTestRig rig = DosRig();

        await using DosProgramSession session = await rig.Manager.RunDosProgramAsync(User, Run("Tool.exe", TestPrograms.Dos(), arguments: ["a", "b c"]), None);

        FakeEngine engine = rig.Engines.Last;
        EngineStartOptions options = engine.StartedWith!;
        Assert.Equal(EngineBootKind.Dos, options.Kind);
        Assert.True(options.Keyboard);
        Assert.Equal(EngineWriteLayerMode.Persistent, options.Disks["hda"].Layer.Mode);
        Assert.True(options.Disks["hdb"].Source.IsInMemory);
        Assert.Equal(EngineWriteLayerMode.Discard, options.Disks["hdb"].Layer.Mode);
        string disk = System.Text.Encoding.ASCII.GetString(options.Disks["hdb"].Source.Content!.Value.Span);
        Assert.Contains("D:\\TOOL.EXE a \"b c\"", disk, StringComparison.Ordinal);
        Assert.Contains("TOOL    EXE", disk, StringComparison.Ordinal);
        VmInstance instance = await rig.Manager.GetInstanceAsync(User, session.VmId, None);
        Assert.Equal(VmState.Running, instance.State);
        Assert.False(instance.HasAgent);
        Assert.Equal(1, instance.Applications);
        Assert.Equal("dos", session.VmName);
    }

    [Fact]
    public async Task The_markers_of_com1_tell_the_start_and_the_exit_code_and_the_vm_stops_at_once()
    {
        await using VmTestRig rig = DosRig();
        DosProgramSession session = await rig.Manager.RunDosProgramAsync(User, Run(), None);
        FakeEngine engine = rig.Engines.Last;
        VmInstance instance = await rig.Manager.GetInstanceAsync(User, session.VmId, None);

        engine.RaiseScreenSize(720, 400, 8); // the BIOS while FreeDOS boots: not the program's
        engine.SendSerialLine("Booting...");
        engine.SendSerialLine("OSX86-START ");
        await session.Started.WaitAsync(VmTestRig.Soon);
        Assert.False(session.GraphicsModeEntered.IsCompleted);
        engine.SendSerialLine("OSX86-EXIT 42 ");
        Assert.Equal(42, await session.Exited.WaitAsync(VmTestRig.Soon));

        await session.DisposeAsync();
        Assert.Equal(VmState.Stopped, instance.State); // no grace: a DOS VM runs one program per start
        Assert.True(engine.Disposed);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_graphical_mode_after_the_start_is_the_signal_of_217_A()
    {
        await using VmTestRig rig = DosRig();
        await using DosProgramSession session = await rig.Manager.RunDosProgramAsync(User, Run(), None);
        FakeEngine engine = rig.Engines.Last;

        engine.SendSerialLine("OSX86-START");
        await session.Started.WaitAsync(VmTestRig.Soon);
        engine.RaiseScreenSize(80, 25, 0);
        Assert.False(session.GraphicsModeEntered.IsCompleted);
        engine.RaiseScreenSize(320, 200, 8);

        ScreenSizeChangedEventArgs mode = await session.GraphicsModeEntered.WaitAsync(VmTestRig.Soon);
        Assert.Equal(320, mode.Columns);
        Assert.Equal(8, mode.BitsPerPixel);
    }

    [Fact]
    public async Task Keys_and_the_screen_go_through_the_engine_and_alt_is_held()
    {
        await using VmTestRig rig = DosRig();
        await using DosProgramSession session = await rig.Manager.RunDosProgramAsync(User, Run(), None);
        FakeEngine engine = rig.Engines.Last;
        engine.Screen = GuestKeyboardTests.Screen(["HELLO"], [0x1E], new EngineTextCursor(0, 5, true));

        Assert.Equal("HELLO", (await session.ReadScreenAsync(None)).Text);
        await session.SendKeyAsync(GuestKey.Of('a'), None);
        await session.SendKeyAsync(GuestKey.Of('é'), None);
        Task alt = session.SendKeyAsync(GuestKey.Of('x', alt: true), None).AsTask();
        await VmTestRig.WaitUntilAsync(() => engine.Scancodes.Count == 3, "Alt down");
        rig.Time.Advance(TimeSpan.FromMilliseconds(40));
        await VmTestRig.WaitUntilAsync(() => engine.Scancodes.Count == 5, "the X key");
        rig.Time.Advance(TimeSpan.FromMilliseconds(40));
        await alt.WaitAsync(VmTestRig.Soon);

        Assert.Equal(new byte[] { 0x1E, 0x9E, 0x38, 0x2D, 0xAD, 0xB8 }, engine.Scancodes);
    }

    [Fact]
    public async Task A_vm_that_stops_under_the_program_fails_its_end_and_its_reads()
    {
        await using VmTestRig rig = DosRig();
        await using DosProgramSession session = await rig.Manager.RunDosProgramAsync(User, Run(), None);
        FakeEngine engine = rig.Engines.Last;
        engine.SendSerialLine("OSX86-START");
        await session.Started.WaitAsync(VmTestRig.Soon);

        engine.Crash();

        X86Exception ended = await Assert.ThrowsAsync<X86Exception>(async () => await session.Exited.WaitAsync(VmTestRig.Soon));
        Assert.Equal(X86ErrorCodes.ConsoleClosed, ended.Code);
        await VmTestRig.WaitUntilAsync(() => engine.Disposed, "the run torn down");
        Assert.Equal(X86ErrorCodes.ConsoleClosed, (await Assert.ThrowsAsync<X86Exception>(async () => await session.ReadScreenAsync(None))).Code);
        Assert.Equal(X86ErrorCodes.ConsoleClosed, (await Assert.ThrowsAsync<X86Exception>(async () => await session.SendKeyAsync(GuestKey.Of('a'), None))).Code);
    }

    [Fact]
    public async Task What_cannot_be_a_dos_run_is_refused()
    {
        await using VmTestRig rig = DosRig();
        VmInstance linux = await rig.CreateAsync("alpine");
        await using DosProgramSession running = await rig.Manager.RunDosProgramAsync(User, Run(), None);

        Assert.Equal(X86ErrorCodes.VmBusy, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunDosProgramAsync(User, Run(vm: running.VmId), None))).Code);
        Assert.Equal(X86ErrorCodes.ProgramNotSupported, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunDosProgramAsync(User, Run(vm: linux.Id), None))).Code);
        Assert.Equal(X86ErrorCodes.ProgramNotSupported, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunDosProgramAsync(User, Run("hello.exe", TestPrograms.Pe()), None))).Code);
        Assert.Equal(X86ErrorCodes.ProgramNotSupported, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunDosProgramAsync(User, Run(content: new byte[DosProgramDisk.MaxContentBytes + 1]), None))).Code);
        Assert.Equal(X86ErrorCodes.ProgramNotSupported, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunDosProgramAsync(User, Run(arguments: ["a|b"]), None))).Code);
        Assert.Equal(VmState.Stopped, linux.State);
    }

    [Fact]
    public async Task An_image_that_already_uses_hdb_cannot_take_a_program_disk()
    {
        await using var rig = new VmTestRig(withAgent: false);
        rig.Images.Add("freedos", "0.2.0", new BootImage("k", "i", "console=ttyS0"));

        X86Exception error = await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RunDosProgramAsync(User, Run(), None));

        Assert.Equal(X86ErrorCodes.ImageUnavailable, error.Code);
        Assert.Empty(rig.Engines.Created);
    }
}

/// <summary>A full screen of Kazo's Terminal for the tests: frames recorded, keys typed by the test.</summary>
internal sealed partial class FakeFullScreen : IFullScreenTerminalSession
{
    private readonly Lock _lock = new();
    private readonly List<TerminalScreenFrame> _frames = [];
    private readonly Channel<TerminalKeyEvent> _keys = Channel.CreateUnbounded<TerminalKeyEvent>();

    public TerminalViewport Viewport { get; set; } = new(80, 25);

    public bool Active { get; private set; }

    public int Enters { get; private set; }

    public int Leaves { get; private set; }

    public IReadOnlyList<TerminalScreenFrame> Frames
    {
        get
        {
            lock (_lock)
            {
                return [.. _frames];
            }
        }
    }

    /// <summary>The text of a frame without its SGR sequences.</summary>
    public static string Text(TerminalScreenFrame frame) => string.Join('\n', frame.Lines.Select(static line => Sgr().Replace(line, string.Empty).TrimEnd()));

    public ValueTask EnterAlternateScreenAsync(CancellationToken cancellationToken = default)
    {
        Assert.False(Active, "entered twice");
        Active = true;
        Enters++;
        return ValueTask.CompletedTask;
    }

    public ValueTask RenderAsync(TerminalScreenFrame frame, CancellationToken cancellationToken = default)
    {
        Assert.True(Active, "a frame outside the alternate screen");
        lock (_lock)
        {
            _frames.Add(frame);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<TerminalKeyEvent> ReadKeyAsync(CancellationToken cancellationToken = default) => _keys.Reader.ReadAsync(cancellationToken);

    public ValueTask LeaveAlternateScreenAsync(CancellationToken cancellationToken = default)
    {
        Active = false;
        Leaves++;
        return ValueTask.CompletedTask;
    }

    public void Type(TerminalKeyEvent key) => _keys.Writer.TryWrite(key);

    public async Task<TerminalScreenFrame> WaitForAsync(Func<TerminalScreenFrame, bool> predicate, string what)
    {
        TerminalScreenFrame? found = null;
        await VmTestRig.WaitUntilAsync(() => (found = Frames.LastOrDefault(predicate)) is not null, what);
        return found!;
    }

    [GeneratedRegex("\u001b\\[[0-9;]*m")]
    private static partial Regex Sgr();
}
