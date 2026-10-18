using GibiOMatic.Destinations.Sync;
using GibiOMatic.Library.Index;
using Microsoft.Extensions.Logging;

namespace GibiOMatic.Destinations.Kindle;

public enum KindleSendKind
{
    /// <summary>Not on the device yet.</summary>
    New,

    /// <summary>The app sent an older version with this name: it is replaced (the Kindle's notes stay).</summary>
    Replace,

    /// <summary>The device has a file with this name the app did not send: it is never overwritten.</summary>
    Blocked,
}

/// <param name="DeviceFile">Path inside the device's <c>documents</c> folder.</param>
public sealed record KindleSendItem(string BookId, string Title, string LocalFile, string DeviceFile, long Bytes, string ContentHash,
    DateTime LocalWrittenAt, KindleSendKind Kind);

/// <summary>What sending would do: shown to the user before anything is copied (CLAUDE.md, rule 7).</summary>
public sealed record KindleSendPlan(string DestinationId, IReadOnlyList<KindleSendItem> Items)
{
    public IReadOnlyList<KindleSendItem> ToSend => Items.Where(i => i.Kind != KindleSendKind.Blocked).ToList();

    public long Bytes => ToSend.Sum(i => i.Bytes);
}

public sealed record KindleSendStep(int Done, int Total, string Title, double FileFraction);

public sealed record KindleSendResult(int Sent, IReadOnlyList<(string Title, string Error)> Failures);

public enum KindleBookState
{
    /// <summary>In the list, not prepared yet (or prepared before a change: it is prepared again).</summary>
    Waiting,

    /// <summary>Prepared, waiting for the device.</summary>
    Ready,

    /// <summary>Prepared again after a change; sending replaces the older version on the device.</summary>
    ReadyToReplace,

    /// <summary>On the device, up to date.</summary>
    Sent,
}

public sealed record KindleBookStatus(KindleBookState State, long? Bytes, DateTimeOffset? SentAt);

/// <summary>
/// Copies prepared books to a connected Kindle and deletes books from it when the user asks. The
/// app never deletes from the device on its own (decision of 2026-10-18): the only deletions are
/// the ones the user picks in the Kindle manager, and replacing a file the app itself sent.
/// </summary>
public sealed class KindleSender(LibraryIndex index, ILogger logger)
{
    private readonly OutputStore _outputs = new(index);
    private readonly DestinationLogStore _log = new(index);

    /// <summary>The prepared books of the destination (still in its list), against what the device has.</summary>
    public async Task<KindleSendPlan> PlanAsync(KindleDestination destination, IKindleConnection device, CancellationToken ct)
    {
        var folder = destination.Config.Folder ?? throw new InvalidOperationException("The destination has no folder.");
        var books = (await index.GetBooksAsync(ct)).ToDictionary(b => b.Metadata.Id);
        var items = new List<KindleSendItem>();
        foreach (var record in (await _outputs.GetAsync(destination.Config.Id, ct)).Values.OrderBy(r => r.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            var local = DestinationSync.InsideFolder(folder, record.Path);
            if (!File.Exists(local) || !books.TryGetValue(record.BookId, out var book) || !destination.Includes(book.Metadata))
            {
                continue;
            }

            var deviceFile = Path.GetFileName(record.Path);
            var kind = !await Task.Run(() => device.Exists(deviceFile), ct)
                ? KindleSendKind.New
                : KindleSentStore.Read(folder, record.BookId)?.DeviceFile == deviceFile ? KindleSendKind.Replace : KindleSendKind.Blocked;
            var info = new FileInfo(local);
            items.Add(new KindleSendItem(record.BookId, book.Metadata.Title, local, deviceFile, info.Length, record.ContentHash,
                info.LastWriteTimeUtc, kind));
        }

        return new KindleSendPlan(destination.Config.Id, items);
    }

    /// <summary>Where each book of the destination's list is: waiting, prepared, or on the device.</summary>
    public async Task<IReadOnlyDictionary<string, KindleBookStatus>> StatusAsync(KindleDestination destination, CancellationToken ct)
    {
        var folder = destination.Config.Folder!;
        var recorded = await _outputs.GetAsync(destination.Config.Id, ct);
        var states = new Dictionary<string, KindleBookStatus>();
        foreach (var book in (await index.GetBooksAsync(ct)).Where(b => destination.Includes(b.Metadata)))
        {
            var id = book.Metadata.Id;
            var signature = destination.ContentSignature(book);
            var sent = KindleSentStore.Read(folder, id);
            var local = recorded.TryGetValue(id, out var record) && record.ContentHash == signature
                ? new FileInfo(DestinationSync.InsideFolder(folder, record.Path))
                : null;
            states[id] = local is { Exists: true }
                ? new KindleBookStatus(sent is null ? KindleBookState.Ready : KindleBookState.ReadyToReplace, local.Length, sent?.SentAt)
                : record is not null && sent?.ContentHash == signature
                    ? new KindleBookStatus(KindleBookState.Sent, null, sent.SentAt)
                    : new KindleBookStatus(KindleBookState.Waiting, null, sent?.SentAt);
        }

        return states;
    }

    /// <summary>Device files the app sent (to mark them in the Kindle manager).</summary>
    public static IReadOnlySet<string> SentFiles(KindleDestination destination) =>
        KindleSentStore.All(destination.Config.Folder!).Select(s => s.DeviceFile).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the plan's books; each one sent is remembered and its prepared file deleted.</summary>
    public async Task<KindleSendResult> SendAsync(KindleDestination destination, KindleSendPlan plan, IKindleConnection device,
        IProgress<KindleSendStep>? progress, CancellationToken ct)
    {
        var folder = destination.Config.Folder!;
        var items = plan.ToSend;
        var failures = new List<(string, string)>();
        var sent = 0;
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[i];
            var step = i;
            progress?.Report(new KindleSendStep(step, items.Count, item.Title, 0));
            try
            {
                var fileProgress = progress is null ? null : new Progress(f => progress.Report(new KindleSendStep(step, items.Count, item.Title, f)));
                await Task.Run(() => device.Upload(item.LocalFile, item.DeviceFile, fileProgress, ct), ct);
                KindleSentStore.Write(folder, new KindleSent(item.BookId, item.Title, item.DeviceFile, item.ContentHash, DateTimeOffset.UtcNow));

                // Prepared again while it was being sent (the book changed): that newer copy stays to be sent.
                if (File.Exists(item.LocalFile) && File.GetLastWriteTimeUtc(item.LocalFile) == item.LocalWrittenAt)
                {
                    File.Delete(item.LocalFile);
                }

                await _log.AddAsync(destination.Config.Id, DestinationLogKind.Sent, item.Title, item.DeviceFile, null, CancellationToken.None);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Kindle {Destination}: sending {Title} failed", destination.Config.Name, item.Title);
                failures.Add((item.Title, ex.Message));
                await _log.AddAsync(destination.Config.Id, DestinationLogKind.Failed, item.Title, item.DeviceFile, ex.Message, CancellationToken.None);
            }
        }

        progress?.Report(new KindleSendStep(items.Count, items.Count, string.Empty, 0));
        logger.LogInformation("Kindle {Destination}: {Sent} sent, {Failed} failed", destination.Config.Name, sent, failures.Count);
        return new KindleSendResult(sent, failures);
    }

    /// <summary>
    /// Deletes the books the user picked in the Kindle manager (with the Kindle's notes of each).
    /// Returns the ids of the books the app had sent among them, so they can leave the destination's
    /// list: otherwise they would be prepared and offered again.
    /// </summary>
    public async Task<IReadOnlyList<string>> DeleteAsync(KindleDestination destination, IKindleConnection device, IReadOnlyList<KindleFile> files,
        CancellationToken ct)
    {
        var folder = destination.Config.Folder!;
        var sentByFile = KindleSentStore.All(folder).GroupBy(s => s.DeviceFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var books = new List<string>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => device.Delete(file.Path), ct);
            foreach (var sent in sentByFile.GetValueOrDefault(file.Path) ?? [])
            {
                KindleSentStore.Forget(folder, sent.BookId);
                books.Add(sent.BookId);
            }

            await _log.AddAsync(destination.Config.Id, DestinationLogKind.RemovedFromDevice, file.Name, file.Path, null, CancellationToken.None);
            logger.LogInformation("Kindle {Destination}: the user deleted {File} from the device", destination.Config.Name, file.Path);
        }

        return books;
    }

    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
