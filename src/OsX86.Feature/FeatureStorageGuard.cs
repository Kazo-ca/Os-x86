using System.Globalization;
using Kazo.Os.AppSdk.Storage;
using OsX86.Storage;

namespace OsX86.Feature;

/// <summary>The <c>osx86/</c> tree of the Feature in the OPFS, as the guard needs it (tests give a simulated one).</summary>
internal interface IFeatureStorageTree
{
    /// <summary>Writes the marker Kazo checks at each boot (<see cref="StorageLayout.MarkerPath"/>).</summary>
    ValueTask WriteMarkerAsync(CancellationToken cancellationToken);

    /// <summary>The bytes of the whole tree.</summary>
    ValueTask<long> MeasureAsync(CancellationToken cancellationToken);
}

/// <summary>The tree through the storage worker (work item 3).</summary>
/// <param name="store">The image store of the host.</param>
internal sealed class ImageStoreTree(ImageStore store) : IFeatureStorageTree
{
    private readonly ImageStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public ValueTask WriteMarkerAsync(CancellationToken cancellationToken) => _store.WriteMarkerAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<long> MeasureAsync(CancellationToken cancellationToken) =>
        (await _store.EstimateAsync(cancellationToken).ConfigureAwait(false)).FeatureBytes;
}

/// <summary>
/// What the Feature does with the storage of the browser (ADR 0250 of Kazo, work item 8, decision
/// 289 A). After a gesture of the user that stores something (<c>x86 vm create</c>), it asks the
/// browser to protect the origin, writes and registers its marker, and declares the bytes it keeps;
/// at each <c>x86</c>, it tells the user once per page, in the language of the OS, that the browser
/// erased its storage or withdrew its protection. One instance per page.
/// </summary>
internal sealed class FeatureStorageGuard
{
    private readonly Lock _lock = new();
    private readonly HashSet<(KazoStorageChangeKind Kind, DateTimeOffset DetectedAtUtc)> _told = [];
    private bool _persisted;
    private bool _markerWritten;

    /// <summary>
    /// After a gesture of the user that stores data: asks for persistence until the browser grants it,
    /// writes and registers the marker once (again after an eviction), declares the bytes.
    /// </summary>
    /// <param name="kazo">KazoStorage of the app that runs.</param>
    /// <param name="tree">The tree, or <see langword="null"/> on a host without storage.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public async ValueTask ProtectAsync(IAppStorageGateway kazo, IFeatureStorageTree? tree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kazo);
        if (!kazo.IsSupported)
        {
            return;
        }

        bool askPersistence;
        bool writeMarker;
        lock (_lock)
        {
            askPersistence = !_persisted;
            writeMarker = !_markerWritten && tree is not null;
        }

        if (askPersistence && await kazo.RequestPersistenceAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_lock)
            {
                _persisted = true;
            }
        }

        if (writeMarker)
        {
            await tree!.WriteMarkerAsync(cancellationToken).ConfigureAwait(false);
            await kazo.RegisterMarkerAsync(StorageLayout.MarkerPath, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _markerWritten = true;
            }
        }

        await ReportUsageAsync(kazo, tree, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Declares the bytes of the tree to Kazo (Settings ▸ Storage, <c>df</c>); nothing without a tree.</summary>
    /// <param name="kazo">KazoStorage of the app that runs.</param>
    /// <param name="tree">The tree, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public async ValueTask ReportUsageAsync(IAppStorageGateway kazo, IFeatureStorageTree? tree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kazo);
        if (tree is null || !kazo.IsSupported)
        {
            return;
        }

        long bytes = await tree.MeasureAsync(cancellationToken).ConfigureAwait(false);
        await kazo.ReportUsageAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The changes Kazo found that the user was not told of yet in this page, each as a sentence in
    /// <paramref name="culture"/>. Kazo delivers a change found at boot as soon as a handler is added.
    /// </summary>
    /// <param name="kazo">KazoStorage of the app that runs.</param>
    /// <param name="culture">The language of the OS for this process.</param>
    public IReadOnlyList<string> TakeNotices(IAppStorageGateway kazo, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(kazo);
        ArgumentNullException.ThrowIfNull(culture);
        if (!kazo.IsSupported)
        {
            return [];
        }

        List<KazoStorageChangedEventArgs> changes = [];
        EventHandler<KazoStorageChangedEventArgs> collect = (_, change) => changes.Add(change);
        kazo.Changed += collect;
        kazo.Changed -= collect;

        var texts = Strings.For(culture);
        List<string> notices = [];
        lock (_lock)
        {
            foreach (KazoStorageChangedEventArgs change in changes)
            {
                if (!_told.Add((change.Kind, change.DetectedAtUtc)))
                {
                    continue;
                }

                if (change.Kind == KazoStorageChangeKind.Evicted)
                {
                    // The marker went with the rest: the next gesture writes it again.
                    _markerWritten = false;
                    notices.Add(string.Format(culture, texts["Storage.Evicted"].Value, change.DetectedAtUtc.LocalDateTime));
                }
                else if (change.Kind == KazoStorageChangeKind.PersistenceLost)
                {
                    _persisted = false;
                    notices.Add(texts["Storage.PersistenceLost"].Value);
                }
            }
        }

        return notices;
    }
}
