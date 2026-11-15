using System.Diagnostics;
using OsX86.Tests.Images;
using Xunit.Abstractions;

namespace OsX86.Tests.Engine;

/// <summary>
/// The JavaScript of the engine disk (<c>wwwroot/engine/zstd-seekable.js</c> and <c>disk.js</c>,
/// work item 6a), tested by Node with <c>disk-tests.js</c>: seek table, read plan, the zstd decoder
/// of the vendored <c>v86.wasm</c>, frame cache, copy-on-write layers on a fake OPFS (persistence,
/// one writer, refusal of another image version, engine major or format without writing), and an
/// entry made by <c>pack-image.py</c>. Node 22.15 or later is needed (its zlib makes the zstd
/// fixtures); without it the test is skipped locally and fails in the CI.
/// </summary>
[Trait("Category", "unit")]
public sealed class DiskScriptTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "osx86-disk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // already gone, or a file still open by an antivirus: the temporary directory stays
        }
    }

    [SkippableFact]
    public void The_disk_scripts_pass_their_node_tests()
    {
        string node = RequireNode();
        string pack = MakePack();
        string script = Path.Combine(PythonTools.RepositoryRoot, "tests", "OsX86.Tests", "Engine", "disk-tests.js");

        var start = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = PythonTools.RepositoryRoot,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(PythonTools.RepositoryRoot);
        start.ArgumentList.Add(pack);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("node did not start");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "the node tests did not finish within 2 minutes");
        output.WriteLine(stdout);
        output.WriteLine(stderr.Result);

        Assert.True(process.ExitCode == 0, $"node disk-tests.js exited with {process.ExitCode}:\n{stdout}\n{stderr.Result}");
        Assert.Contains("an entry made by pack-image.py decompresses to its sha256", stdout, StringComparison.Ordinal);
        Assert.Contains(" 0 failed", stdout, StringComparison.Ordinal);
    }

    /// <summary>A pack of two synthetic entries (several frames each, small chunks), made by pack-image.py.</summary>
    private string MakePack()
    {
        PythonTools.Require();
        Directory.CreateDirectory(_directory);
        var random = new Random(11);
        byte[] kernel = new byte[200_000];
        random.NextBytes(kernel);
        byte[] rootfs = new byte[300_000];
        for (int index = 0; index < rootfs.Length; index++)
        {
            rootfs[index] = (byte)((index / 512) ^ (index % 13));
        }

        string kernelPath = Path.Combine(_directory, "kernel.bin");
        string rootfsPath = Path.Combine(_directory, "rootfs.bin");
        File.WriteAllBytes(kernelPath, kernel);
        File.WriteAllBytes(rootfsPath, rootfs);
        string pack = Path.Combine(_directory, "pack");
        PythonTools.RunOrThrow(PythonTools.PackImage, "pack", "--image", "disk-test", "--version", "0.1.0",
            "--entry", $"kernel={kernelPath}", "--entry", $"rootfs={rootfsPath}", "--output", pack,
            "--chunk-size", "65536", "--block-size", "65536", "--level", "3");
        return pack;
    }

    private static string RequireNode()
    {
        foreach (string candidate in new[] { Environment.GetEnvironmentVariable("OSX86_NODE"), "node" }.OfType<string>().Where(c => c.Length > 0))
        {
            try
            {
                var start = new ProcessStartInfo(candidate)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add("process.stdout.write(typeof require('zlib').zstdCompressSync)");
                using Process? process = Process.Start(start);
                if (process is null)
                {
                    continue;
                }

                string text = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(30_000);
                if (process.ExitCode == 0 && text == "function")
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // not installed under that name
            }
        }

        const string message = "No Node 22.15 or later found (its zlib makes the zstd fixtures): the disk scripts cannot be tested.";
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")), message);
        throw new InvalidOperationException(message);
    }
}
