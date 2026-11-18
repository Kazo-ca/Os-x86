using System.Globalization;
using Kazo.Os.AppSdk.Storage;
using OsX86.Feature;

namespace OsX86.Tests;

/// <summary>
/// What the Feature does with the storage of the browser (ADR 0250 of Kazo, decision 289 A): it asks
/// for persistence after a gesture of the user, writes and registers its marker, declares its bytes,
/// and tells the user once, in the language of the OS, when the browser erased its storage or
/// withdrew its protection.
/// </summary>
[Trait("Category", "unit")]
public sealed class FeatureStorageGuardTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    [Fact]
    public async Task The_first_protection_asks_persistence_writes_and_registers_the_marker_and_declares_the_bytes()
    {
        var kazo = new FakeStorageGateway();
        var tree = new FakeStorageTree { Bytes = 4096 };
        var guard = new FeatureStorageGuard();

        await guard.ProtectAsync(kazo, tree, None);
        await guard.ProtectAsync(kazo, tree, None);

        Assert.Equal(1, kazo.PersistenceRequests);
        Assert.Equal(1, tree.MarkersWritten);
        Assert.Equal(["osx86/.kazoos-marker"], kazo.Markers);
        Assert.Equal([4096L, 4096L], kazo.Reports);
    }

    [Fact]
    public async Task A_refused_persistence_is_asked_again_at_the_next_gesture()
    {
        var kazo = new FakeStorageGateway { GrantPersistence = false };
        var guard = new FeatureStorageGuard();

        await guard.ProtectAsync(kazo, new FakeStorageTree(), None);
        kazo.GrantPersistence = true;
        await guard.ProtectAsync(kazo, new FakeStorageTree(), None);
        await guard.ProtectAsync(kazo, new FakeStorageTree(), None);

        Assert.Equal(2, kazo.PersistenceRequests);
    }

    [Fact]
    public async Task Without_browser_storage_or_without_a_tree_nothing_is_written()
    {
        var unsupported = new FakeStorageGateway { IsSupported = false };
        var guard = new FeatureStorageGuard();
        var tree = new FakeStorageTree();

        await guard.ProtectAsync(unsupported, tree, None);
        Assert.Equal(0, unsupported.PersistenceRequests);
        Assert.Equal(0, tree.MarkersWritten);

        var kazo = new FakeStorageGateway();
        await guard.ProtectAsync(kazo, null, None);
        Assert.Equal(1, kazo.PersistenceRequests);
        Assert.Empty(kazo.Markers);
        Assert.Empty(kazo.Reports);
    }

    [Fact]
    public void An_eviction_is_told_once_in_the_language_of_the_os()
    {
        var kazo = new FakeStorageGateway();
        kazo.Pending.Add(new KazoStorageChangedEventArgs(KazoStorageChangeKind.Evicted, "ca.kazo.x86", "osx86/.kazoos-marker", new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero)));
        var guard = new FeatureStorageGuard();

        string notice = Assert.Single(guard.TakeNotices(kazo, French));

        Assert.StartsWith("x86 : le navigateur a effacé", notice, StringComparison.Ordinal);
        Assert.Contains("disques", notice, StringComparison.Ordinal);
        Assert.Empty(guard.TakeNotices(kazo, French)); // once per page
        Assert.Equal(0, kazo.Handlers);
    }

    [Fact]
    public void A_withdrawn_protection_is_told_once_and_in_english_for_an_english_os()
    {
        var kazo = new FakeStorageGateway();
        kazo.Pending.Add(new KazoStorageChangedEventArgs(KazoStorageChangeKind.PersistenceLost, "ca.kazo.x86", null, DateTimeOffset.UnixEpoch));
        var guard = new FeatureStorageGuard();

        string notice = Assert.Single(guard.TakeNotices(kazo, English));

        Assert.StartsWith("x86: the browser no longer protects", notice, StringComparison.Ordinal);
        Assert.Empty(guard.TakeNotices(kazo, English));
    }

    [Fact]
    public async Task After_an_eviction_the_next_protection_writes_the_marker_again()
    {
        var kazo = new FakeStorageGateway();
        var tree = new FakeStorageTree();
        var guard = new FeatureStorageGuard();
        await guard.ProtectAsync(kazo, tree, None);

        kazo.Pending.Add(new KazoStorageChangedEventArgs(KazoStorageChangeKind.Evicted, "ca.kazo.x86", "osx86/.kazoos-marker", DateTimeOffset.UnixEpoch));
        Assert.Single(guard.TakeNotices(kazo, English));
        await guard.ProtectAsync(kazo, tree, None);

        Assert.Equal(2, tree.MarkersWritten);
        Assert.Equal(2, kazo.Markers.Count);
    }

    [Fact]
    public async Task Declaring_the_bytes_alone_needs_a_tree()
    {
        var kazo = new FakeStorageGateway();
        var guard = new FeatureStorageGuard();

        await guard.ReportUsageAsync(kazo, new FakeStorageTree { Bytes = 10 }, None);
        await guard.ReportUsageAsync(kazo, null, None);

        Assert.Equal([10L], kazo.Reports);
        Assert.Equal(0, kazo.PersistenceRequests);
    }

    /// <summary>The OPFS tree of the Feature, simulated.</summary>
    internal sealed class FakeStorageTree : IFeatureStorageTree
    {
        public long Bytes { get; set; }

        public int MarkersWritten { get; private set; }

        public ValueTask WriteMarkerAsync(CancellationToken cancellationToken)
        {
            MarkersWritten++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<long> MeasureAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Bytes);
    }

    /// <summary>KazoStorage as a feature sees it: a change found at boot comes as soon as a handler is added.</summary>
    internal sealed class FakeStorageGateway : IAppStorageGateway
    {
        private EventHandler<KazoStorageChangedEventArgs>? _changed;

        public bool IsSupported { get; set; } = true;

        public bool GrantPersistence { get; set; } = true;

        public int PersistenceRequests { get; private set; }

        public List<string> Markers { get; } = [];

        public List<long> Reports { get; } = [];

        public List<KazoStorageChangedEventArgs> Pending { get; } = [];

        public int Handlers => _changed?.GetInvocationList().Length ?? 0;

        public event EventHandler<KazoStorageChangedEventArgs>? Changed
        {
            add
            {
                _changed += value;
                foreach (KazoStorageChangedEventArgs change in Pending)
                {
                    value?.Invoke(this, change);
                }
            }

            remove => _changed -= value;
        }

        public ValueTask<KazoStorageEstimate> GetEstimateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(KazoStorageEstimate.Unknown);

        public ValueTask<bool> RequestPersistenceAsync(CancellationToken cancellationToken = default)
        {
            PersistenceRequests++;
            return ValueTask.FromResult(GrantPersistence);
        }

        public ValueTask RegisterMarkerAsync(string path, CancellationToken cancellationToken = default)
        {
            Markers.Add(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReportUsageAsync(long bytes, CancellationToken cancellationToken = default)
        {
            Reports.Add(bytes);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<KazoFeatureStorageUsage>> GetFeatureUsageAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<KazoFeatureStorageUsage>>([]);
    }
}
