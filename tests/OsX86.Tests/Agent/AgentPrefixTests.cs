using System.Text.Json;
using OsX86.Agent;

namespace OsX86.Tests.Agent;

/// <summary>The operations of protocol 2 (work item 10) in the client: prefixes, files, sync, and the versions accepted.</summary>
[Trait("Category", "unit")]
public sealed class AgentPrefixTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly AgentClientOptions Fast = new()
    {
        RequestTimeout = TimeSpan.FromSeconds(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectProbeInterval = TimeSpan.FromMilliseconds(40),
    };

    private static async Task<AgentClient> ConnectedAsync(SimulatedAgentChannel channel, int version = 2)
    {
        channel.On("ping", _ => $$"""{"ok":true,"version":{{version}},"uptime":1,"rssKb":170,"launches":0,"consoles":0,"cgroups":true}""");
        var client = new AgentClient(channel, Fast);
        await client.ConnectAsync(None);
        return client;
    }

    [Fact]
    public async Task Ensure_delete_and_list_send_the_prefix_and_read_the_replies()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("prefix-ensure", _ => """{"ok":true,"created":true,"prefix":"work","uid":2001,"home":"/home/osx86-work","ms":4100}""");
        channel.On("prefix-delete", _ => """{"ok":true,"deleted":false,"ms":3}""");
        channel.On("prefix-list", _ => """{"ok":true,"prefixes":[{"prefix":"work","uid":2001,"home":"/home/osx86-work"},{"prefix":"Bad Name","uid":3,"home":"/"}],"admin":true}""");
        await using AgentClient client = await ConnectedAsync(channel);

        (GuestPrefix prefix, bool created) = await client.EnsurePrefixAsync("work", None);
        bool deleted = await client.DeletePrefixAsync("work", None);
        IReadOnlyList<GuestPrefix> listed = await client.ListPrefixesAsync(None);

        Assert.True(client.HasPrefixOperations);
        Assert.True(created);
        Assert.Equal(new GuestPrefix("work", 2001, "/home/osx86-work"), prefix);
        Assert.False(deleted);
        Assert.Equal([new GuestPrefix("work", 2001, "/home/osx86-work")], listed); // a malformed name is left out
        Assert.Equal("work", channel.Request("prefix-ensure").GetProperty("prefix").GetString());
        Assert.Equal("work", channel.Request("prefix-delete").GetProperty("prefix").GetString());
    }

    [Fact]
    public async Task A_file_goes_in_pieces_the_first_one_truncating()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("put-file", _ => """{"ok":true,"size":1,"ms":2}""");
        await using AgentClient client = await ConnectedAsync(channel);
        byte[] content = new byte[(AgentClient.PutFileChunkBytes * 2) + 10];
        new Random(4).NextBytes(content);

        await client.PutFileAsync("work", ".wine/drive_c/x/a.exe", content, None);

        IReadOnlyList<JsonElement> puts = channel.Requests("put-file");
        Assert.Equal(3, puts.Count);
        Assert.True(puts[0].GetProperty("truncate").GetBoolean());
        Assert.False(puts[0].TryGetProperty("offset", out _));
        Assert.False(puts[1].TryGetProperty("truncate", out _));
        Assert.Equal(AgentClient.PutFileChunkBytes, puts[1].GetProperty("offset").GetInt64());
        Assert.Equal(AgentClient.PutFileChunkBytes * 2, puts[2].GetProperty("offset").GetInt64());
        byte[] joined = [.. puts.SelectMany(put => Convert.FromBase64String(put.GetProperty("data").GetString()!))];
        Assert.Equal(content, joined);
        Assert.All(puts, put => Assert.True(put.GetRawText().Length < 9 * 1024, "a request line stays well below what the guest's receive buffers take at once"));
    }

    [Fact]
    public async Task An_empty_file_is_one_request()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("put-file", _ => """{"ok":true,"size":0,"ms":2}""");
        await using AgentClient client = await ConnectedAsync(channel);

        await client.PutFileAsync("work", "empty.txt", ReadOnlyMemory<byte>.Empty, None);

        Assert.Equal(string.Empty, channel.Request("put-file").GetProperty("data").GetString());
    }

    [Fact]
    public async Task A_refused_write_is_an_agent_error()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("put-file", _ => """{"ok":false,"error":"put-failed","message":"cannot write a: Permission denied"}""");
        await using AgentClient client = await ConnectedAsync(channel);

        AgentException error = await Assert.ThrowsAsync<AgentException>(() => client.PutFileAsync("work", "a", new byte[3], None));

        Assert.Equal(AgentErrorCodes.PutFailed, error.Code);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../other/x")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("dir/")]
    [InlineData("a\u0001b")]
    public async Task A_path_outside_the_home_is_refused_before_sending(string path)
    {
        var channel = new SimulatedAgentChannel();
        await using AgentClient client = await ConnectedAsync(channel);

        await Assert.ThrowsAsync<ArgumentException>(() => client.PutFileAsync("work", path, new byte[1], None));

        Assert.Empty(channel.Requests("put-file"));
    }

    [Fact]
    public async Task Sync_waits_for_the_guest()
    {
        var channel = new SimulatedAgentChannel();
        channel.On("sync", _ => """{"ok":true,"timedOut":false,"ms":12}""");
        await using AgentClient client = await ConnectedAsync(channel);

        await client.SyncAsync(None);

        Assert.Single(channel.Requests("sync"));
    }

    [Fact]
    public async Task An_agent_of_protocol_1_is_accepted_without_the_new_operations()
    {
        var channel = new SimulatedAgentChannel();
        await using AgentClient client = await ConnectedAsync(channel, version: 1);

        Assert.False(client.HasPrefixOperations);
        AgentException error = await Assert.ThrowsAsync<AgentException>(() => client.EnsurePrefixAsync("work", None));
        Assert.Equal(AgentErrorCodes.UnknownOperation, error.Code);
        await Assert.ThrowsAsync<AgentException>(() => client.DeletePrefixAsync("work", None));
        await Assert.ThrowsAsync<AgentException>(() => client.ListPrefixesAsync(None));
        await Assert.ThrowsAsync<AgentException>(() => client.PutFileAsync("work", "a", new byte[1], None));
        await Assert.ThrowsAsync<AgentException>(() => client.SyncAsync(None));
        Assert.Empty(channel.Requests("prefix-ensure"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Another_version_is_refused(int version)
    {
        var channel = new SimulatedAgentChannel();
        channel.On("ping", _ => $$"""{"ok":true,"version":{{version}}}""");
        await using var client = new AgentClient(channel, Fast);

        AgentProtocolException error = await Assert.ThrowsAsync<AgentProtocolException>(() => client.ConnectAsync(None));

        Assert.Equal(AgentErrorCodes.UnsupportedVersion, error.Code);
    }

    [Fact]
    public async Task A_bad_prefix_name_is_refused_before_sending()
    {
        var channel = new SimulatedAgentChannel();
        await using AgentClient client = await ConnectedAsync(channel);

        await Assert.ThrowsAsync<ArgumentException>(() => client.EnsurePrefixAsync("Bad", None));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeletePrefixAsync("", None));
        Assert.Empty(channel.Requests("prefix-ensure"));
    }
}
