using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;

namespace OsX86.Os;

/// <summary>
/// PROVISIONAL workaround of the dev host (decision 291 B), to remove when Os-x86 is pinned on Kazo
/// OS 3.5.1, which fixes https://github.com/Kazo-ca/Os/issues/621. Kazo 3.5.0 discovers the entry
/// point of a lazily loaded app with a catalog of that manifest alone
/// (<c>BuildKnownLazyAppDescriptorRegistry</c>), which refuses a feature's child
/// (<c>catalog.feature.parent.missing</c>): the <c>x86</c> command of the Feature was « command not
/// found ». This discovers each child together with its feature, as the fix of #621 will.
/// </summary>
internal static class FeatureChildDescriptors
{
    /// <summary>
    /// Discovers the children of the features of <paramref name="catalog"/> whose entry-point
    /// assembly is in <paramref name="assemblies"/>; the others are left to Kazo.
    /// </summary>
    /// <param name="catalog">The apps of the host.</param>
    /// <param name="assemblies">The loaded assemblies, by file name (<c>OsX86.Feature.dll</c>).</param>
    /// <returns>One descriptor per child found, the way Kazo would describe it.</returns>
    [RequiresUnreferencedCode("Resolves entry-point types by name, as Kazo's own discovery does.")]
    public static IReadOnlyList<AppDescriptor> Discover(AppCatalog catalog, IReadOnlyDictionary<string, Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(assemblies);
        List<AppDescriptor> found = [];
        foreach (AppManifest feature in catalog.Manifests.Values.Where(static manifest => manifest.Kind == AppKind.Feature))
        {
            AppManifest[] children =
            [
                .. catalog.Manifests.Values.Where(manifest =>
                    string.Equals(manifest.Parent, feature.Id, StringComparison.Ordinal)
                    && manifest.EntryPoint is { } entryPoint
                    && assemblies.ContainsKey(entryPoint.Assembly)),
            ];
            if (children.Length == 0
                || AppCatalog.Build([feature, .. children]).Catalog is not { } unit
                || AppEntryPointDiscovery.Discover(unit, assemblies).Descriptors is not { } descriptors)
            {
                continue;
            }

            found.AddRange(children.Select(child => descriptors.GetValueOrDefault(child.Id)).OfType<AppDescriptor>());
        }

        return found;
    }
}
