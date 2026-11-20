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
/// <para>
/// A path counts as handled only while its file exists. The capture service
/// numbers jobs from 1 again whenever it restarts, so once a job has been
/// collected and deleted, a new job can arrive under the very same name - and
/// must be seen as new, not as the old one again.
/// </para>
/// </summary>
public sealed class CapturedFolderWatcher : IDisposable
{
    private readonly string _filter;
    private FileSystemWatcher? _watcher;
    private Timer? _waitForFolder;
    private readonly object _startLock = new();
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
        _filter = filter;
    }

    /// <summary>The folder being watched (absolute).</summary>
    public string Folder { get; }

    /// <summary>How long to wait for a file to finish being written.</summary>
    public TimeSpan CompletionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the folder is compared with what has been handled, to catch anything an event missed.</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a file that never settled is tried before <see cref="JobTimedOut"/> gives it up.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Whether <see cref="Start"/> creates the folder. Off for a folder someone
    /// else creates - the capture service makes each user's own folder with
    /// their first job, and they could not create it themselves. Until it
    /// exists, the watcher looks for it every <see cref="FolderPollInterval"/>.
    /// </summary>
    public bool CreateFolder { get; init; } = true;

    /// <summary>How often a folder that does not exist yet is looked for.</summary>
    public TimeSpan FolderPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Optional veto, e.g. to ignore a nested output folder.</summary>
    public Func<string, bool>? Accept { get; init; }

    /// <summary>A file finished arriving. Raised on a background thread.</summary>
    public event EventHandler<string>? JobArrived;

    /// <summary>A file never stopped changing within <see cref="CompletionTimeout"/>, <see cref="MaxAttempts"/> times over.</summary>
    public event EventHandler<string>? JobTimedOut;

    /// <summary>
    /// Handling a file failed - a <see cref="JobArrived"/> handler threw, or the
    /// file could not be looked at. The watcher carries on with the next file;
    /// this is for logging.
    /// </summary>
    public event EventHandler<(string Path, Exception Error)>? HandlingFailed;

    /// <summary>Starts watching. <paramref name="includeExisting"/> also picks up what is already there.</summary>
    public void Start(bool includeExisting = false)
    {
        if (_consumer is not null) return;

        if (CreateFolder) Directory.CreateDirectory(Folder);
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        _consumer = new Thread(() => Consume(token))
        {
            IsBackground = true,
            Name = "OpenLeanPrint capture watcher",
        };
        _consumer.Start();

        // A folder that only appears later holds nothing old: whatever is in it
        // then - the job that made it, at least - is new.
        if (!TryBeginWatching(includeExisting))
            _waitForFolder = new Timer(_ => TryBeginWatching(includeExisting: true), null,
                                       FolderPollInterval, FolderPollInterval);
    }

    /// <summary>Is the folder being watched yet? False while it does not exist.</summary>
    public bool IsWatching
    {
        get { lock (_startLock) return _watcher is not null; }
    }

    /// <summary>Starts the actual watching, once the folder is there.</summary>
    private bool TryBeginWatching(bool includeExisting)
    {
        lock (_startLock)
        {
            if (_watcher is not null) return true;
            if (_cancellation is null || _cancellation.IsCancellationRequested) return false;
            if (!Directory.Exists(Folder)) return false;

            FileSystemWatcher watcher;
            try
            {
                watcher = new FileSystemWatcher(Folder, _filter)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
            }
            catch (Exception)
            {
                return false; // gone again, or not ours to watch yet
            }

            watcher.Created += (_, e) => Enqueue(e.FullPath);
            // Some writers create a temp file and rename it into place.
            watcher.Renamed += (_, e) => Enqueue(e.FullPath);
            // Renamed away or deleted: the name is free for a new job.
            watcher.Renamed += (_, e) => Forget(e.OldFullPath);
            watcher.Deleted += (_, e) => Forget(e.FullPath);
            // A buffer overflow means events were lost; the folder still knows.
            watcher.Error += (_, _) => Reconcile();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;

            _waitForFolder?.Dispose();
            _waitForFolder = null;
            _reconcile = new Timer(_ => Reconcile(), null, ReconcileInterval, ReconcileInterval);

            if (includeExisting) Reconcile();
            return true;
        }
    }

    /// <summary>Stops watching. Safe to call more than once.</summary>
    public void Stop()
    {
        lock (_startLock)
        {
            _waitForFolder?.Dispose();
            _waitForFolder = null;
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            _reconcile?.Dispose();
            _reconcile = null;
        }
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
                // One file going wrong - a handler that throws, a file nobody
                // may open - must not end this thread: every job after it would
                // silently stay in the folder.
                try
                {
                    Handle(path);
                }
                catch (Exception ex)
                {
                    try { HandlingFailed?.Invoke(this, (path, ex)); } catch (Exception) { /* logging must not stop us either */ }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() - a clean shutdown.
        }
    }

    private void Handle(string path)
    {
        if (WaitUntilComplete(path, CompletionTimeout))
        {
            JobArrived?.Invoke(this, path);
        }
        else if (!File.Exists(path))
        {
            // Gone before it settled - removed, or renamed into its real name,
            // which raises an event of its own.
            Forget(path);
        }
        else if (GiveUp(path))
        {
            JobTimedOut?.Invoke(this, path);
        }
    }

    /// <summary>The file is gone; a new one under this name is a new job.</summary>
    private void Forget(string path)
    {
        lock (_seen)
        {
            _seen.Remove(path);
            _attempts.Remove(path);
        }
    }

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

    /// <summary>
    /// Queues every file in the folder that has not been handled yet, and
    /// forgets handled files that are gone - their delete event may have been
    /// among the lost ones.
    /// </summary>
    private void Reconcile()
    {
        try
        {
            var present = new HashSet<string>(Directory.EnumerateFiles(Folder, _filter),
                                              StringComparer.OrdinalIgnoreCase);
            lock (_seen)
            {
                _seen.RemoveWhere(path => !present.Contains(path));
            }
            foreach (string path in present) Enqueue(path);
        }
        catch (Exception)
        {
            // The folder is briefly gone or unreadable; the next round will see.
        }
    }

    /// <summary>
    /// Waits until a file has stopped growing and is no longer locked by its
    /// writer. Returns false if it never settled.
    /// </summary>
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
        _queue.Dispose();
    }
}
