using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using OsX86.Storage;

namespace OsX86.Tests.Storage;

/// <summary><see cref="JsStorageWorkerChannel"/> against a fake JS runtime: the calls it makes to the host module.</summary>
[Trait("Category", "unit")]
public sealed class JsStorageWorkerChannelTests
{
    private sealed class FakeJsObject(string name, List<string> calls, Func<bool> failCreateHost, Func<bool> failDispose) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            calls.Add($"{name}.{identifier}({string.Join(", ", (args ?? []).Select(Describe))})");
            if (identifier == "createHost" && failCreateHost())
            {
                throw new InvalidOperationException("createHost failed");
            }

            if (identifier == "dispose" && failDispose())
            {
                throw new JSDisconnectedException("gone");
            }

            if (typeof(TValue) == typeof(IJSObjectReference))
            {
                return ValueTask.FromResult((TValue)(object)new FakeJsObject(identifier, calls, failCreateHost, failDispose));
            }

            if (typeof(TValue) == typeof(IJSVoidResult))
            {
                return ValueTask.FromResult(default(TValue)!);
            }

            throw new NotSupportedException(typeof(TValue).Name);
        }

        public ValueTask DisposeAsync()
        {
            calls.Add($"{name}.dispose");
            if (failDispose())
            {
                throw new JSDisconnectedException("gone");
            }

            return ValueTask.CompletedTask;
        }

        private static string Describe(object? arg) => arg switch
        {
            null => "null",
            string text => $"\"{text}\"",
            DotNetObjectReference<JsStorageWorkerChannel> => "dotnet-ref",
            _ => arg.GetType().Name,
        };
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public List<string> Calls { get; } = [];

        public bool FailCreateHost { get; set; }

        public bool FailDispose { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add($"js.{identifier}({string.Join(", ", (args ?? []).Select(a => a is string s ? $"\"{s}\"" : a?.GetType().Name ?? "null"))})");
            return ValueTask.FromResult((TValue)(object)new FakeJsObject("module", Calls, () => FailCreateHost, () => FailDispose));
        }
    }

    [Fact]
    public async Task Start_imports_the_module_and_creates_the_host_with_a_dotnet_reference()
    {
        var js = new FakeJsRuntime();
        var channel = new JsStorageWorkerChannel(js);
        var received = new List<string>();
        Assert.False(channel.IsStarted);

        await channel.StartAsync(json => { received.Add(json); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.True(channel.IsStarted);
        Assert.Equal(["js.import(\"./_content/OsX86/storage/storage-host.js\")", "module.createHost(dotnet-ref)"], js.Calls);
        await channel.OnWorkerMessage("{\"type\":\"ready\"}");
        Assert.Equal(["{\"type\":\"ready\"}"], received);
    }

    [Fact]
    public async Task Messages_before_start_are_dropped_and_a_custom_module_path_is_used()
    {
        var js = new FakeJsRuntime();
        var channel = new JsStorageWorkerChannel(js, "./custom/storage-host.js");

        await channel.OnWorkerMessage("{}");
        await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("js.import(\"./custom/storage-host.js\")", js.Calls[0]);
    }

    [Fact]
    public async Task Post_goes_to_the_host()
    {
        var js = new FakeJsRuntime();
        var channel = new JsStorageWorkerChannel(js);
        await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);

        await channel.PostAsync("{\"type\":\"list\",\"id\":1}", CancellationToken.None);

        Assert.Equal("createHost.post(\"{\"type\":\"list\",\"id\":1}\")", js.Calls[^1]);
    }

    [Fact]
    public async Task Post_before_start_and_a_second_start_are_refused()
    {
        var channel = new JsStorageWorkerChannel(new FakeJsRuntime());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel.PostAsync("{}", CancellationToken.None));
        await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_createHost_releases_the_module_and_leaves_the_channel_unstarted()
    {
        var js = new FakeJsRuntime { FailCreateHost = true };
        var channel = new JsStorageWorkerChannel(js);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None));

        Assert.False(channel.IsStarted);
        Assert.Equal("module.dispose", js.Calls[^1]);
        js.FailCreateHost = false;
        await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        Assert.True(channel.IsStarted);
    }

    [Fact]
    public async Task Dispose_terminates_the_worker_and_releases_the_references()
    {
        var js = new FakeJsRuntime();
        var channel = new JsStorageWorkerChannel(js);
        await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);

        await channel.DisposeAsync();
        await channel.DisposeAsync();

        Assert.Equal(["createHost.dispose()", "createHost.dispose", "module.dispose"], js.Calls.Skip(2));
        Assert.False(channel.IsStarted);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await channel.PostAsync("{}", CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_tolerates_a_JS_side_that_is_gone()
    {
        var js = new FakeJsRuntime();
        var channel = new JsStorageWorkerChannel(js);
        await channel.StartAsync(_ => ValueTask.CompletedTask, CancellationToken.None);
        js.FailDispose = true;

        await channel.DisposeAsync();

        Assert.False(channel.IsStarted);
    }

    [Fact]
    public async Task Dispose_before_start_does_nothing()
    {
        var js = new FakeJsRuntime();
        var channel = new JsStorageWorkerChannel(js);

        await channel.DisposeAsync();

        Assert.Empty(js.Calls);
    }

    [Fact]
    public async Task Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => new JsStorageWorkerChannel(null!));
        Assert.Throws<ArgumentException>(() => new JsStorageWorkerChannel(new FakeJsRuntime(), ""));
        var channel = new JsStorageWorkerChannel(new FakeJsRuntime());
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await channel.StartAsync(null!, CancellationToken.None));
    }
}
