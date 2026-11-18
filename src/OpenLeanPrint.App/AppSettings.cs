using System.IO;
using System.Text.Json;
using OpenLeanPrint.Core;
using Path = System.IO.Path;

namespace OpenLeanPrint.App;

/// <summary>A named layout you can come back to — "4-up draft", "booklet".</summary>
public sealed record LayoutProfile
{
    public string Name { get; init; } = string.Empty;
    public int Rows { get; init; } = 2;
    public int Columns { get; init; } = 2;
    public bool Booklet { get; init; }
    public string Paper { get; init; } = "A4";
    public double MarginMm { get; init; } = 8;

    /// <summary>Gutter in points, like the rest of the geometry. The app shows millimetres.</summary>
    public double Gutter { get; init; } = 6;
    public bool PageBorders { get; init; }
    public string? Watermark { get; init; }
    public string Duplex { get; init; } = "Default";
}

/// <summary>
/// What the app remembers between runs: the layout you last used, the printer
/// you picked, whether it was collecting captured jobs, and your saved layouts.
/// </summary>
public sealed record AppSettings
{
    public int Rows { get; init; } = 2;
    public int Columns { get; init; } = 2;
    public bool Booklet { get; init; }
    public string Paper { get; init; } = "A4";
    public double MarginMm { get; init; } = 8;

    /// <summary>Gutter in points, like the rest of the geometry. The app shows millimetres.</summary>
    public double Gutter { get; init; } = 6;
    public bool PageBorders { get; init; }
    public string? Printer { get; init; }
    public string Duplex { get; init; } = "Default";
    public string? Watermark { get; init; }
    /// <summary>
    /// On by default. The app exists to receive what you print into the virtual
    /// printer; a pool that stays empty after printing is indistinguishable from
    /// a broken printer.
    /// </summary>
    public bool CollectCapturedJobs { get; init; } = true;

    /// <summary>Bring the window up when a job arrives, the way a print dialog would.</summary>
    public bool ShowOnCapture { get; init; } = true;

    /// <summary>System, Light or Dark. Stored as text so the file stays readable.</summary>
    public string Theme { get; init; } = nameof(ThemePreference.System);

    /// <summary>System, English, German, French or Spanish. Text for the same reason.</summary>
    public string Language { get; init; } = nameof(LanguagePreference.System);

    /// <summary>Open the window maximized, at start and when it comes back from the tray.</summary>
    public bool OpenMaximized { get; init; }

    /// <summary>Ask GitHub for a newer release each time the app starts.</summary>
    public bool CheckUpdatesOnStart { get; init; } = true;
    /// <summary>
    /// Announce with a balloon that closing the window only hid it. On by
    /// default, because the first time it is the only explanation of where the
    /// window went; after "Print &amp; Close" a dozen times a day it is noise.
    /// </summary>
    public bool NoticeOnHide { get; init; } = true;


    /// <summary>Whether the app has already offered to create the printer.</summary>
    public bool PrinterSetupOffered { get; init; }
    public List<LayoutProfile> Profiles { get; init; } = new();

    /// <summary><c>%APPDATA%\OpenLeanPrint\settings.json</c>.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                                  Environment.SpecialFolderOption.Create),
        "OpenLeanPrint", "settings.json");

    /// <summary>The previous settings, kept by every save; read when the current file is unreadable.</summary>
    private static string BackupPath => FilePath + ".bak";

    public static AppSettings Load()
    {
        foreach (string path in new[] { FilePath, BackupPath })
        {
            try
            {
                if (File.Exists(path) &&
                    JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) is { } settings)
                {
                    if (path == BackupPath) App.Log("settings.json was unreadable; used the backup");
                    return settings;
                }
            }
            catch (Exception)
            {
                // Corrupt - try the backup, then the defaults. Starting is more
                // important than any one preference.
            }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            // The file being replaced becomes the backup - but only if it can be
            // read, so a corrupt file never pushes out the last good copy.
            if (File.Exists(FilePath))
            {
                try
                {
                    JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                    File.Copy(FilePath, BackupPath, overwrite: true);
                }
                catch (Exception) { /* keep the backup we have */ }
            }
            AtomicFile.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));
        }
        catch (Exception)
        {
            // Losing preferences is annoying; crashing on exit is worse.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
