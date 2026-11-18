using OsX86.Interface;
using OsX86.Network;
using OsX86.Vm;

namespace OsX86.Storage;

/// <summary>
/// The state of each user in the OPFS of the Feature (work item 8, decision 287 A): the VM registry
/// with the imported profiles, the network rules and the network journal, as files of
/// <c>osx86/users/&lt;guid&gt;/</c> written by the storage worker. A Feature cannot reach the private
/// directory of its users in Kazo 3.5 (its command is the child <c>ca.kazo.x86.cli</c>, its service
/// acts for the calling app), so the state lives next to the disks it describes: the marker of
/// ADR 0250 covers both, and an eviction takes them together.
/// <para>
/// The users are separated by the code of Os-x86 only: every caller passes the identifier Kazo gave
/// it for the user (the command, the user running it; the service, <c>Caller.UserId</c>), as the
/// layers of the VMs already are.
/// </para>
/// </summary>
/// <param name="store">The image store, whose worker reads and writes the files.</param>
public sealed class UserStateStore(ImageStore store) : IVmRegistryStore, INetworkStore
{
    private readonly ImageStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="userId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.StorageFailed"/>: the worker could not read it.</exception>
    public ValueTask<string?> ReadAsync(Guid userId, CancellationToken cancellationToken) =>
        ReadAsync(userId, StorageLayout.RegistryFileName, cancellationToken);

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="userId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.StorageFailed"/>: the worker could not write it.</exception>
    public ValueTask WriteAsync(Guid userId, string json, CancellationToken cancellationToken) =>
        WriteAsync(userId, StorageLayout.RegistryFileName, json, cancellationToken);

    /// <inheritdoc />
    public ValueTask<string?> ReadAsync(Guid userId, NetworkDocumentKind kind, CancellationToken cancellationToken) =>
        ReadAsync(userId, FileOf(kind), cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteAsync(Guid userId, NetworkDocumentKind kind, string json, CancellationToken cancellationToken) =>
        WriteAsync(userId, FileOf(kind), json, cancellationToken);

    private static string FileOf(NetworkDocumentKind kind) => kind switch
    {
        NetworkDocumentKind.Rules => StorageLayout.NetworkRulesFileName,
        NetworkDocumentKind.Journal => StorageLayout.NetworkJournalFileName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a network document."),
    };

    private async ValueTask<string?> ReadAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.ReadUserFileAsync(userId, name, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException exception) when (exception.Code != StorageErrorCodes.Cancelled)
        {
            throw Failed("read", name, exception);
        }
    }

    private async ValueTask WriteAsync(Guid userId, string name, string json, CancellationToken cancellationToken)
    {
        try
        {
            await _store.WriteUserFileAsync(userId, name, json, cancellationToken).ConfigureAwait(false);
        }
        catch (StorageException exception) when (exception.Code != StorageErrorCodes.Cancelled)
        {
            throw Failed("written", name, exception);
        }
    }

    private static X86Exception Failed(string what, string name, StorageException exception) =>
        new(X86ErrorCodes.StorageFailed, $"The {name} of the user could not be {what} in the storage of Os-x86 ({exception.Code}: {exception.Message}).", exception);
}
