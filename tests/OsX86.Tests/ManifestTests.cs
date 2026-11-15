using Kazo.Os.App.Abstractions;
using OsX86.Feature;

namespace OsX86.Tests;

/// <summary>The manifest class of the feature, the source of the generated manifest.json.</summary>
[Trait("Category", "unit")]
public sealed class ManifestTests
{
    private readonly X86FeatureApp _app = new();

    [Fact]
    public void The_feature_is_the_x86_terminal_command_with_its_id()
    {
        Assert.Equal("ca.kazo.x86", _app.Id);
        Assert.Equal(AppKind.Terminal, _app.Kind);
        Assert.Equal(typeof(X86Command), _app.EntryPoint);
        Assert.Equal("x86", _app.Terminal!.Name);
    }

    [Fact]
    public void The_feature_asks_only_for_its_private_files()
    {
        // Work item 6: the VM registry and the imported profiles live in ~/.config/ca.kazo.x86/.
        Assert.Equal([AppCapabilities.FileSystemPrivateRead, AppCapabilities.FileSystemPrivateWrite], _app.Capabilities);
    }
}
