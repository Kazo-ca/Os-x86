using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Simulation.Abstractions.Gateways;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Vm;
using static OsX86.Tests.Vm.VmTestRig;

namespace OsX86.Tests.Vm;

/// <summary>
/// The <see cref="Ix86"/> Kazo hands to apps (ADR 0262): one instance per page, every operation
/// bound to the calling app's user (190 A) and checked against its permissions (ADR 0246, 176 A).
/// </summary>
[Trait("Category", "unit")]
public sealed class FeatureServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_caller_with_vm_use_sees_and_drives_only_its_users_vms()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptConsole(engine);
        VmInstance mine = await rig.CreateAsync("mine");
        VmInstance theirs = await rig.CreateAsync("theirs", user: OtherUser);
        var context = new TestServiceContext { Caller = Caller(User, X86Permissions.VmUse) };
        var service = new X86FeatureService(context, () => rig.Manager);

        Assert.Equal(["mine"], (await service.GetVmsAsync(None)).Select(vm => vm.Name));
        await service.StartVmAsync(mine.Id, None);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await service.StartVmAsync(theirs.Id, None))).Code);
        await using (IVmConsole console = await service.OpenConsoleAsync(mine.Id, new VmConsoleOptions(), None))
        {
            Assert.Equal(mine.Id, console.VmId);
        }

        await service.StopVmAsync(mine.Id, force: true, None);

        // The same instance serves another app of another user (one instance per page).
        context.Caller = Caller(OtherUser, X86Permissions.VmUse);
        Assert.Equal(["theirs"], (await service.GetVmsAsync(None)).Select(vm => vm.Name));
    }

    [Fact]
    public async Task Each_vm_operation_requires_vm_use()
    {
        await using var rig = new VmTestRig();
        VmInstance mine = await rig.CreateAsync("mine");
        var service = new X86FeatureService(new TestServiceContext { Caller = Caller(User, X86Permissions.ProgramsLaunch) }, () => rig.Manager);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.GetVmsAsync(None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.StartVmAsync(mine.Id, None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.StopVmAsync(mine.Id, force: false, None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.OpenConsoleAsync(mine.Id, new VmConsoleOptions(), None));
        Assert.Throws<UnauthorizedAccessException>(() => service.VmStateChanged += (_, _) => { });
        Assert.Equal(VmState.Stopped, mine.State);
    }

    [Fact]
    public async Task Running_a_program_requires_programs_launch()
    {
        await using var rig = new VmTestRig();
        var service = new X86FeatureService(new TestServiceContext { Caller = Caller(User, X86Permissions.VmUse) }, () => rig.Manager);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.RunProgramAsync(new ProgramRunOptions { FileName = "hello.exe", Content = new byte[] { (byte)'M', (byte)'Z' } }, None));
    }

    [Fact]
    public async Task Version_and_detection_need_no_permission()
    {
        await using var rig = new VmTestRig();
        var service = new X86FeatureService(new TestServiceContext { Caller = Caller(User) }, () => rig.Manager);

        Assert.Equal(X86Service.FeatureVersion, service.Version);
        ProgramDetection detection = await service.DetectProgramAsync(new byte[] { (byte)'M', (byte)'Z' }, "hello.exe", None);
        Assert.NotNull(detection);
    }

    [Fact]
    public async Task Without_a_caller_or_with_an_unreadable_user_the_service_refuses()
    {
        await using var rig = new VmTestRig();
        var context = new TestServiceContext();
        var service = new X86FeatureService(context, () => rig.Manager);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.GetVmsAsync(None));

        context.Caller = new KazoFeatureCaller("com.example.client", 7, "user-1") { Permissions = new GrantedPermissions(X86Permissions.VmUse) };
        Assert.Equal(X86ErrorCodes.InvalidUser, (await Assert.ThrowsAsync<X86Exception>(async () => await service.GetVmsAsync(None))).Code);
    }

    [Fact]
    public async Task A_subscriber_hears_only_its_users_state_changes_until_it_unsubscribes()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptConsole(engine);
        VmInstance mine = await rig.CreateAsync("mine");
        VmInstance theirs = await rig.CreateAsync("theirs", user: OtherUser);
        var context = new TestServiceContext { Caller = Caller(User, X86Permissions.VmUse) };
        var service = new X86FeatureService(context, () => rig.Manager);
        var seen = new List<(object? Sender, VmStateChangedEventArgs Args)>();
        EventHandler<VmStateChangedEventArgs> handler = (sender, e) => seen.Add((sender, e));

        service.VmStateChanged += handler;
        context.Caller = Caller(OtherUser, X86Permissions.VmUse); // another app now calls the same instance
        await theirs.HoldAsync(None);
        await mine.HoldAsync(None);
        await WaitUntilAsync(() => seen.Any(e => e.Args.Current == VmState.Running), "my VM running");

        Assert.All(seen, e => Assert.Equal(mine.Id, e.Args.VmId));
        Assert.All(seen, e => Assert.Same(service, e.Sender));

        context.Caller = Caller(User, X86Permissions.VmUse);
        service.VmStateChanged -= handler;
        int heard = seen.Count;
        await mine.StopAsync(force: true, None);
        Assert.Equal(heard, seen.Count);
    }

    private static KazoFeatureCaller Caller(Guid user, params string[] permissions) =>
        new("com.example.client", 7, user.ToString("N")) { Permissions = new GrantedPermissions(permissions) };

    /// <summary>The caller's permissions as Kazo would decide them: granted when listed.</summary>
    private sealed class GrantedPermissions(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            granted.Contains(capability, StringComparer.Ordinal)
                ? CapabilityPolicyEvaluation.Permit(new CapabilityGrant(CapabilityGrantId.FromGuid(Guid.NewGuid()), "com.example.client", "user-1", capability, 1, CapabilityGrantSource.UserApproval))
                : CapabilityPolicyEvaluation.DenyMissing(1);

        public void Require(string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!Evaluate(capability, requiredAuthority, resourceCandidate).Granted)
            {
                throw new UnauthorizedAccessException(capability);
            }
        }
    }

    /// <summary>What Kazo gives a feature's service; the caller changes with the app that calls.</summary>
    private sealed class TestServiceContext : IFeatureServiceContext
    {
        public IAppExecutionContext Feature { get; } = new TestAppContext();

        public KazoFeatureCaller? Caller { get; set; }

        public CancellationToken Stopping => CancellationToken.None;

        public IDisposable EnterFeature() => new NoScope();

        private sealed class NoScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
