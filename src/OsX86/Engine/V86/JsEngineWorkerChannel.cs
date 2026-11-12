using Microsoft.JSInterop;

namespace OsX86.Engine.V86;

/// <summary>
/// The worker channel over JS interop: imports the host module of the engine
/// (<c>_content/OsX86/engine/v86-host.js</c>, the one import Kazo OS allows an app), asks it to
/// create the worker, and receives the worker's messages through <see cref="OnWorkerMessage"/>.
/// Not thread-safe: Blazor WebAssembly calls it from its single thread, and <see cref="V86Engine"/>
/// starts it once.
/// </summary>
public sealed class JsEngineWorkerChannel : IEngineWorkerChannel
{
    /// <summary>Where the host serves the engine module (static web assets of the OsX86 library).</summary>
    public const string DefaultModulePath = "./_content/OsX86/engine/v86-host.js";

    private readonly IJSRuntime _js;
    private readonly string _modulePath;
    private IJSObjectReference? _module;
    private IJSObjectReference? _host;
    private DotNetObjectReference<JsEngineWorkerChannel>? _self;
    private Func<string, ValueTask>? _onMessage;
    private bool _starting;
    private bool _disposed;

    /// <summary>Creates the channel on <paramref name="js"/>, importing <paramref name="modulePath"/>.</summary>
    public JsEngineWorkerChannel(IJSRuntime js, string modulePath = DefaultModulePath)
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
            DotNetObjectReference<JsEngineWorkerChannel> self = DotNetObjectReference.Create(this);
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
    public ValueTask PostAsync(string json, CancellationToken cancellationToken) =>
        Host.InvokeVoidAsync("post", cancellationToken, json);

    /// <inheritdoc />
    public ValueTask AttachCanvasAsync(object canvas, CancellationToken cancellationToken) =>
        Host.InvokeVoidAsync("attachCanvas", cancellationToken, canvas);

    /// <inheritdoc />
    public ValueTask DetachCanvasAsync(CancellationToken cancellationToken) =>
        Host.InvokeVoidAsync("detachCanvas", cancellationToken);

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
            catch (Exception exception) when (exception is JSException or JSDisconnectedException or OperationCanceledException or ObjectDisposedException)
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

    private static async ValueTask DisposeQuietlyAsync(IJSObjectReference reference)
    {
        try
        {
            await reference.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or OperationCanceledException or ObjectDisposedException)
        {
            // Same: the JS side is gone.
        }
    }

    private IJSObjectReference Host
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _host ?? throw new InvalidOperationException("The worker is not started.");
        }
    }
}
