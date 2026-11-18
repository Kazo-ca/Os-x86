using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Blazor.LazyLoading;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Lifecycle;
using OsX86.Feature;

namespace OsX86.Tests;

/// <summary>
/// Work item 8b: Kazo OS 3.5.1 finds a child of a feature with its feature
/// (https://github.com/Kazo-ca/Os/issues/621). The dev host no longer works around it (291 B): these
/// tests go through the real lazy registry of Kazo, the one that answered « command not found » in
/// Kazo 3.5.0.
/// </summary>
[Trait("Category", "unit")]
public sealed class FeatureChildLaunchTests
{
    [Fact]
    public async Task Kazo_finds_the_x86_command_with_its_feature_and_runs_it()
    {
        BuildKnownLazyAppDescriptorRegistry registry = Registry(out FeatureManifests feature);
        AppManifest cli = feature.Children.Single(static child => child.Id == "ca.kazo.x86.cli");

        AppDescriptorLoadResult result = await registry.EnsureAvailableAsync(cli.Id, CancellationToken.None);

        Assert.Equal(AppDescriptorLoadStatus.Available, result.Status);
        AppDescriptor descriptor = registry.Descriptors[cli.Id];
        Assert.Equal(typeof(X86Command), descriptor.EntryPointType);
        // What the orchestrator of Kazo does with a terminal descriptor: the entry point built with its manifest.
        var command = (TerminalAppBase)Activator.CreateInstance(descriptor.EntryPointType, descriptor.Manifest)!;
        var output = new StringWriter();
        int code = await command.ExecuteAsync(TestAppContext.Terminal(["--version"], output, new StringWriter()), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal($"Os-x86 {OsX86.Vm.X86Service.FeatureVersion}\n", output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Kazo_finds_the_host_service_with_its_feature()
    {
        BuildKnownLazyAppDescriptorRegistry registry = Registry(out FeatureManifests feature);
        AppManifest host = feature.Children.Single(static child => child.Id == "ca.kazo.x86.host");

        AppDescriptorLoadResult result = await registry.EnsureAvailableAsync(host.Id, CancellationToken.None);

        Assert.Equal(AppDescriptorLoadStatus.Available, result.Status);
        Assert.Equal(typeof(X86HostService), registry.Descriptors[host.Id].EntryPointType);
        // Only the child asked for is registered, not the feature itself.
        Assert.False(registry.Descriptors.ContainsKey(feature.Feature.Id));
    }

    private static BuildKnownLazyAppDescriptorRegistry Registry(out FeatureManifests feature)
    {
        feature = AppDefinitionMapper.ToFeatureManifests(new X86FeatureApp());
        AppCatalog catalog = Assert.IsType<AppCatalog>(AppCatalog.Build(feature.All).Catalog);
        string assembly = feature.Children[0].EntryPoint!.Assembly;
        var assemblies = new BuildKnownAssemblyLoaderRegistry([assembly], new FeatureAssemblyTransport());
        return new BuildKnownLazyAppDescriptorRegistry(catalog, assemblies);
    }

    /// <summary>Gives the assembly of the Feature, as the browser would load it lazily.</summary>
    private sealed class FeatureAssemblyTransport : IBuildKnownAssemblyTransport
    {
        public Task<IReadOnlyList<Assembly>> LoadAsync(IReadOnlyList<string> assemblyNames, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Assembly>>([typeof(X86Command).Assembly]);
    }
}
