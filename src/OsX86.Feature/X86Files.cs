using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Settings;
using OsX86.Vm;

namespace OsX86.Feature;

/// <summary>The files of Kazo the command reads and writes: the profiles a user names on the command line (the state of each user is in the OPFS, decision 287 A). Tests replace it.</summary>
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
