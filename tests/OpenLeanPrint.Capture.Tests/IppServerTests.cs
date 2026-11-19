using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using OpenLeanPrint.Capture;
using OpenLeanPrint.Capture.Ipp;
using OpenLeanPrint.Capture.Server;
using Xunit;

namespace OpenLeanPrint.Capture.Tests;

public class IppServerTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<IppMessage> PostAsync(string prefix, IppMessage request)
    {
        using var client = new HttpClient();
        using var content = new ByteArrayContent(IppWriter.Serialize(request));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");
        var response = await client.PostAsync(prefix.TrimEnd('/') + "/leanprint", content);
        response.EnsureSuccessStatusCode();
        return IppReader.Parse(await response.Content.ReadAsByteArrayAsync());
    }

    private static IppMessage NewRequest(IppOperation op, string printerUri)
    {
        var msg = new IppMessage { OperationOrStatus = (short)op, RequestId = 1 };
        var g = msg.AddGroup(IppTag.OperationAttributes);
        g.Add(new IppAttribute("attributes-charset", IppTag.Charset, "utf-8"));
        g.Add(new IppAttribute("attributes-natural-language", IppTag.NaturalLanguage, "en"));
        g.Add(new IppAttribute("printer-uri", IppTag.Uri, printerUri));
        return msg;
    }

    [Fact]
    public async Task GetPrinterAttributes_ReturnsOkWithPrinterName()
    {
        var options = new IppPrinterOptions { Port = FreePort(), PrinterName = "OLP-Test" };
        using var server = new IppPrinterServer(options);
        server.Start();

        var response = await PostAsync(options.HttpPrefix, NewRequest(IppOperation.GetPrinterAttributes, options.PrinterUri));

        Assert.Equal(IppStatus.SuccessfulOk, response.Status);
        var printer = response.FirstGroup(IppTag.PrinterAttributes);
        Assert.NotNull(printer);
        Assert.Equal("OLP-Test", printer!.Find("printer-name")!.AsString());
        Assert.Equal("application/pdf", printer.Find("document-format-default")!.AsString());

        await server.StopAsync();
    }

    [Fact]
    public async Task GetPrinterAttributes_AdvertisesIppEverywhereRequiredAttributes()
    {
        var options = new IppPrinterOptions { Port = FreePort() };
        using var server = new IppPrinterServer(options);
        server.Start();

        var response = await PostAsync(options.HttpPrefix, NewRequest(IppOperation.GetPrinterAttributes, options.PrinterUri));
        var printer = response.FirstGroup(IppTag.PrinterAttributes)!;

        // A representative slice of the attributes the Windows IPP class driver
        // needs before it will accept a driverless queue.
        foreach (var name in new[]
        {
            "ipp-features-supported", "printer-uuid", "printer-device-id",
            "print-color-mode-supported", "media-ready", "copies-supported",
            "finishings-supported", "urf-supported", "pwg-raster-document-type-supported",
            "job-creation-attributes-supported",
        })
        {
            Assert.NotNull(printer.Find(name));
        }

        Assert.Equal("ipp-everywhere", printer.Find("ipp-features-supported")!.AsString());
        Assert.StartsWith("urn:uuid:", printer.Find("printer-uuid")!.AsString());

        await server.StopAsync();
    }

    [Fact]
    public async Task PrintJob_CapturesPdfAndParsesPages()
    {
        var options = new IppPrinterOptions { Port = FreePort() };
        using var server = new IppPrinterServer(options);

        var tcs = new TaskCompletionSource<CapturedJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.JobCaptured += (_, job) => tcs.TrySetResult(job);
        server.Start();

        byte[] pdf = TestPdfs.WithPages((595, 842), (595, 842), (612, 792));

        var request = NewRequest(IppOperation.PrintJob, options.PrinterUri);
        var op = request.FirstGroup(IppTag.OperationAttributes)!;
        op.Add(new IppAttribute("job-name", IppTag.NameWithoutLanguage, "Hello.pdf"));
        op.Add(new IppAttribute("requesting-user-name", IppTag.NameWithoutLanguage, "tester"));
        op.Add(new IppAttribute("document-format", IppTag.MimeMediaType, "application/pdf"));
        request.Data = pdf;

        var response = await PostAsync(options.HttpPrefix, request);
        Assert.Equal(IppStatus.SuccessfulOk, response.Status);
        Assert.NotNull(response.FirstGroup(IppTag.JobAttributes)!.Find("job-id")!.AsInt());

        var captured = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Hello.pdf", captured.JobName);
        Assert.Equal("tester", captured.UserName);
        Assert.True(captured.IsPdf);
        Assert.Equal(pdf.Length, captured.Data.Length);
        Assert.Null(captured.ParseError);
        Assert.NotNull(captured.Document);
        Assert.Equal(3, captured.Document!.PageCount);
        Assert.Equal(595, captured.Document.PageSizes[0].Width, 0);

        await server.StopAsync();
    }

    [Fact]
    public async Task CreateJobThenSendDocument_CapturesOnLastDocument()
    {
        var options = new IppPrinterOptions { Port = FreePort() };
        using var server = new IppPrinterServer(options);

        var tcs = new TaskCompletionSource<CapturedJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.JobCaptured += (_, job) => tcs.TrySetResult(job);
        server.Start();

        // Create-Job
        var create = NewRequest(IppOperation.CreateJob, options.PrinterUri);
        create.FirstGroup(IppTag.OperationAttributes)!
              .Add(new IppAttribute("job-name", IppTag.NameWithoutLanguage, "Chunked.pdf"));
        var createResp = await PostAsync(options.HttpPrefix, create);
        int jobId = createResp.FirstGroup(IppTag.JobAttributes)!.Find("job-id")!.AsInt()!.Value;
        Assert.True(jobId > 0);

        // Send-Document (last)
        byte[] pdf = TestPdfs.WithPages((595, 842));
        var send = NewRequest(IppOperation.SendDocument, options.PrinterUri);
        var sop = send.FirstGroup(IppTag.OperationAttributes)!;
        sop.Add(new IppAttribute("job-id", IppTag.Integer, jobId));
        sop.Add(new IppAttribute("document-format", IppTag.MimeMediaType, "application/pdf"));
        sop.Add(new IppAttribute("last-document", IppTag.Boolean, true));
        send.Data = pdf;
        var sendResp = await PostAsync(options.HttpPrefix, send);
        Assert.Equal(IppStatus.SuccessfulOk, sendResp.Status);

        var captured = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Chunked.pdf", captured.JobName);
        Assert.Equal(1, captured.Document!.PageCount);

        await server.StopAsync();
    }

    private static async Task<int> CreateJobAsync(IppPrinterOptions options)
    {
        var response = await PostAsync(options.HttpPrefix, NewRequest(IppOperation.CreateJob, options.PrinterUri));
        Assert.Equal(IppStatus.SuccessfulOk, response.Status);
        return response.FirstGroup(IppTag.JobAttributes)!.Find("job-id")!.AsInt()!.Value;
    }

    private static IppMessage SendDocument(IppPrinterOptions options, int jobId, byte[] data, bool last)
    {
        var send = NewRequest(IppOperation.SendDocument, options.PrinterUri);
        var op = send.FirstGroup(IppTag.OperationAttributes)!;
        op.Add(new IppAttribute("job-id", IppTag.Integer, jobId));
        op.Add(new IppAttribute("document-format", IppTag.MimeMediaType, "application/pdf"));
        op.Add(new IppAttribute("last-document", IppTag.Boolean, last));
        send.Data = data;
        return send;
    }

    private static IppMessage PrintJobRequest(IppPrinterOptions options, byte[] pdf)
    {
        var request = NewRequest(IppOperation.PrintJob, options.PrinterUri);
        request.FirstGroup(IppTag.OperationAttributes)!
               .Add(new IppAttribute("document-format", IppTag.MimeMediaType, "application/pdf"));
        request.Data = pdf;
        return request;
    }

    [Fact]
    public async Task AJobThatCannotBeStored_IsReportedAsFailed_NotAsPrinted()
    {
        var options = new IppPrinterOptions { Port = FreePort() };
        using var server = new IppPrinterServer(options) { StoreJob = _ => throw new IOException("disk full") };
        bool announced = false;
        server.JobCaptured += (_, _) => announced = true;
        server.Start();

        var response = await PostAsync(options.HttpPrefix, PrintJobRequest(options, TestPdfs.WithPages((595, 842))));

        Assert.Equal(IppStatus.ServerErrorInternalError, response.Status);
        Assert.Equal((int)IppJobState.Aborted, response.FirstGroup(IppTag.JobAttributes)!.Find("job-state")!.AsInt());
        Assert.False(announced);
        await server.StopAsync();
    }

    [Fact]
    public async Task AStoredJob_IsAnnouncedAfterwards()
    {
        var options = new IppPrinterOptions { Port = FreePort() };
        var order = new List<string>();
        using var server = new IppPrinterServer(options) { StoreJob = _ => { lock (order) order.Add("stored"); return true; } };
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.JobCaptured += (_, _) => { lock (order) order.Add("announced"); tcs.TrySetResult(); };
        server.Start();

        var response = await PostAsync(options.HttpPrefix, PrintJobRequest(options, TestPdfs.WithPages((595, 842))));
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(IppStatus.SuccessfulOk, response.Status);
        Assert.Equal(new[] { "stored", "announced" }, order);
        await server.StopAsync();
    }

    [Fact]
    public async Task ADocumentAboveTheLimit_IsRefused_AndTheServerKeepsAnswering()
    {
        var options = new IppPrinterOptions { Port = FreePort(), MaxDocumentBytes = 4096 };
        using var server = new IppPrinterServer(options);
        server.Start();

        var big = PrintJobRequest(options, new byte[8192]);
        var response = await PostAsync(options.HttpPrefix, big);
        Assert.Equal(IppStatus.ClientErrorRequestEntityTooLarge, response.Status);

        var after = await PostAsync(options.HttpPrefix, NewRequest(IppOperation.GetPrinterAttributes, options.PrinterUri));
        Assert.Equal(IppStatus.SuccessfulOk, after.Status);
        await server.StopAsync();
    }

    [Fact]
    public async Task SendDocuments_AddingUpPastTheLimit_EndTheJob()
    {
        var options = new IppPrinterOptions { Port = FreePort(), MaxDocumentBytes = 6000 };
        using var server = new IppPrinterServer(options);
        server.Start();

        int jobId = await CreateJobAsync(options);
        Assert.Equal(IppStatus.SuccessfulOk, (await PostAsync(options.HttpPrefix, SendDocument(options, jobId, new byte[4000], last: false))).Status);
        Assert.Equal(IppStatus.ClientErrorRequestEntityTooLarge, (await PostAsync(options.HttpPrefix, SendDocument(options, jobId, new byte[4000], last: false))).Status);
        // The job is gone, not half-kept.
        Assert.Equal(IppStatus.ClientErrorNotFound, (await PostAsync(options.HttpPrefix, SendDocument(options, jobId, new byte[10], last: true))).Status);
        await server.StopAsync();
    }

    [Fact]
    public async Task AnAbandonedJob_IsDroppedAfterItsTimeout()
    {
        var options = new IppPrinterOptions { Port = FreePort(), PendingJobTimeout = TimeSpan.FromMilliseconds(200) };
        using var server = new IppPrinterServer(options);
        server.Start();

        int jobId = await CreateJobAsync(options);
        await Task.Delay(500);

        var response = await PostAsync(options.HttpPrefix, SendDocument(options, jobId, TestPdfs.WithPages((595, 842)), last: true));
        Assert.Equal(IppStatus.ClientErrorNotFound, response.Status);
        await server.StopAsync();
    }

    [Fact]
    public async Task TooManyOpenJobs_MakeThePrinterBusy_NotTheServiceFall()
    {
        var options = new IppPrinterOptions { Port = FreePort(), MaxPendingJobs = 2 };
        using var server = new IppPrinterServer(options);
        server.Start();

        await CreateJobAsync(options);
        await CreateJobAsync(options);
        var third = await PostAsync(options.HttpPrefix, NewRequest(IppOperation.CreateJob, options.PrinterUri));

        Assert.Equal(IppStatus.ServerErrorBusy, third.Status);
        await server.StopAsync();
    }

    [Fact]
    public async Task ManyJobsAtOnce_AreAllCaptured_EachOnce()
    {
        var options = new IppPrinterOptions { Port = FreePort() };
        var stored = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var server = new IppPrinterServer(options) { StoreJob = job => { stored.Add(job.JobId); return true; } };
        server.Start();

        byte[] pdf = TestPdfs.WithPages((595, 842));
        var responses = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(_ => PostAsync(options.HttpPrefix, PrintJobRequest(options, pdf))));

        Assert.All(responses, r => Assert.Equal(IppStatus.SuccessfulOk, r.Status));
        Assert.Equal(40, stored.Distinct().Count());
        await server.StopAsync();
    }

    private static IppMessage CancelJobRequest(IppPrinterOptions options, int jobId)
    {
        var cancel = NewRequest(IppOperation.CancelJob, options.PrinterUri);
        cancel.FirstGroup(IppTag.OperationAttributes)!.Add(new IppAttribute("job-id", IppTag.Integer, jobId));
        return cancel;
    }

    [Fact]
    public async Task CancelJob_EndsAnOpenJob_AsTheClientAskedFor()
    {
        var options = new IppPrinterOptions { Port = FreePort() };
        using var server = new IppPrinterServer(options);
        server.Start();

        int jobId = await CreateJobAsync(options);
        var cancelled = await PostAsync(options.HttpPrefix, CancelJobRequest(options, jobId));

        Assert.Equal(IppStatus.SuccessfulOk, cancelled.Status);
        Assert.Equal((int)IppJobState.Canceled, cancelled.FirstGroup(IppTag.JobAttributes)!.Find("job-state")!.AsInt());
        Assert.Equal(IppStatus.ClientErrorNotFound,
            (await PostAsync(options.HttpPrefix, SendDocument(options, jobId, new byte[10], last: true))).Status);
        Assert.Equal(IppStatus.ClientErrorNotFound,
            (await PostAsync(options.HttpPrefix, CancelJobRequest(options, jobId))).Status);
        await server.StopAsync();
    }

    [Fact]
    public async Task OpenJobs_AreCountedExactly_EvenWhenCreatedAllAtOnce()
    {
        var options = new IppPrinterOptions { Port = FreePort(), MaxPendingJobs = 5 };
        using var server = new IppPrinterServer(options);
        server.Start();

        var responses = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(_ => PostAsync(options.HttpPrefix, NewRequest(IppOperation.CreateJob, options.PrinterUri))));

        Assert.Equal(5, responses.Count(r => r.Status == IppStatus.SuccessfulOk));
        Assert.Equal(35, responses.Count(r => r.Status == IppStatus.ServerErrorBusy));
        await server.StopAsync();
    }

    [Fact]
    public async Task AnAbandonedJob_IsDropped_WithoutAnyNewTraffic()
    {
        var options = new IppPrinterOptions
        {
            Port = FreePort(),
            PendingJobTimeout = TimeSpan.FromMilliseconds(200),
            AbandonedJobSweepInterval = TimeSpan.FromMilliseconds(100),
        };
        using var server = new IppPrinterServer(options);
        server.Start();

        await CreateJobAsync(options);
        await Task.Delay(800);

        // Get-Printer-Attributes does not tidy up itself, it only reports.
        var attributes = await PostAsync(options.HttpPrefix, NewRequest(IppOperation.GetPrinterAttributes, options.PrinterUri));
        Assert.Equal(0, attributes.FirstGroup(IppTag.PrinterAttributes)!.Find("queued-job-count")!.AsInt());
        await server.StopAsync();
    }

    [Fact]
    public async Task AClientThatStopsSending_DoesNotKeepThePrinterBusy()
    {
        var options = new IppPrinterOptions
        {
            Port = FreePort(),
            MaxConcurrentRequests = 1,
            RequestTimeout = TimeSpan.FromSeconds(1),
        };
        using var server = new IppPrinterServer(options);
        server.Start();

        // Takes the only slot: promises a body and sends a sliver of it.
        using var stalled = new TcpClient();
        await stalled.ConnectAsync(IPAddress.Loopback, options.Port);
        var stream = stalled.GetStream();
        byte[] head = System.Text.Encoding.ASCII.GetBytes(
            $"POST /{options.ResourcePath} HTTP/1.1\r\nHost: localhost:{options.Port}\r\n" +
            "Content-Type: application/ipp\r\nContent-Length: 100000\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(new byte[16]);
        await stream.FlushAsync();
        await Task.Delay(200);

        var answer = await PostAsync(options.HttpPrefix, NewRequest(IppOperation.GetPrinterAttributes, options.PrinterUri))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(IppStatus.SuccessfulOk, answer.Status);
        await server.StopAsync();
    }

    [Fact]
    public async Task Stopping_LetsAJobThatIsBeingStoredFinish()
    {
        var options = new IppPrinterOptions { Port = FreePort() };
        using var storing = new ManualResetEventSlim(false);
        bool stored = false;
        using var server = new IppPrinterServer(options)
        {
            StoreJob = _ =>
            {
                storing.Set();
                Thread.Sleep(1000); // a slow disk
                stored = true;
                return true;
            },
        };
        server.Start();

        var printing = PostAsync(options.HttpPrefix, PrintJobRequest(options, TestPdfs.WithPages((595, 842))));
        Assert.True(storing.Wait(TimeSpan.FromSeconds(10)));

        await server.StopAsync();

        Assert.True(stored);
        var response = await printing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(IppStatus.SuccessfulOk, response.Status);
    }
}
