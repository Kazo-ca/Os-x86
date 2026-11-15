using System.Text;
using System.Text.Json;
using Kazo.Os.AppSdk;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Profiles;
using OsX86.Vm;
using static OsX86.Tests.Vm.VmTestRig;

namespace OsX86.Tests.Vm;

/// <summary>Profiles (183 A), the registry and its format (190 A, plan §13), the budget (165 A), the service, and the text helpers of the command.</summary>
[Trait("Category", "unit")]
public sealed class VmModelTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public void A_profile_round_trips_and_keeps_its_programs_section_opaque()
    {
        const string json = """
            {
              // comments and trailing commas are accepted, unknown properties ignored
              "format": 1, "name": "box", "kind": "windows-programs", "image": "wine", "imageVersion": "1.2.0",
              "memoryMiB": 512, "serialConsole": false, "prefix": "office", "futureField": [1, 2],
              "programs": {"entries": [{"title": "Editor", "path": "C:\\edit.exe"}]},
            }
            """;
        VmProfile profile = VmProfile.Parse(json);

        Assert.Equal(VmKind.WindowsPrograms, profile.Kind);
        Assert.Equal(512, profile.MemoryMiB);
        Assert.Equal("office", profile.Prefix);
        Assert.Equal("Editor", profile.Programs!.Value.GetProperty("entries")[0].GetProperty("title").GetString());

        string written = profile.ToJson();
        Assert.Contains("\"kind\": \"windows-programs\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("futureField", written, StringComparison.Ordinal);
        VmProfile again = VmProfile.Parse(written);
        Assert.Equal(profile.Name, again.Name);
        Assert.Equal("C:\\edit.exe", again.Programs!.Value.GetProperty("entries")[0].GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("""{"format": 2, "name": "x", "kind": "linux", "image": "a", "imageVersion": "1"}""", X86ErrorCodes.UnsupportedFormat)]
    [InlineData("""{"name": "x"}""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""{"format": "1"}""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""not json""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""[1]""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""null""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""{"format": 1, "kind": "linux", "image": "a", "imageVersion": "1"}""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""{"format": 1, "name": "x", "kind": "beos", "image": "a", "imageVersion": "1"}""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""{"format": 1, "name": "x", "kind": "linux", "image": "Bad Image", "imageVersion": "1"}""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""{"format": 1, "name": "x", "kind": "linux", "image": "a", "imageVersion": "../1"}""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""{"format": 1, "name": "x", "kind": "linux", "image": "a", "imageVersion": "1", "memoryMiB": 8}""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""{"format": 1, "name": "x", "kind": "linux", "image": "a", "imageVersion": "1", "memoryMiB": 9000}""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""{"format": 1, "name": "x", "kind": "linux", "image": "a", "imageVersion": "1", "prefix": "Not Valid"}""", X86ErrorCodes.InvalidProfile)]
    [InlineData("""{"format": 1, "name": " padded", "kind": "linux", "image": "a", "imageVersion": "1"}""", X86ErrorCodes.InvalidProfile)]
    public void An_invalid_profile_is_refused_with_a_code(string json, string code)
    {
        X86Exception error = Assert.Throws<X86Exception>(() => VmProfile.Parse(json));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void A_profile_bigger_than_the_limit_is_refused_and_names_follow_the_rules()
    {
        string huge = "{\"format\":1,\"programs\":\"" + new string('x', VmProfile.MaxBytes) + "\"}";
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => VmProfile.Parse(huge)).Code);
        Assert.True(VmProfile.IsValidName("Shell box 2"));
        Assert.False(VmProfile.IsValidName(null));
        Assert.False(VmProfile.IsValidName("   "));
        Assert.False(VmProfile.IsValidName("tab\there"));
        Assert.False(VmProfile.IsValidName(new string('a', VmProfile.MaxNameLength + 1)));
        Assert.Equal(X86ErrorCodes.UnsupportedFormat, Assert.Throws<X86Exception>(() => (BuiltInProfiles.AlpineTest with { Format = 3 }).Validate()).Code);
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => (BuiltInProfiles.AlpineTest with { Kind = (VmKind)42 }).Validate()).Code);
    }

    [Fact]
    public void The_built_in_profiles_are_valid_and_found_by_name()
    {
        foreach (VmProfile profile in BuiltInProfiles.All)
        {
            profile.Validate();
            Assert.Equal(profile, VmProfile.Parse(profile.ToJson()));
        }

        Assert.Same(BuiltInProfiles.LinuxBase, BuiltInProfiles.Find("LINUX-BASE"));
        Assert.Null(BuiltInProfiles.Find("nope"));
        Assert.Null(BuiltInProfiles.Find(null));
        Assert.True(BuiltInProfiles.AlpineTest.SerialConsole);
        Assert.Equal("test", BuiltInProfiles.AlpineTest.Prefix);
    }

    [Theory]
    [InlineData("linux", VmKind.Linux)]
    [InlineData(" DOS ", VmKind.Dos)]
    [InlineData("windows", VmKind.WindowsPrograms)]
    [InlineData("windows-programs", VmKind.WindowsPrograms)]
    public void The_kind_names_are_read_and_written(string name, VmKind kind)
    {
        Assert.True(VmKindJsonConverter.TryParse(name, out VmKind parsed));
        Assert.Equal(kind, parsed);
        Assert.True(VmKindJsonConverter.TryParse(VmKindJsonConverter.ToName(kind), out _));
        Assert.False(VmKindJsonConverter.TryParse("os2", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => VmKindJsonConverter.ToName((VmKind)9));
    }

    [Fact]
    public async Task The_registry_creates_lists_renames_and_removes_and_persists_per_user()
    {
        var store = new InMemoryVmRegistryStore();
        var time = new ManualTimeProvider();
        var registry = new VmRegistry(store, time);

        VmRecord a = await registry.CreateAsync(User, "Alpine", BuiltInProfiles.AlpineTest, None);
        VmRecord b = await registry.CreateAsync(User, "Base", BuiltInProfiles.LinuxBase, None);
        await registry.CreateAsync(OtherUser, "Alpine", BuiltInProfiles.AlpineTest, None); // same name, another user: allowed

        Assert.Equal(time.GetUtcNow(), a.Created);
        Assert.Equal(2, store.Count);
        var reopened = new VmRegistry(store);
        Assert.Equal([a.Id, b.Id], (await reopened.ListAsync(User, None)).Select(vm => vm.Id));
        Assert.Single(await reopened.ListAsync(OtherUser, None));
        Assert.Empty(await reopened.ListAsync(Guid.NewGuid(), None));

        Assert.Equal(a.Id, (await reopened.ResolveAsync(User, "alpine", None)).Id);
        Assert.Equal(b.Id, (await reopened.ResolveAsync(User, b.Id.ToString("N"), None)).Id);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await reopened.ResolveAsync(User, "Gamma", None))).Code);

        Assert.Equal(X86ErrorCodes.InvalidName, (await Assert.ThrowsAsync<X86Exception>(async () => await reopened.CreateAsync(User, "BASE", BuiltInProfiles.AlpineTest, None))).Code);
        Assert.Equal(X86ErrorCodes.InvalidName, (await Assert.ThrowsAsync<X86Exception>(async () => await reopened.CreateAsync(User, "", BuiltInProfiles.AlpineTest, None))).Code);
        Assert.Equal(X86ErrorCodes.InvalidName, (await Assert.ThrowsAsync<X86Exception>(async () => await reopened.RenameAsync(User, a.Id, "base", None))).Code);

        VmRecord renamed = await reopened.RenameAsync(User, a.Id, "ALPINE", None); // a change of case of its own name
        Assert.Equal("ALPINE", renamed.Name);
        Assert.Equal(a.Created, renamed.Created);
        await reopened.RemoveAsync(User, b.Id, None);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await reopened.RemoveAsync(User, b.Id, None))).Code);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await reopened.RenameAsync(User, b.Id, "x", None))).Code);
        Assert.Equal(["ALPINE"], (await registry.ListAsync(User, None)).Select(vm => vm.Name));
        Assert.Null(await registry.FindAsync(User, b.Id, None));
    }

    [Fact]
    public async Task The_registry_document_has_a_versioned_format_and_names_its_user()
    {
        var store = new InMemoryVmRegistryStore();
        var registry = new VmRegistry(store);
        VmRecord vm = await registry.CreateAsync(User, "Alpine", BuiltInProfiles.AlpineTest, None);

        string json = (await store.ReadAsync(User, None))!;
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("format").GetInt32());
        Assert.Equal(User, document.RootElement.GetProperty("user").GetGuid());
        JsonElement stored = document.RootElement.GetProperty("vms")[0];
        Assert.Equal(vm.Id, stored.GetProperty("id").GetGuid());
        Assert.Equal("alpine-test", stored.GetProperty("profile").GetProperty("image").GetString());
        Assert.False(stored.TryGetProperty("state", out _)); // the state is runtime, never stored

        // Another user's document is never taken for this user's (190 A).
        Assert.Equal(X86ErrorCodes.InvalidProfile, Assert.Throws<X86Exception>(() => VmRegistryDocument.Parse(json, OtherUser)).Code);
        // A format of a later version is refused and nothing is written (169).
        await store.WriteAsync(User, json.Replace("\"format\": 1", "\"format\": 2"), None);
        int writes = store.Writes;
        Assert.Equal(X86ErrorCodes.UnsupportedFormat, (await Assert.ThrowsAsync<X86Exception>(async () => await registry.CreateAsync(User, "Other", BuiltInProfiles.AlpineTest, None))).Code);
        Assert.Equal(writes, store.Writes);
    }

    [Theory]
    [InlineData("""{"format": 1, "user": "0000000000000000000000000000a001", "vms": [{"id": "00000000-0000-0000-0000-000000000000", "name": "a", "created": "2026-11-12T10:00:00Z", "profile": {"format": 1, "name": "p", "kind": "linux", "image": "a", "imageVersion": "1"}}]}""")]
    [InlineData("""{"format": 1, "user": "0000000000000000000000000000a001", "vms": [{"id": "11111111-1111-1111-1111-111111111111", "name": "a", "created": "2026-11-12T10:00:00Z", "profile": {"format": 1, "name": "p", "kind": "linux", "image": "a", "imageVersion": "1"}}, {"id": "22222222-2222-2222-2222-222222222222", "name": "A", "created": "2026-11-12T10:00:00Z", "profile": {"format": 1, "name": "p", "kind": "linux", "image": "a", "imageVersion": "1"}}]}""")]
    [InlineData("""{"format": 1, "user": "0000000000000000000000000000a001", "vms": [{"id": "11111111-1111-1111-1111-111111111111", "name": "a", "created": "2026-11-12T10:00:00Z", "profile": {"format": 1, "name": "p", "kind": "linux", "image": "a", "imageVersion": "1", "memoryMiB": 1}}]}""")]
    [InlineData("""{"format": 1, "user": "0000000000000000000000000000a001", "profiles": [{"format": 1, "name": "p", "kind": "linux", "image": "a", "imageVersion": "1"}, {"format": 1, "name": "P", "kind": "linux", "image": "a", "imageVersion": "1"}]}""")]
    [InlineData("""{"format": 1, "user": "0000000000000000000000000000a001", "vms": "many"}""")]
    [InlineData("""{"format": 1}""")]
    public void An_inconsistent_registry_is_refused(string json)
    {
        Assert.Throws<X86Exception>(() => VmRegistryDocument.Parse(json, User));
    }

    [Fact]
    public async Task Imported_profiles_join_the_built_in_ones_and_replace_their_namesake()
    {
        var registry = new VmRegistry(new InMemoryVmRegistryStore());
        VmProfile mine = BuiltInProfiles.AlpineTest with { MemoryMiB = 64 };

        await registry.ImportProfileAsync(User, mine, None);
        await registry.ImportProfileAsync(User, mine with { MemoryMiB = 96 }, None); // same name: replaced
        await registry.ImportProfileAsync(User, mine with { Name = "extra" }, None);

        IReadOnlyList<VmProfile> profiles = await registry.ListProfilesAsync(User, None);
        Assert.Equal(["linux-base", "alpine-test", "extra"], profiles.Select(p => p.Name));
        Assert.Equal(96, (await registry.FindProfileAsync(User, "ALPINE-TEST", None))!.MemoryMiB);
        Assert.Null(await registry.FindProfileAsync(User, "none", None));
        Assert.Equal(2, (await registry.ListProfilesAsync(OtherUser, None)).Count); // another user sees only the built-in ones
    }

    [Fact]
    public void The_budget_is_half_of_the_announced_memory_or_the_administrators_limit()
    {
        var announced = new MemoryBudget(new FixedHostMemory(8L * 1024 * 1024 * 1024));
        Assert.True(announced.AnnouncedByHost);
        Assert.Equal(8192, announced.AnnouncedMiB);
        Assert.Equal(4096, announced.LimitMiB);
        Assert.True(announced.Fits(3968, 128));
        Assert.False(announced.Fits(3969, 128));

        var unknown = new MemoryBudget(FixedHostMemory.Unknown);
        Assert.False(unknown.AnnouncedByHost);
        Assert.Equal(MemoryBudget.DefaultAnnouncedMiB / 2, unknown.LimitMiB);

        var configured = new MemoryBudget(new FixedHostMemory(8L * 1024 * 1024 * 1024), configuredLimitMiB: 300);
        Assert.Equal(300, configured.LimitMiB);
        Assert.Equal(300, configured.ConfiguredLimitMiB);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryBudget(FixedHostMemory.Unknown, 0));
    }

    [Fact]
    public async Task Under_the_budget_nothing_is_asked_and_over_it_the_refusal_names_only_the_callers_vms()
    {
        await using var rig = new VmTestRig(announcedMiB: 512); // 256 MiB
        VmInstance mine = await rig.CreateAsync("mine");
        VmInstance theirs = await rig.CreateAsync("theirs", user: OtherUser);
        VmInstance dos = await rig.CreateAsync("dos", BuiltInProfiles.AlpineTest with { Name = "d", Kind = VmKind.Dos });
        await mine.HoldAsync(None);
        await theirs.HoldAsync(None); // 256 MiB used: full

        VmMemoryBudgetExceededException refused = await Assert.ThrowsAsync<VmMemoryBudgetExceededException>(async () => await dos.HoldAsync(None));

        Assert.Equal(X86ErrorCodes.MemoryBudgetExceeded, refused.Code);
        Assert.Equal(128, refused.RequestedMiB);
        Assert.Equal(256, refused.RunningMiB);
        Assert.Equal(256, refused.LimitMiB);
        Assert.Equal([mine.Id], refused.Running.Select(vm => vm.Id)); // the other user's VM is counted, never named
        Assert.Empty(refused.Compatible); // another kind
        Assert.Equal(VmState.Stopped, dos.State);
        Assert.Equal(2, rig.Engines.Created.Count); // no engine for the refused VM

        VmInstance twin = await rig.CreateAsync("twin");
        VmMemoryBudgetExceededException same = await Assert.ThrowsAsync<VmMemoryBudgetExceededException>(async () => await twin.HoldAsync(None));
        Assert.Equal([mine.Id], same.Compatible.Select(vm => vm.Id)); // same kind and image (165 A)

        await mine.StopAsync(force: false, None);
        await dos.HoldAsync(None); // fits again
        Assert.Equal(VmState.Running, dos.State);
    }

    [Fact]
    public async Task The_administrators_limit_replaces_the_default()
    {
        await using var rig = new VmTestRig(FastOptions with { MemoryLimitMiB = 100 }, announcedMiB: 65536);
        VmInstance vm = await rig.CreateAsync();
        Assert.Equal(100, rig.Manager.Budget.LimitMiB);
        await Assert.ThrowsAsync<VmMemoryBudgetExceededException>(async () => await vm.HoldAsync(None));
    }

    [Fact]
    public async Task The_service_is_bound_to_one_user_and_raises_only_that_users_state_changes()
    {
        await using var rig = new VmTestRig();
        rig.Engines.Configure = engine => ScriptConsole(engine);
        VmInstance mine = await rig.CreateAsync("mine");
        VmInstance theirs = await rig.CreateAsync("theirs", user: OtherUser);
        var service = new X86Service(rig.Manager, User);
        var seen = new List<VmStateChangedEventArgs>();
        EventHandler<VmStateChangedEventArgs> handler = (_, e) => seen.Add(e);
        service.VmStateChanged += handler;

        Assert.Equal(X86Service.FeatureVersion, service.Version);
        Assert.Equal(User, service.UserId);
        Assert.Equal(["mine"], (await service.GetVmsAsync(None)).Select(vm => vm.Name));
        await service.StartVmAsync(mine.Id, None);
        await theirs.HoldAsync(None);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await service.StartVmAsync(theirs.Id, None))).Code);
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await service.OpenConsoleAsync(theirs.Id, new VmConsoleOptions(), None))).Code);

        await using (IVmConsole console = await service.OpenConsoleAsync(mine.Id, new VmConsoleOptions(), None))
        {
            Assert.Equal(mine.Id, console.VmId);
        }

        await service.StopVmAsync(mine.Id, force: true, None);
        Assert.Equal(VmState.Stopped, (await service.GetVmsAsync(None))[0].State);
        Assert.All(seen, e => Assert.Equal(mine.Id, e.VmId));
        Assert.Equal([VmState.Starting, VmState.Running, VmState.Stopping, VmState.Stopped], seen.Select(e => e.Current));

        service.VmStateChanged -= handler;
        await service.StartVmAsync(mine.Id, None);
        Assert.Equal(4, seen.Count); // unsubscribed
        Assert.Throws<X86Exception>(() => new X86Service(rig.Manager, Guid.Empty));
    }

    [Fact]
    public async Task Removing_a_vm_forgets_its_instance_and_renaming_a_running_one_keeps_it_running()
    {
        await using var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync("old");
        await vm.HoldAsync(None);
        VmSummary renamed = await rig.Manager.RenameAsync(User, vm.Id, "new", None);
        Assert.Equal(VmState.Running, renamed.State);
        Assert.Equal("new", vm.Record.Name);
        Assert.Contains(vm, rig.Manager.Running());

        Assert.Equal(X86ErrorCodes.VmBusy, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.RemoveAsync(User, vm.Id, force: false, None))).Code);
        await rig.Manager.RemoveAsync(User, vm.Id, force: true, None);
        Assert.Equal(VmState.Stopped, rig.Manager.StateOf(vm.Id));
        Assert.Equal(X86ErrorCodes.VmNotFound, (await Assert.ThrowsAsync<X86Exception>(async () => await rig.Manager.GetInstanceAsync(User, vm.Id, None))).Code);
    }

    [Fact]
    public async Task Disposing_the_manager_stops_every_vm()
    {
        var rig = new VmTestRig();
        VmInstance vm = await rig.CreateAsync();
        await vm.HoldAsync(None);
        await rig.DisposeAsync();
        await rig.DisposeAsync(); // twice: a no-op
        Assert.Equal(VmState.Stopped, vm.State);
        Assert.True(rig.Engines.Last.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rig.Manager.GetInstanceAsync(User, vm.Id, None));
    }

    [Fact]
    public void The_text_filter_drops_escapes_carriage_returns_and_the_echo()
    {
        var filter = new TerminalTextFilter();
        Assert.Equal("prompt$ ", filter.Push("\u001b]0;title\u0007\u001b[1;32mprompt$\u001b[0m "u8));
        filter.ExpectEcho("ls");
        filter.ExpectEcho(string.Empty); // nothing to expect
        Assert.Equal(string.Empty, filter.Push("l"u8)); // held: it may be the start of the echo
        Assert.Equal("l", filter.Flush()); // released at the end of the stream
        Assert.Equal(string.Empty, filter.Flush());

        var echo = new TerminalTextFilter();
        echo.ExpectEcho("ls");
        Assert.Equal(string.Empty, echo.Push("ls\r\n"u8));
        Assert.Equal("a\tb\u0007\n", echo.Push("a\tb\u0007\u0001\r\n"u8));

        // Output that comes before the echo is shown, and the echo is still removed after it.
        var late = new TerminalTextFilter();
        late.ExpectEcho("exit 7");
        Assert.Equal("^C\nprompt$ bye\n", late.Push("^C\r\nprompt$ exit 7\r\nbye\r\n"u8));

        // A partial echo that turns out not to be one is shown whole.
        var partial = new TerminalTextFilter();
        partial.ExpectEcho("echo");
        Assert.Equal("ecstatic", partial.Push("ecstatic"u8));

        // A UTF-8 character split across two chunks, ESC sequences split too, OSC ended by ST.
        var split = new TerminalTextFilter();
        byte[] e = Encoding.UTF8.GetBytes("é");
        Assert.Equal(string.Empty, split.Push(e.AsSpan(0, 1)));
        Assert.Equal("é", split.Push(e.AsSpan(1)));
        Assert.Equal(string.Empty, split.Push("\u001b["u8));
        Assert.Equal("x", split.Push("2Jx"u8));
        Assert.Equal("y", split.Push("\u001b]2;t\u001b\\y\u001b7"u8));
    }

    [Fact]
    public void The_echo_of_lines_never_echoed_stops_being_expected()
    {
        var filter = new TerminalTextFilter();
        for (int index = 0; index < 20; index++)
        {
            filter.ExpectEcho($"secret{index}"); // a program with echo off (a password prompt)
        }

        Assert.Equal("ok\n", filter.Push("ok\r\n"u8));
        Assert.Equal("secret19\n", filter.Push("secret19\r\n"u8)); // echoes are matched in order: the oldest kept is secret12
        Assert.Equal(string.Empty, filter.Push("secret12\r\n"u8)); // the eight most recent are still expected
    }

    [Fact]
    public void The_arguments_take_values_flags_and_a_double_dash()
    {
        CommandArguments parsed = CommandArguments.Parse(["a", "--profile", "p", "--force", "--", "--literal"], ["profile"], ["force"], out string? error)!;
        Assert.Null(error);
        Assert.Equal(["a", "--literal"], parsed.Positional);
        Assert.Equal("p", parsed.Value("profile"));
        Assert.True(parsed.Has("force"));
        Assert.Null(parsed.Value("other"));

        Assert.Equal("q", CommandArguments.Parse(["--profile=q"], ["profile"], [], out _)!.Value("profile"));
        Assert.Null(CommandArguments.Parse(["--force=1"], [], ["force"], out error));
        Assert.Contains("takes no value", error, StringComparison.Ordinal);
        Assert.Null(CommandArguments.Parse(["--profile"], ["profile"], [], out error));
        Assert.Contains("needs a value", error, StringComparison.Ordinal);
        Assert.Null(CommandArguments.Parse(["--what"], [], [], out error));
        Assert.Contains("unknown option --what", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Kazo_user_identifiers_are_read_and_the_registry_is_kept_per_identifier()
    {
        Assert.True(X86Runtime.TryParseUserId("0000000000000000000000000000a001", out Guid id));
        Assert.Equal(User, id);
        Assert.True(X86Runtime.TryParseUserId(User.ToString("D"), out _));
        Assert.False(X86Runtime.TryParseUserId("alice", out _));
        Assert.False(X86Runtime.TryParseUserId(Guid.Empty.ToString(), out _));
        Assert.False(X86Runtime.TryParseUserId(null, out _));

        using (KazoExecutionContext.Enter(new TestAppContext()))
        {
            Assert.Equal($"/home/{TestAppContext.DefaultUserId}/.config/ca.kazo.x86/vms/{User:N}.json", KazoVmRegistryStore.PathFor(User));
            Assert.Throws<UnauthorizedAccessException>(() => KazoVmRegistryStore.PathFor(OtherUser)); // never another user's registry
        }

        using (KazoExecutionContext.Enter(new TestAppContext("user-1")))
        {
            Assert.Throws<UnauthorizedAccessException>(() => KazoVmRegistryStore.PathFor(User));
        }
    }

    [Fact]
    public async Task The_registry_store_of_the_command_goes_through_the_files_of_the_running_user()
    {
        var files = new X86CommandTests.FakeFiles();
        var store = new KazoVmRegistryStore(files);
        using (KazoExecutionContext.Enter(new TestAppContext()))
        {
            Assert.Null(await store.ReadAsync(User, None));
            await store.WriteAsync(User, "{}", None);
            Assert.Equal("{}", await store.ReadAsync(User, None));
        }

        Assert.Equal([KazoVmRegistryStorePath()], files.Files.Keys);
        static string KazoVmRegistryStorePath() => $"/home/{TestAppContext.DefaultUserId}/{KazoVmRegistryStore.Directory}/{User:N}.json";
    }

    [Fact]
    public async Task The_static_boot_image_source_and_the_unavailable_factory_say_what_is_missing()
    {
        Assert.Equal(X86ErrorCodes.ImageUnavailable, (await Assert.ThrowsAsync<X86Exception>(async () => await StaticBootImageSource.Empty.ResolveAsync("a", "1", None))).Code);
        Assert.Equal(X86ErrorCodes.EngineUnavailable, Assert.Throws<X86Exception>(() => UnavailableEngineFactory.Instance.Create()).Code);
        Assert.NotNull(X86Runtime.Services.Manager);
    }
}
