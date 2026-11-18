using Microsoft.JSInterop;
using OsX86.Storage;
using OsX86.Vm;

namespace OsX86.Feature;

/// <summary>
/// How the Feature installs the emulator and the storage in the page (work item 8, decision 288 A).
/// Both need the <c>IJSRuntime</c> of the page, which Kazo 3.5.0 gives to the constructor of an entry
/// point on the main thread, never to a feature service: the service child <c>ca.kazo.x86.host</c>
/// installs them at login and the <c>x86</c> command when it runs first, so that an app that gets
/// <c>Ix86</c> finds them.
/// </summary>
internal static class X86Host
{
    /// <summary>
    /// Installs the v86 engine and the image store of the OPFS when no host did yet; a host installed
    /// before (the dev host, with its image catalog and its test relay) is kept. The image catalog of
    /// the Feature is empty: the hosting of the images is not settled (work item 6b), so an installed
    /// Feature boots what is already in the OPFS and downloads nothing.
    /// </summary>
    /// <param name="js">The JS runtime of the page.</param>
    /// <returns>Whether this call installed them.</returns>
    public static bool EnsureInstalled(IJSRuntime js)
    {
        ArgumentNullException.ThrowIfNull(js);
        return VmHost.TryConfigure(() => VmHost.ForBrowser(js, ImageCatalog.Empty));
    }
}
