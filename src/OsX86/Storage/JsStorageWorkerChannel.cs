using Microsoft.JSInterop;

namespace OsX86.Storage;

/// <summary>
/// The storage worker channel over JS interop, on the model of the engine's
/// <see cref="OsX86.Engine.V86.JsEngineWorkerChannel"/>: imports the host module
/// (<c>_content/OsX86/storage/storage-host.js</c>), asks it to create the worker, and receives
/// the worker's messages through <see cref="OnWorkerMessage"/>. Not thread-safe: Blazor
/// WebAssembly calls it from its single thread, and <see cref="ImageStore"/> starts it once.
/// </summary>
public sealed class JsStorageWorkerChannel : IStorageWorkerChannel
{
    /// <summary>Where the host serves the storage module (static web assets of the OsX86 library).</summary>
    public const string DefaultModulePath = "./_content/OsX86/storage/storage-host.js";

    private readonly IJSRuntime _js;
    private readonly string _modulePath;
    private IJSObjectReference? _module;
    private IJSObjectReference? _host;
    private DotNetObjectReference<JsStorageWorkerChannel>? _self;
    private Func<string, ValueTask>? _onMessage;
    private bool _starting;
    private bool _disposed;

    /// <summary>Creates the channel on <paramref name="js"/>, importing <paramref name="modulePath"/>.</summary>
    public JsStorageWorkerChannel(IJSRuntime js, string modulePath = DefaultModulePath)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentException.ThrowIfNullOrEmpty(modulePath);
        _js = js;
        _modulePath = modulePath;
    }

    /// <summary>Whether the worker has been created.</summary>
    public bool IsStarted => _host is not null;

    /// <inheritdoc />
    public async ValueTask StartAsync(Func<string, ValueTask> onMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host is not null || _starting)
        {
            throw new InvalidOperationException("The worker is already started.");
        }

        _starting = true;
        try
        {
            IJSObjectReference module = await _js.InvokeAsync<IJSObjectReference>("import", cancellationToken, _modulePath).ConfigureAwait(false);
            DotNetObjectReference<JsStorageWorkerChannel> self = DotNetObjectReference.Create(this);
            try
            {
                IJSObjectReference host = await module.InvokeAsync<IJSObjectReference>("createHost", cancellationToken, self).ConfigureAwait(false);
                _onMessage = onMessage;
                _module = module;
                _self = self;
                _host = host;
            }
            catch (Exception)
            {
                self.Dispose();
                await DisposeQuietlyAsync(module).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _starting = false;
        }
    }

    /// <inheritdoc />
    public ValueTask PostAsync(string json, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IJSObjectReference host = _host ?? throw new InvalidOperationException("The worker is not started.");
        return host.InvokeVoidAsync("post", cancellationToken, json);
    }

    /// <summary>Called by the host module for every message the worker posts.</summary>
    [JSInvokable]
    public Task OnWorkerMessage(string json) => _onMessage is null ? Task.CompletedTask : _onMessage(json).AsTask();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IJSObjectReference? host = _host;
        IJSObjectReference? module = _module;
        _host = null;
        _module = null;
        _onMessage = null;
        if (host is not null)
        {
            try
            {
                await host.InvokeVoidAsync("dispose").ConfigureAwait(false);
            }
            catch (Exception exception) when (IsGone(exception))
            {
                // The worker or the page is already gone: nothing left to terminate.
            }

            await DisposeQuietlyAsync(host).ConfigureAwait(false);
        }

        if (module is not null)
        {
            await DisposeQuietlyAsync(module).ConfigureAwait(false);
        }

        _self?.Dispose();
        _self = null;
    }

    private static bool IsGone(Exception exception) =>
        exception is JSException or JSDisconnectedException or OperationCanceledException or ObjectDisposedException;

    private static async ValueTask DisposeQuietlyAsync(IJSObjectReference reference)
    {
        try
        {
            await reference.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsGone(exception))
        {
            // Same: the JS side is gone.
        }
    }
}
