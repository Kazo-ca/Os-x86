using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;
using OsX86.Feature;
using OsX86.Os;

namespace OsX86.Tests;

/// <summary>
/// Provisional workaround of the dev host (decision 291 B), to remove once Os-x86 is pinned on Kazo
/// OS 3.5.1: Kazo 3.5.0 discovers a lazily loaded app with a catalog of its manifest alone, which
/// refuses a feature's child, so <c>x86</c> was « command not found »
/// (https://github.com/Kazo-ca/Os/issues/621). The dev host discovers the children with their feature.
/// </summary>
[Trait("Category", "unit")]
public sealed class FeatureChildDescriptorsTests
{
    private static readonly IReadOnlyDictionary<string, Assembly> FeatureAssembly =
        new Dictionary<string, Assembly>(StringComparer.Ordinal) { ["OsX86.Feature.dll"] = typeof(X86Command).Assembly };

    [Fact]
    public void Kazo_3_5_0_still_refuses_to_discover_a_child_alone()
    {
        // When this fails, Kazo fixed #621: pin Os-x86 on that version and remove FeatureChildDescriptors.
        AppManifest child = AppDefinitionMapper.ToFeatureManifests(new X86FeatureApp()).Children[0];

        AppCatalogBuildResult alone = AppCatalog.Build([child]);

        Assert.Null(alone.Catalog);
        Assert.Contains(alone.Errors, error => error.Code == "catalog.feature.parent.missing");
    }

    [Fact]
    public async Task The_x86_command_runs_through_the_descriptor_discovered_with_its_feature()
    {
        AppCatalog catalog = Catalog();

        AppDescriptor cli = Assert.Single(FeatureChildDescriptors.Discover(catalog, FeatureAssembly));

        Assert.Equal("ca.kazo.x86.cli", cli.Manifest.Id);
        Assert.Equal(typeof(X86Command), cli.EntryPointType);
        // What the orchestrator of Kazo does with a terminal descriptor: the entry point built with its manifest.
        var command = (TerminalAppBase)Activator.CreateInstance(cli.EntryPointType, cli.Manifest)!;
        var output = new StringWriter();
        int code = await command.ExecuteAsync(TestAppContext.Terminal(["--version"], output, new StringWriter()), CancellationToken.None);
        Assert.Equal(0, code);
        Assert.Equal($"Os-x86 {OsX86.Vm.X86Service.FeatureVersion}\n", output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void A_child_whose_assembly_is_not_loaded_is_left_to_Kazo()
    {
        Assert.Empty(FeatureChildDescriptors.Discover(Catalog(), new Dictionary<string, Assembly>(StringComparer.Ordinal)));
    }

    private static AppCatalog Catalog()
    {
        AppCatalogBuildResult built = AppCatalog.Build(AppDefinitionMapper.ToFeatureManifests(new X86FeatureApp()).All);
        return built.Catalog ?? throw new InvalidOperationException(string.Join("; ", built.Errors.Select(error => error.Message)));
    }
}
