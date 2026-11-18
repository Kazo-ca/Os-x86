using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Microsoft.JSInterop;

namespace OsX86.Feature;

/// <summary>
/// The service child <c>ca.kazo.x86.host</c> (work item 8, decision 288 A): hidden, started on its own
/// at each login, on the main thread of the page. Kazo builds it with the <c>IJSRuntime</c> of the
/// page (ADR 0034 and 0055 of Kazo), which a feature service never gets; it installs the emulator and
/// the storage (<see cref="X86Host"/>), then runs until the session ends. Before it started, <c>Ix86</c>
/// answers <c>engine-unavailable</c>.
/// </summary>
public sealed class X86HostService : ServiceAppBase
{
    private readonly IJSRuntime _js;

    /// <summary>Creates the service for the manifest the OS loaded, with the JS runtime of the page.</summary>
    /// <param name="manifest">This child's manifest.</param>
    /// <param name="js">The JS runtime of the page.</param>
    public X86HostService(AppManifest manifest, IJSRuntime js)
        : base(manifest)
    {
        ArgumentNullException.ThrowIfNull(js);
        _js = js;
    }

    /// <inheritdoc />
    protected override async Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        X86Host.EnsureInstalled(_js);
        try
        {
            // The VMs live in the page, not in this process: there is nothing else to do until the session ends.
            await Task.Delay(Timeout.InfiniteTimeSpan, sessionCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sessionCancellationToken.IsCancellationRequested)
        {
            // Logout or shutdown: the emulator stays installed for the page.
        }
    }
}
