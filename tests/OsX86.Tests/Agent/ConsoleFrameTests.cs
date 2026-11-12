using OsX86.Agent;

namespace OsX86.Tests.Agent;

/// <summary>The frames of the console port: what the client writes, and reading them back from chunks of any shape.</summary>
[Trait("Category", "unit")]
public sealed class ConsoleFrameTests
{
    [Fact]
    public void Data_is_encoded_with_the_magic_the_type_the_console_and_the_length()
    {
        byte[] frame = ConsoleFrame.EncodeData(0x01020304, "hi"u8);
        Assert.Equal([0xAE, 0x01, 0x01, 0x02, 0x03, 0x04, 0x00, 0x02, (byte)'h', (byte)'i'], frame);
    }

    [Fact]
    public void Nothing_to_send_is_no_frame_at_all()
    {
        Assert.Empty(ConsoleFrame.EncodeData(1, []));
    }

    [Fact]
    public void Long_data_is_cut_into_frames_of_at_most_4096_bytes()
    {
        byte[] data = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        byte[] encoded = ConsoleFrame.EncodeData(7, data);
        Assert.Equal(10_000 + 3 * ConsoleFrame.HeaderLength, encoded.Length);

        IReadOnlyList<ConsoleFrame> frames = new ConsoleFrameReader().Push(encoded);
        Assert.Equal([4096, 4096, 1808], frames.Select(f => f.Payload.Length));
        Assert.All(frames, f => Assert.Equal((ConsoleFrameType.Data, 7), (f.Type, f.Console)));
        Assert.Equal(data, frames.SelectMany(f => f.Payload.ToArray()).ToArray());
    }

    [Fact]
    public void A_negative_console_number_is_refused_and_a_large_one_round_trips()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleFrame.EncodeData(-1, "x"u8));
        // The number is the sequence of a launch: it passes 65 535 in a VM that lives long enough.
        ConsoleFrame frame = Assert.Single(new ConsoleFrameReader().Push(ConsoleFrame.EncodeData(ConsoleFrame.MaxConsole, "x"u8)));
        Assert.Equal(int.MaxValue, frame.Console);
        Assert.Equal(70_000, Assert.Single(new ConsoleFrameReader().Push(ConsoleFrame.EncodeData(70_000, "y"u8))).Console);
    }

    [Fact]
    public void A_frame_split_byte_by_byte_is_read_once_complete()
    {
        byte[] encoded = [.. ConsoleFrame.EncodeData(3, "hello"u8), .. ConsoleFrame.EncodeData(4, "yo"u8)];
        var reader = new ConsoleFrameReader();
        var frames = new List<ConsoleFrame>();
        foreach (byte value in encoded)
        {
            frames.AddRange(reader.Push([value]));
        }

        Assert.Equal(2, frames.Count);
        Assert.Equal((3, "hello"), (frames[0].Console, System.Text.Encoding.ASCII.GetString(frames[0].Payload.Span)));
        Assert.Equal((4, "yo"), (frames[1].Console, System.Text.Encoding.ASCII.GetString(frames[1].Payload.Span)));
        Assert.Equal(0, reader.SkippedBytes);
    }

    [Fact]
    public void Bytes_before_a_frame_are_skipped_until_the_magic()
    {
        var reader = new ConsoleFrameReader();
        IReadOnlyList<ConsoleFrame> frames = reader.Push([0x00, 0x41, 0x42, .. ConsoleFrame.EncodeData(9, "ok"u8)]);
        ConsoleFrame frame = Assert.Single(frames);
        Assert.Equal(9, frame.Console);
        Assert.Equal(3, reader.SkippedBytes);
        Assert.Empty(reader.Push([]));
    }

    [Fact]
    public void The_closed_frame_carries_the_exit_code_and_the_signal()
    {
        byte[] closed = [0xAE, 0x02, 0x00, 0x00, 0x00, 0x05, 0x00, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x0F];
        ConsoleFrame frame = Assert.Single(new ConsoleFrameReader().Push(closed));
        Assert.Equal(ConsoleFrameType.Closed, frame.Type);
        Assert.Equal(5, frame.Console);
        Assert.True(frame.TryReadExit(out ProcessExit exit));
        Assert.Equal(new ProcessExit(-1, 15), exit);
    }

    [Fact]
    public void An_exit_is_only_read_from_a_whole_closed_frame()
    {
        Assert.False(new ConsoleFrame(ConsoleFrameType.Data, 1, new byte[8]).TryReadExit(out _));
        Assert.False(new ConsoleFrame(ConsoleFrameType.Closed, 1, new byte[7]).TryReadExit(out _));
    }

    [Fact]
    public void An_empty_frame_and_an_unknown_type_are_frames_too()
    {
        IReadOnlyList<ConsoleFrame> frames = new ConsoleFrameReader().Push([0xAE, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0xAE, 0x63, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01, 0x7A]);
        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].Payload.IsEmpty);
        Assert.Equal((ConsoleFrameType)0x63, frames[1].Type);
        Assert.Equal([0x7A], frames[1].Payload.ToArray());
    }
}
