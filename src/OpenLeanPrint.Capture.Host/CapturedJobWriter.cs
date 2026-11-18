namespace OpenLeanPrint.Capture.Host;

/// <summary>Writes a captured job to disk, shared by the console host and the service.</summary>
internal static class CapturedJobWriter
{
    /// <summary>
    /// Saves the job and returns the path. Job ids restart when the host does,
    /// so an existing file is never overwritten - that would silently destroy a
    /// document somebody printed. The file appears complete or not at all, so a
    /// watcher never picks up half a job.
    /// </summary>
    public static string Save(CapturedJob job, string folder) =>
        OpenLeanPrint.Core.AtomicFile.CreateUnique(folder, $"job-{job.JobId:D4}", job.IsPdf ? "pdf" : "bin", job.Data);
}
