using OsX86.Feature;
using OsX86.Interface;

namespace OsX86.Tests;

/// <summary>The <c>x86</c> command against a fake feature service.</summary>
[Trait("Category", "unit")]
public sealed class X86CommandTests
{
    private sealed class FakeX86 : Ix86
    {
        public string Version => "9.9.9-test";

        public IReadOnlyList<VmSummary> Vms { get; init; } = [];

        public ValueTask<IReadOnlyList<VmSummary>> GetVmsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Vms);
    }

    private static async Task<(int ExitCode, string Out, string Err)> RunAsync(Ix86 x86, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        X86Command.ServiceFactory = () => x86;
        var command = new X86Command();
        int code = await command.ExecuteAsync(TestAppContext.Terminal(args, stdout, stderr), CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Version_prints_the_feature_version()
    {
        var (code, output, _) = await RunAsync(new FakeX86(), "--version");
        Assert.Equal(0, code);
        Assert.Equal("Os-x86 9.9.9-test" + Environment.NewLine, output);
    }

    [Fact]
    public async Task Vm_list_prints_each_vm_or_says_there_is_none()
    {
        var (code, output, _) = await RunAsync(new FakeX86(), "vm", "list");
        Assert.Equal(0, code);
        Assert.Contains("No virtual machine.", output);

        var id = Guid.NewGuid();
        var withVm = new FakeX86 { Vms = [new VmSummary(id, "Linux", VmKind.Linux, VmState.Stopped)] };
        (code, output, _) = await RunAsync(withVm, "vm", "list");
        Assert.Equal(0, code);
        Assert.Contains(id.ToString(), output);
        Assert.Contains("Linux", output);
    }

    [Fact]
    public async Task An_unknown_command_fails_with_usage_on_stderr()
    {
        var (code, output, error) = await RunAsync(new FakeX86(), "frobnicate");
        Assert.Equal(2, code);
        Assert.Equal(string.Empty, output);
        Assert.Contains("unknown command", error);
        Assert.Contains("Usage:", error);
    }

    [Fact]
    public async Task The_real_service_answers_version_and_an_empty_list()
    {
        var (code, output, _) = await RunAsync(new OsX86.Vm.X86Service(), "--version");
        Assert.Equal(0, code);
        Assert.StartsWith("Os-x86 ", output);
    }
}
