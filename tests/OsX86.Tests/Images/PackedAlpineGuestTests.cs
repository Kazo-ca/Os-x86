using System.Text.Json;
using Microsoft.Playwright;
using OsX86.Tests.Guest;
using Xunit.Abstractions;

namespace OsX86.Tests.Images;

/// <summary>
/// The <c>guest</c> test of work item 4: the Alpine test image in its published form (the chunks
/// and the manifest of <c>images/build/alpine-test/pack</c>) is checked with <c>pack-image.py</c>,
/// its entries are extracted, and the engine worker boots them from the OPFS in a real browser
/// up to the serial prompt. Skipped with a message when the pack is not built
/// (<c>python -I images/alpine-test/build-alpine-test.py</c>) or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
public sealed class PackedAlpineGuestTests(ITestOutputHelper output)
{
    private const int BootTimeoutMs = 180_000;

    [SkippableFact]
    public async Task The_packed_alpine_image_checks_extracts_and_boots_to_the_serial_prompt()
    {
        string root = PythonTools.RepositoryRoot;
        string pack = Path.Combine(root, "images", "build", "alpine-test", "pack");
        Skip.IfNot(File.Exists(Path.Combine(pack, "manifest.json")), $"Alpine test pack not built ({pack} missing): run `python -I images/alpine-test/build-alpine-test.py`.");
        PythonTools.Require();

        // The published form is sound: chunks, seek tables and manifest.sha256 recomputed.
        Assert.Contains("OK alpine-test", PythonTools.RunOrThrow(PythonTools.PackImage, "check", pack), StringComparison.Ordinal);
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(pack, "manifest.json")));
        JsonElement boot = manifest.RootElement.GetProperty("boot");
        Assert.Equal(33_554_432, manifest.RootElement.GetProperty("chunkSize").GetInt32());

        // The entries, decompressed, in the shape the test page expects (a build description).
        string imageDirectory = Path.Combine(Path.GetTempPath(), "osx86-packed-alpine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(imageDirectory);
        try
        {
            PythonTools.RunOrThrow(PythonTools.PackImage, "extract", pack, "--to", imageDirectory);
            string readyMarker = boot.GetProperty("readyMarker").GetString()!;
            string prompt = boot.GetProperty("prompt").GetString()!;
            var description = new
            {
                kernel = new { file = "kernel", size = new FileInfo(Path.Combine(imageDirectory, "kernel")).Length },
                initrd = new { file = "initramfs", size = new FileInfo(Path.Combine(imageDirectory, "initramfs")).Length },
                cmdline = boot.GetProperty("cmdline").GetString(),
                readyMarker,
                prompt,
            };
            await File.WriteAllTextAsync(Path.Combine(imageDirectory, "manifest.json"), JsonSerializer.Serialize(description));

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
                JsonElement result = await page.EvaluateAsync<JsonElement>(
                    "async () => { const r = await window.guest.boot({ bootTimeoutMs: " + BootTimeoutMs + " }); return { state: r.state }; }");
                Assert.Equal("running", result.GetProperty("state").GetString());
                string serial = await page.EvaluateAsync<string>("() => window.guest.sendAndWait('echo OSX86-$((5*5))', 'OSX86-25\\r\\n')");
                Assert.Contains(readyMarker, serial, StringComparison.Ordinal);
                Assert.Contains(prompt, serial, StringComparison.Ordinal);
                Assert.Equal("stopped", await page.EvaluateAsync<string>("() => window.guest.stop()"));
            }
            catch (Exception)
            {
                output.WriteLine("Serial transcript:");
                output.WriteLine(await page.EvaluateAsync<string>("() => window.guest.transcript()"));
                throw;
            }
        }
        finally
        {
            try
            {
                Directory.Delete(imageDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<IBrowser> LaunchAsync(IPlaywright playwright)
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
}
