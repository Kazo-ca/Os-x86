using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using OsX86.Vm;

namespace OsX86.Feature;

/// <summary>The files the command reads and writes: profiles given on the command line, and the VM registry. Tests replace it.</summary>
internal interface IX86Files
{
    /// <summary>The text of a file, or <see langword="null"/> when it does not exist. A relative path is relative to the command's working directory.</summary>
    ValueTask<string?> ReadTextAsync(string path, CancellationToken cancellationToken);

    /// <summary>Writes a whole text file, creating its directory.</summary>
    ValueTask WriteTextAsync(string path, string text, CancellationToken cancellationToken);
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
    public async ValueTask WriteTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        string full = KazoPath.GetFullPath(path);
        int slash = full.LastIndexOf('/');
        if (slash > 0)
        {
            await KazoDirectory.CreateDirectoryAsync(full[..slash], cancellationToken).ConfigureAwait(false);
        }

        await KazoFile.WriteAllTextAsync(full, text, cancellationToken).ConfigureAwait(false);
    }
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
