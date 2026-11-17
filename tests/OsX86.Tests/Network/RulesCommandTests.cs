using OsX86.Feature;
using OsX86.Network;
using OsX86.Prefixes;
using OsX86.Tests.Vm;
using OsX86.Vm;

namespace OsX86.Tests.Network;

/// <summary>Work item 12: <c>x86 rules list|allow|deny|ask|remove|log</c>, their parsing, output and exit codes.</summary>
[Trait("Category", "unit")]
[Collection(X86CommandCollection.Name)]
public sealed class RulesCommandTests
{
    private static NetworkHub Hub() => new(new InMemoryNetworkStore(), new LocalTestRelay(), UnavailablePermissionPrompt.Instance);

    private static Task<(int ExitCode, string Out, string Err)> RunAsync(VmTestRig rig, params string[] args) => X86CommandTests.RunAsync(rig, null, args);

    [Fact]
    public async Task Rules_are_added_listed_filtered_and_removed_per_prefix_or_vm()
    {
        await using var rig = new VmTestRig(network: Hub());
        Assert.Equal(0, (await RunAsync(rig, "vm", "create", "base")).ExitCode);
        VmRecord vm = await rig.Manager.ResolveAsync(VmTestRig.User, "base", CancellationToken.None);
        await rig.Manager.CreatePrefixAsync(VmTestRig.User, vm.Id, new PrefixId("work"), CancellationToken.None);

        var (code, output, _) = await RunAsync(rig, "rules", "list");
        Assert.Equal((0, "No network rule: the first connection to a destination asks (or is refused when no window can be shown).\n"), (code, output));

        (code, output, _) = await RunAsync(rig, "rules", "allow", "203.0.113.9:80");
        Assert.Equal(0, code);
        Assert.Equal(
            "Rule 1: allow 203.0.113.9:80 for the prefix default of base.\nThe limits still hold: ports 80, 443, 4 connections at once, cut after 300 s idle.\n",
            output);
        Assert.Equal("Rule 2: deny any for the virtual machine base itself.\n", (await RunAsync(rig, "rules", "deny", "any", "--machine", "--vm", "base")).Out);
        Assert.Equal("Rule 3: ask every time for *:443 for the prefix work of base.\n", (await RunAsync(rig, "rules", "ask", "*:443", "--prefix", "work")).Out);

        (code, output, _) = await RunAsync(rig, "rules", "list");
        string[] lines = output.TrimEnd().Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("ID   VM              PREFIX            DESTINATION", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"^1\s+base\s+default\s+203\.0\.113\.9:80\s+allow\s+always$", lines[1]);
        Assert.Matches(@"^2\s+base\s+\(machine\)\s+any\s+deny\s+always$", lines[2]);
        Assert.Matches(@"^3\s+base\s+work\s+\*:443\s+ask\s+always$", lines[3]);

        Assert.Equal(2, (await RunAsync(rig, "rules", "list", "--machine")).Out.TrimEnd().Split('\n').Length);
        Assert.Equal(2, (await RunAsync(rig, "rules", "list", "--prefix", "work")).Out.TrimEnd().Split('\n').Length);
        Assert.Equal(4, (await RunAsync(rig, "rules", "list", "--vm", "base")).Out.TrimEnd().Split('\n').Length);

        var removed = await RunAsync(rig, "rules", "remove", "2");
        Assert.Equal((0, "Removed rule 2.\n"), (removed.ExitCode, removed.Out));
        Assert.DoesNotContain("(machine)", (await RunAsync(rig, "rules", "list")).Out, StringComparison.Ordinal);
        var missing = await RunAsync(rig, "rules", "remove", "2");
        Assert.Equal(X86ExitCodes.NotFound, missing.ExitCode);
        Assert.Contains("no network rule 2", missing.Err, StringComparison.Ordinal);
        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, "rules", "allow", "any", "--prefix", "nope")).ExitCode);
        Assert.Equal(X86ExitCodes.Failed, (await RunAsync(rig, "rules", "allow", "any", "--prefix", "Bad Name")).ExitCode);
        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, "rules", "allow", "any", "--vm", "ghost")).ExitCode);
    }

    [Fact]
    public async Task The_journal_is_shown_newest_last_with_its_reasons()
    {
        await using var rig = new VmTestRig(network: Hub());
        await RunAsync(rig, "vm", "create", "base");
        VmRecord vm = await rig.Manager.ResolveAsync(VmTestRig.User, "base", CancellationToken.None);
        Assert.Equal("The network journal is empty.\n", (await RunAsync(rig, "rules", "log")).Out);

        NetworkJournal journal = rig.Manager.Network!.For(VmTestRig.User).Journal;
        await journal.AppendAsync(new NetworkJournalEntry { Time = default, Vm = vm.Id, Prefix = PrefixId.Default, Host = "203.0.113.9", Port = 80, Event = NetworkJournalEvent.Opened, Reason = NetworkJournalReasons.Prompt }, CancellationToken.None);
        await journal.AppendAsync(new NetworkJournalEntry { Time = default, Vm = vm.Id, Prefix = PrefixId.Default, Host = "203.0.113.9", Port = 80, Event = NetworkJournalEvent.Closed, Reason = NetworkJournalReasons.Ended, BytesOut = 23, BytesIn = 120, DurationMs = 512 }, CancellationToken.None);
        await journal.AppendAsync(new NetworkJournalEntry { Time = default, Vm = Guid.NewGuid(), Prefix = null, Host = "8.8.8.8", Port = 53, Event = NetworkJournalEvent.Refused, Reason = NetworkJournalReasons.Udp, Detail = "udp/53 (dns)", Rule = 4 }, CancellationToken.None);

        var (code, output, _) = await RunAsync(rig, "rules", "log");
        string[] lines = output.TrimEnd().Split('\n');
        Assert.Equal(0, code);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("TIME (UTC)           VM              PREFIX", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"base\s+default\s+203\.0\.113\.9:80\s+opened\s+prompt\s+-$", lines[1]);
        Assert.Matches(@"closed\s+ended\s+-\s+out 23 B, in 120 B, 512 ms$", lines[2]);
        Assert.Matches(@"^\S+ \S+\s+[0-9a-f]{8}\s+\(machine\)\s+8\.8\.8\.8:53\s+refused\s+udp\s+4\s+udp/53 \(dns\)$", lines[3]);

        Assert.Equal(2, (await RunAsync(rig, "rules", "log", "--last", "1")).Out.TrimEnd().Split('\n').Length);
        Assert.Equal(3, (await RunAsync(rig, "rules", "log", "--vm", "base")).Out.TrimEnd().Split('\n').Length);
    }

    [Theory]
    [InlineData("rules")]
    [InlineData("rules", "frob")]
    [InlineData("rules", "allow")]
    [InlineData("rules", "allow", "bad host")]
    [InlineData("rules", "allow", "any", "--machine", "--prefix", "p")]
    [InlineData("rules", "remove", "abc")]
    [InlineData("rules", "remove", "0")]
    [InlineData("rules", "log", "--last", "0")]
    [InlineData("rules", "list", "--force")]
    public async Task Wrong_command_lines_are_usage_errors(params string[] args)
    {
        await using var rig = new VmTestRig(network: Hub());
        await RunAsync(rig, "vm", "create", "base");
        var (code, _, error) = await RunAsync(rig, args);
        Assert.Equal(X86ExitCodes.Usage, code);
        Assert.StartsWith("x86: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_network_or_a_vm_the_command_says_so()
    {
        await using (var none = new VmTestRig())
        {
            var (code, _, error) = await RunAsync(none, "rules", "list");
            Assert.Equal(X86ExitCodes.Unavailable, code);
            Assert.Contains("no private network", error, StringComparison.Ordinal);
        }

        await using var rig = new VmTestRig(network: Hub());
        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, "rules", "allow", "any")).ExitCode);
        await RunAsync(rig, "vm", "create", "one");
        await RunAsync(rig, "vm", "create", "two");
        Assert.Equal(X86ExitCodes.Usage, (await RunAsync(rig, "rules", "allow", "any")).ExitCode);
        Assert.Equal(X86ExitCodes.NotFound, (await RunAsync(rig, "rules", "list", "--prefix", "default")).ExitCode);
        Assert.Contains("rules allow|deny|ask <destination>", (await RunAsync(rig, "--help")).Out, StringComparison.Ordinal);
    }
}
