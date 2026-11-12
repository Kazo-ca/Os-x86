using OsX86.Engine;

namespace OsX86.Tests.Engine;

/// <summary>The small value types of the engine: console identifiers, start options, exceptions, event arguments.</summary>
[Trait("Category", "unit")]
public sealed class EngineTypesTests
{
    [Fact]
    public void Console_ids_have_the_channel_names_of_the_protocol()
    {
        Assert.Equal("serial0", ConsoleId.Serial0.ChannelName);
        Assert.Equal("virtio2", ConsoleId.Virtio(2).ChannelName);
        Assert.Equal("virtio2", ConsoleId.Virtio(2).ToString());
        Assert.True(ConsoleId.Serial0.IsValid);
        Assert.True(ConsoleId.Virtio(3).IsValid);
        Assert.False(ConsoleId.Virtio(4).IsValid);
        Assert.False(new ConsoleId(ConsoleKind.Serial, 1).IsValid);
        Assert.False(new ConsoleId((ConsoleKind)7, 0).IsValid);
    }

    [Theory]
    [InlineData("serial0", ConsoleKind.Serial, 0)]
    [InlineData("virtio0", ConsoleKind.Virtio, 0)]
    [InlineData("virtio3", ConsoleKind.Virtio, 3)]
    public void Channel_names_parse_back(string name, ConsoleKind kind, int index)
    {
        Assert.True(ConsoleId.TryParse(name, out ConsoleId id));
        Assert.Equal(new ConsoleId(kind, index), id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("serial")]
    [InlineData("serial1")]
    [InlineData("virtio4")]
    [InlineData("virtio10")]
    [InlineData("virtioX")]
    [InlineData("usb0")]
    public void Other_channel_names_do_not_parse(string? name)
    {
        Assert.False(ConsoleId.TryParse(name, out _));
    }

    [Fact]
    public void Start_options_have_sensible_defaults_and_validate()
    {
        var options = new EngineStartOptions { KernelPath = "images/alpine/vmlinuz", InitrdPath = "images/alpine/initramfs" };
        options.Validate();
        Assert.Equal(128, options.MemoryMiB);
        Assert.Equal(2, options.VgaMemoryMiB);
        Assert.True(options.VirtioConsole);
        Assert.Equal(EngineStartOptions.DefaultKernelCommandLine, options.KernelCommandLine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/absolute/path")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    [InlineData("a\\b")]
    public void Start_options_reject_bad_paths(string path)
    {
        Assert.Throws<ArgumentException>(() => new EngineStartOptions { KernelPath = path, InitrdPath = "ok/initrd" }.Validate());
        Assert.Throws<ArgumentException>(() => new EngineStartOptions { KernelPath = "ok/kernel", InitrdPath = path }.Validate());
    }

    [Theory]
    [InlineData(15)]
    [InlineData(4097)]
    public void Start_options_reject_memory_out_of_range(int memoryMiB)
    {
        var options = new EngineStartOptions { KernelPath = "k", InitrdPath = "i", MemoryMiB = memoryMiB };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void Start_options_reject_vga_memory_out_of_range(int vgaMiB)
    {
        var options = new EngineStartOptions { KernelPath = "k", InitrdPath = "i", VgaMemoryMiB = vgaMiB };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("console=ttyS0\nquiet")]
    public void Start_options_reject_a_bad_command_line(string commandLine)
    {
        var options = new EngineStartOptions { KernelPath = "k", InitrdPath = "i", KernelCommandLine = commandLine };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Exceptions_carry_their_code_and_state()
    {
        var error = new EngineException(EngineErrorCodes.ImageNotFound, "no kernel");
        Assert.Equal("image-not-found", error.Code);
        Assert.Equal("no kernel", error.Message);
        Assert.Throws<ArgumentException>(() => new EngineException("", "x"));

        var stateError = new EngineStateException(EngineOperation.Pause, EngineState.Stopped);
        Assert.Equal(EngineErrorCodes.InvalidState, stateError.Code);
        Assert.Equal(EngineOperation.Pause, stateError.Operation);
        Assert.Equal(EngineState.Stopped, stateError.State);
        Assert.Contains("pause", stateError.Message, StringComparison.Ordinal);
        Assert.Contains("stopped", stateError.Message, StringComparison.Ordinal);

        var custom = new EngineStateException(EngineOperation.Start, EngineState.Running, "busy");
        Assert.Equal("busy", custom.Message);
    }

    [Fact]
    public void Event_arguments_expose_their_values()
    {
        var change = new EngineStateChangedEventArgs(EngineState.Starting, EngineState.Running, null);
        Assert.Equal(EngineState.Starting, change.Previous);
        Assert.Equal(EngineState.Running, change.Current);
        Assert.Null(change.Reason);

        var error = new EngineErrorEventArgs("worker-error", "boom", fatal: true);
        Assert.Equal("worker-error", error.Code);
        Assert.Equal("boom", error.Message);
        Assert.True(error.Fatal);

        var text = new ScreenSizeChangedEventArgs(80, 25, 0);
        Assert.True(text.IsText);
        var graphics = new ScreenSizeChangedEventArgs(320, 200, 8);
        Assert.False(graphics.IsText);
        Assert.Equal((320, 200, 8), (graphics.Columns, graphics.Rows, graphics.BitsPerPixel));
    }
}
