using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Path = System.IO.Path;

namespace OpenLeanPrint.App;

/// <summary>A release newer than the running copy.</summary>
/// <param name="Installer">The .msi for this machine's architecture; null when the release has none.</param>
/// <param name="Sha256">The installer's digest as GitHub reports it, if it does.</param>
internal sealed record UpdateInfo(Version Version, Uri Page, Uri? Installer, string? InstallerName, long Size, string? Sha256);

/// <summary>
/// Looks for a newer release of this fork on GitHub and installs it.
/// <para>
/// One request per check, to GitHub's public API: it carries the app's name and
/// version in the User-Agent - GitHub refuses requests without one - and
/// nothing else. No account, no identifier, nothing about the machine or what
/// was printed. PRIVACY.md says the same.
/// </para>
/// </summary>
internal static class Updates
{
    public const string Repository = "MartinKammler/Open-LeanPrint";

    private static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>The running version, as major.minor.patch.</summary>
    /// <remarks>Declared before <see cref="Http"/> on purpose: static fields start in
    /// the order they are written, and the client puts this in its User-Agent.</remarks>
    public static Version Current { get; } = Trim(typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The installer name this machine wants, e.g. <c>-win-x64.msi</c>.</summary>
    private static string InstallerSuffix =>
        $"-win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}.msi";

    /// <summary>
    /// The newest release if it is newer than this copy; null when this copy is
    /// current or nothing has been released yet. Network trouble throws.
    /// </summary>
    public static async Task<UpdateInfo?> FindNewerAsync(CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestRelease);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, cancel);

        // "latest" skips drafts and pre-releases; a repository without a
        // release answers 404, which is not an error from where we stand.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancel), cancellationToken: cancel);
        var release = json.RootElement;

        string tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var version)) return null;
        version = Trim(version);
        if (version <= Current) return null;

        var page = new Uri(release.GetProperty("html_url").GetString()!);
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            return new UpdateInfo(version, page,
                new Uri(asset.GetProperty("browser_download_url").GetString()!), name,
                asset.GetProperty("size").GetInt64(),
                digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
        }
        return new UpdateInfo(version, page, null, null, 0, null);
    }

    /// <summary>
    /// Downloads the installer into a folder of its own under %TEMP% and checks
    /// it against what the release says it is: the size always, the SHA-256 when
    /// GitHub provides one. A file that does not match is deleted, not run.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<int> percent, CancellationToken cancel = default)
    {
        if (update.Installer is null || update.InstallerName is null)
            throw new InvalidOperationException("The release has no installer for this machine.");

        string folder = Path.Combine(Path.GetTempPath(), "OpenLeanPrint-update");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, Path.GetFileName(update.InstallerName));

        using (var response = await Http.GetAsync(update.Installer, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.Size;

            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var file = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read, last = -1;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                int now = total > 0 ? (int)(done * 100 / total) : 0;
                if (now != last) percent.Report(last = now);
            }
        }

        bool sizeOk = update.Size <= 0 || new FileInfo(target).Length == update.Size;
        bool hashOk = true;
        if (update.Sha256 is not null)
        {
            await using var stream = File.OpenRead(target);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
            hashOk = string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        if (!sizeOk || !hashOk)
        {
            File.Delete(target);
            throw new InvalidDataException(Loc.Get("Update.Mismatch"));
        }
        return target;
    }

    /// <summary>
    /// Starts the installer elevated. Elevated from the start matters: the
    /// installer's printer and service steps run as the user, and only get the
    /// user's administrator token when msiexec itself was started with it.
    /// Started plainly, they fail quietly and leave no printer behind.
    /// </summary>
    /// <returns>False when the user declined the elevation prompt.</returns>
    public static bool StartInstaller(string msi)
    {
        try
        {
            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /qb")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    public static void OpenPage(Uri page) =>
        Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Open-LeanPrint/{Current}");
        return client;
    }
}
