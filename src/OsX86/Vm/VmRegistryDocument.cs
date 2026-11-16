using System.Text.Json;
using OsX86.Interface;
using OsX86.Prefixes;
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

    /// <summary>
    /// The layer set that keeps the writes of the VM's disks (work item 6b: one per VM,
    /// <c>osx86/layers/&lt;set&gt;/</c>, deleted with the VM). Set at creation; a VM registered
    /// before it existed (<see langword="null"/>) uses <see cref="LayerSetOf"/> of its identifier.
    /// </summary>
    public string? LayerSet { get; init; }

    /// <summary>The layer set of the VM: <see cref="LayerSet"/>, or the one its identifier gives.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveLayerSet => LayerSet ?? LayerSetOf(Id);

    /// <summary>The layer set of a new VM: <c>vm-</c> and the 32 hexadecimal digits of its identifier.</summary>
    public static string LayerSetOf(Guid vmId) => "vm-" + vmId.ToString("N");

    /// <summary>
    /// The prefixes the user made in the VM (work item 10, decision 200 B), in creation order; the
    /// default prefix is added on its first use. <see langword="null"/> in a registry written before
    /// work item 10: read as none.
    /// </summary>
    public IReadOnlyList<Prefix>? Prefixes { get; init; }

    /// <summary>
    /// Prefixes deleted while the VM was stopped: their guest user and files are removed at the next
    /// start, before anything runs (the disk is the VM's, only its guest can delete in it).
    /// </summary>
    public IReadOnlyList<PrefixId>? PendingPrefixDeletions { get; init; }

    /// <summary>The prefixes, never <see langword="null"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<Prefix> EffectivePrefixes => Prefixes ?? [];

    /// <summary>The prefixes deleted while stopped, never <see langword="null"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<PrefixId> EffectivePendingPrefixDeletions => PendingPrefixDeletions ?? [];

    /// <summary>
    /// The prefix used when a caller names none (193 A: Os-x86 chooses): the profile's prefix when it
    /// has one, otherwise <see cref="PrefixId.Default"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PrefixId DefaultPrefix => Profile.Prefix is { } named && PrefixId.TryParse(named, out PrefixId id) ? id : PrefixId.Default;

    /// <summary>Whether a prefix may be used in this VM: one the user made, or the default prefix.</summary>
    public bool KnowsPrefix(PrefixId prefix) => prefix == DefaultPrefix || EffectivePrefixes.Any(p => p.Id == prefix);

    /// <summary>The same VM under another name.</summary>
    public VmRecord WithName(string name) => With(name, EffectivePrefixes, EffectivePendingPrefixDeletions);

    /// <summary>The same VM with other prefixes.</summary>
    public VmRecord WithPrefixes(IReadOnlyList<Prefix> prefixes, IReadOnlyList<PrefixId> pendingDeletions) => With(Name, prefixes, pendingDeletions);

    private VmRecord With(string name, IReadOnlyList<Prefix> prefixes, IReadOnlyList<PrefixId> pendingDeletions) => new()
    {
        Id = Id,
        Name = name,
        Created = Created,
        Profile = Profile,
        LayerSet = LayerSet,
        Prefixes = prefixes.Count == 0 ? null : prefixes,
        PendingPrefixDeletions = pendingDeletions.Count == 0 ? null : pendingDeletions,
    };

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

        // Untrusted text (a user can edit their own file): null lists or entries are refused, never a NullReferenceException.
        if (document.Vms is null || document.Profiles is null || document.Vms.Any(vm => vm is null) || document.Profiles.Any(profile => profile is null))
        {
            throw new X86Exception(X86ErrorCodes.InvalidProfile, "The VM registry is invalid: a list or an entry is null.");
        }

        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VmRecord vm in document.Vms)
        {
            if (vm.Id == Guid.Empty || !ids.Add(vm.Id) || !VmProfile.IsValidName(vm.Name) || !names.Add(vm.Name!) || vm.Profile is null)
            {
                throw new X86Exception(X86ErrorCodes.InvalidProfile, "The VM registry is invalid: a VM has no identifier, a duplicate identifier or name, or no profile.");
            }

            vm.Profile.Validate();
            if (vm.EffectivePrefixes.Any(prefix => prefix is null) || vm.EffectivePrefixes.Select(prefix => prefix.Id).Distinct().Count() != vm.EffectivePrefixes.Count)
            {
                throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The VM registry is invalid: {vm.Name} has a null or duplicate prefix.");
            }

            if (vm.LayerSet is not null && !Storage.StorageLayout.IsValidImageId(vm.LayerSet))
            {
                throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The VM registry is invalid: the layer set '{vm.LayerSet}' of {vm.Name} is not an identifier.");
            }
        }

        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VmProfile profile in document.Profiles)
        {
            if (!profileNames.Add(profile.Name ?? string.Empty))
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
