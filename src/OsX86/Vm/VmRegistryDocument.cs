using System.Text.Json;
using OsX86.Interface;
using OsX86.Profiles;

namespace OsX86.Vm;

/// <summary>One VM as the registry stores it (decision 190 A: identified by a GUID; its state is not stored, it is runtime).</summary>
public sealed class VmRecord
{
    /// <summary>The stable identifier of the VM.</summary>
    public required Guid Id { get; init; }

    /// <summary>The name the user gave it; unique among the user's VMs, compared ordinally ignoring case.</summary>
    public required string Name { get; init; }

    /// <summary>When it was created.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>The recipe of the VM: kind, image and version, memory, serial port, default prefix (183 A).</summary>
    public required VmProfile Profile { get; init; }

    /// <summary>The summary a client sees, with the given state.</summary>
    public VmSummary ToSummary(VmState state) => new(Id, Name, Profile.Kind, state, Profile.Image, Profile.ImageVersion, Profile.MemoryMiB);
}

/// <summary>
/// The persisted registry of one user (decision 190 A: one document per stable user identifier,
/// never per login name), versioned (plan §13). Format 1:
/// <code>
/// {"format": 1, "user": "00000000000000000000000000000001",
///  "vms": [{"id": "…", "name": "Alpine", "created": "2026-11-12T10:00:00+00:00", "profile": {…}}],
///  "profiles": [{…}]}
/// </code>
/// <c>profiles</c> are the profiles the user imported (<c>x86 profile import</c>), next to the
/// built-in ones. An unknown format is refused and nothing is written (169).
/// </summary>
public sealed class VmRegistryDocument
{
    /// <summary>The only format this version reads and writes.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The largest registry text accepted, in bytes.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>The format number.</summary>
    public int Format { get; init; } = CurrentFormat;

    /// <summary>The stable identifier of the user the registry belongs to.</summary>
    public required Guid User { get; init; }

    /// <summary>The user's VMs, in creation order.</summary>
    public IReadOnlyList<VmRecord> Vms { get; init; } = [];

    /// <summary>The profiles the user imported, in import order.</summary>
    public IReadOnlyList<VmProfile> Profiles { get; init; } = [];

    /// <summary>An empty registry of a user.</summary>
    public static VmRegistryDocument Empty(Guid user) => new() { User = user };

    /// <summary>The JSON text of the registry, as stored.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, VmJsonContext.Indented.VmRegistryDocument);

    /// <summary>Reads a registry text; refuses an unknown format, another user's document, or an invalid record.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.UnsupportedFormat"/> or <see cref="X86ErrorCodes.InvalidProfile"/>.</exception>
    public static VmRegistryDocument Parse(string json, Guid expectedUser)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The VM registry is more than the {MaxBytes} bytes accepted.");
        }

        int format = VersionedDocument.PeekFormat(json, "VM registry");
        if (format != CurrentFormat)
        {
            throw new X86Exception(X86ErrorCodes.UnsupportedFormat, $"The VM registry has format {format}; this version of Os-x86 reads format {CurrentFormat} only. It is refused and nothing was written.");
        }

        VmRegistryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, VmJsonContext.Default.VmRegistryDocument);
        }
        catch (JsonException exception)
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The VM registry is invalid: {exception.Message}", exception);
        }

        if (document is null || document.User != expectedUser)
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, "The VM registry is empty or belongs to another user.");
        }

        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VmRecord vm in document.Vms)
        {
            if (vm.Id == Guid.Empty || !ids.Add(vm.Id) || !VmProfile.IsValidName(vm.Name) || !names.Add(vm.Name) || vm.Profile is null)
            {
                throw new X86Exception(X86ErrorCodes.InvalidProfile, "The VM registry is invalid: a VM has no identifier, a duplicate identifier or name, or no profile.");
            }

            vm.Profile.Validate();
        }

        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VmProfile profile in document.Profiles)
        {
            if (profile is null || !profileNames.Add(profile.Name ?? string.Empty))
            {
                throw new X86Exception(X86ErrorCodes.InvalidProfile, "The VM registry is invalid: a profile is missing or duplicated.");
            }

            profile.Validate();
        }

        return document;
    }
}

/// <summary>
/// Where a user's registry document is kept: the app's storage (plan §13). One text per user,
/// keyed by the stable identifier (190 A). Implementations: in memory (tests, hosts without
/// storage) and the Kazo file system of the Feature.
/// </summary>
public interface IVmRegistryStore
{
    /// <summary>The registry text of a user, or <see langword="null"/> when the user has none yet.</summary>
    ValueTask<string?> ReadAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Replaces the registry text of a user.</summary>
    ValueTask WriteAsync(Guid userId, string json, CancellationToken cancellationToken);
}

/// <summary>A registry store that keeps the documents in memory: for tests and for a host without storage.</summary>
public sealed class InMemoryVmRegistryStore : IVmRegistryStore
{
    private readonly Dictionary<Guid, string> _documents = [];
    private readonly Lock _lock = new();

    /// <summary>How many documents are stored.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _documents.Count;
            }
        }
    }

    /// <summary>How many writes happened.</summary>
    public int Writes { get; private set; }

    /// <inheritdoc />
    public ValueTask<string?> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return ValueTask.FromResult(_documents.GetValueOrDefault(userId));
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(Guid userId, string json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _documents[userId] = json;
            Writes++;
        }

        return ValueTask.CompletedTask;
    }
}
