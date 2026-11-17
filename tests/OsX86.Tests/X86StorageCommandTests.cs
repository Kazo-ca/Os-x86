using Kazo.Os.AppSdk.Storage;
using OsX86.Feature;
using OsX86.Tests.Vm;
using static OsX86.Tests.FeatureStorageGuardTests;

namespace OsX86.Tests;

/// <summary>
/// The <c>x86</c> command and the storage of the browser (ADR 0250 of Kazo, decision 289 A):
/// <c>x86 vm create</c> protects the storage, the next <c>x86</c> tells an eviction once.
/// </summary>
[Trait("Category", "unit")]
public sealed class X86StorageCommandTests
{
    [Fact]
    public async Task Creating_a_vm_asks_persistence_registers_the_marker_and_declares_the_bytes()
    {
        await using var rig = new VmTestRig();
        var kazo = new FakeStorageGateway();
        var tree = new FakeStorageTree { Bytes = 73_000_000 };
        var services = new X86CommandServices(rig.Manager, new X86CommandTests.FakeFiles()) { StorageTree = tree };

        var (code, _, error) = await X86CommandTests.RunAsync(rig, null, ["vm", "create", "essai", "--profile", "alpine-test"], storage: kazo, services: services);

        Assert.Equal(0, code);
        Assert.Equal("", error);
        Assert.Equal(1, kazo.PersistenceRequests);
        Assert.Equal(["osx86/.kazoos-marker"], kazo.Markers);
        Assert.Equal(1, tree.MarkersWritten);
        Assert.Equal([73_000_000L], kazo.Reports);

        // A command that changes the storage declares the bytes again, without asking anything.
        (code, _, _) = await X86CommandTests.RunAsync(rig, null, ["vm", "remove", "essai"], storage: kazo, services: services);
        Assert.Equal(0, code);
        Assert.Equal(1, kazo.PersistenceRequests);
        Assert.Equal(2, kazo.Reports.Count);
    }

    [Fact]
    public async Task The_next_x86_tells_an_eviction_once_on_the_error_output()
    {
        await using var rig = new VmTestRig();
        var kazo = new FakeStorageGateway();
        kazo.Pending.Add(new KazoStorageChangedEventArgs(KazoStorageChangeKind.Evicted, "ca.kazo.x86", "osx86/.kazoos-marker", DateTimeOffset.UnixEpoch));
        var services = new X86CommandServices(rig.Manager, new X86CommandTests.FakeFiles());

        var (code, output, error) = await X86CommandTests.RunAsync(rig, null, ["vm", "list"], storage: kazo, services: services);
        Assert.Equal(0, code);
        Assert.Equal("No virtual machine.\n", output);
        Assert.Contains("the browser erased the storage of Os-x86", error, StringComparison.Ordinal);

        (_, _, error) = await X86CommandTests.RunAsync(rig, null, ["vm", "list"], storage: kazo, services: services);
        Assert.Equal("", error);
    }
}
