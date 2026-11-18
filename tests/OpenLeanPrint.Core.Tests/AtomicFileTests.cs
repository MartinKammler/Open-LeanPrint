using OpenLeanPrint.Core;
using Xunit;

namespace OpenLeanPrint.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "olp-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public void WriteAllBytes_ReplacesAnExistingFile_AndLeavesNoTemporaryBehind()
    {
        string path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllBytes(path, "new"u8.ToArray());

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void CreateUnique_NeverOverwrites_ItTakesTheNextName()
    {
        string first = AtomicFile.CreateUnique(_folder, "job-0001", "pdf", new byte[] { 1 });
        string second = AtomicFile.CreateUnique(_folder, "job-0001", "pdf", new byte[] { 2 });

        Assert.Equal("job-0001.pdf", Path.GetFileName(first));
        Assert.Equal("job-0001-1.pdf", Path.GetFileName(second));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(first));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(second));
    }

    [Fact]
    public async Task CreateUnique_ManyWritersAtOnce_EachGetAFileOfTheirOwn()
    {
        var writes = Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            AtomicFile.CreateUnique(_folder, "job-0001", "pdf", BitConverter.GetBytes(i))));
        string[] paths = await Task.WhenAll(writes);

        Assert.Equal(40, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var contents = paths.Select(p => BitConverter.ToInt32(File.ReadAllBytes(p))).OrderBy(v => v);
        Assert.Equal(Enumerable.Range(0, 40), contents);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }
}
