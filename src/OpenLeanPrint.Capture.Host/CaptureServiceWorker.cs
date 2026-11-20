using System.Globalization;
using Microsoft.Extensions.Hosting;
using OpenLeanPrint.Capture;
using OpenLeanPrint.Capture.Server;

namespace OpenLeanPrint.Capture.Host;

/// <summary>
/// The capture service: holds the loopback IPP listener for as long as the
/// machine is running, so the virtual printer works without anybody being
/// logged in and without the desktop app running.
/// <para>
/// It keeps a plain text log next to the captured jobs. A service that fails
/// silently is impossible to diagnose, and this project has already paid for
/// that lesson once.
/// </para>
/// </summary>
internal sealed class CaptureServiceWorker : BackgroundService
{
    private readonly CaptureSettings _settings;
    private IppPrinterServer? _server;

    public CaptureServiceWorker(CaptureSettings settings) => _settings = settings;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        // Which build is running should never again be a matter of comparing
        // file hashes by hand.
        string build = System.Diagnostics.FileVersionInfo
            .GetVersionInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).FileVersion ?? "unknown";
        Log($"Starting build {build} on port {_settings.Port}, writing to {_settings.OutputFolder}");

        // Every user's jobs go into a folder only they can open; the folder
        // above is closed to ordinary users, so nobody can see - or set up in
        // advance - anyone else's.
        if (_settings.PerUserFolders && OperatingSystem.IsWindows())
        {
            Log(UserCaptureFolders.SecureRoot(_settings.OutputFolder)
                ? "Each user's jobs are kept in a folder only they can open."
                : "Could not restrict access to the capture folder.");
        }

        Prune();

        try
        {
            var server = new IppPrinterServer(new IppPrinterOptions
            {
                PrinterName = _settings.PrinterName,
                Port = _settings.Port,
            });

            // Stored before the print queue hears anything: if the job cannot be
            // kept, Windows is told it failed instead of that it was printed.
            server.StoreJob = job =>
            {
                try
                {
                    string path = CapturedJobWriter.Save(job, FolderFor(job));
                    Log($"Captured job #{job.JobId} from {job.UserName ?? "(unknown user)"}, " +
                        $"{job.Data.Length:N0} bytes, sides={job.Sides ?? "(unset)"}, " +
                        $"colour={job.ColorMode ?? "(unset)"} -> {Path.GetFileName(path)}");
                }
                catch (Exception ex)
                {
                    Log($"Could not save job #{job.JobId}, reported it as failed: {ex.Message}");
                    return false;
                }

                try { Prune(); } catch (Exception) { /* tidying up is not the job */ }
                return true;
            };
            server.RequestLog += (_, line) =>
            {
                // Only what went wrong; every Get-Printer-Attributes would drown the log.
                if (line.Contains("dropped", StringComparison.Ordinal) ||
                    line.Contains("not stored", StringComparison.Ordinal) ||
                    line.Contains("refused", StringComparison.Ordinal))
                    Log(line);
            };

            server.Start();
            _server = server;
            Log("Listening.");
        }
        catch (Exception ex)
        {
            // Most likely something else already owns the port.
            Log($"Could not start: {ex.Message}");
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Where a job goes: the folder of the user who printed it. A job that
    /// names nobody this machine knows has no such folder and is refused -
    /// put where everyone can read it, it would undo the point of the folders.
    /// </summary>
    private string FolderFor(CapturedJob job)
    {
        if (!_settings.PerUserFolders || !OperatingSystem.IsWindows()) return _settings.OutputFolder;

        var user = UserCaptureFolders.Resolve(job.UserName)
            ?? throw new InvalidOperationException(
                $"the job names no user of this machine ({job.UserName ?? "no name at all"})");
        return UserCaptureFolders.Prepare(_settings.OutputFolder, user);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            Log("Stopping.");
            try { await _server.StopAsync(); } catch (Exception) { /* already gone */ }
            _server.Dispose();
            _server = null;
        }
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Keeps the folder from growing without bound. Captured jobs are a
    /// hand-over to the app, and nothing removed them before - so the folder
    /// grew with every page anyone printed, somewhere nobody looks.
    /// </summary>
    private void Prune()
    {
        // The top folder too: jobs from before there were per-user folders.
        var folders = new List<string> { _settings.OutputFolder };
        if (_settings.PerUserFolders && OperatingSystem.IsWindows())
            folders.AddRange(UserCaptureFolders.All(_settings.OutputFolder));

        foreach (string folder in folders)
        {
            var removed = CapturedFolder.Prune(folder, _settings.MaxAge, _settings.MaxBytes);
            if (removed.RemovedAnything)
                Log($"Removed {removed.Files} captured job(s), {removed.Bytes / (1024.0 * 1024.0):N1} MB, " +
                    $"from {Path.GetFileName(folder)}.");
        }
    }

    private void Log(string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settings.LogPath)!);
            File.AppendAllText(_settings.LogPath, line);
        }
        catch (Exception)
        {
            // Logging must never be the reason the service fails.
        }
    }
}
