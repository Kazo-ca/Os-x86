using OsX86.Interface;
using OsX86.Prefixes;
using OsX86.Profiles;

namespace OsX86.Vm;

/// <summary>
/// The registry of the VMs and imported profiles, per user (decision 190 A): creation, list,
/// rename, removal, persisted through an <see cref="IVmRegistryStore"/>. Every operation names the
/// user; a user never sees another user's document (the admin view is work item 7). A document is
/// read before each operation and written after each change, under one lock: documents are small
/// and the store is the truth.
/// </summary>
public sealed class VmRegistry
{
    private readonly IVmRegistryStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the registry on a store.</summary>
    public VmRegistry(IVmRegistryStore store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The user's document, empty when the user has none yet.</summary>
    public async ValueTask<VmRegistryDocument> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The user's VMs, in creation order.</summary>
    public async ValueTask<IReadOnlyList<VmRecord>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        (await LoadAsync(userId, cancellationToken).ConfigureAwait(false)).Vms;

    /// <summary>The VM of the user with this identifier, or <see langword="null"/>.</summary>
    public async ValueTask<VmRecord?> FindAsync(Guid userId, Guid vmId, CancellationToken cancellationToken) =>
        (await ListAsync(userId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(vm => vm.Id == vmId);

    /// <summary>
    /// The VM of the user named by <paramref name="nameOrId"/>: its GUID (any format) or its name
    /// (ordinal, ignoring case). The GUID wins when both match.
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>.</exception>
    public async ValueTask<VmRecord> ResolveAsync(Guid userId, string nameOrId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);
        IReadOnlyList<VmRecord> vms = await ListAsync(userId, cancellationToken).ConfigureAwait(false);
        VmRecord? found = null;
        if (Guid.TryParse(nameOrId, out Guid id))
        {
            found = vms.FirstOrDefault(vm => vm.Id == id);
        }

        found ??= vms.FirstOrDefault(vm => string.Equals(vm.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
        return found ?? throw new X86Exception(X86ErrorCodes.VmNotFound, $"No virtual machine named '{nameOrId}'.");
    }

    /// <summary>Creates a VM from a profile; the profile is copied into the VM (the recipe, 121).</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.InvalidName"/> (blank, too long, or taken), <see cref="X86ErrorCodes.InvalidProfile"/>.</exception>
    public async ValueTask<VmRecord> CreateAsync(Guid userId, string name, VmProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RequireName(name);
        profile.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VmRegistryDocument document = await ReadAsync(userId, cancellationToken).ConfigureAwait(false);
            RequireFreeName(document, name);
            Guid id = Guid.NewGuid();
            var record = new VmRecord { Id = id, Name = name, Created = _time.GetUtcNow(), Profile = profile, LayerSet = VmRecord.LayerSetOf(id) };
            await WriteAsync(new VmRegistryDocument { User = userId, Vms = [.. document.Vms, record], Profiles = document.Profiles }, cancellationToken).ConfigureAwait(false);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Renames a VM.</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>, <see cref="X86ErrorCodes.InvalidName"/>.</exception>
    public async ValueTask<VmRecord> RenameAsync(Guid userId, Guid vmId, string name, CancellationToken cancellationToken)
    {
        RequireName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VmRegistryDocument document = await ReadAsync(userId, cancellationToken).ConfigureAwait(false);
            VmRecord existing = document.Vms.FirstOrDefault(vm => vm.Id == vmId) ?? throw NotFound(vmId);
            if (!string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                RequireFreeName(document, name);
            }

            VmRecord renamed = existing.WithName(name);
            await WriteAsync(new VmRegistryDocument { User = userId, Vms = [.. document.Vms.Select(vm => vm.Id == vmId ? renamed : vm)], Profiles = document.Profiles }, cancellationToken).ConfigureAwait(false);
            return renamed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a VM from the registry (its disks are the business of later work items).</summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>.</exception>
    public async ValueTask RemoveAsync(Guid userId, Guid vmId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VmRegistryDocument document = await ReadAsync(userId, cancellationToken).ConfigureAwait(false);
            if (document.Vms.All(vm => vm.Id != vmId))
            {
                throw NotFound(vmId);
            }

            await WriteAsync(new VmRegistryDocument { User = userId, Vms = [.. document.Vms.Where(vm => vm.Id != vmId)], Profiles = document.Profiles }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The profiles a user may create a VM from: the built-in ones, then the imported ones (an imported one hides a built-in one of the same name).</summary>
    public async ValueTask<IReadOnlyList<VmProfile>> ListProfilesAsync(Guid userId, CancellationToken cancellationToken)
    {
        VmRegistryDocument document = await LoadAsync(userId, cancellationToken).ConfigureAwait(false);
        var imported = new HashSet<string>(document.Profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        return [.. BuiltInProfiles.All.Where(p => !imported.Contains(p.Name)), .. document.Profiles];
    }

    /// <summary>The profile named <paramref name="name"/> among <see cref="ListProfilesAsync"/>, or <see langword="null"/>.</summary>
    public async ValueTask<VmProfile?> FindProfileAsync(Guid userId, string name, CancellationToken cancellationToken) =>
        (await ListProfilesAsync(userId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Imports a profile: it replaces an imported profile of the same name.</summary>
    public async ValueTask ImportProfileAsync(Guid userId, VmProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VmRegistryDocument document = await ReadAsync(userId, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<VmProfile> others = [.. document.Profiles.Where(p => !string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase))];
            await WriteAsync(new VmRegistryDocument { User = userId, Vms = document.Vms, Profiles = [.. others, profile] }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds a prefix to a VM (work item 10): <c>x86 prefix create</c>, or the default prefix on its
    /// first use (<paramref name="ifMissing"/>: no error when it is already there).
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>, <see cref="X86ErrorCodes.PrefixExists"/>.</exception>
    public ValueTask<VmRecord> AddPrefixAsync(Guid userId, Guid vmId, PrefixId prefix, bool ifMissing, CancellationToken cancellationToken) =>
        UpdateVmAsync(userId, vmId, vm =>
        {
            if (vm.EffectivePrefixes.Any(p => p.Id == prefix))
            {
                return ifMissing ? vm : throw new X86Exception(X86ErrorCodes.PrefixExists, $"The virtual machine {vm.Name} already has a prefix named '{prefix}'.");
            }

            return vm.WithPrefixes([.. vm.EffectivePrefixes, new Prefix(prefix, _time.GetUtcNow())], vm.EffectivePendingPrefixDeletions);
        }, cancellationToken);

    /// <summary>
    /// Removes a prefix from a VM. When the guest could not delete its user and files (the VM is
    /// stopped), the prefix is kept for deletion at the next start.
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmNotFound"/>, <see cref="X86ErrorCodes.PrefixNotFound"/>.</exception>
    public ValueTask<VmRecord> RemovePrefixAsync(Guid userId, Guid vmId, PrefixId prefix, bool deletedInGuest, CancellationToken cancellationToken) =>
        UpdateVmAsync(userId, vmId, vm =>
        {
            if (!vm.KnowsPrefix(prefix))
            {
                throw new X86Exception(X86ErrorCodes.PrefixNotFound, $"The virtual machine {vm.Name} has no prefix named '{prefix}'.");
            }

            IReadOnlyList<PrefixId> pending = deletedInGuest || vm.EffectivePendingPrefixDeletions.Contains(prefix)
                ? vm.EffectivePendingPrefixDeletions
                : [.. vm.EffectivePendingPrefixDeletions, prefix];
            return vm.WithPrefixes([.. vm.EffectivePrefixes.Where(p => p.Id != prefix)], pending);
        }, cancellationToken);

    /// <summary>Forgets the deletions the guest has done (at the start of the VM).</summary>
    public ValueTask<VmRecord> CompletePrefixDeletionsAsync(Guid userId, Guid vmId, IReadOnlyCollection<PrefixId> done, CancellationToken cancellationToken) =>
        UpdateVmAsync(userId, vmId, vm => vm.WithPrefixes(vm.EffectivePrefixes, [.. vm.EffectivePendingPrefixDeletions.Where(p => !done.Contains(p))]), cancellationToken);

    private async ValueTask<VmRecord> UpdateVmAsync(Guid userId, Guid vmId, Func<VmRecord, VmRecord> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VmRegistryDocument document = await ReadAsync(userId, cancellationToken).ConfigureAwait(false);
            VmRecord existing = document.Vms.FirstOrDefault(vm => vm.Id == vmId) ?? throw NotFound(vmId);
            VmRecord changed = change(existing);
            if (!ReferenceEquals(changed, existing))
            {
                await WriteAsync(new VmRegistryDocument { User = userId, Vms = [.. document.Vms.Select(vm => vm.Id == vmId ? changed : vm)], Profiles = document.Profiles }, cancellationToken).ConfigureAwait(false);
            }

            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<VmRegistryDocument> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        string? json = await _store.ReadAsync(userId, cancellationToken).ConfigureAwait(false);
        return json is null ? VmRegistryDocument.Empty(userId) : VmRegistryDocument.Parse(json, userId);
    }

    private ValueTask WriteAsync(VmRegistryDocument document, CancellationToken cancellationToken)
    {
        string json = document.ToJson();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > VmRegistryDocument.MaxBytes)
        {
            // Never write what could not be read back: the user would be locked out of their registry.
            throw new X86Exception(X86ErrorCodes.InvalidProfile, $"The VM registry would exceed {VmRegistryDocument.MaxBytes} bytes; remove imported profiles or VMs first.");
        }

        return _store.WriteAsync(document.User, json, cancellationToken);
    }

    private static void RequireName(string name)
    {
        if (!VmProfile.IsValidName(name))
        {
            throw new X86Exception(X86ErrorCodes.InvalidName, "A VM name is 1 to 64 characters, without control characters or leading and trailing blanks.");
        }
    }

    private static void RequireFreeName(VmRegistryDocument document, string name)
    {
        if (document.Vms.Any(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new X86Exception(X86ErrorCodes.InvalidName, $"A virtual machine named '{name}' already exists.");
        }
    }

    private static X86Exception NotFound(Guid vmId) => new(X86ErrorCodes.VmNotFound, $"No virtual machine with the identifier {vmId}.");
}
