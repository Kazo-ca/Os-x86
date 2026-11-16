namespace OsX86.Agent;

/// <summary>A prefix as the guest knows it: its guest user (<c>osx86-&lt;prefix&gt;</c>) and its home.</summary>
/// <param name="Prefix">The prefix name.</param>
/// <param name="UserId">The uid of its guest user.</param>
/// <param name="Home">The home directory of that user (its Wine prefix is <c>.wine</c> under it).</param>
public sealed record GuestPrefix(string Prefix, int UserId, string Home);

/// <content>The operations of protocol 2 (work item 10): prefixes, files, sync.</content>
public sealed partial class AgentClient
{
    /// <summary>
    /// How many bytes of a file one <c>put-file</c> request carries (a line of about 8 KiB). Not the
    /// 64 KiB a line of the agent may have: v86 hands a host write to the guest in 1 KiB pieces, one
    /// receive buffer of the guest each, and drops what finds no buffer free; a line of 49 KiB was
    /// lost in the first guest test of work item 10. One request at a time, each answered before the
    /// next, leaves the guest the time to post its buffers again.
    /// </summary>
    public const int PutFileChunkBytes = 6 * 1024;

    /// <summary>The time the agent leaves <c>prefix-admin</c> (180 s; copying the Wine template takes 4 to 6 s in the VM).</summary>
    public static readonly TimeSpan PrefixAdminTimeout = TimeSpan.FromSeconds(180);

    /// <summary>The time the agent leaves one <c>put-file</c> and one <c>sync</c>.</summary>
    public static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Whether the connected agent has the operations of protocol 2.</summary>
    public bool HasPrefixOperations => Status is { ProtocolVersion: >= 2 };

    /// <summary>
    /// Makes sure the guest user of a prefix and its files exist (the image's <c>prefix-admin</c>:
    /// the user, its 0700 home, the image's own steps such as the copy of the Wine template).
    /// </summary>
    /// <returns>The prefix, and whether something was created.</returns>
    /// <exception cref="AgentException"><see cref="AgentErrorCodes.NoPrefixSupport"/>, <see cref="AgentErrorCodes.PrefixFailed"/>, <see cref="AgentErrorCodes.UnknownOperation"/> (an agent of protocol 1).</exception>
    public async Task<(GuestPrefix Prefix, bool Created)> EnsurePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        ValidatePrefix(prefix);
        RequireVersion2(AgentProtocol.Operations.PrefixEnsure);
        var request = new AgentRequest { Op = AgentProtocol.Operations.PrefixEnsure, Prefix = prefix };
        AgentMessage reply = await SendAsync(request, PrefixAdminTimeout + _options.LongOperationMargin, null, null, cancellationToken).ConfigureAwait(false);
        return (AgentProtocol.ToGuestPrefix(reply, prefix), reply.Created);
    }

    /// <summary>Deletes a prefix in the guest: its launches are killed, its user, home and files removed.</summary>
    /// <returns>Whether there was something to delete.</returns>
    public async Task<bool> DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        ValidatePrefix(prefix);
        RequireVersion2(AgentProtocol.Operations.PrefixDelete);
        var request = new AgentRequest { Op = AgentProtocol.Operations.PrefixDelete, Prefix = prefix };
        AgentMessage reply = await SendAsync(request, PrefixAdminTimeout + _options.LongOperationMargin, null, null, cancellationToken).ConfigureAwait(false);
        return reply.Deleted;
    }

    /// <summary>The prefixes of the guest (its <c>osx86-*</c> users).</summary>
    public async Task<IReadOnlyList<GuestPrefix>> ListPrefixesAsync(CancellationToken cancellationToken)
    {
        RequireVersion2(AgentProtocol.Operations.PrefixList);
        AgentMessage reply = await SendAsync(new AgentRequest { Op = AgentProtocol.Operations.PrefixList }, _options.RequestTimeout, null, null, cancellationToken).ConfigureAwait(false);
        return AgentProtocol.ToGuestPrefixes(reply);
    }

    /// <summary>
    /// Writes a file in the home of a prefix's user, as that user, in pieces of
    /// <see cref="PutFileChunkBytes"/>: missing directories are made (0700), a final link is not
    /// followed, the file is replaced.
    /// </summary>
    /// <param name="prefix">The prefix.</param>
    /// <param name="path">The path relative to the home (no <c>..</c>, no empty component).</param>
    /// <param name="content">The bytes.</param>
    /// <param name="cancellationToken">Cancels between two pieces.</param>
    /// <exception cref="AgentException"><see cref="AgentErrorCodes.PutFailed"/>, <see cref="AgentErrorCodes.UnknownPrefix"/>, <see cref="AgentErrorCodes.InvalidRequest"/>.</exception>
    public async Task PutFileAsync(string prefix, string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        ValidatePrefix(prefix);
        ValidateRelativePath(path);
        RequireVersion2(AgentProtocol.Operations.PutFile);
        long offset = 0;
        do
        {
            ReadOnlyMemory<byte> piece = content.Slice((int)offset, (int)Math.Min(PutFileChunkBytes, content.Length - offset));
            var request = new AgentRequest
            {
                Op = AgentProtocol.Operations.PutFile,
                Prefix = prefix,
                Path = path,
                Data = Convert.ToBase64String(piece.Span),
                Offset = offset == 0 ? null : offset,
                Truncate = offset == 0 ? true : null,
            };
            await SendAsync(request, FileTimeout + _options.LongOperationMargin, null, null, cancellationToken).ConfigureAwait(false);
            offset += piece.Length;
        }
        while (offset < content.Length);
    }

    /// <summary>Flushes the guest's file systems to its disks (before the VM stops: what a prefix wrote reaches its layer).</summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        RequireVersion2(AgentProtocol.Operations.Sync);
        await SendAsync(new AgentRequest { Op = AgentProtocol.Operations.Sync }, FileTimeout + _options.LongOperationMargin, null, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The checks of the agent, made here first: a path relative to the home, without <c>.</c>, <c>..</c> or empty components.</summary>
    internal static void ValidateRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        bool bad = path.Length > 1024 || path.StartsWith('/') || path.EndsWith('/') || path.Any(c => c < ' ' || c == '\x7f')
            || path.Split('/').Any(part => part is "" or "." or "..");
        if (bad)
        {
            throw new ArgumentException($"'{path}' is not a path relative to the home of the prefix's user.", nameof(path));
        }
    }

    private void RequireVersion2(string operation)
    {
        AgentStatus? status = Status;
        if (status is not null && status.ProtocolVersion < 2)
        {
            throw new AgentException(AgentErrorCodes.UnknownOperation, $"The agent speaks version {status.ProtocolVersion} of the protocol; '{operation}' needs version 2.");
        }
    }
}
