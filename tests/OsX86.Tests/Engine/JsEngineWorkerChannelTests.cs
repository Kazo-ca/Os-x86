using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using OsX86.Engine.V86;

namespace OsX86.Tests.Engine;

/// <summary><see cref="JsEngineWorkerChannel"/> against a fake JS runtime: the calls it makes to the host module.</summary>
[Trait("Category", "unit")]
public sealed class JsEngineWorkerChannelTests
{
    private sealed class FakeJsObject(string name, List<string> calls, Func<bool> failCreateHost) : IJSObjectReference
    {
        public bool Disposed { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            calls.Add($"{name}.{identifier}({string.Join(", ", (args ?? []).Select(Describe))})");
            if (identifier == "createHost" && failCreateHost())
            {
                throw new InvalidOperationException("createHost failed");
            }

            if (typeof(TValue) == typeof(IJSObjectReference))
            {
                return ValueTask.FromResult((TValue)(object)new FakeJsObject(identifier, calls, failCreateHost));
            }

            if (typeof(TValue) == typeof(IJSVoidResult))
            {
                return ValueTask.FromResult(default(TValue)!);
            }

            throw new NotSupportedException(typeof(TValue).Name);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            calls.Add($"{name}.dispose");
            return ValueTask.CompletedTask;
        }

        private static string Describe(object? arg) => arg switch
        {
            null => "null",
            string text => $"\"{text}\"",
            DotNetObjectReference<JsEngineWorkerChannel> => "dotnet-ref",
            _ => arg.GetType().Name,
        };
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public List<string> Calls { get; } = [];

        public bool FailCreateHost { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add($"js.{identifier}({string.Join(", ", (args ?? []).Select(a => a is string s ? $"\"{s}\"" : a?.GetType().Name ?? "null"))})");
            return ValueTask.FromResult((TValue)(object)new FakeJsObject("module", Calls, () => FailCreateHost));
        }
    }

    [Fact]
    public async Task Start_imports_the_module_and_creates_the_host_with_a_dotnet_reference()
    {
        var js = new FakeJsRuntime();
        var channel = new JsEngineWorkerChannel(js);
        var received = new List<string>();
        Assert.False(channel.IsStarted);

        await channel.StartAsync(json => { received.Add(json); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.True(channel.IsStarted);
        Assert.Equal(["js.import(\"./_content/OsX86/engine/v86-host.js\")", "module.createHost(dotnet-ref)"], js.Calls);

        await channel.OnWorkerMessage("{\"type\":\"ready\"}");
        Assert.Equal(["{\"type\":\"ready\"}"], received);
    }

    [Fact]
    public async Task Commands_and_the_canvas_go_to_the_host_object()
    {
        var js = new FakeJsRuntime();
        var channel = new JsEngineWorkerChannel(js, "./custom/host.js");
        await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        js.Calls.Clear();

        await channel.PostAsync("{\"type\":\"stop\"}", CancellationToken.None);
        var canvas = new object();
        await channel.AttachCanvasAsync(canvas, CancellationToken.None);
        await channel.DetachCanvasAsync(CancellationToken.None);

        Assert.Equal(["createHost.post(\"{\"type\":\"stop\"}\")", "createHost.attachCanvas(Object)", "createHost.detachCanvas()"], js.Calls);
    }

    [Fact]
    public async Task The_channel_refuses_use_before_start_and_a_second_start()
    {
        var channel = new JsEngineWorkerChannel(new FakeJsRuntime());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel.PostAsync("{}", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await channel.StartAsync(null!, CancellationToken.None));
        await channel.OnWorkerMessage("ignored before start");
        await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_releases_the_host_the_module_and_the_dotnet_reference()
    {
        var js = new FakeJsRuntime();
        var channel = new JsEngineWorkerChannel(js);
        await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        js.Calls.Clear();

        await channel.DisposeAsync();
        await channel.DisposeAsync();

        Assert.Equal(["createHost.dispose()", "createHost.dispose", "module.dispose"], js.Calls);
        Assert.False(channel.IsStarted);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await channel.PostAsync("{}", CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_host_creation_releases_the_module_and_allows_a_retry()
    {
        var js = new FakeJsRuntime { FailCreateHost = true };
        var channel = new JsEngineWorkerChannel(js);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None));
        Assert.False(channel.IsStarted);
        Assert.Contains("module.dispose", js.Calls);

        js.FailCreateHost = false;
        await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.True(channel.IsStarted);
    }

    [Fact]
    public void The_constructor_checks_its_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new JsEngineWorkerChannel(null!));
        Assert.Throws<ArgumentException>(() => new JsEngineWorkerChannel(new FakeJsRuntime(), ""));
    }
}
