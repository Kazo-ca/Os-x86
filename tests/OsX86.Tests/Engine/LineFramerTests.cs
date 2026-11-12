using System.Text;
using OsX86.Engine;

namespace OsX86.Tests.Engine;

/// <summary>Framing of the serial bytes into lines (<see cref="LineFramer"/>).</summary>
[Trait("Category", "unit")]
public sealed class LineFramerTests
{
    private static IReadOnlyList<string> Push(LineFramer framer, string text) => framer.Push(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Complete_lines_are_returned_without_their_line_ending()
    {
        var framer = new LineFramer();
        Assert.Equal(["OSX86-READY", "second"], Push(framer, "OSX86-READY\r\nsecond\n"));
        Assert.Equal(string.Empty, framer.Pending);
    }

    [Fact]
    public void A_line_split_across_chunks_is_assembled()
    {
        var framer = new LineFramer();
        Assert.Empty(Push(framer, "OSX86-"));
        Assert.Equal("OSX86-", framer.Pending);
        Assert.Equal(["OSX86-READY"], Push(framer, "READY\r\n"));
    }

    [Fact]
    public void A_multibyte_character_split_across_chunks_is_decoded()
    {
        var framer = new LineFramer();
        byte[] bytes = Encoding.UTF8.GetBytes("é\n");
        Assert.Empty(framer.Push(bytes.AsSpan(0, 1)));
        Assert.Equal(["é"], framer.Push(bytes.AsSpan(1)));
    }

    [Fact]
    public void Invalid_bytes_become_the_replacement_character_instead_of_throwing()
    {
        var framer = new LineFramer();
        IReadOnlyList<string> lines = framer.Push([0xFF, (byte)'a', (byte)'\n']);
        Assert.Equal(["�a"], lines);
    }

    [Fact]
    public void A_lone_carriage_return_inside_a_line_is_kept()
    {
        var framer = new LineFramer();
        Assert.Equal(["progress 10%\rprogress 20%"], Push(framer, "progress 10%\rprogress 20%\n"));
    }

    [Fact]
    public void Empty_lines_are_reported()
    {
        var framer = new LineFramer();
        Assert.Equal(["", "", "x"], Push(framer, "\n\r\nx\n"));
    }

    [Fact]
    public void A_line_longer_than_the_limit_is_cut()
    {
        var framer = new LineFramer(maxLineLength: 4);
        Assert.Equal(["abcd", "efgh"], Push(framer, "abcdefghij"));
        Assert.Equal("ij", framer.Pending);
        Assert.Equal(4, framer.MaxLineLength);
    }

    [Fact]
    public void Flush_returns_the_pending_text_once()
    {
        var framer = new LineFramer();
        Assert.Null(framer.Flush());
        Push(framer, "osx86# ");
        Assert.Equal("osx86# ", framer.Flush());
        Assert.Null(framer.Flush());
        Assert.Equal(string.Empty, framer.Pending);
    }

    [Fact]
    public void Empty_input_returns_no_line()
    {
        var framer = new LineFramer();
        Assert.Empty(framer.Push(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void The_limit_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LineFramer(0));
    }
}
