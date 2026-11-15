using System.Diagnostics;
using System.Text.Json;
using OsX86.Engine;
using Xunit.Abstractions;

namespace OsX86.Tests.Guest;

/// <summary>
/// The <c>guest</c> test of FreeDOS (work item 6a, trial E14): the published pack is copied into the
/// OPFS, the engine boots its disk as <c>hda</c> (frames decompressed on demand, writes in memory),
/// and the VGA text screen is read, as E14 read DOS, until <c>FDAUTO.BAT</c> has printed its marker
/// and the prompt. The Terminal path of Kazo is work item 11. Skipped when the pack is not built
/// (<c>python -I images/freedos/build-freedos.py</c>) or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
public sealed class FreeDosGuestTests(ITestOutputHelper output)
{
    private const int BootTimeoutMs = 120_000;

    [SkippableFact]
    public async Task FreeDos_boots_from_its_disk_in_the_opfs_to_its_prompt()
    {
        string pack = PackedGuestSession.RequirePack("freedos", "images/freedos/build-freedos.py");
        await using PackedGuestSession session = await PackedGuestSession.OpenAsync(pack, output);
        try
        {
            long installMs = await session.InstallAsync();
            EngineStartOptions options = ImageBoot.CreateStartOptions(session.Manifest);
            Assert.Equal(EngineBootKind.Dos, options.Kind);

            var clock = Stopwatch.StartNew();
            await session.StartAsync(options);
            string screen = await session.WaitForScreenAsync(session.Manifest.Boot!.ReadyMarker!, BootTimeoutMs);
            screen = await session.WaitForScreenAsync("C:\\>", 30_000);
            long bootMs = clock.ElapsedMilliseconds;
            Assert.Contains("Os-x86 FreeDOS", screen, StringComparison.Ordinal);

            JsonElement disks = await session.QueryDisksAsync();
            JsonElement hda = disks.GetProperty("disks").GetProperty("hda");
            JsonElement cache = disks.GetProperty("cache");
            output.WriteLine($"FreeDOS: pack copied into the OPFS in {installMs} ms, boot to the prompt {bootMs} ms; "
                + $"hda read {hda.GetProperty("bytesRead").GetInt64()} bytes ({hda.GetProperty("compressedBytesRead").GetInt64()} compressed), "
                + $"wrote {hda.GetProperty("bytesWritten").GetInt64()}; cache {cache.GetProperty("cacheBytes").GetInt64()} of {cache.GetProperty("cacheMaxBytes").GetInt64()} bytes");
            output.WriteLine(screen);
            Assert.Equal("discard", hda.GetProperty("layer").GetProperty("mode").GetString());
            Assert.True(hda.GetProperty("reads").GetInt64() > 0, "the BIOS read nothing from hda");

            await session.StopAsync();
            Assert.Equal(0, (await session.ErrorsAsync()).GetArrayLength());
        }
        catch (Exception)
        {
            await session.DumpAsync();
            throw;
        }
    }
}
