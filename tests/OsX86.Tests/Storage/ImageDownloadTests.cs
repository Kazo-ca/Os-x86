using System.Text.Json;
using Microsoft.Playwright;
using OsX86.Storage;
using Xunit.Abstractions;

namespace OsX86.Tests.Storage;

/// <summary>
/// The `guest` test of work item 3 (trial E3 made a test): in a real browser, the storage worker
/// downloads an image of 800 MiB served by a local server that answers 503 and cuts
/// connections, verifies every chunk, writes it in the OPFS; a second worker is refused the lock;
/// after a page reload the download resumes without fetching the verified chunks again; the
/// image verifies in place, lists, counts in the estimate and deletes. The image is generated on
/// the fly, never written to disk. Size: OSX86_STORAGE_TEST_MIB (default 800). Not part of the
/// `unit` suite; skipped with a message when no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
public sealed class ImageDownloadTests(ITestOutputHelper output)
{
    private const int DownloadTimeoutMs = 600_000;

    [SkippableFact]
    public async Task An_image_downloads_verifies_and_resumes_after_cuts_and_a_reload()
    {
        string root = RepositoryRoot();
        long size = (long.TryParse(Environment.GetEnvironmentVariable("OSX86_STORAGE_TEST_MIB"), out long mib) && mib > 0 ? mib : 800) * 1024 * 1024 + 4096;
        await using var server = new ChunkImageServer(
        [
            ("/storage/", Path.Combine(root, "src", "OsX86", "wwwroot", "storage")),
            ("/page/", Path.Combine(root, "tests", "OsX86.Tests", "Storage")),
        ], size);
        server.Start();
        output.WriteLine($"Image: {size} bytes, {server.ChunkCount} chunks, manifest {server.ManifestSha256}");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await LaunchAsync(playwright);
        await using IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        page.Console += (_, message) => output.WriteLine($"[browser {message.Type}] {message.Text}");
        page.PageError += (_, error) => output.WriteLine($"[browser error] {error}");
        page.SetDefaultTimeout(DownloadTimeoutMs);
        await page.GotoAsync($"{server.BaseUrl}page/storage.html?host=/storage/storage-host.js");
        await page.WaitForFunctionAsync("() => typeof window.storage === 'object'");
        string source = JsonSerializer.Serialize(new { baseUrl = server.ImageUrl, sha256 = server.ManifestSha256, parallelism = 2 });

        // 1. Download through 503s and cut connections (E3 scenario a): nothing lost but the chunk in progress.
        server.ServiceUnavailableNext = 2;
        server.CutNextResponses = 3;
        server.CutFromResponse = 4;
        JsonElement first = await page.EvaluateAsync<JsonElement>($"() => window.storage.download({source})");
        IReadOnlyList<ChunkImageServer.ChunkRequest> requests = server.Requests;
        output.WriteLine($"First download: {first.GetProperty("seconds").GetDouble():F1} s, {requests.Count} chunk requests, {requests.Count(r => r.Status == 503)} x 503, {requests.Count(r => r.Cut)} cut");
        Assert.Equal(server.ChunkCount, first.GetProperty("chunksRequested").GetInt32());
        Assert.Equal(0, first.GetProperty("verifiedBefore").GetInt32());
        Assert.Equal(2, requests.Count(r => r.Status == 503));
        Assert.Equal(3, requests.Count(r => r.Cut));
        Assert.Equal(server.ChunkCount, requests.Count(r => r.Status == 200 && !r.Cut));
        Assert.True(first.GetProperty("bytesOnDisk").GetInt64() >= size, "the version directory is smaller than the payload");
        JsonElement lastProgress = first.GetProperty("progress").EnumerateArray().Last();
        Assert.Equal(server.ChunkCount, lastProgress.GetProperty("chunksVerified").GetInt32());
        Assert.Equal(size, lastProgress.GetProperty("bytesVerified").GetInt64());
        Assert.True(first.GetProperty("progress").EnumerateArray().Any(p => p.GetProperty("bytesPerSecond").GetDouble() > 0), "no speed reported");

        // 2. The stored image verifies in place and is listed complete.
        JsonElement verified = await page.EvaluateAsync<JsonElement>("() => window.storage.verify('storage-test', '1.0.0')");
        Assert.Equal(server.ChunkCount, verified.GetProperty("chunksTotal").GetInt32());
        Assert.Equal(0, verified.GetProperty("bad").GetArrayLength());
        JsonElement listed = await page.EvaluateAsync<JsonElement>("() => window.storage.list()");
        JsonElement entry = Assert.Single(listed.GetProperty("images").EnumerateArray());
        Assert.Equal("storage-test", entry.GetProperty("image").GetString());
        Assert.True(entry.GetProperty("complete").GetBoolean());
        Assert.Equal(1, entry.GetProperty("format").GetInt32());
        Assert.Equal(size, entry.GetProperty("size").GetInt64());
        JsonElement estimate = await page.EvaluateAsync<JsonElement>("() => window.storage.estimate()");
        Assert.True(estimate.GetProperty("repositoryBytes").GetInt64() >= size);
        Assert.True(estimate.GetProperty("usage").GetInt64() >= size, $"estimate() usage {estimate.GetProperty("usage").GetInt64()} < {size}");

        // 3. A complete image downloads again without any chunk request.
        server.ClearRequests();
        JsonElement again = await page.EvaluateAsync<JsonElement>($"() => window.storage.download({source})");
        Assert.Equal(0, again.GetProperty("chunksRequested").GetInt32());
        Assert.Empty(server.Requests);

        // 4. Delete, then start over with one stream; while it runs, a second worker is refused the lock.
        await page.EvaluateAsync("() => window.storage.delete('storage-test', '1.0.0')");
        Assert.Empty((await page.EvaluateAsync<JsonElement>("() => window.storage.list()")).GetProperty("images").EnumerateArray());
        server.ClearRequests();
        string slow = JsonSerializer.Serialize(new { baseUrl = server.ImageUrl, sha256 = server.ManifestSha256, parallelism = 1 });
        await page.EvaluateAsync($"() => {{ window.secondRun = window.storage.download({slow}).catch(e => ({{ error: e.code }})); }}");
        await WaitUntilAsync(() => server.Requests.Count(r => r.Status == 200 && !r.Cut) >= 2, TimeSpan.FromSeconds(120));
        JsonElement conflict = await page.EvaluateAsync<JsonElement>($"() => window.storage.conflictingDownload({source})");
        Assert.Equal("error", conflict.GetProperty("type").GetString());
        Assert.Equal(StorageErrorCodes.Locked, conflict.GetProperty("code").GetString());

        // 5. Reload the page in the middle: the worker dies, the verified chunks stay, the download resumes from the state.
        await WaitUntilAsync(() => server.Requests.Count(r => r.Status == 200 && !r.Cut) >= Math.Min(5, server.ChunkCount - 1), TimeSpan.FromSeconds(120));
        int servedBeforeReload = server.Requests.Count(r => r.Status == 200 && !r.Cut);
        await page.ReloadAsync();
        await page.WaitForFunctionAsync("() => typeof window.storage === 'object'");
        JsonElement state = await page.EvaluateAsync<JsonElement>("() => window.storage.readState('storage-test', '1.0.0')");
        int verifiedAtReload = state.GetProperty("verified").GetArrayLength();
        output.WriteLine($"Reload after {servedBeforeReload} chunks served: {verifiedAtReload} verified in the state");
        Assert.False(state.GetProperty("complete").GetBoolean());
        Assert.InRange(verifiedAtReload, 1, servedBeforeReload);
        server.ClearRequests();
        JsonElement resumed = await page.EvaluateAsync<JsonElement>($"() => window.storage.download({source})");
        requests = server.Requests;
        output.WriteLine($"Resumed download: {resumed.GetProperty("seconds").GetDouble():F1} s, {requests.Count} chunk requests");
        Assert.Equal(verifiedAtReload, resumed.GetProperty("verifiedBefore").GetInt32());
        Assert.Equal(server.ChunkCount - verifiedAtReload, resumed.GetProperty("chunksRequested").GetInt32());
        Assert.False(resumed.GetProperty("reset").GetBoolean());
        Assert.Equal(server.ChunkCount - verifiedAtReload, requests.Count(r => r.Status == 200 && !r.Cut));
        Assert.DoesNotContain(requests, r => state.GetProperty("verified").EnumerateArray().Any(v => v.GetInt32() == r.Index));
        JsonElement verifiedAgain = await page.EvaluateAsync<JsonElement>("() => window.storage.verify('storage-test', '1.0.0')");
        Assert.Equal(0, verifiedAgain.GetProperty("bad").GetArrayLength());

        // 6. Cleanup: the repository is empty afterwards.
        await page.EvaluateAsync("() => window.storage.delete('storage-test', '1.0.0')");
        Assert.Empty((await page.EvaluateAsync<JsonElement>("() => window.storage.list()")).GetProperty("images").EnumerateArray());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the server did not see the expected chunk requests in time");
            }

            await Task.Delay(50);
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
