using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace OsX86.Tests.Guest;

/// <summary>
/// The `guest` test of work item 2: the engine worker boots the Alpine test image from the OPFS in
/// a real browser, the serial console is read (the ready marker and the prompt) and written (a
/// command whose output comes back), the virtio-console port carries bytes both ways, and the
/// engine pauses, resumes and stops. Skipped with a message when the image is not built
/// (<c>python -I images/alpine-test/build-alpine-test.py</c>) or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
public sealed class AlpineGuestTests(ITestOutputHelper output)
{
    private const int BootTimeoutMs = 180_000;

    [SkippableFact]
    public async Task The_alpine_image_boots_in_the_worker_and_the_serial_console_is_read_and_written()
    {
        string root = RepositoryRoot();
        string imageDirectory = Path.Combine(root, "images", "build", "alpine-test");
        string manifestPath = Path.Combine(imageDirectory, "manifest.json");
        Skip.IfNot(File.Exists(manifestPath), $"Alpine test image not built ({manifestPath} missing): run `python -I images/alpine-test/build-alpine-test.py`.");
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        string prompt = manifest.RootElement.GetProperty("prompt").GetString()!;

        await using var server = new StaticFileServer(
        [
            ("/engine/", Path.Combine(root, "src", "OsX86", "wwwroot", "engine")),
            ("/v86/", Path.Combine(root, "src", "OsX86", "wwwroot", "v86")),
            ("/image/", imageDirectory),
            ("/page/", Path.Combine(root, "tests", "OsX86.Tests", "Guest")),
        ]);
        server.Start();

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await LaunchAsync(playwright);
        await using IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        page.Console += (_, message) => output.WriteLine($"[browser {message.Type}] {message.Text}");
        page.PageError += (_, error) => output.WriteLine($"[browser error] {error}");

        await page.GotoAsync($"{server.BaseUrl}page/guest.html?host=/engine/v86-host.js&image=/image/");
        await page.WaitForFunctionAsync("() => typeof window.guest === 'object'");

        try
        {
            // Boot from the OPFS: the page copies the image there, the worker reads it back.
            JsonElement boot = await page.EvaluateAsync<JsonElement>(
                "async () => { const r = await window.guest.boot({ bootTimeoutMs: " + BootTimeoutMs + " }); return { state: r.state, sizes: window.guest.screenSizes.length }; }");
            Assert.Equal("running", boot.GetProperty("state").GetString());
            Assert.True(boot.GetProperty("sizes").GetInt32() > 0, "the display bridge reported no screen size");

            // Serial console: write a command, read its output.
            string serial = await page.EvaluateAsync<string>("() => window.guest.sendAndWait('echo OSX86-$((6*7))', 'OSX86-42\\r\\n')");
            Assert.Contains("OSX86-READY", serial, StringComparison.Ordinal);
            Assert.Contains(prompt, serial, StringComparison.Ordinal);
            Assert.Contains("OSX86-42\r\n", serial, StringComparison.Ordinal);

            // virtio-console, guest to host then host to guest.
            string virtio = await page.EvaluateAsync<string>("() => window.guest.sendAndWait('echo hello-from-guest > /dev/hvc1', 'hello-from-guest', 20000, 'virtio1')");
            Assert.Contains("hello-from-guest", virtio, StringComparison.Ordinal);
            string echoed = await page.EvaluateAsync<string>(
                "async () => { window.guest.send('read line < /dev/hvc1; echo GOT:$line'); await new Promise(r => setTimeout(r, 800)); window.guest.send('ping-from-host', 'virtio1'); return window.guest.waitForText('GOT:ping-from-host', 20000); }");
            Assert.Contains("GOT:ping-from-host", echoed, StringComparison.Ordinal);

            // Pause stops the output, resume brings it back, stop ends the engine.
            JsonElement lifecycle = await page.EvaluateAsync<JsonElement>(
                "async () => { const g = window.guest; const paused = await g.pause(); const before = g.transcript().length; g.send('echo while-paused'); await new Promise(r => setTimeout(r, 1500)); const silent = g.transcript().length === before; const resumed = await g.resume(); await g.waitForText('while-paused\\r\\n', 20000); const stopped = await g.stop(); return { paused, silent, resumed, stopped, errors: g.events.filter(e => e.type === 'error') }; }");
            Assert.Equal("paused", lifecycle.GetProperty("paused").GetString());
            Assert.True(lifecycle.GetProperty("silent").GetBoolean(), "the guest wrote on the console while paused");
            Assert.Equal("running", lifecycle.GetProperty("resumed").GetString());
            Assert.Equal("stopped", lifecycle.GetProperty("stopped").GetString());
            Assert.Equal(0, lifecycle.GetProperty("errors").GetArrayLength());
        }
        catch (Exception)
        {
            output.WriteLine("Serial transcript:");
            output.WriteLine(await page.EvaluateAsync<string>("() => window.guest.transcript()"));
            throw;
        }
    }

    private async Task<IBrowser> LaunchAsync(IPlaywright playwright)
    {
        string? configured = Environment.GetEnvironmentVariable("OSX86_BROWSER_CHANNEL");
        string?[] channels = configured is null ? [null, "chrome", "msedge"] : [configured == "chromium" ? null : configured];
        var failures = new List<string>();
        foreach (string? channel in channels)
        {
            try
            {
                return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true, Channel = channel });
            }
            catch (PlaywrightException exception)
            {
                failures.Add($"{channel ?? "chromium"}: {exception.Message.Split('\n')[0]}");
            }
        }

        Skip.If(true, "No Chromium-based browser could be launched (install one with `pwsh bin/Release/net10.0/playwright.ps1 install chromium`, or set OSX86_BROWSER_CHANNEL): " + string.Join("; ", failures));
        throw new InvalidOperationException("unreachable");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OsX86.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("OsX86.sln not found above the test directory");
    }
}
