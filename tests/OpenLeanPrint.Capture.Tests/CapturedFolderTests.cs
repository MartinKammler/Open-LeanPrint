using OpenLeanPrint.Capture;
using Xunit;

namespace OpenLeanPrint.Capture.Tests;

public class CapturedFolderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "olp-prune-" + Guid.NewGuid().ToString("N"));

    public CapturedFolderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string Job(string name, int bytes, TimeSpan age)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void OldJobs_AreRemoved_AndRecentOnesKept()
    {
        Job("job-0001.pdf", 10, TimeSpan.FromDays(30));
        Job("job-0002.pdf", 10, TimeSpan.FromHours(1));

        var removed = CapturedFolder.Prune(_folder, TimeSpan.FromDays(7), maxBytes: 0);

        Assert.Equal(1, removed.Files);
        Assert.False(File.Exists(Path.Combine(_folder, "job-0001.pdf")));
        Assert.True(File.Exists(Path.Combine(_folder, "job-0002.pdf")));
    }

    [Fact]
    public void OverTheSizeLimit_TheOldestGoFirst()
    {
        Job("job-0001.pdf", 400, TimeSpan.FromHours(3));
        Job("job-0002.pdf", 400, TimeSpan.FromHours(2));
        Job("job-0003.pdf", 400, TimeSpan.FromHours(1));

        // Room for one and a bit: the two oldest have to go.
        var removed = CapturedFolder.Prune(_folder, TimeSpan.Zero, maxBytes: 500, sizeGrace: TimeSpan.Zero);

        Assert.Equal(2, removed.Files);
        Assert.Equal(800, removed.Bytes);
        Assert.True(File.Exists(Path.Combine(_folder, "job-0003.pdf")));
    }

    [Fact]
    public void AJobLargerThanTheLimit_IsNotDeletedRightAfterItArrived()
    {
        // The service prunes straight after storing a job. A job that alone
        // exceeds the limit used to be deleted by that pass - after Windows had
        // been told it was printed, before the app could collect it.
        string fresh = Job("job-0001.pdf", 600, TimeSpan.FromSeconds(1));

        var removed = CapturedFolder.Prune(_folder, CapturedFolder.DefaultMaxAge, maxBytes: 500);

        Assert.False(removed.RemovedAnything);
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void AFullFolder_MakesRoomFromStaleJobs_NotFromUncollectedFreshOnes()
    {
        Job("job-0001.pdf", 400, TimeSpan.FromDays(2));   // stale: may go
        Job("job-0002.pdf", 400, TimeSpan.FromHours(2));  // waiting to be collected
        Job("job-0003.pdf", 400, TimeSpan.FromMinutes(1));

        var removed = CapturedFolder.Prune(_folder, CapturedFolder.DefaultMaxAge, maxBytes: 500);

        Assert.Equal(1, removed.Files);
        Assert.False(File.Exists(Path.Combine(_folder, "job-0001.pdf")));
        Assert.True(File.Exists(Path.Combine(_folder, "job-0002.pdf")));
        Assert.True(File.Exists(Path.Combine(_folder, "job-0003.pdf")));
    }

    [Fact]
    public void AnythingThatIsNotACapturedJob_IsLeftAlone()
    {
        // The log lives here too, and so might whatever a user has put in.
        string log = Path.Combine(_folder, "service.log");
        File.WriteAllText(log, "old");
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow - TimeSpan.FromDays(365));
        string mine = Job("holiday-photos.pdf", 10, TimeSpan.FromDays(365));

        var removed = CapturedFolder.Prune(_folder, TimeSpan.FromDays(7), CapturedFolder.DefaultMaxBytes);

        Assert.False(removed.RemovedAnything);
        Assert.True(File.Exists(log));
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void NoLimits_MeansNothingIsRemoved()
    {
        Job("job-0001.pdf", 10, TimeSpan.FromDays(3650));

        Assert.False(CapturedFolder.Prune(_folder, TimeSpan.Zero, maxBytes: 0).RemovedAnything);
    }

    [Fact]
    public void AMissingFolder_IsNotAnError()
    {
        Assert.False(CapturedFolder.Prune(Path.Combine(_folder, "nope")).RemovedAnything);
    }

    [Fact]
    public void OnlyFilesInACaptureFolder_CountAsOurs()
    {
        // The guard before deleting anything: a PDF the user dragged in from
        // their own documents must never be treated as a captured hand-over.
        Assert.True(CapturedFolder.Holds(Path.Combine(CaptureLocations.DefaultFolder, "job-0001.pdf")));
        Assert.True(CapturedFolder.Holds(Path.Combine(CaptureLocations.SharedFolder, "job-0001.pdf")));

        Assert.False(CapturedFolder.Holds(Path.Combine(_folder, "job-0001.pdf")));
        Assert.False(CapturedFolder.Holds(Path.Combine(CaptureLocations.DefaultFolder, "sub", "job-0001.pdf")));
        Assert.False(CapturedFolder.Holds(""));
    }
}
