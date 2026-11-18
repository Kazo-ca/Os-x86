using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Microsoft.JSInterop;
using OsX86.Feature;
using OsX86.Storage;
using OsX86.Tests.Vm;
using OsX86.Vm;

namespace OsX86.Tests;

/// <summary>
/// Who installs the emulator and the storage in the page (work item 8, decision 288 A): the hidden
/// service child <c>ca.kazo.x86.host</c>, started at login with the <c>IJSRuntime</c> of the page; the
/// <c>x86</c> command does the same when it runs first. A host that installed its own (the dev host,
/// with its image catalog and test relay) is kept.
/// </summary>
[Trait("Category", "unit")]
[Collection(X86CommandCollection.Name)]
public sealed class X86HostTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    private static AppManifest HostManifest => AppDefinitionMapper.ToFeatureManifests(new X86FeatureApp()).Children.Single(child => child.Id == "ca.kazo.x86.host");

    private static AppManifest CommandManifest => AppDefinitionMapper.ToFeatureManifests(new X86FeatureApp()).Children.Single(child => child.Id == "ca.kazo.x86.cli");

    [Fact]
    public async Task The_host_service_installs_the_emulator_and_the_storage_at_login_and_runs_until_the_session_ends()
    {
        try
        {
            VmHost.Clear();
            var service = new X86HostService(HostManifest, new UnusedJsRuntime());
            using var session = new CancellationTokenSource();

            Task running = service.RunAsync(new TestAppContext(), session.Token);

            VmHostEnvironment host = Assert.IsType<VmHostEnvironment>(VmHost.Current);
            Assert.IsType<JsEngineFactory>(host.Engines);
            Assert.IsType<StoreBootImageSource>(host.Images);
            Assert.NotNull(host.Store);
            Assert.False(running.IsCompleted);

            await session.CancelAsync();
            await running.WaitAsync(Soon);
        }
        finally
        {
            VmHost.Clear();
        }
    }

    [Fact]
    public async Task Ix86_has_an_emulator_once_the_host_service_started_without_any_x86_command()
    {
        // Work item 8c: Kazo 3.5.2 starts the service at login (Kazo-ca/Os#625). Ix86 resolves its
        // manager through X86Runtime.Services, as X86FeatureService does.
        try
        {
            VmHost.Clear();
            X86Runtime.Reset();
            // Before the service: what an app calling Ix86 got in Kazo 3.5.1 (engine-unavailable).
            Assert.IsType<UnavailableEngineFactory>(X86Runtime.Services.Manager.Engines);
            using var session = new CancellationTokenSource();

            Task running = new X86HostService(HostManifest, new UnusedJsRuntime()).RunAsync(new TestAppContext(), session.Token);

            Assert.IsType<JsEngineFactory>(X86Runtime.Services.Manager.Engines);
            await session.CancelAsync();
            await running.WaitAsync(Soon);
        }
        finally
        {
            VmHost.Clear();
            X86Runtime.Reset();
        }
    }

    [Fact]
    public async Task A_host_installed_before_the_feature_ran_is_kept()
    {
        try
        {
            var devHost = new VmHostEnvironment(UnavailableEngineFactory.Instance, StaticBootImageSource.Empty, FixedHostMemory.Unknown);
            VmHost.Configure(devHost);
            using var session = new CancellationTokenSource();

            Task running = new X86HostService(HostManifest, new UnusedJsRuntime()).RunAsync(new TestAppContext(), session.Token);
            Assert.False(X86Host.EnsureInstalled(new UnusedJsRuntime()));

            Assert.Same(devHost, VmHost.Current);
            await session.CancelAsync();
            await running.WaitAsync(Soon);
        }
        finally
        {
            VmHost.Clear();
        }
    }

    [Fact]
    public void The_command_the_os_builds_with_the_js_runtime_installs_the_host_when_it_runs_first()
    {
        Func<X86CommandServices> previous = X86Command.ServicesFactory;
        try
        {
            VmHost.Clear();
            X86Command.ServicesFactory = static () => new X86CommandServices(new VmTestRig().Manager, new X86CommandTests.FakeFiles());

            _ = new X86Command(CommandManifest, new UnusedJsRuntime());

            Assert.NotNull(VmHost.Current?.Store);
        }
        finally
        {
            X86Command.ServicesFactory = previous;
            VmHost.Clear();
        }
    }

    [Fact]
    public void Services_made_before_the_host_are_made_again_once_it_is_installed_then_kept()
    {
        try
        {
            VmHost.Clear();
            X86Runtime.Reset();
            X86CommandServices before = X86Runtime.Services;
            Assert.Null(before.StorageTree);

            Assert.True(X86Host.EnsureInstalled(new UnusedJsRuntime()));
            X86CommandServices after = X86Runtime.Services;

            Assert.NotSame(before, after);
            Assert.NotNull(after.StorageTree);
            Assert.Same(after, X86Runtime.Services);
        }
        finally
        {
            VmHost.Clear();
            X86Runtime.Reset();
        }
    }

    [Fact]
    public void Services_a_host_configured_are_never_replaced()
    {
        try
        {
            VmHost.Clear();
            var configured = new X86CommandServices(new VmTestRig().Manager, new X86CommandTests.FakeFiles());
            X86Runtime.Configure(configured);

            X86Host.EnsureInstalled(new UnusedJsRuntime());

            Assert.Same(configured, X86Runtime.Services);
        }
        finally
        {
            VmHost.Clear();
            X86Runtime.Reset();
        }
    }

    /// <summary>A JS runtime the tests never reach: installing the host only creates the bridges, it starts no worker.</summary>
    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
}
