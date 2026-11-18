using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Kazo.Os.Platform.Blazor.LazyLoading;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;
using Microsoft.Extensions.DependencyInjection;

namespace OsX86.Os;

/// <summary>
/// PROVISIONAL (decision 291 B, https://github.com/Kazo-ca/Os/issues/621), dev host only: before
/// the OS runs, loads the assemblies of the features' children and adds their descriptors,
/// discovered with their feature (<see cref="FeatureChildDescriptors"/>), to those Kazo's
/// orchestrator reads first. Kazo's own loader is never asked for them, so its refusal of a child
/// alone no longer applies. A feature installed while the page runs is not covered. Remove this
/// file and its call in <c>Program.cs</c> when Os-x86 is pinned on Kazo OS 3.5.1.
/// </summary>
internal static class FeatureChildPreload
{
    /// <summary>Registers the children of the features of the live catalog.</summary>
    /// <param name="services">The services of the host, built.</param>
    /// <param name="cancellationToken">Cancels the loads.</param>
    /// <returns>A line per problem met; empty when every child is registered.</returns>
    [RequiresUnreferencedCode("Resolves entry-point types by name, as Kazo's own discovery does.")]
    public static async Task<IReadOnlyList<string>> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        AppCatalog catalog = services.GetRequiredService<AppCatalog>();
        BuildKnownLazyAppDescriptorRegistry registry = services.GetRequiredService<BuildKnownLazyAppDescriptorRegistry>();
        if (registry.Descriptors is not ConcurrentDictionary<string, AppDescriptor> descriptors)
        {
            // Kazo changed its registry: this workaround no longer fits; the fix of #621 may be in.
            return ["features: the descriptors of Kazo are no longer a ConcurrentDictionary; children of features left to Kazo (#621)."];
        }

        BuildKnownAssemblyLoaderRegistry loader = services.GetRequiredService<BuildKnownAssemblyLoaderRegistry>();
        List<string> problems = [];
        Dictionary<string, Assembly> assemblies = new(StringComparer.Ordinal);
        foreach (string name in catalog.Manifests.Values
            .Where(static manifest => manifest.Parent is not null)
            .Select(static manifest => manifest.EntryPoint?.Assembly)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal))
        {
            BuildKnownAssemblyLoadOutcome outcome = await loader.LoadAsync(name, cancellationToken).ConfigureAwait(false);
            if (outcome.Status == BuildKnownAssemblyLoadStatus.Loaded && outcome.Assembly is { } assembly)
            {
                assemblies[name] = assembly;
            }
            else
            {
                problems.Add($"features: {name} not loaded ({outcome.Status}: {outcome.Detail}); its children are left to Kazo (#621).");
            }
        }

        foreach (AppDescriptor descriptor in FeatureChildDescriptors.Discover(catalog, assemblies))
        {
            descriptors.TryAdd(descriptor.Manifest.Id, descriptor);
        }

        return problems;
    }
}
