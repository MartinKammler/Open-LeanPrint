using System.Collections.Concurrent;
using System.Net;
using OpenLeanPrint.Capture.Ipp;
using OpenLeanPrint.Capture.Pdf;

namespace OpenLeanPrint.Capture.Server;

/// <summary>Configuration for a <see cref="IppPrinterServer"/>.</summary>
public sealed record IppPrinterOptions
{
    /// <summary>The printer name advertised to the OS.</summary>
    public string PrinterName { get; init; } = "Open-LeanPrint";

    /// <summary>TCP port the loopback HTTP/IPP endpoint listens on.</summary>
    public int Port { get; init; } = 6310;

    /// <summary>Resource path of the print queue (the tail of the printer URI).</summary>
    public string ResourcePath { get; init; } = "leanprint";

    /// <summary>The ipp:// URI advertised as printer-uri-supported.</summary>
    public string PrinterUri => $"ipp://localhost:{Port}/{ResourcePath}";

    /// <summary>The http:// prefix the listener binds to.</summary>
    public string HttpPrefix => $"http://localhost:{Port}/";

    /// <summary>
    /// The largest request, and the largest document across a job's
    /// Send-Documents, that is accepted. Everything is held in memory until the
    /// job is stored, so without a ceiling one runaway client - anything on the
    /// machine can reach the port - could take the service down. 256 MiB is
    /// far beyond any document a print dialog produces, and stays well under
    /// the capture folder's size limit, so an accepted job always fits there.
    /// </summary>
    public long MaxDocumentBytes { get; init; } = 256L << 20;

    /// <summary>How long a job begun with Create-Job may wait for its next Send-Document before it is dropped.</summary>
    public TimeSpan PendingJobTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How many Create-Job jobs may be open at once; beyond that the printer reports itself busy.</summary>
    public int MaxPendingJobs { get; init; } = 32;

    /// <summary>How many requests are worked on at the same time; the rest wait in the listener's queue.</summary>
    public int MaxConcurrentRequests { get; init; } = 8;

    /// <summary>
    /// The most time one request may take, body included. A client that sends
    /// part of a request and then goes quiet would otherwise hold one of the
    /// <see cref="MaxConcurrentRequests"/> slots for good; enough of them and
    /// the printer stops answering. Five minutes moves the largest permitted
    /// document many times over on the loopback.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long stopping waits for requests already being worked on - a job
    /// halfway through being stored - before cutting them off.
    /// </summary>
    public TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often jobs whose sender went quiet are looked for, independent of new traffic.</summary>
    public TimeSpan AbandonedJobSweepInterval { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// A minimal loopback IPP print service. The OS is pointed at it via the in-box
/// IPP class driver (no third-party driver); when an application prints, the job
/// arrives here as PDF and is surfaced through <see cref="JobCaptured"/>.
/// <para>
/// Supports the operations Windows needs to discover the queue and submit a job:
/// Get-Printer-Attributes, Validate-Job, Print-Job, and the
/// Create-Job / Send-Document pair.
/// </para>
/// </summary>
public sealed class IppPrinterServer : IDisposable
{
    private readonly IppPrinterOptions _options;
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<int, PendingJob> _pendingJobs = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly SemaphoreSlim _slots;

    // Stable UUID advertised as printer-uuid (IPP Everywhere requires one).
    private const string PrinterUuid = "urn:uuid:6f4c6e50-7072-696e-7420-4f70656e4c50";
    private readonly ConcurrentDictionary<Task, byte> _active = new();
    private readonly object _admission = new();
    private int _nextJobId;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private Timer? _sweeper;
    private volatile bool _stopping;

    public IppPrinterServer(IppPrinterOptions? options = null)
    {
        _options = options ?? new IppPrinterOptions();
        _listener.Prefixes.Add(_options.HttpPrefix);
        _slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentRequests));
    }

    /// <summary>
    /// Keeps a finished job before the client is told it succeeded. Return
    /// false, or throw, when it could not be kept - a full disk, a locked
    /// folder - and the client hears that the job failed, so Windows reports an
    /// error instead of a document vanishing while the queue says "printed".
    /// Without it, a job counts as kept once <see cref="JobCaptured"/> has run.
    /// </summary>
    public Func<CapturedJob, bool>? StoreJob { get; set; }

    /// <summary>Raised on the thread pool whenever a complete job has been captured (and, with <see cref="StoreJob"/>, stored).</summary>
    public event EventHandler<CapturedJob>? JobCaptured;

    /// <summary>
    /// Raised for every incoming HTTP/IPP request with a short diagnostic line
    /// (method, path, byte count, parsed operation or parse error). Useful for
    /// seeing exactly what the OS sends while wiring up printer registration.
    /// </summary>
    public event EventHandler<string>? RequestLog;

    public IppPrinterOptions Options => _options;

    public void Start()
    {
        if (_acceptLoop is not null) return;
        _stopping = false;
        _cts = new CancellationTokenSource();
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        // Abandoned jobs are otherwise only noticed when the next job comes in;
        // on a quiet machine their buffers would stay for as long as it is quiet.
        _sweeper = new Timer(_ => DropAbandonedJobs(), null,
                             _options.AbandonedJobSweepInterval, _options.AbandonedJobSweepInterval);
    }

    /// <summary>
    /// Stops in order: no new requests are taken, the ones being worked on get
    /// <see cref="IppPrinterOptions.StopGracePeriod"/> to finish - a job that is
    /// being stored is stored, and its client hears so - and only then is
    /// whatever is left cut off and the listener closed.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;

        _stopping = true;
        _sweeper?.Dispose();
        _sweeper = null;

        // A finished handler has handed its answer to the system, not yet to
        // the client: closing the listener at once resets the connection and
        // the answer with it - measured, the client saw "connection closed"
        // for a job that had been stored. A moment is plenty on the loopback.
        if (await WaitForActiveAsync(_options.StopGracePeriod).ConfigureAwait(false))
            await Task.Delay(250).ConfigureAwait(false);

        _cts.Cancel(); // ends what is still reading - see HandleRequestAsync
        try { _listener.Stop(); } catch { /* already stopping */ }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _acceptLoop = null;

        // Cut off, they end quickly; the semaphore they release must outlive them.
        await WaitForActiveAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    /// <returns>True when there was anything to wait for.</returns>
    private async Task<bool> WaitForActiveAsync(TimeSpan limit)
    {
        var running = _active.Keys.ToArray();
        if (running.Length == 0) return false;
        try { await Task.WhenAll(running).WaitAsync(limit).ConfigureAwait(false); }
        catch (Exception) { /* timed out, or a handler failed - either way, move on */ }
        return true;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break; // listener stopped
            }

            // Stopping: nothing new is started, so the wait for what is running
            // has an end.
            if (_stopping)
            {
                try { context.Response.Abort(); } catch { /* ignore */ }
                continue;
            }

            // Back-pressure: with every slot busy the loop waits here, and new
            // connections queue in the listener instead of each getting a thread
            // and a buffer of its own.
            try
            {
                await _slots.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { context.Response.Abort(); } catch { /* ignore */ }
                break;
            }

            // Tracked, not fire-and-forget: stopping waits for these.
            var handler = Task.Run(async () =>
            {
                try { await HandleRequestAsync(context, token).ConfigureAwait(false); }
                finally { _slots.Release(); }
            }, CancellationToken.None);
            _active.TryAdd(handler, 0);
            _ = handler.ContinueWith(t => _active.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken stopping)
    {
        // The deadline closes the connection rather than relying on the read
        // honouring a token: a read that is waiting for bytes that never come
        // ends when its connection does.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(_options.RequestTimeout);
        using var cutOff = deadline.Token.Register(() =>
        {
            try { context.Response.Abort(); } catch { /* already gone */ }
        });

        try
        {
            string method = context.Request.HttpMethod;
            string rawUrl = context.Request.RawUrl ?? "/";

            byte[]? body = await ReadBodyAsync(context.Request, _options.MaxDocumentBytes, deadline.Token).ConfigureAwait(false);

            IppMessage response;
            string outcome;
            if (body is null)
            {
                body = Array.Empty<byte>();
                outcome = $"refused: larger than {_options.MaxDocumentBytes:N0} bytes";
                response = Failure(0, IppStatus.ClientErrorRequestEntityTooLarge, "The document is too large.");
            }
            else try
            {
                var request = IppReader.Parse(body);
                outcome = $"IPP {request.Operation} (v{request.VersionMajor}.{request.VersionMinor}, req#{request.RequestId})";
                response = Dispatch(request);
                outcome += $" -> {response.Status}";
            }
            catch (Exception ex)
            {
                outcome = $"not IPP / unparseable ({ex.GetType().Name})";
                response = NewResponse(0, IppStatus.ClientErrorBadRequest);
            }

            RequestLog?.Invoke(this, $"{method} {rawUrl} [{body.Length} bytes] {outcome}");

            byte[] responseBytes = IppWriter.Serialize(response);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/ipp";
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: nothing more we can do for a broken connection.
        }
        finally
        {
            try { context.Response.Close(); } catch { /* ignore */ }
        }
    }

    /// <summary>The request body, or null as soon as it grows past <paramref name="limit"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest request, long limit, CancellationToken cancel)
    {
        if (request.ContentLength64 > limit) return null;

        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await request.InputStream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > limit) return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private IppMessage Dispatch(IppMessage request)
    {
        return request.Operation switch
        {
            IppOperation.GetPrinterAttributes => GetPrinterAttributes(request),
            IppOperation.ValidateJob => SimpleOk(request),
            IppOperation.PrintJob => PrintJob(request),
            IppOperation.CreateJob => CreateJob(request),
            IppOperation.SendDocument => SendDocument(request),
            IppOperation.CancelJob => CancelJob(request),
            IppOperation.GetJobAttributes => SimpleOk(request),
            IppOperation.GetJobs => SimpleOk(request),
            _ => NewResponse(request.RequestId, IppStatus.ServerErrorOperationNotSupported),
        };
    }

    private IppMessage GetPrinterAttributes(IppMessage request)
    {
        var response = NewResponse(request.RequestId, IppStatus.SuccessfulOk);
        var p = response.AddGroup(IppTag.PrinterAttributes);

        int upTime = (int)Math.Max(1, (DateTimeOffset.UtcNow - _startedAt).TotalSeconds);

        // --- Identity / URIs ---
        p.Add(new IppAttribute("printer-uri-supported", IppTag.Uri, _options.PrinterUri));
        p.Add(new IppAttribute("uri-authentication-supported", IppTag.Keyword, "none"));
        p.Add(new IppAttribute("uri-security-supported", IppTag.Keyword, "none"));
        p.Add(new IppAttribute("printer-name", IppTag.NameWithoutLanguage, _options.PrinterName));
        p.Add(new IppAttribute("printer-info", IppTag.TextWithoutLanguage, _options.PrinterName));
        p.Add(new IppAttribute("printer-location", IppTag.TextWithoutLanguage, ""));
        p.Add(new IppAttribute("printer-make-and-model", IppTag.TextWithoutLanguage, "Open-LeanPrint Virtual Printer"));
        p.Add(new IppAttribute("printer-uuid", IppTag.Uri, PrinterUuid));
        p.Add(new IppAttribute("printer-device-id", IppTag.TextWithoutLanguage,
            "MFG:Open-LeanPrint;MDL:Virtual Printer;CMD:PDF;CLS:PRINTER;"));

        // --- State ---
        p.Add(new IppAttribute("printer-state", IppTag.Enum, (int)IppPrinterState.Idle));
        p.Add(new IppAttribute("printer-state-reasons", IppTag.Keyword, "none"));
        p.Add(new IppAttribute("printer-is-accepting-jobs", IppTag.Boolean, true));
        p.Add(new IppAttribute("queued-job-count", IppTag.Integer, _pendingJobs.Count));
        p.Add(new IppAttribute("printer-up-time", IppTag.Integer, upTime));
        p.Add(new IppAttribute("pages-per-minute", IppTag.Integer, 20));

        // --- Protocol / operations ---
        p.Add(new IppAttribute("ipp-versions-supported", IppTag.Keyword, "1.1", "2.0"));
        p.Add(new IppAttribute("ipp-features-supported", IppTag.Keyword, "ipp-everywhere"));
        p.Add(new IppAttribute("operations-supported", IppTag.Enum,
            (int)IppOperation.PrintJob, (int)IppOperation.ValidateJob,
            (int)IppOperation.CreateJob, (int)IppOperation.SendDocument,
            (int)IppOperation.CancelJob, (int)IppOperation.GetJobAttributes,
            (int)IppOperation.GetJobs, (int)IppOperation.GetPrinterAttributes));
        p.Add(new IppAttribute("charset-configured", IppTag.Charset, "utf-8"));
        p.Add(new IppAttribute("charset-supported", IppTag.Charset, "utf-8"));
        p.Add(new IppAttribute("natural-language-configured", IppTag.NaturalLanguage, "en"));
        p.Add(new IppAttribute("generated-natural-language-supported", IppTag.NaturalLanguage, "en"));
        p.Add(new IppAttribute("pdl-override-supported", IppTag.Keyword, "attempted"));
        p.Add(new IppAttribute("compression-supported", IppTag.Keyword, "none"));
        p.Add(new IppAttribute("job-creation-attributes-supported", IppTag.Keyword,
            "copies", "media", "sides", "print-color-mode", "print-quality",
            "printer-resolution", "orientation-requested"));

        // --- Document formats (PDF preferred; raster advertised for IPP Everywhere acceptance) ---
        p.Add(new IppAttribute("document-format-default", IppTag.MimeMediaType, "application/pdf"));
        p.Add(new IppAttribute("document-format-supported", IppTag.MimeMediaType,
            "application/pdf", "image/pwg-raster", "application/octet-stream"));

        // --- Copies / finishings / quality ---
        p.Add(new IppAttribute("copies-default", IppTag.Integer, 1));
        p.Add(new IppAttribute("copies-supported", IppTag.RangeOfInteger, IppValues.RangeOfInteger(1, 999)));
        p.Add(new IppAttribute("finishings-default", IppTag.Enum, 3)); // none
        p.Add(new IppAttribute("finishings-supported", IppTag.Enum, 3));
        p.Add(new IppAttribute("print-quality-default", IppTag.Enum, 4)); // normal
        p.Add(new IppAttribute("print-quality-supported", IppTag.Enum, 3, 4, 5));
        p.Add(new IppAttribute("orientation-requested-default", IppTag.Enum, 3)); // portrait
        p.Add(new IppAttribute("orientation-requested-supported", IppTag.Enum, 3, 4)); // portrait, landscape
        // Colour, because this printer does not print anything: it hands the
        // document on to a real one. Advertising monochrome made Windows convert
        // every job to greyscale on the way in, and a colour document arrived
        // already grey - the loss happens before the file reaches us and cannot
        // be undone afterwards.
        p.Add(new IppAttribute("color-supported", IppTag.Boolean, true));
        p.Add(new IppAttribute("print-color-mode-default", IppTag.Keyword, "color"));
        p.Add(new IppAttribute("print-color-mode-supported", IppTag.Keyword, "color", "monochrome"));

        // --- Sides ---
        p.Add(new IppAttribute("sides-default", IppTag.Keyword, "one-sided"));
        p.Add(new IppAttribute("sides-supported", IppTag.Keyword,
            "one-sided", "two-sided-long-edge", "two-sided-short-edge"));

        // --- Resolution ---
        p.Add(new IppAttribute("printer-resolution-default", IppTag.Resolution, IppValues.Resolution(300, 300)));
        p.Add(new IppAttribute("printer-resolution-supported", IppTag.Resolution, IppValues.Resolution(300, 300)));

        // --- Media ---
        p.Add(new IppAttribute("media-default", IppTag.Keyword, "iso_a4_210x297mm"));
        p.Add(new IppAttribute("media-ready", IppTag.Keyword, "iso_a4_210x297mm"));
        p.Add(new IppAttribute("media-supported", IppTag.Keyword,
            "iso_a4_210x297mm", "na_letter_8.5x11in"));
        p.Add(new IppAttribute("media-source-supported", IppTag.Keyword, "auto"));
        p.Add(new IppAttribute("media-type-supported", IppTag.Keyword, "stationery"));
        p.Add(new IppAttribute("media-left-margin-supported", IppTag.Integer, 0));
        p.Add(new IppAttribute("media-right-margin-supported", IppTag.Integer, 0));
        p.Add(new IppAttribute("media-top-margin-supported", IppTag.Integer, 0));
        p.Add(new IppAttribute("media-bottom-margin-supported", IppTag.Integer, 0));
        p.Add(new IppAttribute("output-bin-default", IppTag.Keyword, "face-down"));
        p.Add(new IppAttribute("output-bin-supported", IppTag.Keyword, "face-down"));

        // --- PWG Raster / URF descriptors (required for the class driver to accept a driverless queue) ---
        p.Add(new IppAttribute("pwg-raster-document-resolution-supported", IppTag.Resolution, IppValues.Resolution(300, 300)));
        p.Add(new IppAttribute("pwg-raster-document-sheet-back", IppTag.Keyword, "normal"));
        p.Add(new IppAttribute("pwg-raster-document-type-supported", IppTag.Keyword, "sgray_8", "srgb_8"));
        p.Add(new IppAttribute("urf-supported", IppTag.Keyword,
            "CP1", "IS1", "MT1-2-3-4-5-6", "OB10", "PQ4", "RS300", "SRGB24", "V1.4", "W8", "DM1"));

        return response;
    }

    private IppMessage PrintJob(IppMessage request)
    {
        int jobId = Interlocked.Increment(ref _nextJobId);
        return FinalizeJob(jobId, request, request.Data)
            ? JobResponse(request, jobId, IppJobState.Completed, "job-completed-successfully")
            : NotStored(request, jobId);
    }

    private IppMessage CreateJob(IppMessage request)
    {
        DropAbandonedJobs();

        // Counting and adding as one step: checked separately, several
        // Create-Jobs at once could each see room for one more.
        int jobId;
        lock (_admission)
        {
            if (_pendingJobs.Count >= _options.MaxPendingJobs)
                return Failure(request.RequestId, IppStatus.ServerErrorBusy, "Too many jobs are open at once.");

            jobId = Interlocked.Increment(ref _nextJobId);
            _pendingJobs[jobId] = new PendingJob
            {
                JobId = jobId,
                JobName = request.FindAttribute("job-name")?.AsString(),
                UserName = request.FindAttribute("requesting-user-name")?.AsString(),
                Sides = request.FindAttribute("sides")?.AsString(),
                ColorMode = request.FindAttribute("print-color-mode")?.AsString(),
            };
        }
        return JobResponse(request, jobId, IppJobState.Pending, "none");
    }

    /// <summary>
    /// Cancels a job that is still waiting for its documents - what Windows
    /// sends when somebody cancels a print halfway. A job that is already
    /// complete has been handed on and cannot be called back.
    /// </summary>
    private IppMessage CancelJob(IppMessage request)
    {
        int jobId = request.FindAttribute("job-id")?.AsInt() ?? 0;
        if (jobId == 0 || !_pendingJobs.TryGetValue(jobId, out var pending))
            return Failure(request.RequestId, IppStatus.ClientErrorNotFound, "No such job is open.");

        lock (pending)
        {
            if (pending.Closed)
                return Failure(request.RequestId, IppStatus.ClientErrorNotFound, "No such job is open.");
            Close(jobId, pending);
        }
        RequestLog?.Invoke(this, $"job {jobId} dropped: cancelled by the client");
        return JobResponse(request, jobId, IppJobState.Canceled, "job-canceled-by-user");
    }

    private IppMessage SendDocument(IppMessage request)
    {
        DropAbandonedJobs();
        int jobId = request.FindAttribute("job-id")?.AsInt() ?? 0;
        if (jobId == 0 || !_pendingJobs.TryGetValue(jobId, out var pending))
            return NewResponse(request.RequestId, IppStatus.ClientErrorNotFound);

        bool lastDocument = request.FindAttribute("last-document") is { } a
                            && a.Values.Count > 0 && a.Values[0] is true;

        // The dictionary is thread-safe, the job in it is not: two Send-Documents
        // for one job would write into the same buffer, and a late one could
        // land after the job had been finished. One lock per job makes adding,
        // finishing and dropping it a single step each.
        byte[] document;
        lock (pending)
        {
            if (pending.Closed)
                return NewResponse(request.RequestId, IppStatus.ClientErrorNotFound);

            if (pending.Buffer.Length + request.Data.Length > _options.MaxDocumentBytes)
            {
                Close(jobId, pending);
                return Failure(request.RequestId, IppStatus.ClientErrorRequestEntityTooLarge, "The document is too large.");
            }

            if (request.Data.Length > 0)
                pending.Buffer.Write(request.Data, 0, request.Data.Length);
            pending.LastActivity = DateTime.UtcNow;

            if (!lastDocument)
                return JobResponse(request, jobId, IppJobState.Pending, "none");

            document = pending.Buffer.ToArray();
            Close(jobId, pending);
        }

        return FinalizeJob(jobId, request, document, pending.JobName, pending.UserName,
                           pending.Sides, pending.ColorMode)
            ? JobResponse(request, jobId, IppJobState.Completed, "job-completed-successfully")
            : NotStored(request, jobId);
    }

    /// <summary>
    /// Forgets jobs whose sender went quiet. A Create-Job that never gets its
    /// last document - a client that crashed, a cancelled print - would
    /// otherwise keep its buffer for as long as the service runs.
    /// </summary>
    private void DropAbandonedJobs()
    {
        var cutoff = DateTime.UtcNow - _options.PendingJobTimeout;
        foreach (var (jobId, pending) in _pendingJobs)
        {
            lock (pending)
            {
                if (!pending.Closed && pending.LastActivity < cutoff)
                {
                    Close(jobId, pending);
                    RequestLog?.Invoke(this, $"job {jobId} dropped: no document for {_options.PendingJobTimeout}");
                }
            }
        }
    }

    /// <summary>Marks a job finished or dropped and lets go of it. Call with the job's lock held.</summary>
    private void Close(int jobId, PendingJob pending)
    {
        pending.Closed = true;
        _pendingJobs.TryRemove(jobId, out _);
        pending.Buffer.Dispose();
    }

    /// <summary>The answer when the job arrived whole but could not be kept.</summary>
    private IppMessage NotStored(IppMessage request, int jobId)
    {
        var response = Failure(request.RequestId, IppStatus.ServerErrorInternalError,
                               "The job could not be stored. Nothing was printed.");
        var job = response.AddGroup(IppTag.JobAttributes);
        job.Add(new IppAttribute("job-id", IppTag.Integer, jobId));
        job.Add(new IppAttribute("job-state", IppTag.Enum, (int)IppJobState.Aborted));
        job.Add(new IppAttribute("job-state-reasons", IppTag.Keyword, "aborted-by-system"));
        return response;
    }

    private static IppMessage Failure(int requestId, IppStatus status, string message)
    {
        var response = NewResponse(requestId, status);
        response.FirstGroup(IppTag.OperationAttributes)!
            .Add(new IppAttribute("status-message", IppTag.TextWithoutLanguage, message));
        return response;
    }

    /// <summary>Builds the captured job, stores it, and says whether it was kept.</summary>
    private bool FinalizeJob(int jobId, IppMessage request, byte[] data,
                                    string? jobName = null, string? userName = null,
                                    string? sides = null, string? colorMode = null)
    {
        string format = request.FindAttribute("document-format")?.AsString() ?? "application/pdf";
        var captured = new CapturedJob
        {
            JobId = jobId,
            JobName = jobName ?? request.FindAttribute("job-name")?.AsString(),
            UserName = userName ?? request.FindAttribute("requesting-user-name")?.AsString(),
            DocumentFormat = format,
            Sides = sides ?? request.FindAttribute("sides")?.AsString(),
            ColorMode = colorMode ?? request.FindAttribute("print-color-mode")?.AsString(),
            Data = data,
        };

        if (captured.IsPdf && data.Length > 0)
        {
            try
            {
                captured.Document = PdfPageExtractor.ToPrintDocument(
                    data, captured.JobName ?? _options.PrinterName, captured.UserName);
            }
            catch (Exception ex)
            {
                captured.ParseError = ex.Message;
            }
        }

        if (StoreJob is { } store)
        {
            bool kept;
            try
            {
                kept = store(captured);
            }
            catch (Exception ex)
            {
                RequestLog?.Invoke(this, $"job {jobId} not stored: {ex.Message}");
                kept = false;
            }
            if (!kept) return false;
        }

        try
        {
            JobCaptured?.Invoke(this, captured);
        }
        catch (Exception ex)
        {
            // The job is stored; a failing listener does not undo that.
            RequestLog?.Invoke(this, $"job {jobId}: a JobCaptured handler failed: {ex.Message}");
        }
        return true;
    }

    private IppMessage JobResponse(IppMessage request, int jobId, IppJobState state, string reason)
    {
        var response = NewResponse(request.RequestId, IppStatus.SuccessfulOk);
        var job = response.AddGroup(IppTag.JobAttributes);
        job.Add(new IppAttribute("job-uri", IppTag.Uri, $"{_options.PrinterUri}/jobs/{jobId}"));
        job.Add(new IppAttribute("job-id", IppTag.Integer, jobId));
        job.Add(new IppAttribute("job-state", IppTag.Enum, (int)state));
        job.Add(new IppAttribute("job-state-reasons", IppTag.Keyword, reason));
        return response;
    }

    private IppMessage SimpleOk(IppMessage request) =>
        NewResponse(request.RequestId, IppStatus.SuccessfulOk);

    private static IppMessage NewResponse(int requestId, IppStatus status)
    {
        var response = new IppMessage
        {
            VersionMajor = 1,
            VersionMinor = 1,
            OperationOrStatus = (short)status,
            RequestId = requestId == 0 ? 1 : requestId,
        };
        var op = response.AddGroup(IppTag.OperationAttributes);
        op.Add(new IppAttribute("attributes-charset", IppTag.Charset, "utf-8"));
        op.Add(new IppAttribute("attributes-natural-language", IppTag.NaturalLanguage, "en"));
        return response;
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* ignore */ }
        _sweeper?.Dispose();
        _cts?.Dispose();
        // Only once no handler is left that could still release it.
        if (_active.IsEmpty) _slots.Dispose();
        (_listener as IDisposable).Dispose();
    }

    /// <summary>
    /// A job between Create-Job and its last Send-Document.
    /// <para>
    /// Everything the client asked for arrives with Create-Job; Send-Document
    /// carries the bytes and little else. So what is wanted has to be kept here
    /// until the document is complete - reading it off the Send-Document request
    /// finds nothing, which is exactly how a measurement of two-sided printing
    /// came back empty and looked like the print path had asked for nothing.
    /// </para>
    /// </summary>
    private sealed class PendingJob
    {
        public required int JobId { get; init; }
        public string? JobName { get; init; }
        public string? UserName { get; init; }
        public string? Sides { get; init; }
        public string? ColorMode { get; init; }
        public MemoryStream Buffer { get; } = new();

        /// <summary>When the last request for this job arrived; abandoned jobs are dropped after a while.</summary>
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;

        /// <summary>Finished or dropped; any further Send-Document is refused.</summary>
        public bool Closed { get; set; }
    }
}
