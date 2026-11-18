namespace OpenLeanPrint.Core;

/// <summary>
/// Writes files so that nobody ever sees half of one.
/// <para>
/// Writing straight to the final name leaves a truncated file behind when the
/// process dies or the disk fills up halfway - and a folder watcher can pick
/// the file up while it is still growing. Here the bytes go to a temporary
/// file next to the target, are flushed to disk, and only then appear under
/// their real name in one rename. The temporary name ends in <c>.tmp</c>, so a
/// watcher filtering on <c>*.pdf</c> never sees it at all.
/// </para>
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="data"/> to <paramref name="path"/>, replacing a
    /// file that is already there only once the new one is complete.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);

        string temp = WriteTemp(path, data);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Writes <paramref name="data"/> under a name in <paramref name="folder"/>
    /// that nothing else has: <c>stem.extension</c>, or <c>stem-1.extension</c>,
    /// <c>stem-2.extension</c> … if that is taken. The rename refuses to replace
    /// an existing file, so two writers racing for the same name cannot
    /// overwrite each other - the loser simply takes the next one.
    /// </summary>
    /// <returns>The path the file was written to.</returns>
    public static string CreateUnique(string folder, string stem, string extension, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);
        ArgumentNullException.ThrowIfNull(data);

        Directory.CreateDirectory(folder);
        string temp = WriteTemp(Path.Combine(folder, $"{stem}.{extension}"), data);
        try
        {
            for (int suffix = 0; suffix < 10_000; suffix++)
            {
                string name = suffix == 0 ? $"{stem}.{extension}" : $"{stem}-{suffix}.{extension}";
                string path = Path.Combine(folder, name);
                try
                {
                    File.Move(temp, path, overwrite: false);
                    return path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Taken - by an earlier job or by a writer that just won the race.
                }
            }
            throw new IOException($"No free name for {stem}.{extension} in {folder}.");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static string WriteTemp(string target, byte[] data)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(target))!;
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(data);
            // To the disk, not just the cache: a rename that survives a power cut
            // must not point at bytes that did not.
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return temp;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
