using System.Security.Cryptography;
using System.Text.Json;

namespace OsX86.Tests.Images;

/// <summary>
/// <c>images/tools/pack-image.py</c> (work item 4): the published form of an image. Synthetic
/// entries are packed with small chunks; the manifest (format 1, the schema the storage of work
/// item 3 reads), the chunk files, the seek tables and the check and extract modes are verified.
/// </summary>
[Trait("Category", "unit")]
public sealed class PackImageTests : IDisposable
{
    private const uint SeekableMagic = 0x8F92EAB1;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "osx86-pack-" + Guid.NewGuid().ToString("N"));

    public PackImageTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [SkippableFact]
    public void Pack_writes_chunks_of_the_given_size_and_a_manifest_of_format_1()
    {
        (string kernel, string rootfs) = WriteEntries();
        string pack = Pack(kernel, rootfs, chunkSize: 100_000, blockSize: 65_536);

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, "manifest.json")));
        JsonElement root = manifest.RootElement;
        Assert.Equal(1, root.GetProperty("format").GetInt32());
        Assert.Equal("test-image", root.GetProperty("image").GetString());
        Assert.Equal("0.1.0", root.GetProperty("version").GetString());
        Assert.Equal("0.5.473", root.GetProperty("engine").GetProperty("v86").GetString());
        Assert.Equal(100_000, root.GetProperty("chunkSize").GetInt32());
        long size = root.GetProperty("size").GetInt64();
        JsonElement chunks = root.GetProperty("chunks");
        Assert.Equal((size + 99_999) / 100_000, chunks.GetArrayLength());

        // One file per chunk, five digits, the last one shorter, each with the SHA-256 of the manifest.
        for (int index = 0; index < chunks.GetArrayLength(); index++)
        {
            string path = Path.Combine(pack, $"chunk-{index:D5}.bin");
            Assert.True(File.Exists(path), path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(Math.Min(100_000, size - index * 100_000L), bytes.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), chunks[index].GetProperty("sha256").GetString());
        }

        // Entries are contiguous and cover the blob; each ends with the zstd seekable magic number.
        JsonElement files = root.GetProperty("files");
        long expectedOffset = 0;
        byte[] blob = ReadBlob(pack, chunks.GetArrayLength());
        foreach (string name in new[] { "kernel", "rootfs" })
        {
            JsonElement entry = files.GetProperty(name);
            Assert.Equal(expectedOffset, entry.GetProperty("offset").GetInt64());
            long entrySize = entry.GetProperty("size").GetInt64();
            Assert.Equal(65_536, entry.GetProperty("blockSize").GetInt32());
            Assert.Equal(SeekableMagic, BitConverter.ToUInt32(blob, (int)(expectedOffset + entrySize - 4)));
            expectedOffset += entrySize;
        }

        Assert.Equal(size, expectedOffset);
        Assert.Equal(new FileInfo(kernel).Length, files.GetProperty("kernel").GetProperty("rawSize").GetInt64());
        Assert.Equal("console=ttyS0", root.GetProperty("boot").GetProperty("cmdline").GetString());

        // manifest.sha256 is the SHA-256 of manifest.json.
        string manifestText = File.ReadAllText(Path.Combine(pack, "manifest.json"));
        string expected = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifestText))).ToLowerInvariant();
        Assert.StartsWith(expected, File.ReadAllText(Path.Combine(pack, "manifest.sha256")), StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Check_passes_on_a_fresh_pack_and_fails_on_a_corrupted_chunk_or_manifest()
    {
        (string kernel, string rootfs) = WriteEntries();
        string pack = Pack(kernel, rootfs, chunkSize: 100_000, blockSize: 65_536);
        Assert.Contains("OK test-image 0.1.0", PythonTools.RunOrThrow(PythonTools.PackImage, "check", pack), StringComparison.Ordinal);
        Assert.Contains("OK test-image 0.1.0", PythonTools.RunOrThrow(PythonTools.PackImage, "--check", pack), StringComparison.Ordinal);

        string chunk = Path.Combine(pack, "chunk-00001.bin");
        byte[] bytes = File.ReadAllBytes(chunk);
        bytes[123] ^= 0xFF;
        File.WriteAllBytes(chunk, bytes);
        (int exitCode, string output) = PythonTools.Run(PythonTools.PackImage, "check", pack);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("chunk-00001.bin", output, StringComparison.Ordinal);

        bytes[123] ^= 0xFF;
        File.WriteAllBytes(chunk, bytes);
        File.AppendAllText(Path.Combine(pack, "manifest.json"), "\n");
        (exitCode, output) = PythonTools.Run(PythonTools.PackImage, "check", pack);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("manifest.sha256", output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Extract_gives_back_the_original_entries()
    {
        (string kernel, string rootfs) = WriteEntries();
        string pack = Pack(kernel, rootfs, chunkSize: 100_000, blockSize: 65_536);
        string extracted = Path.Combine(_directory, "extracted");
        PythonTools.RunOrThrow(PythonTools.PackImage, "extract", pack, "--to", extracted);
        Assert.Equal(File.ReadAllBytes(kernel), File.ReadAllBytes(Path.Combine(extracted, "kernel")));
        Assert.Equal(File.ReadAllBytes(rootfs), File.ReadAllBytes(Path.Combine(extracted, "rootfs")));

        string only = Path.Combine(_directory, "only");
        PythonTools.RunOrThrow(PythonTools.PackImage, "extract", pack, "--to", only, "--entry", "rootfs");
        Assert.True(File.Exists(Path.Combine(only, "rootfs")));
        Assert.False(File.Exists(Path.Combine(only, "kernel")));
    }

    [SkippableFact]
    public void Pack_refuses_a_bad_identifier_a_bad_version_and_a_missing_entry()
    {
        (string kernel, _) = WriteEntries();
        string output = Path.Combine(_directory, "bad");
        (int exitCode, string text) = PythonTools.Run(PythonTools.PackImage, "pack", "--image", "Bad_Id", "--version", "0.1.0", "--entry", $"kernel={kernel}", "--output", output);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("identifier", text, StringComparison.Ordinal);
        (exitCode, text) = PythonTools.Run(PythonTools.PackImage, "pack", "--image", "ok", "--version", "1", "--entry", $"kernel={kernel}", "--output", output);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("semver", text, StringComparison.Ordinal);
        (exitCode, text) = PythonTools.Run(PythonTools.PackImage, "pack", "--image", "ok", "--version", "0.1.0", "--entry", $"kernel={Path.Combine(_directory, "missing")}", "--output", output);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("not a file", text, StringComparison.Ordinal);
    }

    private (string Kernel, string Rootfs) WriteEntries()
    {
        var random = new Random(7);
        byte[] kernel = new byte[300_000];
        random.NextBytes(kernel);
        byte[] rootfs = new byte[250_000];
        for (int index = 0; index < rootfs.Length; index++)
        {
            rootfs[index] = (byte)(index % 251); // compressible, several blocks
        }

        string kernelPath = Path.Combine(_directory, "kernel.bin");
        string rootfsPath = Path.Combine(_directory, "rootfs.bin");
        File.WriteAllBytes(kernelPath, kernel);
        File.WriteAllBytes(rootfsPath, rootfs);
        File.WriteAllText(Path.Combine(_directory, "boot.json"), "{\"cmdline\": \"console=ttyS0\"}");
        return (kernelPath, rootfsPath);
    }

    private string Pack(string kernel, string rootfs, int chunkSize, int blockSize)
    {
        string pack = Path.Combine(_directory, "pack");
        PythonTools.RunOrThrow(PythonTools.PackImage, "pack", "--image", "test-image", "--version", "0.1.0",
            "--entry", $"kernel={kernel}", "--entry", $"rootfs={rootfs}", "--boot-json", Path.Combine(_directory, "boot.json"),
            "--output", pack, "--chunk-size", chunkSize.ToString(), "--block-size", blockSize.ToString(), "--level", "3");
        return pack;
    }

    private static byte[] ReadBlob(string pack, int chunkCount)
    {
        using var stream = new MemoryStream();
        for (int index = 0; index < chunkCount; index++)
        {
            stream.Write(File.ReadAllBytes(Path.Combine(pack, $"chunk-{index:D5}.bin")));
        }

        return stream.ToArray();
    }
}
