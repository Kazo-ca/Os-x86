using OsX86.Storage;

namespace OsX86.Tests.Images;

/// <summary>
/// The manifests written by <c>pack-image.py</c> (work item 4) are read by the storage of work
/// item 3: every pack built under <c>images/build/</c> parses with <see cref="ImageManifest.Parse(string)"/>
/// and says the same sizes and entries. Skipped when no image is built locally.
/// </summary>
[Trait("Category", "unit")]
public sealed class PackedManifestsTests
{
    [SkippableTheory]
    [InlineData("alpine-test", "kernel", "initramfs")]
    [InlineData("freedos", "disk")]
    [InlineData("linux-base", "kernel", "initramfs", "rootfs")]
    public void A_packed_manifest_is_read_by_the_storage_reader(string image, params string[] entries)
    {
        string path = Path.Combine(PythonTools.RepositoryRoot, "images", "build", image, "pack", "manifest.json");
        Skip.IfNot(File.Exists(path), $"{image} not built ({path} missing).");

        ImageManifest manifest = ImageManifest.Parse(File.ReadAllText(path));

        Assert.Equal(image, manifest.Image);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal(entries, manifest.Files.Keys.ToArray());
        long chunkBytes = Directory.GetFiles(Path.GetDirectoryName(path)!, "chunk-*.bin").Sum(file => new FileInfo(file).Length);
        Assert.Equal(chunkBytes, manifest.Size);
    }
}
