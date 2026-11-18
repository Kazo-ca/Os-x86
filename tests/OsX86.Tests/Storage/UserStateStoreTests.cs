using System.Text.Json;
using OsX86.Interface;
using OsX86.Network;
using OsX86.Profiles;
using OsX86.Storage;
using OsX86.Vm;

namespace OsX86.Tests.Storage;

/// <summary>
/// The state of each user in the OPFS of the Feature (work item 8, decision 287 A): the VM registry
/// with the imported profiles, the network rules and journal, under <c>osx86/users/&lt;guid&gt;/</c>,
/// next to the disks, read and written by the storage worker.
/// </summary>
[Trait("Category", "unit")]
public sealed class UserStateStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid User = Guid.Parse("0000000000000000000000000000a001");
    private static readonly Guid OtherUser = Guid.Parse("0000000000000000000000000000b002");

    [Fact]
    public async Task The_registry_of_a_user_is_a_file_of_its_own_directory_in_the_opfs()
    {
        var worker = new SimulatedStorageWorkerChannel();
        await using var images = new ImageStore(worker);
        var store = new UserStateStore(images);

        Assert.Null(await store.ReadAsync(User, None));
        await store.WriteAsync(User, "{\"format\":1}", None);

        Assert.Equal("{\"format\":1}", await store.ReadAsync(User, None));
        Assert.Equal("{\"format\":1}", worker.UserFiles["0000000000000000000000000000a001/registry.json"]);
        Assert.Equal("osx86/users/0000000000000000000000000000a001/registry.json", StorageLayout.UserFilePath(User, StorageLayout.RegistryFileName));
        using JsonDocument posted = JsonDocument.Parse(worker.Posted.Single(message => message.Contains("\"write-user-file\"", StringComparison.Ordinal)));
        Assert.Equal("0000000000000000000000000000a001", posted.RootElement.GetProperty("user").GetString());
        Assert.Equal("registry.json", posted.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_network_rules_and_journal_are_two_other_files_of_the_same_directory()
    {
        var worker = new SimulatedStorageWorkerChannel();
        await using var images = new ImageStore(worker);
        INetworkStore store = new UserStateStore(images);

        await store.WriteAsync(User, NetworkDocumentKind.Rules, "rules", None);
        await store.WriteAsync(User, NetworkDocumentKind.Journal, "journal", None);

        Assert.Equal("rules", await store.ReadAsync(User, NetworkDocumentKind.Rules, None));
        Assert.Equal("journal", await store.ReadAsync(User, NetworkDocumentKind.Journal, None));
        Assert.Equal(
            ["0000000000000000000000000000a001/network-journal.json", "0000000000000000000000000000a001/network-rules.json"],
            worker.UserFiles.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_user_never_reads_the_state_of_another_user()
    {
        var worker = new SimulatedStorageWorkerChannel();
        await using var images = new ImageStore(worker);
        var store = new UserStateStore(images);

        await store.WriteAsync(User, "mine", None);

        Assert.Null(await store.ReadAsync(OtherUser, None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.ReadAsync(Guid.Empty, None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.WriteAsync(Guid.Empty, "x", None));
    }

    [Fact]
    public async Task A_failure_of_the_worker_is_an_error_of_os_x86_naming_the_storage()
    {
        var worker = new SimulatedStorageWorkerChannel { UserFileError = ("quota-exceeded", "the origin is full") };
        await using var images = new ImageStore(worker);
        var store = new UserStateStore(images);

        X86Exception write = await Assert.ThrowsAsync<X86Exception>(async () => await store.WriteAsync(User, "{}", None));
        X86Exception read = await Assert.ThrowsAsync<X86Exception>(async () => await store.ReadAsync(User, NetworkDocumentKind.Rules, None));

        Assert.Equal(X86ErrorCodes.StorageFailed, write.Code);
        Assert.Equal(X86ErrorCodes.StorageFailed, read.Code);
        Assert.Contains("the origin is full", write.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_larger_than_the_worker_accepts_is_refused_before_it_is_sent()
    {
        var worker = new SimulatedStorageWorkerChannel();
        await using var images = new ImageStore(worker);
        var store = new UserStateStore(images);

        X86Exception refused = await Assert.ThrowsAsync<X86Exception>(async () => await store.WriteAsync(User, new string('a', StorageLayout.MaxUserFileBytes + 1), None));

        Assert.Equal(X86ErrorCodes.StorageFailed, refused.Code);
        Assert.DoesNotContain(worker.Posted, message => message.Contains("\"write-user-file\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("registry.json", true)]
    [InlineData("network-rules.json", true)]
    [InlineData("../registry.json", false)]
    [InlineData("Registry.json", false)]
    [InlineData("registry", false)]
    [InlineData("", false)]
    public void Only_plain_lowercase_json_names_are_user_files(string name, bool valid) =>
        Assert.Equal(valid, StorageLayout.IsValidUserFileName(name));

    [Fact]
    public async Task A_vm_created_by_a_user_is_found_again_after_a_reload_of_the_page()
    {
        var worker = new SimulatedStorageWorkerChannel();
        await using (var images = new ImageStore(worker))
        {
            await new VmRegistry(new UserStateStore(images)).CreateAsync(User, "essai", BuiltInProfiles.AlpineTest, None);
        }

        await using var reloaded = new ImageStore(worker);
        IReadOnlyList<VmRecord> vms = await new VmRegistry(new UserStateStore(reloaded)).ListAsync(User, None);

        Assert.Equal("essai", Assert.Single(vms).Name);
    }
}
