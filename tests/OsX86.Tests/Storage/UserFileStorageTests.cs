using System.Text.Json;
using Microsoft.Playwright;
using OsX86.Tests.Guest;
using OsX86.Tests.Images;
using Xunit.Abstractions;

namespace OsX86.Tests.Storage;

/// <summary>
/// The files of each user in a real browser (work item 8, decision 287 A): the storage worker writes
/// and reads <c>osx86/users/&lt;guid&gt;/&lt;name&gt;.json</c>, refuses another directory or name,
/// and its journal gives back the whole new text of a write cut while it replaced the file. Not part
/// of the <c>unit</c> suite; skipped with a message when no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
public sealed class UserFileStorageTests(ITestOutputHelper output)
{
    private const string User = "0000000000000000000000000000a001";
    private const string Directory = "osx86/users/" + User;

    [SkippableFact]
    public async Task The_worker_keeps_the_files_of_a_user_and_recovers_a_write_cut_midway()
    {
        string root = PythonTools.RepositoryRoot;
        await using var server = new ChunkImageServer(
        [
            ("/storage/", Path.Combine(root, "src", "OsX86.Feature", "wwwroot", "storage")),
            ("/page/", Path.Combine(root, "tests", "OsX86.Tests", "Storage")),
        ], 64 * 1024);
        server.Start();

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await GuestBrowser.LaunchAsync(playwright);
        await using IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        page.Console += (_, message) => output.WriteLine($"[browser {message.Type}] {message.Text}");
        await page.GotoAsync($"{server.BaseUrl}page/storage.html?host=/storage/storage-host.js");
        await page.WaitForFunctionAsync("() => typeof window.storage === 'object'");

        // Nothing yet, then a whole write read back; the journal is gone once the write is done.
        Assert.Equal(JsonValueKind.Null, (await CommandAsync(page, Read("registry.json"))).GetProperty("text").ValueKind);
        Assert.Equal("done", (await CommandAsync(page, Write("registry.json", "{\"format\":1}"))).GetProperty("type").GetString());
        Assert.Equal("{\"format\":1}", (await CommandAsync(page, Read("registry.json"))).GetProperty("text").GetString());
        Assert.False(await page.EvaluateAsync<bool>($"() => window.storage.opfsExists('{Directory}/registry.json.tmp')"));

        // A write cut after its journal: the file is half written, the journal whole.
        await page.EvaluateAsync($"() => window.storage.opfsWrite('{Directory}/registry.json', '{{\"format\":1,\"vms\":[')");
        await page.EvaluateAsync($"() => window.storage.opfsWrite('{Directory}/registry.json.tmp', '{{\"format\":1,\"vms\":[]}}')");
        Assert.Equal("{\"format\":1,\"vms\":[]}", (await CommandAsync(page, Read("registry.json"))).GetProperty("text").GetString());

        // A write cut in its journal: the file still has the old text.
        await page.EvaluateAsync($"() => window.storage.opfsWrite('{Directory}/network-rules.json', '{{\"format\":1}}')");
        await page.EvaluateAsync($"() => window.storage.opfsWrite('{Directory}/network-rules.json.tmp', '{{\"form')");
        Assert.Equal("{\"format\":1}", (await CommandAsync(page, Read("network-rules.json"))).GetProperty("text").GetString());

        // Only a user identifier, a plain .json name and a JSON document are accepted.
        Assert.Equal("protocol", (await CommandAsync(page, new { type = "read-user-file", user = "../images", name = "registry.json" })).GetProperty("error").GetString());
        Assert.Equal("protocol", (await CommandAsync(page, new { type = "read-user-file", user = new string('0', 32), name = "registry.json" })).GetProperty("error").GetString());
        Assert.Equal("protocol", (await CommandAsync(page, Read("registry.json.tmp"))).GetProperty("error").GetString());
        Assert.Equal("protocol", (await CommandAsync(page, Write("registry.json", "not json"))).GetProperty("error").GetString());
        Assert.Equal("{\"format\":1,\"vms\":[]}", (await CommandAsync(page, Read("registry.json"))).GetProperty("text").GetString());
    }

    private static object Read(string name) => new { type = "read-user-file", user = User, name };

    private static object Write(string name, string text) => new { type = "write-user-file", user = User, name, text };

    private static Task<JsonElement> CommandAsync(IPage page, object command) =>
        page.EvaluateAsync<JsonElement>("command => window.storage.command(command)", command);
}
