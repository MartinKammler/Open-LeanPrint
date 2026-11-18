using System.Collections.Concurrent;

namespace OpenLeanPrint.Capture;

/// <summary>
/// Raises <see cref="JobArrived"/> for every PDF that lands in a folder — the
/// bridge between the capture host (which writes files) and anything that wants
/// to act on them: the CLI's <c>watch</c> command and the desktop app's job pool.
/// <para>
/// The subtlety this class exists for: a file-creation event fires as soon as
/// the file exists, long before the writer is finished with it. Events are
/// therefore queued and only handed on once the file has stopped growing and can
/// be opened exclusively.
/// </para>
/// <para>
/// Events alone are not trusted to be complete. A FileSystemWatcher drops
/// them when its buffer overflows, and a file that was still locked when its
/// wait ran out has had its only event. So the folder is also compared with
/// what has been handled - after an overflow and every
/// <see cref="ReconcileInterval"/> - and a file that timed out is tried again,
/// up to <see cref="MaxAttempts"/> times, before it is given up on.
/// </para>
/// </summary>
public sealed class CapturedFolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly BlockingCollection<string> _queue = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _reconcile;
    private CancellationTokenSource? _cancellation;
    private Thread? _consumer;

    public CapturedFolderWatcher(string folder, string filter = "*.pdf")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        Folder = Path.GetFullPath(folder);
        _watcher = new FileSystemWatcher(Folder, filter)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        _watcher.Created += (_, e) => Enqueue(e.FullPath);
        // Some writers create a temp file and rename it into place.
        _watcher.Renamed += (_, e) => Enqueue(e.FullPath);
        // A buffer overflow means events were lost; the folder still knows.
        _watcher.Error += (_, _) => Reconcile();
    }

    /// <summary>The folder being watched (absolute).</summary>
    public string Folder { get; }

    /// <summary>How long to wait for a file to finish being written.</summary>
    public TimeSpan CompletionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the folder is compared with what has been handled, to catch anything an event missed.</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a file that never settled is tried before <see cref="JobTimedOut"/> gives it up.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Optional veto, e.g. to ignore a nested output folder.</summary>
    public Func<string, bool>? Accept { get; init; }

    /// <summary>A file finished arriving. Raised on a background thread.</summary>
    public event EventHandler<string>? JobArrived;

    /// <summary>A file never stopped changing within <see cref="CompletionTimeout"/>, <see cref="MaxAttempts"/> times over.</summary>
    public event EventHandler<string>? JobTimedOut;

    /// <summary>Starts watching. <paramref name="includeExisting"/> also picks up what is already there.</summary>
    public void Start(bool includeExisting = false)
    {
        if (_consumer is not null) return;

        Directory.CreateDirectory(Folder);
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        _consumer = new Thread(() => Consume(token))
        {
            IsBackground = true,
            Name = "OpenLeanPrint capture watcher",
        };
        _consumer.Start();

        _watcher.EnableRaisingEvents = true;
        _reconcile = new Timer(_ => Reconcile(), null, ReconcileInterval, ReconcileInterval);

        if (includeExisting)
            foreach (string path in Directory.EnumerateFiles(Folder, _watcher.Filter)) Enqueue(path);
    }

    /// <summary>Stops watching. Safe to call more than once.</summary>
    public void Stop()
    {
        _watcher.EnableRaisingEvents = false;
        _reconcile?.Dispose();
        _reconcile = null;
        _cancellation?.Cancel();
        _consumer?.Join(TimeSpan.FromSeconds(2));
        _consumer = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private void Enqueue(string path)
    {
        if (Accept is not null && !Accept(path)) return;

        lock (_seen)
        {
            if (!_seen.Add(path)) return; // one file can raise several events
        }

        // The queue is only closed on Dispose; losing a late event - or a late
        // reconcile - is harmless. ObjectDisposedException is one of these.
        try { _queue.Add(path); }
        catch (InvalidOperationException) { }
    }

    private void Consume(CancellationToken token)
    {
        try
        {
            foreach (string path in _queue.GetConsumingEnumerable(token))
            {
                if (WaitUntilComplete(path, CompletionTimeout))
                {
                    JobArrived?.Invoke(this, path);
                }
                else if (!File.Exists(path))
                {
                    // Gone before it settled - removed, or renamed into its
                    // real name, which raises an event of its own.
                    lock (_seen) _attempts.Remove(path);
                }
                else if (GiveUp(path))
                {
                    JobTimedOut?.Invoke(this, path);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() - a clean shutdown.
        }
    }

    /// <summary>
    /// Waits until a file has stopped growing and is no longer locked by its
    /// writer. Returns false if it never settled.
    /// </summary>
    /// <summary>
    /// Counts a timed-out attempt. Under the limit the file is forgotten, so the
    /// next comparison with the folder queues it again.
    /// </summary>
    /// <returns>True once the file has had all its attempts.</returns>
    private bool GiveUp(string path)
    {
        lock (_seen)
        {
            int attempts = _attempts.TryGetValue(path, out int n) ? n + 1 : 1;
            if (attempts >= MaxAttempts)
            {
                _attempts.Remove(path);
                return true;
            }
            _attempts[path] = attempts;
            _seen.Remove(path);
            return false;
        }
    }

    /// <summary>Queues every file in the folder that has not been handled yet.</summary>
    private void Reconcile()
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(Folder, _watcher.Filter)) Enqueue(path);
        }
        catch (Exception)
        {
            // The folder is briefly gone or unreadable; the next round will see.
        }
    }

    private static bool WaitUntilComplete(string path, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        long lastLength = -1;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;
                if (info.Length > 0 && info.Length == lastLength)
                {
                    // Opening exclusively proves the writer has let go.
                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return true;
                }
                lastLength = info.Length;
            }
            catch (IOException)
            {
                // Still locked by the writer - keep waiting.
            }
            Thread.Sleep(150);
        }
        return false;
    }

    public void Dispose()
    {
        Stop();
        _watcher.Dispose();
        _queue.Dispose();
    }
}
