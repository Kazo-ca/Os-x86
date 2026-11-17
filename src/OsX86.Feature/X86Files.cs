using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Settings;
using OsX86.Vm;

namespace OsX86.Feature;

/// <summary>The files the command reads and writes: profiles given on the command line, and the VM registry. Tests replace it.</summary>
internal interface IX86Files
{
    /// <summary>The text of a file, or <see langword="null"/> when it does not exist. A relative path is relative to the command's working directory.</summary>
    ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes a whole text file, creating its directory.</summary>
    ValueTask WriteTextAsync(string path, string text, CancellationToken cancellationToken);

    /// <summary>The first <paramref name="maxBytes"/> bytes of a file (all of a smaller one), or <see langword="null"/> when it does not exist.</summary>
    ValueTask<byte[]?> ReadBytesAsync(string path, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>The files of the Kazo file system, through the ambient context of the running command (ADR 0043 of Kazo).</summary>
internal sealed class KazoX86Files : IX86Files
{
    public static KazoX86Files Instance { get; } = new();

    /// <inheritdoc />
    public async ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        if (!await KazoFile.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await KazoFile.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]?> ReadBytesAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        if (!await KazoFile.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using Stream stream = await KazoFile.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[64 * 1024];
        while (buffer.Length < maxBytes)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maxBytes - buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Writes the file, creating its missing directories the way Kazo's own settings do
    /// (<see cref="SettingsTextFile.EnsureDirectoryAsync"/>): an app may create
    /// <c>~/.config/&lt;appId&gt;/</c> (its private capability) without being allowed to look at
    /// <c>~/.config</c> itself, where <see cref="KazoDirectory.CreateDirectoryAsync(string, CancellationToken)"/>
    /// stops (found in the dev host, work item 6b: the first <c>x86 vm create</c> of a user failed).
    /// </summary>
    public async ValueTask WriteTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        string full = KazoPath.GetFullPath(path);
        int slash = full.LastIndexOf('/');
        if (slash > 0)
        {
            string directory = full[..slash];
            FileSystemError? error = await SettingsTextFile.EnsureDirectoryAsync(KazoExecutionContext.Current.FileSystem, VirtualPath.Parse(directory), cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                throw new IOException($"The directory '{directory}' cannot be created ({error.Code}).");
            }
        }

        await KazoFile.WriteAllTextAsync(full, text, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The network rules and journal of a user (work item 12), next to the VM registry:
/// <c>~/.config/ca.kazo.x86/network/&lt;guid&gt;.rules.json</c> and <c>.journal.json</c>, read and
/// written with the identity of the running command (another user's are refused, as for the registry).
/// </summary>
internal sealed class KazoNetworkStore(IX86Files files) : Network.INetworkStore
{
    /// <summary>The directory of the network documents under the home directory.</summary>
    public const string Directory = ".config/ca.kazo.x86/network";

    /// <summary>The path of a document of a user for the running command.</summary>
    public static string PathFor(Guid userId, Network.NetworkDocumentKind kind)
    {
        string registry = KazoVmRegistryStore.PathFor(userId); // refuses another user
        string home = registry[..registry.IndexOf("/" + KazoVmRegistryStore.Directory, StringComparison.Ordinal)];
        string suffix = kind == Network.NetworkDocumentKind.Rules ? "rules" : "journal";
        return $"{home}/{Directory}/{userId:N}.{suffix}.json";
    }

    /// <inheritdoc />
    public ValueTask<string?> ReadAsync(Guid userId, Network.NetworkDocumentKind kind, CancellationToken cancellationToken) => files.ReadTextAsync(PathFor(userId, kind), cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteAsync(Guid userId, Network.NetworkDocumentKind kind, string json, CancellationToken cancellationToken) => files.WriteTextAsync(PathFor(userId, kind), json, cancellationToken);
}

/// <summary>
/// The VM registry in the app's private settings directory of the user (ADR 0044 of Kazo:
/// <c>~/.config/ca.kazo.x86/</c>, needs <c>filesystem.private.read</c> and <c>.write</c>), one file
/// per stable user identifier (decision 190 A): <c>vms/&lt;guid&gt;.json</c>. The file is read and
/// written with the identity of the running command; a call for another user than the one running
/// it is refused, so a user never reaches another user's registry through the command.
/// </summary>
internal sealed class KazoVmRegistryStore(IX86Files files) : IVmRegistryStore
{
    /// <summary>The directory of the registries under the home directory.</summary>
    public const string Directory = ".config/ca.kazo.x86/vms";

    /// <summary>The path of a user's registry for the running command.</summary>
    public static string PathFor(Guid userId)
    {
        IAppExecutionContext context = KazoExecutionContext.Current;
        if (!X86Runtime.TryParseUserId(context.UserId, out Guid running) || running != userId)
        {
            throw new UnauthorizedAccessException("The VM registry of a user is only read and written by that user's own commands.");
        }

        return $"{context.HomeDirectory.TrimEnd('/')}/{Directory}/{userId:N}.json";
    }

    /// <inheritdoc />
    public ValueTask<string?> ReadAsync(Guid userId, CancellationToken cancellationToken) => files.ReadTextAsync(PathFor(userId), cancellationToken);

    /// <inheritdoc />
    public ValueTask WriteAsync(Guid userId, string json, CancellationToken cancellationToken) => files.WriteTextAsync(PathFor(userId), json, cancellationToken);
}
