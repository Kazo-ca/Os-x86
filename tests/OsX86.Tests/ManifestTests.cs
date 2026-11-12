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
    public void The_feature_asks_for_no_capability_yet()
    {
        Assert.Empty(_app.Capabilities);
    }
}
