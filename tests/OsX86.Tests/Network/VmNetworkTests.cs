using System.Buffers.Binary;
using System.Text.Json;
using OsX86.Engine;
using OsX86.Engine.V86;
using OsX86.Interface;
using OsX86.Network;
using OsX86.Prefixes;
using OsX86.Profiles;
using OsX86.Tests.Agent;
using OsX86.Tests.Engine;
using OsX86.Tests.Vm;
using OsX86.Vm;

namespace OsX86.Tests.Network;

/// <summary>
/// Work item 12 on a simulated engine and agent: a VM gets its network card and its address when it
/// starts, a prefix its namespace and address on first use; a deleted prefix loses its rules; an
/// image without network runs without it; the worker protocol carries the frames.
/// </summary>
[Trait("Category", "unit")]
public sealed class VmNetworkTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Guid User = VmTestRig.User;

    private static void ScriptNetworkGuest(FakeEngine engine, string netReply = """{"ok":true,"changed":true,"ms":40}""", int version = 3)
    {
        SimulatedAgentChannel agent = engine.Agent!;
        agent.On("ping", _ => $$"""{"ok":true,"version":{{version}},"uptime":3,"rssKb":170,"launches":0,"consoles":0,"cgroups":true}""");
        agent.On("prefix-ensure", request => $$"""{"ok":true,"created":true,"prefix":"{{request.GetProperty("prefix").GetString()}}","uid":2000,"home":"/home/osx86-{{request.GetProperty("prefix").GetString()}}","ms":40}""");
        agent.On("prefix-delete", _ => """{"ok":true,"deleted":true,"ms":8}""");
        agent.On("sync", _ => """{"ok":true,"timedOut":false,"ms":2}""");
        agent.On("net", _ => netReply);
    }

    private static NetworkHub Hub(out LocalTestRelay relay)
    {
        relay = new LocalTestRelay().WithHttp("203.0.113.9", 80);
        return new NetworkHub(new InMemoryNetworkStore(), relay, new InjectedPermissionPrompt(_ => NetworkPermissionAnswer.AllowOnce));
    }

    [Fact]
    public async Task A_vm_gets_its_card_and_address_and_a_prefix_its_namespace_on_first_use()
    {
        await using var rig = new VmTestRig(network: Hub(out _));
        rig.Engines.Configure = engine => ScriptNetworkGuest(engine);
        VmInstance vm = await rig.CreateAsync("base", BuiltInProfiles.LinuxBase);

        await vm.HoldAsync(None);
        FakeEngine engine = rig.Engines.Last;
        Assert.True(engine.StartedWith!.Network);
        JsonElement up = engine.Agent!.Request("net");
        Assert.Equal(("vm-up", 2), (up.GetProperty("action").GetString(), up.GetProperty("vmSlot").GetInt32()));
        Assert.Equal("10.86.0.2", vm.NetworkAddress.ToString());

        // The guest's card asks for the gateway: the answer reaches the card.
        engine.EmitFrames(ArpRequest(vm.NetworkAddress!.Value));
        await VmTestRig.WaitUntilAsync(() => engine.FramesSent.Count == 1, "the ARP reply on the card");

        Assert.True(await rig.Manager.CreatePrefixAsync(User, vm.Id, new PrefixId("work"), None));
        JsonElement prefixUp = engine.Agent.Requests("net")[1];
        Assert.Equal(("prefix-up", "work", 2, 0), (prefixUp.GetProperty("action").GetString(), prefixUp.GetProperty("prefix").GetString(), prefixUp.GetProperty("vmSlot").GetInt32(), prefixUp.GetProperty("prefixSlot").GetInt32()));
        Assert.Equal("10.86.2.2", vm.NetworkAddressOf(new PrefixId("work")).ToString());
        Assert.Null(vm.NetworkAddressOf(new PrefixId("never")));

        // Its rules go with it.
        UserNetwork network = rig.Manager.Network!.For(User);
        await network.Rules.AddAsync(new FlowIdentity(vm.Id, new PrefixId("work")), NetworkDestination.Any, NetworkRuleAction.Allow, false, None);
        Assert.True(await rig.Manager.DeletePrefixAsync(User, vm.Id, new PrefixId("work"), force: false, None));
        Assert.Empty(await network.Rules.ListAsync(None));
        Assert.Null(vm.NetworkAddressOf(new PrefixId("work")));

        await vm.StopAsync(force: true, None);
        Assert.Null(vm.NetworkAddress);
        Assert.Empty(network.Attached);
        Assert.Empty(rig.BackgroundErrors);

        // A removed VM leaves no rule behind.
        await network.Rules.AddAsync(new FlowIdentity(vm.Id, null), NetworkDestination.Any, NetworkRuleAction.Deny, false, None);
        await rig.Manager.RemoveAsync(User, vm.Id, force: true, None);
        Assert.Empty(await network.Rules.ListAsync(None));
    }

    [Theory]
    [InlineData(2, """{"ok":true,"changed":true}""", false)]
    [InlineData(3, """{"ok":false,"error":"no-net-support","message":"this image has no net-admin"}""", false)]
    [InlineData(3, """{"ok":false,"error":"net-failed","message":"net-admin vm-up failed: no eth0"}""", true)]
    public async Task A_vm_whose_guest_has_no_network_runs_without_it(int version, string netReply, bool reported)
    {
        await using var rig = new VmTestRig(network: Hub(out _));
        rig.Engines.Configure = engine => ScriptNetworkGuest(engine, netReply, version);
        VmInstance vm = await rig.CreateAsync("base", BuiltInProfiles.LinuxBase);

        await vm.HoldAsync(None);

        Assert.Equal(VmState.Running, vm.State);
        Assert.Null(vm.NetworkAddress);
        Assert.Empty(rig.Manager.Network!.For(User).Attached);
        Assert.Equal(reported, rig.BackgroundErrors.Count == 1);
        Assert.True(await rig.Manager.CreatePrefixAsync(User, vm.Id, new PrefixId("work"), None)); // prefixes still work
        Assert.Null(vm.NetworkAddressOf(new PrefixId("work")));
    }

    [Fact]
    public async Task A_prefix_whose_namespace_fails_still_runs_and_the_error_is_reported()
    {
        await using var rig = new VmTestRig(network: Hub(out _));
        rig.Engines.Configure = engine =>
        {
            ScriptNetworkGuest(engine);
            engine.Agent!.On("net", request => request.GetProperty("action").GetString() == "vm-up"
                ? """{"ok":true,"changed":true}"""
                : """{"ok":false,"error":"net-failed","message":"net-admin prefix-up failed"}""");
        };
        VmInstance vm = await rig.CreateAsync("base", BuiltInProfiles.LinuxBase);
        await vm.HoldAsync(None);

        Assert.True(await rig.Manager.CreatePrefixAsync(User, vm.Id, new PrefixId("work"), None));
        Assert.Null(vm.NetworkAddressOf(new PrefixId("work")));
        Assert.Single(rig.BackgroundErrors);
        Assert.NotNull(vm.NetworkAddress);
    }

    [Fact]
    public async Task Without_a_hub_or_for_dos_no_card_is_given()
    {
        await using (var plain = new VmTestRig())
        {
            plain.Engines.Configure = engine => ScriptNetworkGuest(engine);
            VmInstance vm = await plain.CreateAsync("base", BuiltInProfiles.LinuxBase);
            await vm.HoldAsync(None);
            Assert.False(plain.Engines.Last.StartedWith!.Network);
            Assert.Empty(plain.Engines.Last.Agent!.Requests("net"));
        }

        await using var rig = new VmTestRig(network: Hub(out _));
        VmInstance alpine = await rig.CreateAsync("alpine", BuiltInProfiles.AlpineTest);
        await alpine.HoldAsync(None);
        Assert.True(rig.Engines.Last.StartedWith!.Network); // a card, but its agent of protocol 1 has no network operation
        Assert.Null(alpine.NetworkAddress);
    }

    [Fact]
    public async Task The_worker_protocol_carries_frames_both_ways_and_the_start_asks_for_the_card()
    {
        var channel = new SimulatedWorkerChannel();
        await using var engine = new V86Engine(channel);
        var received = new List<byte[]>();
        engine.FramesReceived += (_, e) => received.AddRange(e.Frames);

        await Assert.ThrowsAsync<EngineStateException>(async () => await engine.SendFramesAsync([[1, 2]], None));
        await engine.StartAsync(new EngineStartOptions { KernelPath = "k", InitrdPath = "i", Network = true }, None);
        Assert.True(Posted(channel, "start").GetProperty("options").GetProperty("network").GetBoolean());

        await engine.SendFramesAsync([[1, 2, 3], [4]], None);
        JsonElement net = Posted(channel, "net");
        Assert.Equal(["AQID", "BA=="], net.GetProperty("frames").EnumerateArray().Select(frame => frame.GetString()));
        await engine.SendFramesAsync([], None);

        await channel.SendRawAsync("""{"type":"net","frames":["AQID","BA=="]}""");
        await channel.SendRawAsync("""{"type":"net","frames":["not base64!"]}""");
        await channel.SendRawAsync("""{"type":"net"}""");
        Assert.Equal([new byte[] { 1, 2, 3 }, new byte[] { 4 }], received);
        Assert.Equal(EngineErrorCodes.Protocol, engine.LastError!.Code);
        Assert.Equal("""{"type":"net","frames":["AQ=="]}""", WorkerProtocol.Net([[1]]));
    }

    private static JsonElement Posted(SimulatedWorkerChannel channel, string type) =>
        JsonDocument.Parse(channel.Posted.Last(json => JsonDocument.Parse(json).RootElement.GetProperty("type").GetString() == type)).RootElement;

    private static byte[] ArpRequest(Ipv4Address sender)
    {
        byte[] frame = PacketBuilder.ArpReply(NetworkRig.GuestMac, sender, MacAddress.Broadcast, AddressPlan.Gateway);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(EthernetFrame.HeaderLength + 6), ArpPacket.Request);
        return frame;
    }
}
