using Microsoft.Playwright;
using OsX86.Engine;
using OsX86.Engine.V86;
using OsX86.Vm;

namespace OsX86.Tests.Vm;

/// <summary>
/// An <see cref="IEngineWorkerChannel"/> on a real engine worker of the test page <c>Vm/vm.html</c>:
/// the page starts the worker through the engine's own host module and forwards its messages to
/// this process through one Playwright binding, numbered so that they are delivered in order. The
/// <see cref="V86Engine"/> on top is the production one; only the JS interop is replaced.
/// </summary>
internal sealed class PlaywrightEngineWorkerChannel : IEngineWorkerChannel
{
    private readonly IPage _page;
    private readonly PlaywrightWorkerHub _hub;
    private int _host;

    public PlaywrightEngineWorkerChannel(IPage page, PlaywrightWorkerHub hub)
    {
        _page = page;
        _hub = hub;
    }

    public async ValueTask StartAsync(Func<string, ValueTask> onMessage, CancellationToken cancellationToken)
    {
        // The handler is registered before the worker exists: its first message (ready) is never lost.
        int host = await _hub.ReserveAsync(onMessage);
        _host = await _page.EvaluateAsync<int>("() => window.vm.createHost()");
        PlaywrightWorkerHub.Bind(host, _host);
    }

    public async ValueTask PostAsync(string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.EvaluateAsync("([id, json]) => window.vm.post(id, json)", new object[] { _host, json });
    }

    public ValueTask AttachCanvasAsync(object canvas, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DetachCanvasAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_host != 0)
        {
            try
            {
                await _page.EvaluateAsync("id => window.vm.disposeHost(id)", _host);
            }
            catch (PlaywrightException)
            {
                // The page is gone: so is the worker.
            }
        }
    }
}

/// <summary>
/// The one binding of the page, shared by every worker: it reorders the messages of each worker by
/// their sequence number (binding calls may be dispatched out of order) and hands them, one at a
/// time and off the Playwright thread, to the engine of that worker.
/// </summary>
internal sealed class PlaywrightWorkerHub
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Worker> _workers = [];
    private int _nextHost;

    public static async Task<PlaywrightWorkerHub> AttachAsync(IPage page)
    {
        var hub = new PlaywrightWorkerHub();
        await page.ExposeFunctionAsync<int, long, string, bool>("osx86WorkerMessage", hub.OnMessage);
        return hub;
    }

    /// <summary>Registers the handler of the next worker (the page numbers hosts 1, 2, 3, ... in creation order).</summary>
    public Task<int> ReserveAsync(Func<string, ValueTask> onMessage)
    {
        lock (_lock)
        {
            int host = ++_nextHost;
            _workers[host] = new Worker(onMessage);
            return Task.FromResult(host);
        }
    }

    public static void Bind(int reservation, int host)
    {
        if (reservation != host)
        {
            throw new InvalidOperationException($"The page created host {host} for reservation {reservation}: hosts must be created one at a time.");
        }
    }

    private bool OnMessage(int host, long sequence, string json)
    {
        lock (_lock)
        {
            Worker worker = _workers[host];
            worker.Waiting[sequence] = json;
            while (worker.Waiting.Remove(worker.Next, out string? next))
            {
                worker.Next++;
                worker.Queue.Writer.TryWrite(next);
            }
        }

        return true;
    }

    private sealed class Worker
    {
        public Worker(Func<string, ValueTask> handler)
        {
            _ = Task.Run(async () =>
            {
                await foreach (string message in Queue.Reader.ReadAllAsync())
                {
                    await handler(message);
                }
            });
        }

        public System.Threading.Channels.Channel<string> Queue { get; } = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public Dictionary<long, string> Waiting { get; } = [];

        public long Next { get; set; }
    }
}

/// <summary>Creates one <see cref="V86Engine"/> per run, each on its own worker of the test page.</summary>
internal sealed class PlaywrightEngineFactory(IPage page, PlaywrightWorkerHub hub) : IEngineFactory
{
    public IEngine Create() => new V86Engine(new PlaywrightEngineWorkerChannel(page, hub));
}
