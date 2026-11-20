using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenLeanPrint.Capture;
using OpenLeanPrint.Compose;
using OpenLeanPrint.Core;
using OpenLeanPrint.Core.Imposition;
using OpenLeanPrint.Print;
// WPF implicitly imports System.Windows.Shapes, whose Path would clash with
// System.IO.Path - the alias keeps the file-system one unambiguous.
using Path = System.IO.Path;

namespace OpenLeanPrint.App;

/// <summary>
/// The whole app in one window: a job pool on the left, the imposed sheet in
/// the middle, and the actions that let it leave the app along the bottom.
/// Every change re-imposes in the background and repaints the preview, so what
/// you see is what the printer gets.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Preview resolution. Enough to read the layout, cheap to render.</summary>
    private const int PreviewDpi = 120;

    /// <summary>The zoom steps Ctrl+wheel moves through; 1 is "the sheet fits the window".</summary>
    private static readonly double[] ZoomSteps = { 1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6 };

    /// <summary>
    /// No sharper than this, however far it is zoomed: a 6x A3 sheet at screen
    /// resolution is a few hundred megapixels, and PDFium would render every one.
    /// </summary>
    private const int MaxPreviewDpi = 400;

    private double _zoom = 1;
    private int _renderedDpi;
    private int _renderGeneration;
    private Point? _panFrom;
    private readonly DispatcherTimer _sharpen = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Resolution the printer path rasterises at.</summary>
    private const int PrintDpi = 200;

    private static readonly string[] Papers = { "A4", "A5", "A3", "A6", "Letter", "Legal", "Tabloid" };

    /// <summary>One entry in the "Sides" box; ToString is what the box shows, in the language in force.</summary>
    private sealed record DuplexChoice(string Key, DuplexMode Mode)
    {
        public override string ToString() => Loc.Get(Key);
    }

    private static readonly DuplexChoice[] DuplexChoices =
    {
        new("Duplex.Default", DuplexMode.Default),
        new("Duplex.Simplex", DuplexMode.Simplex),
        new("Duplex.Long", DuplexMode.LongEdge),
        new("Duplex.Short", DuplexMode.ShortEdge),
    };

    private readonly ObservableCollection<JobItem> _jobs = new();
    private readonly ObservableCollection<LayoutProfile> _profiles = new();
    private readonly DispatcherTimer _debounce;

    private byte[]? _imposed;
    private int _sheetIndex;
    private int _sheetCount;
    private CancellationTokenSource? _work;
    private readonly List<CapturedFolderWatcher> _capture = new();
    private readonly CaptureService _service = new();
    private bool _suppressPagesEdit;
    private bool _suppressGridEdit;
    private bool _printerSetupOffered;

    private bool _showOnCapture = true;
    private bool _noticeOnHide = true;
    private bool _openMaximized;
    private bool _checkUpdatesOnStart = true;
    private UpdateInfo? _update;
    private bool _updating;

    /// <summary>
    /// Jobs that were already waiting when collecting started. They go into the
    /// pool but must not raise the window: at login that would mean a window in
    /// your face for something you printed yesterday.
    /// </summary>
    private readonly HashSet<string> _backlog = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Jobs left in the folder because one start found more than it is willing
    /// to take at once. They are neither loaded nor deleted; the next start
    /// picks them up.
    /// </summary>
    private readonly HashSet<string> _tooMany = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The layout behind the picture, so a click can be traced back to a page.</summary>
    private ImpositionResult? _layout;
    private readonly TrayPresence _tray;
    private bool _exiting;

    private int _rows = 2;
    private int _columns = 2;
    private bool _booklet;

    /// <summary>
    /// The gutter is kept in points, like the rest of the geometry, but shown in
    /// millimetres: having the margin in mm and the gutter in points next to it
    /// invited reading "1" as a millimetre and getting a third of one.
    /// </summary>
    private const double PointsPerMm = 72.0 / 25.4;

    private static double PointsFromMm(double mm) => mm * PointsPerMm;

    private static double MmFromPoints(double points) => Math.Round(points / PointsPerMm, 1);

    public MainWindow()
    {
        InitializeComponent();

        // Which version is running should be a glance, not a trip to the
        // Windows settings - the update dialog says "you have 0.5.4", and the
        // window should say the same.
        Title = $"Open-LeanPrint {Updates.Current}";

        JobList.ItemsSource = _jobs;
        PaperBox.ItemsSource = Papers;
        PaperBox.SelectedIndex = 0;

        PrinterBox.ItemsSource = PdfPrinter.InstalledPrinters();
        PrinterBox.SelectedItem = PdfPrinter.DefaultPrinter();
        DuplexBox.ItemsSource = DuplexChoices;
        DuplexBox.SelectedIndex = 0;

        ProfileBox.ItemsSource = _profiles;
        ProfileBox.DisplayMemberPath = nameof(LayoutProfile.Name);

        // Typing in a number box should not re-impose on every keystroke.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RefreshAsync();
        };

        // Zooming enlarges the picture at once and renders it sharp once the
        // wheel has rested - rendering on every notch would stutter.
        _sharpen.Tick += (_, _) =>
        {
            _sharpen.Stop();
            if (WantedDpi() > _renderedDpi * 1.15) _ = ShowSheetAsync(CancellationToken.None);
        };
        PreviewKeyDown += Window_PreviewKeyDown;

        _tray = new TrayPresence();
        _tray.ShowRequested += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        _tray.ExitRequested += (_, _) => Dispatcher.Invoke(ExitForGood);
        _tray.CollectingChanged += (_, collecting) => Dispatcher.Invoke(() => SetCollecting(collecting));
        _tray.ShowOnCaptureChanged += (_, show) => Dispatcher.Invoke(() => SetShowOnCapture(show));
        _tray.ThemeChosen += (_, preference) => Dispatcher.Invoke(() => SetTheme(preference));
        _tray.LanguageChosen += (_, preference) => Dispatcher.Invoke(() => SetLanguage(preference));
        Loc.Changed += (_, _) => RefreshTexts();
        _tray.OpenMaximizedChanged += (_, maximized) => Dispatcher.Invoke(() => SetOpenMaximized(maximized));
        _tray.CheckOnStartChanged += (_, check) => Dispatcher.Invoke(() => SetCheckUpdatesOnStart(check));
        _tray.UpdateRequested += (_, _) => Dispatcher.Invoke(() => _ = UpdateRequestedAsync());

        ThemeManager.Track(this);
        // Covers Windows switching on its own while "Same as Windows" is chosen.
        ThemeManager.Changed += (_, _) => _tray.SetTheme(ThemeManager.Preference, ThemeManager.IsDark);
        _tray.NoticeOnHideChanged += (_, notice) => Dispatcher.Invoke(() => SetNoticeOnHide(notice));

        var settings = AppSettings.Load();
        ApplySettings(settings);
        UpdatePrinterSetupButton();
        UpdateControls();

        // Ask once, after the window is up - a dialog during construction would
        // appear before anything is on screen.
        Loaded += (_, _) => OfferPrinterSetup(settings);

        // Said out loud rather than left to be noticed: nothing here needs
        // administrator rights, and a window with a file dialog that has them is
        // a way to reach the whole machine as administrator. It happened once,
        // because the installer started the app and handed it its own token.
        if (App.RunningElevated)
            Loaded += (_, _) => StatusText.Text =
                Loc.Get("Status.Elevated");

        // "OpenLeanPrint a.pdf b.pdf" (or "Open with…") starts with a filled pool.
        var startupFiles = Environment.GetCommandLineArgs().Skip(1)
            .Where(path => File.Exists(path)).ToList();
        if (startupFiles.Count > 0) Loaded += (_, _) => LoadFiles(startupFiles);

        // Not on Loaded: started into the tray, the window is never shown and
        // Loaded never comes. Idle priority keeps the network out of the way of
        // the first paint.
        if (_checkUpdatesOnStart)
            Dispatcher.InvokeAsync(() => _ = CheckForUpdatesAsync(manual: false), DispatcherPriority.ApplicationIdle);
    }

    // ---------- settings ----------

    private void ApplySettings(AppSettings settings)
    {
        _printerSetupOffered = settings.PrinterSetupOffered;
        SetShowOnCapture(settings.ShowOnCapture);
        SetTheme(ThemeManager.TryParse(settings.Theme, out var theme) ? theme : ThemePreference.System);
        SetLanguage(Loc.TryParse(settings.Language, out var language) ? language : LanguagePreference.System);
        SetOpenMaximized(settings.OpenMaximized);
        SetCheckUpdatesOnStart(settings.CheckUpdatesOnStart);
        SetNoticeOnHide(settings.NoticeOnHide);
        _booklet = settings.Booklet;
        _rows = Math.Max(1, settings.Rows);
        _columns = Math.Max(1, settings.Columns);

        if (Papers.Contains(settings.Paper)) PaperBox.SelectedItem = settings.Paper;
        MarginBox.Text = settings.MarginMm.ToString(CultureInfo.CurrentCulture);
        GutterBox.Text = MmFromPoints(settings.Gutter).ToString(CultureInfo.CurrentCulture);
        BorderToggle.IsChecked = settings.PageBorders;

        if (settings.Printer is not null && PrinterBox.Items.Contains(settings.Printer))
            PrinterBox.SelectedItem = settings.Printer;

        WatermarkBox.Text = settings.Watermark ?? string.Empty;

        _profiles.Clear();
        foreach (var profile in settings.Profiles) _profiles.Add(profile);

        if (DuplexModes.TryParse(settings.Duplex, out var duplex))
            DuplexBox.SelectedItem = DuplexChoices.FirstOrDefault(choice => choice.Mode == duplex) ?? DuplexChoices[0];

        CheckMatchingPreset();

        // Restoring this means the app picks up where it left off: still collecting.
        if (settings.CollectCapturedJobs) SetCollecting(true);
    }

    private AppSettings CurrentSettings() => new()
    {
        Rows = _rows,
        Columns = _columns,
        Booklet = _booklet,
        Paper = (string)PaperBox.SelectedItem,
        MarginMm = ParseNumber(MarginBox.Text, 0),
        Gutter = GutterPoints(),
        PageBorders = BorderToggle.IsChecked == true,
        Printer = PrinterBox.SelectedItem as string,
        Duplex = SelectedDuplex().ToString(),
        PrinterSetupOffered = _printerSetupOffered,
        Watermark = string.IsNullOrWhiteSpace(WatermarkBox.Text) ? null : WatermarkBox.Text.Trim(),
        Profiles = _profiles.ToList(),
        CollectCapturedJobs = CollectToggle.IsChecked == true,
        ShowOnCapture = _showOnCapture,
        Theme = ThemeManager.Preference.ToString(),
        Language = Loc.Preference.ToString(),
        OpenMaximized = _openMaximized,
        CheckUpdatesOnStart = _checkUpdatesOnStart,
        NoticeOnHide = _noticeOnHide,
    };

    private DuplexMode SelectedDuplex() =>
        DuplexBox.SelectedItem is DuplexChoice choice ? choice.Mode : DuplexMode.Default;

    /// <summary>
    /// Closing the window while collecting only hides it — the whole point of
    /// collecting is that jobs keep arriving. "Exit" in the tray menu is how you
    /// really quit.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window ends the session, pool and all. Coming back to a
        // list of documents dealt with days ago is not picking up where you left
        // off - it is a stack of things waiting to be printed a second time by
        // accident. The captured files themselves stay where they are.
        ClearPool();

        if (!_exiting && _capture.Count > 0)
        {
            e.Cancel = true;
            Hide();
            if (_noticeOnHide)
                _tray.Notify(Loc.Get("Balloon.StillCollecting"), Loc.Get("Balloon.StillCollecting.Text"));
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CurrentSettings().Save();
        StopCollecting();
        _service.Dispose();
        _tray.Dispose();
        base.OnClosed(e);

        // ShutdownMode is OnExplicitShutdown so hiding to the tray cannot end the
        // app; that makes this the one place that ends it.
        Application.Current?.Shutdown();
    }

    /// <summary>
    /// Starts hidden with collecting already on - what the login shortcut uses,
    /// so print jobs are caught without a window appearing at every boot.
    /// </summary>
    public void StartInTray()
    {
        SetCollecting(true);
        UpdateControls();
        // Never shown, so nothing to hide: the tray icon is the whole presence.
    }

    /// <summary>
    /// Another copy of the app was started - which means "show me", plus
    /// whatever it was asked to open.
    /// </summary>
    public void BringToFront(IReadOnlyList<string> files)
    {
        if (files.Count > 0) LoadFiles(files);
        RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        if (_openMaximized) WindowState = WindowState.Maximized;
        else if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitForGood()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// Stop for real, from outside - the uninstaller. Going through the normal
    /// exit is the point: it disposes the tray icon, which a terminated process
    /// never gets to do, leaving the icon on screen until someone hovers over it.
    /// </summary>
    public void QuitForGood() => ExitForGood();

    /// <summary>
    /// Lights up the preset button that matches the current layout, if any, and
    /// keeps the grid box showing the same thing. A grid with no preset - 2x3,
    /// say - simply leaves them all unlit.
    /// </summary>
    /// <summary>Lights the matching preset and writes the grid box to match.</summary>
    private void CheckMatchingPreset()
    {
        HighlightPresets();
        ShowGrid();
    }

    /// <summary>
    /// Lights the preset that matches the current layout, without touching the
    /// grid box - which matters while the box is the thing being typed into.
    /// </summary>
    private void HighlightPresets()
    {
        string tag = _booklet ? "booklet" : NUpGrid.Format(_rows, _columns);
        foreach (var preset in Presets)
            preset.IsChecked = (string)preset.Tag == tag;
    }

    private ToggleButton[] Presets => new[] { Preset1Up, Preset2Up, Preset4Up, Preset9Up, PresetBooklet };

    // ---------- collecting captured jobs ----------

    private void Collect_Click(object sender, RoutedEventArgs e) => SetCollecting(CollectToggle.IsChecked == true);

    private void Border_Changed(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    /// <summary>The gutter box in millimetres, as the points the engine works in.</summary>
    private double GutterPoints() => PointsFromMm(ParseNumber(GutterBox.Text, 0));

    /// <summary>Single entry point, so the toolbar toggle and the tray menu cannot drift apart.</summary>
    private void SetCollecting(bool collecting)
    {
        if (collecting) StartCollecting();
        else StopCollecting();

        bool actuallyCollecting = _capture.Count > 0;
        CollectToggle.IsChecked = actuallyCollecting;
        _tray.SetCollecting(actuallyCollecting);
    }

    private void StartCollecting()
    {
        if (_capture.Count > 0) return;

        // Two places, because jobs can arrive from two directions: the Windows
        // service writes to this user's own folder inside the machine-wide one
        // (it runs as LocalSystem and has no per-user profile), while a console
        // host or this app writes to the per-user data folder.
        string[] folders = new[] { CaptureLocations.DefaultFolder, CaptureLocations.MySharedFolder }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        foreach (string folder in folders) CapturedFolder.Prune(folder);

        int skipped = LimitInitialIntake(folders);

        foreach (string folder in folders)
        {
            try
            {
                // Jobs already lying in the folder count too. The capture service
                // runs whether or not this app does, so anything printed while it
                // was closed is waiting there - and passing those over looked
                // exactly like a printer that swallows everything. LastCollectedUtc
                // is what keeps a job from being shown twice.
                //
                // The service's folder is created by the service, with this
                // user's first job; until then the watcher waits for it.
                var watcher = new CapturedFolderWatcher(folder)
                {
                    CreateFolder = !string.Equals(folder, CaptureLocations.MySharedFolder,
                                                  StringComparison.OrdinalIgnoreCase),
                };
                watcher.JobArrived += (_, path) => Dispatcher.Invoke(() => CollectJob(path));
                watcher.HandlingFailed += (_, failure) =>
                    App.Log($"could not take {Path.GetFileName(failure.Path)}: {failure.Error.Message}");
                watcher.Start(includeExisting: true);
                _capture.Add(watcher);
            }
            catch (Exception ex)
            {
                StatusText.Text = Loc.Format("Status.WatchFailed", folder, ex.Message);
            }
        }

        if (_capture.Count == 0) return;

        // Host the loopback IPP service ourselves when nothing else does, so a
        // copy without the Windows service still captures. Jobs land in a
        // watched folder either way.
        try
        {
            _service.Start();
            StatusText.Text = Loc.Format("Status.Listening", _service.Port);
        }
        catch (Exception)
        {
            // Normally the Windows service already owns the port - then it does
            // the listening and the watchers feed the pool.
            StatusText.Text = PrinterSetup.IsCaptureServiceRunning()
                ? Loc.Get("Status.FromService")
                : Loc.Format("Status.PortBusy", CaptureService.DefaultPort);
        }

        if (skipped > 0)
            StatusText.Text += Loc.Format("Status.Skipped", skipped);
    }

    /// <summary>
    /// Caps what a first start pulls in. A machine that has been capturing for
    /// weeks with nobody looking can have a lot waiting, and filling the pool
    /// with months of history helps no one. The rest stay in the folder and are
    /// skipped for now; the number is reported rather than swallowed. Returns how
    /// many were passed over.
    /// </summary>
    private int LimitInitialIntake(IEnumerable<string> folders)
    {
        const int mostToTakeAtOnce = 20;

        _backlog.Clear();
        _tooMany.Clear();
        var waiting = folders
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.pdf"))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        foreach (var file in waiting.Take(mostToTakeAtOnce)) _backlog.Add(file.FullName);
        foreach (var file in waiting.Skip(mostToTakeAtOnce)) _tooMany.Add(file.FullName);
        return _tooMany.Count;
    }

    /// <summary>Single entry point, so the tray menu and this cannot drift apart.</summary>
    private void SetShowOnCapture(bool show)
    {
        _showOnCapture = show;
        _tray.SetShowOnCapture(show);
    }

    /// <summary>
    /// Single entry point, so the tray menu and this cannot drift apart. Turning
    /// it on maximizes now; turning it off leaves the window where it is rather
    /// than shrinking it under the pointer.
    /// </summary>
    private void SetOpenMaximized(bool maximized)
    {
        _openMaximized = maximized;
        _tray.SetOpenMaximized(maximized);
        if (maximized) WindowState = WindowState.Maximized;
    }

    private void SetCheckUpdatesOnStart(bool check)
    {
        _checkUpdatesOnStart = check;
        _tray.SetCheckOnStart(check);
    }

    // ---------- updates ----------

    /// <summary>
    /// A message box that works with the window hidden. Owned by a hidden
    /// window, it would open behind everything or not at all - and these come
    /// from the tray as often as from the window.
    /// </summary>
    private MessageBoxResult Say(string text, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        IsVisible ? MessageBox.Show(this, text, title, buttons, image)
                  : MessageBox.Show(text, title, buttons, image);

    /// <summary>The tray entry: install what was found, or look now.</summary>
    private Task UpdateRequestedAsync() =>
        _update is not null ? OfferUpdateAsync(_update) : CheckForUpdatesAsync(manual: true);

    /// <summary>
    /// Asks GitHub for a newer release. Asked for by hand, every outcome is
    /// reported. At start only a newer version is worth a word: a failed check
    /// - offline, a proxy, GitHub down - goes to the log, because a dialog
    /// about it at every login would be the app's most frequent message.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updating) return;
        try
        {
            var update = await Updates.FindNewerAsync();
            if (update is null)
            {
                if (manual)
                    Say(Loc.Format("Update.UpToDate", Updates.Current), Loc.Get("Update.Title"),
                                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _update = update;
            _tray.SetUpdateAvailable(update.Version.ToString());
            App.Log($"update available: {update.Version}");

            // Hidden in the tray, a dialog would arrive out of nowhere - at login,
            // in front of whatever you are doing. A balloon can be ignored.
            if (manual || IsVisible) await OfferUpdateAsync(update);
            else
                _tray.Notify(Loc.Get("Update.BalloonTitle"), Loc.Format("Update.BalloonText", update.Version),
                             () => Dispatcher.Invoke(() => _ = OfferUpdateAsync(update)));
        }
        catch (Exception ex)
        {
            App.Log($"update check failed: {ex.Message}");
            if (manual)
                Say(Loc.Format("Update.CheckFailed", ex.Message), Loc.Get("Update.Title"),
                                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task OfferUpdateAsync(UpdateInfo update)
    {
        if (_updating) return;

        if (update.Installer is null)
        {
            // A release without an installer for this machine - an arm64 PC and
            // an x64-only release, say. The page is the honest next step.
            if (Say(Loc.Format("Update.NoInstaller", update.Version, Updates.Current),
                                Loc.Get("Update.Title"), MessageBoxButton.YesNo, MessageBoxImage.Information)
                == MessageBoxResult.Yes)
                Updates.OpenPage(update.Page);
            return;
        }

        if (Say(Loc.Format("Update.Available", update.Version, Updates.Current),
                            Loc.Get("Update.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return;

        _updating = true;
        try
        {
            var progress = new Progress<int>(p => StatusText.Text = Loc.Format("Update.Downloading", update.Version, p));
            string msi = await Updates.DownloadAsync(update, progress);

            if (!Updates.StartInstaller(msi))
            {
                StatusText.Text = Loc.Get("Update.Cancelled");
                return;
            }

            // The installer stops this copy anyway and starts the new one when
            // it is done; leaving first keeps its files free to be replaced.
            App.Log($"installing update {update.Version}");
            ExitForGood();
        }
        catch (Exception ex)
        {
            App.Log($"update failed: {ex.Message}");
            Say(Loc.Format("Update.DownloadFailed", ex.Message), Loc.Get("Update.Title"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Single entry point, so the tray menu and this cannot drift apart.</summary>
    private void SetLanguage(LanguagePreference preference)
    {
        Loc.Apply(preference);
        _tray.SetLanguage(preference);
    }

    /// <summary>
    /// Rewrites what code put on screen. XAML follows a change of language on
    /// its own; these are set from code and would otherwise keep the old one
    /// until something happened to touch them. A status message that reported
    /// an event stays as it was written - it is history, not a label.
    /// </summary>
    private void RefreshTexts()
    {
        UpdatePrinterSetupButton();
        DuplexBox.Items.Refresh();
        foreach (var job in _jobs) job.NotifySummaryChanged();
        UpdateControls();
        _tray.Relabel();
    }

    /// <summary>Single entry point, so the tray menu and this cannot drift apart.</summary>
    private void SetTheme(ThemePreference preference)
    {
        ThemeManager.Apply(preference);
        // Changed only fires when the colours change; choosing "Same as
        // Windows" while Windows is light moves the tick and nothing else.
        _tray.SetTheme(ThemeManager.Preference, ThemeManager.IsDark);
    }

    /// <summary>Single entry point, so the tray menu and this cannot drift apart.</summary>
    private void SetNoticeOnHide(bool notice)
    {
        _noticeOnHide = notice;
        _tray.SetNoticeOnHide(notice);
    }

    /// <summary>
    /// Takes one captured file into the pool and makes it visible - a job you
    /// just printed landing in a hidden window is the same experience as nothing
    /// happening at all.
    /// <para>
    /// The file is deleted straight afterwards, because that was all it was for.
    /// A captured job is spool output on its way from the print queue into this
    /// window, not a document: the real one is still open in whatever you printed
    /// from. The pool holds the bytes, so nothing here needs the file again.
    /// </para>
    /// </summary>
    private void CollectJob(string path)
    {
        string full = Path.GetFullPath(path);
        if (_tooMany.Contains(full)) return;   // backlog this start is not taking

        bool wasWaiting = _backlog.Remove(full);

        int before = _jobs.Count;
        LoadFiles(new[] { path });
        if (_jobs.Count == before) return; // unreadable; LoadFiles has said so, and it stays

        DiscardCapturedFile(full);

        if (_showOnCapture && !wasWaiting) RestoreFromTray();
        else if (!IsVisible) _tray.Notify(Loc.Get("Balloon.JobCollected"), Path.GetFileName(path));
    }

    /// <summary>
    /// Removes a captured file now that the pool has its contents.
    /// <para>
    /// The guard is the whole point: only files inside a capture folder are ever
    /// deleted. The pool also holds PDFs dragged in from someone's own documents,
    /// and for those the file <em>is</em> the document - deleting one would be
    /// data loss rather than housekeeping.
    /// </para>
    /// </summary>
    private static void DiscardCapturedFile(string path)
    {
        if (!CapturedFolder.Holds(path)) return;

        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Locked, or a folder that will not have it. The service prunes by
            // age and size, so nothing accumulates for ever either way.
        }
    }

    private void StopCollecting()
    {
        foreach (var watcher in _capture) watcher.Dispose();
        _capture.Clear();
        _service.Stop();
    }

    // ---------- drag & drop ----------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPdfs(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var paths = DroppedPdfs(e.Data);
        if (paths.Count > 0) LoadFiles(paths);
        e.Handled = true;
    }

    private static List<string> DroppedPdfs(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files)
            return new List<string>();

        return files
            .Where(file => File.Exists(file) &&
                           Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // ---------- job pool ----------

    private void AddJobs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("Dialog.AddTitle"),
            Filter = Loc.Get("Dialog.PdfFilterAll"),
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        LoadFiles(dialog.FileNames);
    }

    private void LoadFiles(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        foreach (string path in paths)
        {
            try
            {
                byte[] pdf = File.ReadAllBytes(path);
                _jobs.Add(new JobItem
                {
                    FilePath = path,
                    Pdf = pdf,
                    PageCount = PdfImposer.ReadPageSizes(pdf).Count,
                });
            }
            catch (Exception ex)
            {
                // One unreadable file must not lose the others.
                failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (failed.Count > 0)
            MessageBox.Show(this, string.Join(Environment.NewLine, failed), Loc.Get("Dialog.AddFailed"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);

        if (JobList.SelectedIndex < 0 && _jobs.Count > 0) JobList.SelectedIndex = 0;
        _ = RefreshAsync();
    }

    /// <summary>Whether the last right-click landed on a job rather than below them.</summary>
    private bool _rightClickedAJob;

    /// <summary>
    /// Right-clicking a job selects it first.
    /// <para>
    /// WPF opens the menu without selecting anything, so it would appear over
    /// one job while its commands acted on whichever was selected before - and
    /// "Remove" would take away the wrong one.
    /// </para>
    /// </summary>
    private void JobList_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(JobList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        _rightClickedAJob = row is not null;
        if (row is not null) row.IsSelected = true;
    }

    /// <summary>Right-clicking the empty space below the jobs offers nothing.</summary>
    private void JobList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!_rightClickedAJob) e.Handled = true;
    }

    /// <summary>Puts every page of the selected job back into the printing.</summary>
    private void RestoreJobPages_Click(object sender, RoutedEventArgs e)
    {
        if (JobList.SelectedItem is not JobItem job) return;
        if (job.Pages.IsAll && job.Rotations.Count == 0 && job.Blanks.Count == 0) return;

        job.Pages = PageSelection.All;
        job.Rotations.Clear();
        job.Blanks.Clear();
        job.NotifySummaryChanged();

        _suppressPagesEdit = true;
        PagesBox.Text = string.Empty;
        _suppressPagesEdit = false;

        _ = RefreshAsync();
    }

    private void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        int index = JobList.SelectedIndex;
        if (index < 0) return;

        _jobs.RemoveAt(index);
        JobList.SelectedIndex = Math.Min(index, _jobs.Count - 1);
        _ = RefreshAsync();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearPool();

    /// <summary>Empties the pool and the preview with it.</summary>
    private void ClearPool()
    {
        if (_jobs.Count == 0) return;

        _jobs.Clear();
        _imposed = null;
        _layout = null;
        _sheetIndex = 0;
        _sheetCount = 0;
        _ = RefreshAsync();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        int index = JobList.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _jobs.Count) return;

        _jobs.Move(index, target);
        JobList.SelectedIndex = target;
        _ = RefreshAsync();
    }

    private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Show the selected job's own page range without treating it as an edit.
        _suppressPagesEdit = true;
        PagesBox.Text = JobList.SelectedItem is JobItem job && !job.Pages.IsAll ? job.Pages.ToString() : string.Empty;
        _suppressPagesEdit = false;

        ShowSheetOf(JobList.SelectedIndex);
        UpdateControls();
    }

    /// <summary>Applies the "Pages" box to the selected job, e.g. 1-4,7 or 3-.</summary>
    private void Pages_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressPagesEdit) return;
        if (JobList.SelectedItem is not JobItem job) return;

        bool valid = PageSelection.TryParse(PagesBox.Text, out var selection);
        // Red while it does not parse - but never silently print the wrong pages.
        ShowValidity(PagesBox, valid);
        if (!valid) return;

        job.Pages = selection;
        _sheetIndex = 0;
        _debounce.Stop();
        _debounce.Start();
    }

    // ---------- layout settings ----------

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string tag) return;

        foreach (var preset in Presets)
            preset.IsChecked = ReferenceEquals(preset, clicked);

        if (tag == "booklet")
        {
            _booklet = true;
        }
        else
        {
            var parts = tag.Split('x');
            _booklet = false;
            _rows = int.Parse(parts[0], CultureInfo.InvariantCulture);
            _columns = int.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        // The box is meant to show the layout in force; without this the buttons
        // changed the layout and left it reading whatever it said before.
        ShowGrid();

        _sheetIndex = 0;
        _ = RefreshAsync();
    }

    /// <summary>
    /// The grid box is the way to any layout the presets do not cover - 2x3,
    /// 1x4, 4x4. Presets write into it, so the two never disagree.
    /// </summary>
    private void Grid_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _suppressGridEdit) return;

        bool valid = NUpGrid.TryParse(GridBox.Text, out int rows, out int columns);
        ShowValidity(GridBox, valid);
        if (!valid) return;

        _rows = rows;
        _columns = columns;
        _booklet = false;

        // Only the buttons, deliberately: rewriting the box here would rewrite
        // it under the caret. Typing "2x3" passes through "2", which parses as a
        // count of two - the box became "1x2" after the first keystroke and the
        // rest of what you typed landed after it.
        HighlightPresets();

        _sheetIndex = 0;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// Red text for input that does not parse. A reference into the palette
    /// rather than a brush, so the colour follows the theme; clearing it hands
    /// the box back to its style.
    /// </summary>
    private static void ShowValidity(TextBox box, bool valid)
    {
        if (valid) box.ClearValue(ForegroundProperty);
        else box.SetResourceReference(ForegroundProperty, "Error");
    }

    /// <summary>Writes the grid box without it counting as an edit.</summary>
    private void ShowGrid()
    {
        _suppressGridEdit = true;
        GridBox.Text = _booklet ? "2-up" : NUpGrid.Format(_rows, _columns);
        GridBox.IsEnabled = !_booklet;
        ShowValidity(GridBox, valid: true);
        _suppressGridEdit = false;
    }

    private void Paper_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _ = RefreshAsync();
    }

    private void Debounced_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Accepts both "8.5" and "8,5" — the box is typed in, not parsed from a file.</summary>
    private static double ParseNumber(string text, double fallback)
    {
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out double value) ||
            double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            return value >= 0 ? value : fallback;
        return fallback;
    }

    // ---------- imposition & preview ----------

    private bool _composing;
    private bool _recompose;

    /// <summary>
    /// Re-imposes the pool - one run at a time. A run that has started cannot
    /// be stopped halfway: composing a PDF has no point at which to give up.
    /// So instead of starting another next to it, a change during a run only
    /// notes that one more is needed, and that one starts from the state as it
    /// is when the first is done. However fast the boxes are typed into, at
    /// most two runs exist: the one working and the one waiting.
    /// </summary>
    private async Task RefreshAsync()
    {
        if (_composing)
        {
            _recompose = true;
            return;
        }

        _composing = true;
        try
        {
            do
            {
                _recompose = false;
                await RefreshOnceAsync();
            }
            while (_recompose);
        }
        finally
        {
            _composing = false;
        }
    }

    private async Task RefreshOnceAsync()
    {
        _work?.Cancel();
        var cancellation = new CancellationTokenSource();
        _work = cancellation;
        var token = cancellation.Token;

        var documents = _jobs.Select(job => job.Pdf).ToList();
        var selections = _jobs.Select(job => job.Pages).ToList();
        // Copy the turns out of the jobs: the imposition runs off the UI thread.
        var rotations = _jobs
            .Select((job, index) => (index, turns: new Dictionary<int, int>(job.Rotations)))
            .ToDictionary(entry => entry.index, entry => entry.turns);
        var blanks = _jobs
            .Select((job, index) => (index, blanks: (IReadOnlyDictionary<int, int>)new Dictionary<int, int>(job.Blanks)))
            .ToDictionary(entry => entry.index, entry => entry.blanks);
        if (documents.Count == 0)
        {
            _imposed = null;
            _sheetCount = 0;
            PreviewImage.Source = null;
            UpdateControls();
            return;
        }

        var paper = PaperSizes.ByName((string)PaperBox.SelectedItem) ?? PaperSizes.A4;
        double marginMm = ParseNumber(MarginBox.Text, 0);
        double gutter = GutterPoints();
        bool booklet = _booklet;
        // A new imposer per run: watermark and borders are part of its configuration.
        var imposer = new PdfImposer
        {
            Watermark = string.IsNullOrWhiteSpace(WatermarkBox.Text)
                ? null
                : new Watermark { Text = WatermarkBox.Text.Trim() },
            PageBorder = BorderToggle.IsChecked == true ? new PageBorder() : null,
        };
        var settings = ImpositionSettings.NUp(_rows, _columns) with
        {
            SheetSize = paper,
            Margins = PtMargins.UniformMm(marginMm),
            GutterX = gutter,
            GutterY = gutter,
        };

        StatusText.Text = Loc.Get("Status.Imposing");
        try
        {
            var (imposed, sheets, layout) = await Task.Run(() =>
            {
                // Composing in two steps rather than calling ImposeToPdf keeps the
                // layout, which is what lets a right-click find the page it hit.
                var sourcePages = PdfImposer.ReadPageSizes(documents, selections)
                    .Select(page => rotations.TryGetValue(page.DocumentIndex, out var turns) &&
                                    turns.TryGetValue(page.PageIndex + 1, out int degrees)
                        ? page with { Rotation = degrees }
                        : page)
                    .ToList();
                sourcePages = BlankPages.Insert(sourcePages, blanks).ToList();
                ImpositionResult result = booklet
                    ? new BookletImposer().Impose(sourcePages, paper, PtMargins.UniformMm(marginMm), gutter)
                    : new NUpImposer().Impose(sourcePages, settings);
                byte[] pdf = imposer.Compose(documents, result);
                return (pdf, PdfRasterizer.PageSizes(pdf).Count, result);
            }, token);

            if (token.IsCancellationRequested) return;

            _imposed = imposed;
            _sheetCount = sheets;
            _layout = layout;
            _sheetIndex = Math.Clamp(_sheetIndex, 0, Math.Max(0, sheets - 1));
            await ShowSheetAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer change - nothing to report.
        }
        catch (Exception ex)
        {
            _imposed = null;
            _sheetCount = 0;
            _layout = null;
            PreviewImage.Source = null;
            StatusText.Text = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_work, cancellation)) UpdateControls();
        }
    }

    private async Task ShowSheetAsync(CancellationToken token)
    {
        byte[]? pdf = _imposed;
        if (pdf is null || _sheetCount == 0) return;

        int index = _sheetIndex;
        SizeSheet();
        int dpi = WantedDpi();
        int generation = ++_renderGeneration;
        byte[] png = await Task.Run(() => PdfRasterizer.RenderPagePng(pdf, index, dpi), token);
        // Zooming can start a render while another is running; the older one
        // must not land on top of the newer.
        if (token.IsCancellationRequested || generation != _renderGeneration) return;
        _renderedDpi = dpi;

        var image = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // decode now so the stream can go
            image.StreamSource = stream;
            image.EndInit();
        }
        PreviewImage.Source = image;
        UpdateControls();
    }

    // ---------- removing pages from the preview ----------

    /// <summary>
    /// Right-clicking a page offers to drop it. This is the direct way to say
    /// "not that one"; the Pages box is the same thing spelled out.
    /// </summary>
    private void Preview_RightClick(object sender, MouseButtonEventArgs e)
    {
        var hit = PlacedPageAt(e.GetPosition(PreviewImage));
        if (hit is null) return;

        var job = _jobs[hit.Source.DocumentIndex];
        var menu = new ContextMenu { PlacementTarget = PreviewImage };

        if (hit.Source.IsBlank)
        {
            int anchor = hit.Source.PageIndex;
            var removeBlank = new MenuItem { Header = Loc.Get("PageMenu.RemoveBlank") };
            removeBlank.Click += (_, _) => ChangeBlanks(job, anchor, -1);
            menu.Items.Add(removeBlank);

            var another = new MenuItem { Header = Loc.Get("PageMenu.AnotherBlank") };
            another.Click += (_, _) => ChangeBlanks(job, anchor, +1);
            menu.Items.Add(another);

            menu.IsOpen = true;
            e.Handled = true;
            return;
        }

        int pageNumber = hit.Source.PageIndex + 1;
        var remove = new MenuItem { Header = Loc.Format("PageMenu.Remove", pageNumber, job.Name) };
        remove.Click += (_, _) => RemovePage(job, pageNumber);
        menu.Items.Add(remove);

        var rotate = new MenuItem { Header = Loc.Format("PageMenu.Turn", pageNumber) };
        rotate.Click += (_, _) => RotatePage(job, pageNumber, 90);
        menu.Items.Add(rotate);

        if (job.Rotations.ContainsKey(pageNumber))
        {
            var straighten = new MenuItem { Header = Loc.Format("PageMenu.Upright", pageNumber) };
            straighten.Click += (_, _) => RotatePage(job, pageNumber, 0, absolute: true);
            menu.Items.Add(straighten);
        }

        menu.Items.Add(new Separator());
        var before = new MenuItem { Header = Loc.Format("PageMenu.BlankBefore", pageNumber) };
        before.Click += (_, _) => ChangeBlanks(job, pageNumber - 1, +1);
        menu.Items.Add(before);

        var after = new MenuItem { Header = Loc.Format("PageMenu.BlankAfter", pageNumber) };
        after.Click += (_, _) => ChangeBlanks(job, pageNumber, +1);
        menu.Items.Add(after);

        if (!job.Pages.IsAll)
        {
            var restore = new MenuItem { Header = Loc.Format("PageMenu.Restore", job.Name) };
            restore.Click += (_, _) => SetJobPages(job, PageSelection.All);
            menu.Items.Add(restore);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---------- preview zoom ----------

    /// <summary>
    /// Gives the sheet its size on screen: as large as fits the visible area at
    /// zoom 1, that times the zoom otherwise. Everything else - the hit test
    /// for right-clicks included - measures against this size.
    /// </summary>
    private void SizeSheet()
    {
        if (_layout is null || _sheetIndex >= _layout.Sheets.Count) return;

        var size = _layout.Sheets[_sheetIndex].Size;
        double margin = SheetShadow.Margin.Left + SheetShadow.Margin.Right;
        // One pixel short of the edge: an exact fit can round up into a scroll
        // bar, which shrinks the area, which refits - a flicker loop.
        double width = Math.Max(1, PreviewScroller.ActualWidth - margin - 1);
        double height = Math.Max(1, PreviewScroller.ActualHeight - margin - 1);
        double fit = Math.Min(width / size.Width, height / size.Height);

        SheetShadow.Width = size.Width * fit * _zoom;
        SheetShadow.Height = size.Height * fit * _zoom;
    }

    /// <summary>The render resolution at which one sheet point lands on one screen pixel or more.</summary>
    private int WantedDpi()
    {
        if (_layout is null || _sheetIndex >= _layout.Sheets.Count || double.IsNaN(SheetShadow.Width)) return PreviewDpi;

        double inches = _layout.Sheets[_sheetIndex].Size.Width / 72.0;
        double pixels = SheetShadow.Width * System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        return (int)Math.Clamp(Math.Ceiling(pixels / inches), PreviewDpi, MaxPreviewDpi);
    }

    private void PreviewScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SizeSheet();
        // A larger window needs a sharper picture as much as a larger zoom does.
        _sharpen.Stop();
        _sharpen.Start();
    }

    /// <summary>Ctrl+wheel zooms around the pointer; the wheel alone scrolls, as everywhere else.</summary>
    private void PreviewScroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _imposed is null) return;
        e.Handled = true;

        int current = Array.FindLastIndex(ZoomSteps, step => step <= _zoom + 1e-6);
        int next = Math.Clamp(current + (e.Delta > 0 ? 1 : -1), 0, ZoomSteps.Length - 1);
        SetZoom(ZoomSteps[next], e.GetPosition(PreviewScroller));
    }

    /// <summary>
    /// Changes the zoom and keeps the spot under <paramref name="anchor"/> (a
    /// point in the visible area) where it was, so zooming in goes towards what
    /// you are pointing at instead of drifting off to the top left.
    /// </summary>
    private void SetZoom(double zoom, Point? anchor = null)
    {
        if (Math.Abs(zoom - _zoom) < 1e-6) return;

        var spot = anchor ?? new Point(PreviewScroller.ViewportWidth / 2, PreviewScroller.ViewportHeight / 2);
        var before = PreviewScroller.TranslatePoint(spot, SheetShadow);
        double fx = SheetShadow.ActualWidth > 0 ? before.X / SheetShadow.ActualWidth : 0.5;
        double fy = SheetShadow.ActualHeight > 0 ? before.Y / SheetShadow.ActualHeight : 0.5;

        _zoom = zoom;
        SizeSheet();
        PreviewScroller.UpdateLayout();

        var origin = SheetShadow.TranslatePoint(new Point(0, 0), PreviewScroller);
        PreviewScroller.ScrollToHorizontalOffset(PreviewScroller.HorizontalOffset + origin.X + fx * SheetShadow.Width - spot.X);
        PreviewScroller.ScrollToVerticalOffset(PreviewScroller.VerticalOffset + origin.Y + fy * SheetShadow.Height - spot.Y);

        UpdateControls();
        _sharpen.Stop();
        _sharpen.Start();
    }

    /// <summary>Ctrl+0 fits the sheet again; Ctrl+plus and Ctrl+minus zoom without a wheel.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || _imposed is null) return;
        // In a text box, Ctrl+0 and Ctrl+minus belong to the text.
        if (Keyboard.FocusedElement is TextBox) return;

        int current = Array.FindLastIndex(ZoomSteps, step => step <= _zoom + 1e-6);
        switch (e.Key)
        {
            case Key.D0 or Key.NumPad0:
                SetZoom(1);
                break;
            case Key.OemPlus or Key.Add:
                SetZoom(ZoomSteps[Math.Min(current + 1, ZoomSteps.Length - 1)]);
                break;
            case Key.OemMinus or Key.Subtract:
                SetZoom(ZoomSteps[Math.Max(current - 1, 0)]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// A double-click fits the sheet again; the middle button grabs it and
    /// drags it around, like a hand tool in a PDF reader.
    /// </summary>
    private void PreviewScroller_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && _zoom != 1)
        {
            SetZoom(1);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Middle && _zoom > 1)
        {
            _panFrom = e.GetPosition(PreviewScroller);
            PreviewScroller.CaptureMouse();
            PreviewScroller.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }
    }

    private void PreviewScroller_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panFrom is not { } from) return;
        var now = e.GetPosition(PreviewScroller);
        PreviewScroller.ScrollToHorizontalOffset(PreviewScroller.HorizontalOffset - (now.X - from.X));
        PreviewScroller.ScrollToVerticalOffset(PreviewScroller.VerticalOffset - (now.Y - from.Y));
        _panFrom = now;
    }

    private void PreviewScroller_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _panFrom is null) return;
        _panFrom = null;
        PreviewScroller.ReleaseMouseCapture();
        PreviewScroller.Cursor = null;
        e.Handled = true;
    }

    /// <summary>Which placed page sits under a point on the preview image.</summary>
    private PlacedPage? PlacedPageAt(Point position)
    {
        if (_layout is null || _sheetIndex >= _layout.Sheets.Count) return null;
        if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0) return null;

        // The image shows exactly one sheet, so this is a straight scale from
        // control pixels to sheet points.
        var sheet = _layout.Sheets[_sheetIndex];
        double x = position.X / PreviewImage.ActualWidth * sheet.Size.Width;
        double y = position.Y / PreviewImage.ActualHeight * sheet.Size.Height;

        foreach (var placed in sheet.Pages)
        {
            var rect = placed.DestRect;
            if (x >= rect.X && x <= rect.Right && y >= rect.Y && y <= rect.Bottom) return placed;
        }
        return null;
    }

    private void RemovePage(JobItem job, int pageNumber)
    {
        var kept = Enumerable.Range(1, job.PageCount)
            .Where(number => number != pageNumber && job.Pages.Includes(number))
            .ToList();

        // Something has to be left to impose, across the whole pool.
        int remaining = kept.Count + _jobs.Where(other => !ReferenceEquals(other, job))
            .Sum(other => Enumerable.Range(1, other.PageCount).Count(other.Pages.Includes));
        if (remaining == 0)
        {
            StatusText.Text = Loc.Get("Status.LastPage");
            return;
        }

        SetJobPages(job, PageSelection.FromPages(kept));
    }

    /// <summary>Turns one page, either by a further quarter or back to upright.</summary>
    private void RotatePage(JobItem job, int pageNumber, int degrees, bool absolute = false)
    {
        int current = job.Rotations.TryGetValue(pageNumber, out int existing) ? existing : 0;
        int turned = absolute ? degrees : (current + degrees) % 360;

        if (turned == 0) job.Rotations.Remove(pageNumber);
        else job.Rotations[pageNumber] = turned;

        job.NotifySummaryChanged();
        _ = RefreshAsync();
    }

    /// <summary>Adds or takes away a blank at one anchor of a job; the sheet in view stays in view.</summary>
    private void ChangeBlanks(JobItem job, int anchor, int delta)
    {
        int count = job.Blanks.TryGetValue(anchor, out int existing) ? existing + delta : delta;
        if (count > 0) job.Blanks[anchor] = count;
        else job.Blanks.Remove(anchor);

        job.NotifySummaryChanged();
        _ = RefreshAsync();
    }

    private void SetJobPages(JobItem job, PageSelection pages)
    {
        job.Pages = pages;

        if (ReferenceEquals(JobList.SelectedItem, job))
        {
            _suppressPagesEdit = true;
            PagesBox.Text = pages.IsAll ? string.Empty : pages.ToString();
            _suppressPagesEdit = false;
        }

        _sheetIndex = 0;
        _ = RefreshAsync();
    }

    // ---------- layout profiles ----------

    /// <summary>Applying a saved layout: one selection instead of five settings.</summary>
    private void Profile_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ProfileBox.SelectedItem is not LayoutProfile profile) return;

        _booklet = profile.Booklet;
        _rows = Math.Max(1, profile.Rows);
        _columns = Math.Max(1, profile.Columns);
        if (Papers.Contains(profile.Paper)) PaperBox.SelectedItem = profile.Paper;
        MarginBox.Text = profile.MarginMm.ToString(CultureInfo.CurrentCulture);
        GutterBox.Text = MmFromPoints(profile.Gutter).ToString(CultureInfo.CurrentCulture);
        BorderToggle.IsChecked = profile.PageBorders;
        WatermarkBox.Text = profile.Watermark ?? string.Empty;
        if (DuplexModes.TryParse(profile.Duplex, out var duplex))
            DuplexBox.SelectedItem = DuplexChoices.FirstOrDefault(choice => choice.Mode == duplex) ?? DuplexChoices[0];

        CheckMatchingPreset();
        _sheetIndex = 0;
        _debounce.Stop();
        _ = RefreshAsync();
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        string name = ProfileBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = Loc.Get("Status.ProfileNameNeeded");
            return;
        }

        var profile = new LayoutProfile
        {
            Name = name,
            Rows = _rows,
            Columns = _columns,
            Booklet = _booklet,
            Paper = (string)PaperBox.SelectedItem,
            MarginMm = ParseNumber(MarginBox.Text, 0),
            Gutter = GutterPoints(),
            PageBorders = BorderToggle.IsChecked == true,
            Watermark = string.IsNullOrWhiteSpace(WatermarkBox.Text) ? null : WatermarkBox.Text.Trim(),
            Duplex = SelectedDuplex().ToString(),
        };

        // Saving under an existing name replaces it, which is what "Save" means
        // everywhere else.
        int existing = _profiles.ToList().FindIndex(p =>
            string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing >= 0) _profiles[existing] = profile;
        else _profiles.Add(profile);

        ProfileBox.SelectedItem = profile;
        StatusText.Text = Loc.Format("Status.ProfileSaved", name);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not LayoutProfile profile) return;

        _profiles.Remove(profile);
        ProfileBox.Text = string.Empty;
        StatusText.Text = Loc.Format("Status.ProfileDeleted", profile.Name);
    }

    // ---------- the virtual printer ----------

    /// <summary>
    /// Creates or removes the Windows printer queue. This needs administrator
    /// rights once, so it goes through a UAC prompt rather than the app running
    /// elevated.
    /// </summary>
    private void PrinterSetup_Click(object sender, RoutedEventArgs e)
    {
        PrinterSetupButton.IsEnabled = false;
        try
        {
            if (PrinterSetup.IsRegistered())
            {
                StatusText.Text = PrinterSetup.Unregister()
                    ? Loc.Get("Status.PrinterRemoved")
                    : Loc.Get("Status.PrinterNotRemoved");
            }
            else
            {
                CreatePrinter();
            }
        }
        finally
        {
            PrinterSetupButton.IsEnabled = true;
            UpdatePrinterSetupButton();
        }
    }

    /// <summary>
    /// Creates the printer queue. Windows only accepts an IPP printer while the
    /// service behind it is answering, so the service has to be up *first* -
    /// getting that order wrong is the whole reason this is one method.
    /// </summary>
    /// <summary>
    /// Creates the printer queue, which Windows will only do while its endpoint
    /// answers.
    /// <para>
    /// Something has to be listening - but it does not have to be this app. On
    /// an installed machine the Windows service owns the port, and insisting on
    /// hosting it here meant the button did nothing whatsoever: our own listener
    /// could not start, so the code returned before it ever asked for the
    /// printer. No elevation prompt, no printer, no explanation.
    /// </para>
    /// </summary>
    private void CreatePrinter()
    {
        int port = CaptureService.DefaultPort;

        if (_service.IsRunning)
        {
            port = _service.Port;
        }
        else if (!CaptureService.IsAnswering(port))
        {
            // Nobody is serving it, so we will.
            SetCollecting(true);
            if (!_service.IsRunning || !CaptureService.IsAnswering(_service.Port))
            {
                StatusText.Text = Loc.Get("Status.NoListener");
                return;
            }
            port = _service.Port;
        }

        StatusText.Text = Loc.Get("Status.CreatingPrinter");
        StatusText.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        StatusText.Text = PrinterSetup.Register(port)
            ? Loc.Get("Status.PrinterReady")
            : Loc.Get("Status.PrinterNotCreated");
    }

    /// <summary>
    /// Offers to finish the setup the installer cannot do: creating the printer
    /// needs the user's own session, so it is asked for once, here.
    /// </summary>
    /// <summary>
    /// Offers to create the printer, once, when there is none.
    /// <para>
    /// This is the path for copies an installer cannot help: an .msix, or a run
    /// from source. An installed copy never gets here - the installer has made
    /// the printer already, and it does so without asking anybody for anything.
    /// </para>
    /// <para>
    /// Both the question and the answer are logged. An unexplained elevation
    /// prompt is alarming, and one turned up once from a copy left running in
    /// the background, with nothing anywhere recording that it had asked.
    /// </para>
    /// </summary>
    private void OfferPrinterSetup(AppSettings settings)
    {
        if (settings.PrinterSetupOffered || PrinterSetup.IsRegistered()) return;

        _printerSetupOffered = true;
        App.Log("no virtual printer found; asking whether to create one");
        var answer = MessageBox.Show(
            this,
            Loc.Get("Dialog.SetupText"),
            Loc.Get("Dialog.SetupTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        App.Log($"answer to the printer question: {answer}");
        if (answer == MessageBoxResult.Yes) CreatePrinter();
        UpdatePrinterSetupButton();
    }

    private void UpdatePrinterSetupButton() =>
        PrinterSetupButton.Content = PrinterSetup.IsRegistered()
            ? Loc.Get("PrinterSetup.Remove")
            : Loc.Get("PrinterSetup.Add");

    /// <summary>
    /// Turns the preview to the first sheet carrying a job. Selecting one in the
    /// pool otherwise had no visible effect at all, which made the list look
    /// decorative - with several jobs pooled, "where did mine end up?" is the
    /// question it should answer.
    /// </summary>
    private void ShowSheetOf(int jobIndex)
    {
        if (jobIndex < 0 || _layout is null || _sheetCount == 0) return;

        for (int sheet = 0; sheet < _layout.Sheets.Count && sheet < _sheetCount; sheet++)
        {
            if (!_layout.Sheets[sheet].Pages.Any(page => page.Source.DocumentIndex == jobIndex)) continue;
            if (sheet == _sheetIndex) return;

            _sheetIndex = sheet;
            _ = ShowSheetAsync(CancellationToken.None);
            return;
        }
    }

    private void PrevSheet_Click(object sender, RoutedEventArgs e) => StepSheet(-1);

    private void NextSheet_Click(object sender, RoutedEventArgs e) => StepSheet(+1);

    private void StepSheet(int delta)
    {
        int target = _sheetIndex + delta;
        if (target < 0 || target >= _sheetCount) return;

        _sheetIndex = target;
        // Page turns only repaint - no need to impose again.
        _ = ShowSheetAsync(CancellationToken.None);
    }

    // ---------- output ----------

    private async void Print_Click(object sender, RoutedEventArgs e) => await PrintAsync();

    /// <summary>
    /// The usual end of a job: print, empty the pool, put the window away. What
    /// has been printed is dealt with, and leaving it in the pool only risks it
    /// going out again with the next job. Collecting carries on in the tray.
    /// </summary>
    private async void PrintAndClose_Click(object sender, RoutedEventArgs e)
    {
        if (await PrintAsync()) Close();
    }

    /// <summary>Prints the imposed document. False when nothing was sent.</summary>
    private async Task<bool> PrintAsync()
    {
        if (_imposed is null) return false;
        if (PrinterBox.SelectedItem is not string printer)
        {
            MessageBox.Show(this, Loc.Get("Dialog.ChoosePrinter"), "Open-LeanPrint",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        byte[] pdf = _imposed;
        PrintButton.IsEnabled = false;
        PrintCloseButton.IsEnabled = false;
        StatusText.Text = Loc.Format("Status.Printing", printer);
        try
        {
            var duplex = SelectedDuplex();
            var report = await Task.Run(() => PdfPrinter.Print(pdf, printer, new PrintOptions
            {
                Dpi = PrintDpi,
                Duplex = duplex,
                JobName = _jobs.Count == 1 ? $"Open-LeanPrint - {_jobs[0].Name}" : "Open-LeanPrint",
            }));
            // Reporting the duplex mode that was applied, not the one that was
            // asked for: they differ when the printer cannot do it, and until
            // this said so there was no way to tell from inside the app.
            string sides = report.DuplexUnsupported
                ? Loc.Get("Sides.Unsupported")
                : Loc.Get(report.Duplex switch
                {
                    DuplexMode.Simplex => "Sides.Simplex",
                    DuplexMode.LongEdge => "Sides.Long",
                    DuplexMode.ShortEdge => "Sides.Short",
                    _ => "Sides.Default",
                });
            StatusText.Text = Loc.Format("Status.Sent", report.Sheets, report.PrinterName,
                                         string.Join("/", report.PaperNames)) + sides;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.Get("Dialog.PrintFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateControls();
            return false;
        }
        finally
        {
            PrintButton.IsEnabled = _imposed is not null;
            PrintCloseButton.IsEnabled = _imposed is not null;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_imposed is null) return;

        string suggested = _jobs.Count > 0
            ? $"{Path.GetFileNameWithoutExtension(_jobs[0].Name)}-{LayoutTag()}.pdf"
            : $"openleanprint-{LayoutTag()}.pdf";

        var dialog = new SaveFileDialog
        {
            Title = Loc.Get("Dialog.SaveTitle"),
            Filter = Loc.Get("Dialog.PdfFilter"),
            FileName = suggested,
            DefaultExt = ".pdf",
        };
        if (dialog.ShowDialog(this) != true) return;

        // Whole or not at all: replacing a PDF somebody chose to overwrite must
        // not leave half of the new one if the disk fills up.
        AtomicFile.WriteAllBytes(dialog.FileName, _imposed);
        StatusText.Text = Loc.Format("Status.Saved", Path.GetFileName(dialog.FileName), _imposed.Length);
    }

    // ---------- ui state ----------

    private string LayoutTag() => _booklet ? "booklet" : $"{_rows}x{_columns}up";

    private string LayoutDescription() =>
        _booklet
            ? Loc.Format("Layout.Booklet", PaperBox.SelectedItem)
            : Loc.Format("Layout.NUp", _rows, _columns, PaperBox.SelectedItem);

    private void UpdateControls()
    {
        bool hasJobs = _jobs.Count > 0;
        bool hasSheets = _imposed is not null && _sheetCount > 0;
        bool hasSelection = JobList.SelectedIndex >= 0;

        EmptyPoolHint.Visibility = hasJobs ? Visibility.Collapsed : Visibility.Visible;
        SheetShadow.Visibility = hasSheets ? Visibility.Visible : Visibility.Collapsed;
        PreviewHint.Visibility = hasSheets ? Visibility.Collapsed : Visibility.Visible;

        RemoveButton.IsEnabled = hasSelection;
        UpButton.IsEnabled = hasSelection && JobList.SelectedIndex > 0;
        DownButton.IsEnabled = hasSelection && JobList.SelectedIndex < _jobs.Count - 1;
        ClearButton.IsEnabled = hasJobs;
        PrintButton.IsEnabled = hasSheets;
        PrintCloseButton.IsEnabled = hasSheets;
        SaveButton.IsEnabled = hasSheets;
        PrevSheet.IsEnabled = hasSheets && _sheetIndex > 0;
        NextSheet.IsEnabled = hasSheets && _sheetIndex < _sheetCount - 1;

        SheetLabel.Text = hasSheets ? Loc.Format("Sheet.Label", _sheetIndex + 1, _sheetCount) : "—";
        if (hasSheets && _zoom != 1)
            SheetLabel.Text += string.Format(CultureInfo.CurrentCulture, " · {0:P0}", _zoom);
        PreviewTip.Visibility = hasSheets ? Visibility.Visible : Visibility.Hidden;

        if (!hasJobs)
        {
            StatusText.Text = Loc.Get("Status.NoJobs");
            return;
        }

        int pages = _jobs.Sum(job => job.Pages.IsAll
            ? job.PageCount
            : Enumerable.Range(1, job.PageCount).Count(job.Pages.Includes));
        string jobs = Loc.Count("Count.Job", _jobs.Count);
        string pageCount = Loc.Count("Count.Page", pages);
        StatusText.Text = hasSheets
            ? $"{jobs} · {pageCount} → {Loc.Count("Count.Sheet", _sheetCount)} · {LayoutDescription()}"
            : $"{jobs} · {pageCount}";
    }
}
