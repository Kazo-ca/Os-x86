using System.Text.Json;
using Microsoft.Playwright;
using OsX86.Engine;
using OsX86.Engine.V86;
using OsX86.Storage;
using OsX86.Tests.Images;
using Xunit.Abstractions;

namespace OsX86.Tests.Guest;

/// <summary>
/// A published image pack (<c>images/build/&lt;image&gt;/pack</c>) booted in the engine worker of a
/// real browser, the way work item 6a starts images: the page copies the chunks into the OPFS as
/// <c>osx86/images/&lt;image&gt;/&lt;version&gt;/image.bin</c> (where the storage keeps them), the start
/// command is built in C# from the manifest (<see cref="ImageBoot"/>, <see cref="WorkerProtocol"/>), and
/// the worker reads the kernel, the initramfs and the disks from that one file.
/// </summary>
internal sealed class PackedGuestSession : IAsyncDisposable
{
    private readonly StaticFileServer _server;
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly ITestOutputHelper _output;

    private PackedGuestSession(StaticFileServer server, IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page, ImageManifest manifest, ITestOutputHelper output)
    {
        _server = server;
        _playwright = playwright;
        _browser = browser;
        _context = context;
        Page = page;
        Manifest = manifest;
        _output = output;
    }

    public IPage Page { get; }

    public ImageManifest Manifest { get; }

    /// <summary>The pack directory of an image, or skips the calling test when it is not built.</summary>
    public static string RequirePack(string image, string recipe)
    {
        string pack = Path.Combine(PythonTools.RepositoryRoot, "images", "build", image, "pack");
        Skip.IfNot(File.Exists(Path.Combine(pack, "manifest.json")), $"The {image} pack is not built ({pack} missing): run `python -I {recipe}`.");
        return pack;
    }

    public static async Task<PackedGuestSession> OpenAsync(string pack, ITestOutputHelper output)
    {
        ImageManifest manifest = ImageManifest.Parse(await File.ReadAllTextAsync(Path.Combine(pack, "manifest.json")));
        string root = PythonTools.RepositoryRoot;
        var server = new StaticFileServer(
        [
            ("/engine/", Path.Combine(root, "src", "OsX86", "wwwroot", "engine")),
            ("/v86/", Path.Combine(root, "src", "OsX86", "wwwroot", "v86")),
            ("/pack/", pack),
            ("/page/", Path.Combine(root, "tests", "OsX86.Tests", "Guest")),
        ]);
        server.Start();
        IPlaywright playwright = await Playwright.CreateAsync();
        IBrowser browser = await GuestBrowser.LaunchAsync(playwright);
        IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        page.Console += (_, message) => output.WriteLine($"[browser {message.Type}] {message.Text}");
        page.PageError += (_, error) => output.WriteLine($"[browser error] {error}");
        await page.GotoAsync($"{server.BaseUrl}page/guest.html?host=/engine/v86-host.js");
        await page.WaitForFunctionAsync("() => typeof window.guest === 'object'");
        return new PackedGuestSession(server, playwright, browser, context, page, manifest, output);
    }

    /// <summary>Copies the pack into the OPFS; returns how long it took.</summary>
    public async Task<long> InstallAsync()
    {
        JsonElement result = await Page.EvaluateAsync<JsonElement>("() => window.guest.installPack('/pack/')");
        Assert.Equal(Manifest.Size, result.GetProperty("size").GetInt64());
        return result.GetProperty("ms").GetInt64();
    }

    /// <summary>Starts the engine with the options; returns the milliseconds until the worker reported running.</summary>
    public async Task<long> StartAsync(EngineStartOptions options)
    {
        await Page.EvaluateAsync("() => window.guest.clearTranscripts()");
        JsonElement result = await Page.EvaluateAsync<JsonElement>("([json, timeout]) => window.guest.startWith(json, { startTimeoutMs: timeout })", new object[] { WorkerProtocol.Start(options), 180_000 });
        Assert.Equal("running", result.GetProperty("state").GetString());
        return result.GetProperty("ms").GetInt64();
    }

    public Task<string> WaitForSerialAsync(string needle, int timeoutMs) =>
        Page.EvaluateAsync<string>("([needle, timeout]) => window.guest.waitForText(needle, timeout)", new object[] { needle, timeoutMs });

    /// <summary>Writes a line on the serial console and waits for <paramref name="needle"/>; returns the transcript.</summary>
    public Task<string> SerialAsync(string line, string needle, int timeoutMs = 60_000) =>
        Page.EvaluateAsync<string>("([line, needle, timeout]) => window.guest.sendAndWait(line, needle, timeout)", new object[] { line, needle, timeoutMs });

    public Task<string> TranscriptAsync() => Page.EvaluateAsync<string>("() => window.guest.transcript()");

    public Task<string> WaitForScreenAsync(string needle, int timeoutMs) =>
        Page.EvaluateAsync<string>("([needle, timeout]) => window.guest.waitForScreen(needle, timeout)", new object[] { needle, timeoutMs });

    public Task<JsonElement> QueryDisksAsync() => Page.EvaluateAsync<JsonElement>("() => window.guest.queryDisks()");

    public Task<JsonElement> BenchmarkAsync(int sequentialMiB, int randomReads) =>
        Page.EvaluateAsync<JsonElement>("([mib, reads]) => window.guest.benchmark('hda', mib, reads)", new object[] { sequentialMiB, randomReads });

    public Task<long> OpfsFileSizeAsync(string path) => Page.EvaluateAsync<long>("(path) => window.guest.opfsFileSize(path)", path);

    /// <summary>Stops the engine cleanly (the layers are flushed and closed by the worker).</summary>
    public async Task StopAsync() => Assert.Equal("stopped", await Page.EvaluateAsync<string>("() => window.guest.stop()"));

    /// <summary>The errors the worker reported so far.</summary>
    public Task<JsonElement> ErrorsAsync() => Page.EvaluateAsync<JsonElement>("() => window.guest.events.filter(e => e.type === 'error')");

    /// <summary>Writes the serial transcript to the test output (after a failure).</summary>
    public async Task DumpAsync()
    {
        try
        {
            _output.WriteLine("Serial transcript:");
            _output.WriteLine(await TranscriptAsync());
            _output.WriteLine("Errors: " + (await ErrorsAsync()).GetRawText());
        }
        catch (PlaywrightException exception)
        {
            _output.WriteLine($"(no transcript: {exception.Message})");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        await _browser.DisposeAsync();
        _playwright.Dispose();
        await _server.DisposeAsync();
    }
}
