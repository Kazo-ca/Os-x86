using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Features;
using Kazo.Os.Platform.Core.Lifecycle;
using OsX86.Feature;
using OsX86.Interface;

namespace OsX86.Tests.Vm;

/// <summary>
/// What <c>KazoFeatures.GetServiceAsync&lt;Ix86&gt;()</c> gives an app (ADR 0262, work item 8), through
/// the real resolver of Kazo OS fed with the manifests the build generates from
/// <see cref="X86FeatureApp"/>: the instance of <see cref="X86FeatureService"/> when Os-x86 is
/// installed, <see langword="null"/> without it (decisions 10 B, 158 A).
/// </summary>
[Trait("Category", "unit")]
public sealed class FeatureResolutionTests
{
    private const string ClientId = "com.example.x86-client";

    [Fact]
    public async Task An_app_that_declares_Os_x86_gets_its_Ix86()
    {
        AppManifest client = Client(optional: true);
        using Page page = new([.. OsX86Manifests(), client]);

        (object? instance, KazoFeatureServiceStatus status) = await page.Resolver.ResolveAsync(client, typeof(Ix86));

        Ix86 x86 = Assert.IsType<X86FeatureService>(instance);
        Assert.Equal(KazoFeatureServiceState.Available, status.State);
        Assert.Equal("ca.kazo.x86", status.FeatureId);
        Assert.Equal(OsX86.Vm.X86Service.FeatureVersion, status.InstalledVersion);
        Assert.Equal(OsX86.Vm.X86Service.FeatureVersion, x86.Version);
        // ADR 0262 §1: created once, in the feature's context, from the feature's own assembly.
        Assert.Equal([("ca.kazo.x86", typeof(X86FeatureService))], page.Started);
        Assert.Equal(["OsX86.Feature.dll"], page.Loads);
    }

    [Fact]
    public async Task Without_Os_x86_the_app_gets_null_then_the_service_once_installed()
    {
        AppManifest client = Client(optional: true);
        using Page page = new([client]);

        (object? none, KazoFeatureServiceStatus status) = await page.Resolver.ResolveAsync(client, typeof(Ix86));

        Assert.Null(none);
        Assert.Equal(KazoFeatureServiceState.NotInstalled, status.State);

        // ADR 0262 §2: a null is never cached; Os-x86 installed while the page runs is found.
        page.Install([.. OsX86Manifests(), client]);
        (object? found, _) = await page.Resolver.ResolveAsync(client, typeof(Ix86));
        Assert.IsType<X86FeatureService>(found);
    }

    [Fact]
    public async Task An_app_that_does_not_declare_Os_x86_gets_null()
    {
        AppManifest client = AppDefinitionMapper.ToManifest(new ClientApp([]));
        using Page page = new([.. OsX86Manifests(), client]);

        (object? instance, KazoFeatureServiceStatus status) = await page.Resolver.ResolveAsync(client, typeof(Ix86));

        Assert.Null(instance);
        Assert.Equal(KazoFeatureServiceState.NotDeclared, status.State);
    }

    private static IEnumerable<AppManifest> OsX86Manifests() => AppDefinitionMapper.ToFeatureManifests(new X86FeatureApp()).All;

    private static AppManifest Client(bool optional) =>
        AppDefinitionMapper.ToManifest(new ClientApp([new AppDependencyManifest("ca.kazo.x86", OsX86.Vm.X86Service.FeatureVersion, Optional: optional)]));

    /// <summary>An app that asks for Ix86, such as BoxedWine (work item 13).</summary>
    private sealed class ClientApp(IReadOnlyList<AppDependencyManifest> dependencies) : AppDefinition
    {
        public override string Id => ClientId;

        public override string Name => "x86 client";

        public override AppKind Kind => AppKind.Window;

        public override Type EntryPoint => typeof(ClientApp);

        public override IReadOnlyList<AppDependencyManifest> Dependencies => dependencies;
    }

    /// <summary>One page of Kazo: its catalog, which apps are enabled, the resolver of the feature services.</summary>
    private sealed class Page : IDisposable
    {
        public Page(IEnumerable<AppManifest> manifests)
        {
            Catalog = Build(manifests).CreateLiveCopy();
            Resolver = new FeatureServiceResolver(
                Catalog,
                new AppEnablementRegistry(Catalog),
                (assembly, _) =>
                {
                    Loads.Add(assembly);
                    return ValueTask.FromResult<Assembly?>(typeof(X86FeatureService).Assembly);
                },
                (runAs, implementation) =>
                {
                    Started.Add((runAs.Id, implementation));
                    return new TestAppContext("kazoos-svc") { Manifest = runAs };
                });
        }

        public AppCatalog Catalog { get; }

        public FeatureServiceResolver Resolver { get; }

        public List<string> Loads { get; } = [];

        public List<(string AppId, Type Implementation)> Started { get; } = [];

        public void Install(IEnumerable<AppManifest> manifests) => Catalog.ReplaceContents(Build(manifests));

        public void Dispose() => Resolver.Dispose();

        private static AppCatalog Build(IEnumerable<AppManifest> manifests)
        {
            AppCatalogBuildResult built = AppCatalog.Build(manifests);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(static error => error.Message)));
            return built.Catalog!;
        }
    }
}
