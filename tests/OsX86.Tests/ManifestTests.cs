using System.Reflection;
using Kazo.Os.App.Abstractions;
using OsX86.Feature;
using OsX86.Interface;

namespace OsX86.Tests;

/// <summary>
/// The manifest class of Os-x86, the source of the generated manifest.json: a Feature of Kazo OS
/// (ADR 0260) with its command child, its permissions (ADR 0246, decision 176 A), its role (ADR 0247,
/// 281 A) and its service <see cref="Ix86"/> (ADR 0262).
/// </summary>
[Trait("Category", "unit")]
public sealed class ManifestTests
{
    private readonly X86FeatureApp _app = new();

    [Fact]
    public void Os_x86_is_a_feature_hidden_from_launchers_with_its_id()
    {
        Assert.Equal("ca.kazo.x86", _app.Id);
        Assert.Equal(AppKind.Feature, _app.Kind);
        Assert.Equal(AppLaunchVisibility.Hidden, _app.LaunchVisibility);
        Assert.Equal(OsX86.Vm.X86Service.FeatureVersion, _app.Version);
        Assert.Empty(_app.Dependencies);
    }

    [Fact]
    public void The_x86_command_is_the_cli_child_on_the_main_thread()
    {
        FeatureEntryPoint cli = Assert.Single(_app.EntryPoints, entry => entry.Suffix == X86FeatureApp.CommandSuffix);
        Assert.Equal(AppKind.Terminal, cli.Kind);
        Assert.Equal(typeof(X86Command), cli.EntryPoint);
        Assert.Equal("x86", cli.Terminal!.Name);
        // ADR 0070 of Kazo: the VMs live on the page's main thread, so does the command that drives them.
        Assert.Equal(AppExecutionHost.MainThread, cli.Execution);
        Assert.Null(cli.RequiresRole);
    }

    [Fact]
    public void Every_capability_of_a_child_is_in_the_feature_set()
    {
        Assert.All(_app.EntryPoints, entry => Assert.All(entry.Capabilities, capability => Assert.Contains(capability, _app.Capabilities)));
    }

    [Fact]
    public void The_feature_declares_the_permissions_of_176_A_under_the_x86_prefix()
    {
        Assert.Equal("x86", _app.CapabilityPrefix);
        var byId = _app.ProvidedCapabilities.ToDictionary(capability => capability.Id, StringComparer.Ordinal);

        Assert.Equal(
            [X86Permissions.ProgramsLaunch, X86Permissions.Vm, X86Permissions.VmDestroy, X86Permissions.VmUse],
            byId.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(CapabilityRisk.Normal, byId[X86Permissions.ProgramsLaunch].Risk);
        Assert.Equal(CapabilityRisk.Normal, byId[X86Permissions.VmUse].Risk);
        Assert.Equal(CapabilityRisk.Dangerous, byId[X86Permissions.VmDestroy].Risk);
        Assert.Equal(CapabilityRisk.Dangerous, byId[X86Permissions.Vm].Risk);
        Assert.Equal([X86Permissions.VmUse, X86Permissions.VmDestroy], byId[X86Permissions.Vm].Covers);
        Assert.All(
            byId.Values.Where(capability => capability.Risk == CapabilityRisk.Dangerous),
            capability => Assert.False(string.IsNullOrWhiteSpace(capability.RiskStatement)));
    }

    [Fact]
    public void The_feature_declares_the_vm_manager_role_under_its_prefix()
    {
        FeatureRole role = Assert.Single(_app.Roles);
        Assert.Equal("x86:vm-manager", role.Id);
        Assert.Equal(X86FeatureApp.VmManagerRole, role.Id);
        // Work item 7 gives it the admin entry points; until then it opens nothing.
        Assert.Empty(role.Grants);
    }

    [Fact]
    public void The_feature_provides_Ix86_from_the_shared_contracts_library()
    {
        FeatureService service = Assert.Single(_app.ProvidedServices);
        Assert.Equal(typeof(Ix86), service.Contract);
        Assert.Equal(typeof(X86FeatureService), service.Implementation);
        Assert.True(typeof(Ix86).IsAssignableFrom(service.Implementation));
        // ADR 0261 §2: the contract lives in a library marked shared, one copy per page.
        Assert.Contains(
            typeof(Ix86).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>(),
            attribute => attribute.Key == "Kazo.Os.SharedLibrary" && attribute.Value == "true");
    }

    [Fact]
    public void The_generated_manifests_are_valid_for_Kazo()
    {
        FeatureManifests manifests = AppDefinitionMapper.ToFeatureManifests(_app);

        AssertValid(AppManifestValidator.Validate(manifests.Feature));
        Assert.All(manifests.Children, child => AssertValid(AppManifestValidator.Validate(child)));
        AssertValid(AppManifestValidator.ValidateFeature(manifests.Feature, manifests.Children));
        Assert.Equal(["ca.kazo.x86.cli"], manifests.Children.Select(child => child.Id));
        Assert.All(manifests.Children, child => Assert.Equal("ca.kazo.x86", child.Parent));
    }

    private static void AssertValid(ManifestValidationResult result) =>
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Code + ": " + error.Message)));
}
