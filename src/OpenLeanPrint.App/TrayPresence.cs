using System.Windows;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace OpenLeanPrint.App;

/// <summary>
/// The tray icon. It exists so the app can keep collecting captured jobs after
/// its window is closed — printing from another application should still land
/// in the pool, and reopening the window should not be a new start.
/// <para>
/// WPF has no tray icon of its own, so this wraps WinForms' NotifyIcon. All
/// WinForms types stay behind this class.
/// </para>
/// </summary>
internal sealed class TrayPresence : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _collectItem;
    private readonly WinForms.ToolStripMenuItem _popUpItem;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _appearanceItem;
    private readonly Dictionary<ThemePreference, WinForms.ToolStripMenuItem> _themeItems = new();
    private readonly WinForms.ToolStripMenuItem _languageItem;
    private readonly Dictionary<LanguagePreference, WinForms.ToolStripMenuItem> _languageItems = new();
    private readonly WinForms.ToolStripMenuItem _showItem;
    private readonly WinForms.ToolStripMenuItem _maximizedItem;
    private readonly WinForms.ToolStripMenuItem _updateItem;
    private readonly WinForms.ToolStripMenuItem _checkOnStartItem;
    private string? _updateVersion;
    private Action? _balloonClicked;

    /// <summary>Set while the app mirrors its state into a toggle, so that does not count as a click.</summary>
    private bool _mirroring;
    private readonly WinForms.ToolStripMenuItem _exitItem;
    private readonly WinForms.ToolStripMenuItem _hideNoticeItem;
    private bool _suppressCollectEvent;
    private bool _suppressPopUpEvent;
    private bool _suppressHideNoticeEvent;

    public TrayPresence()
    {
        var showItem = _showItem = new WinForms.ToolStripMenuItem();
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        _collectItem = new WinForms.ToolStripMenuItem { CheckOnClick = true };
        _collectItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressCollectEvent) CollectingChanged?.Invoke(this, _collectItem.Checked);
        };

        _popUpItem = new WinForms.ToolStripMenuItem { CheckOnClick = true };
        _popUpItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressPopUpEvent) ShowOnCaptureChanged?.Invoke(this, _popUpItem.Checked);
        };

        // Radio-style: the one ticked is what is in force. Ticked here rather
        // than with CheckOnClick, so clicking the current choice cannot untick it.
        _appearanceItem = new WinForms.ToolStripMenuItem();
        foreach (var preference in Enum.GetValues<ThemePreference>())
        {
            var item = new WinForms.ToolStripMenuItem();
            item.Click += (_, _) => ThemeChosen?.Invoke(this, preference);
            _themeItems[preference] = item;
            _appearanceItem.DropDownItems.Add(item);
        }
        _hideNoticeItem = new WinForms.ToolStripMenuItem { CheckOnClick = true };
        _hideNoticeItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressHideNoticeEvent) NoticeOnHideChanged?.Invoke(this, _hideNoticeItem.Checked);
        };

        // Each language is named in itself: someone who cannot read the current
        // interface still has to find their way back to one they can.
        _languageItem = new WinForms.ToolStripMenuItem();
        foreach (var preference in Enum.GetValues<LanguagePreference>())
        {
            var item = new WinForms.ToolStripMenuItem(preference switch
            {
                LanguagePreference.English => "English",
                LanguagePreference.German => "Deutsch",
                LanguagePreference.French => "Français",
                LanguagePreference.Spanish => "Español",
                _ => string.Empty, // "Same as Windows", in the current language - see Relabel
            });
            item.Click += (_, _) => LanguageChosen?.Invoke(this, preference);
            _languageItems[preference] = item;
            _languageItem.DropDownItems.Add(item);
        }

        _maximizedItem = new WinForms.ToolStripMenuItem { CheckOnClick = true };
        _maximizedItem.CheckedChanged += (_, _) =>
        {
            if (!_mirroring) OpenMaximizedChanged?.Invoke(this, _maximizedItem.Checked);
        };

        _updateItem = new WinForms.ToolStripMenuItem();
        _updateItem.Click += (_, _) => UpdateRequested?.Invoke(this, EventArgs.Empty);

        _checkOnStartItem = new WinForms.ToolStripMenuItem { CheckOnClick = true };
        _checkOnStartItem.CheckedChanged += (_, _) =>
        {
            if (!_mirroring) CheckOnStartChanged?.Invoke(this, _checkOnStartItem.Checked);
        };

        var exitItem = _exitItem = new WinForms.ToolStripMenuItem();
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = _menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(showItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_collectItem);
        menu.Items.Add(_popUpItem);
        menu.Items.Add(_hideNoticeItem);
        menu.Items.Add(_maximizedItem);
        menu.Items.Add(_appearanceItem);
        menu.Items.Add(_languageItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_updateItem);
        menu.Items.Add(_checkOnStartItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = $"Open-LeanPrint {Updates.Current}", // the tooltip; Windows allows 63 characters
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        // A balloon knows nothing of what it was about; the one shown last
        // decides what a click on it does, and closing it forgets that.
        _icon.BalloonTipClicked += (_, _) =>
        {
            var clicked = _balloonClicked;
            _balloonClicked = null;
            clicked?.Invoke();
        };
        _icon.BalloonTipClosed += (_, _) => _balloonClicked = null;
        Relabel();
    }

    /// <summary>Writes the menu's texts in the language in force.</summary>
    public void Relabel()
    {
        _showItem.Text = Loc.Get("Tray.Show");
        _collectItem.Text = Loc.Get("Collect");
        _popUpItem.Text = Loc.Get("Tray.PopUp");
        _hideNoticeItem.Text = Loc.Get("Tray.NoticeOnHide");
        _appearanceItem.Text = Loc.Get("Tray.Appearance");
        _themeItems[ThemePreference.System].Text = Loc.Get("Tray.SameAsWindows");
        _themeItems[ThemePreference.Light].Text = Loc.Get("Tray.Light");
        _themeItems[ThemePreference.Dark].Text = Loc.Get("Tray.Dark");
        _languageItem.Text = Loc.Get("Tray.Language");
        _languageItems[LanguagePreference.System].Text = Loc.Get("Tray.SameAsWindows");
        _exitItem.Text = Loc.Get("Tray.Exit");
        _maximizedItem.Text = Loc.Get("Tray.OpenMaximized");
        _checkOnStartItem.Text = Loc.Get("Tray.CheckOnStart");
        _updateItem.Text = _updateVersion is null
            ? Loc.Get("Tray.CheckUpdates")
            : Loc.Format("Tray.InstallUpdate", _updateVersion);
    }

    /// <summary>The user toggled "open the window maximized".</summary>
    public event EventHandler<bool>? OpenMaximizedChanged;

    /// <summary>The user toggled the check at start.</summary>
    public event EventHandler<bool>? CheckOnStartChanged;

    /// <summary>The user asked to check for updates - or to install the one found.</summary>
    public event EventHandler? UpdateRequested;

    public void SetOpenMaximized(bool maximized) => Mirror(_maximizedItem, maximized);

    public void SetCheckOnStart(bool check) => Mirror(_checkOnStartItem, check);

    /// <summary>Turns "Check for updates…" into "Install update 1.2.3…", or back with null.</summary>
    public void SetUpdateAvailable(string? version)
    {
        _updateVersion = version;
        Relabel();
    }

    private void Mirror(WinForms.ToolStripMenuItem item, bool value)
    {
        if (item.Checked == value) return;
        _mirroring = true;
        item.Checked = value;
        _mirroring = false;
    }

    /// <summary>The user picked a language.</summary>
    public event EventHandler<LanguagePreference>? LanguageChosen;

    /// <summary>Ticks the current language choice.</summary>
    public void SetLanguage(LanguagePreference preference)
    {
        foreach (var (choice, item) in _languageItems) item.Checked = choice == preference;
    }

    /// <summary>The user asked for the window back.</summary>
    public event EventHandler? ShowRequested;

    /// <summary>The user asked to quit for real.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>The user toggled collecting from the tray menu.</summary>
    public event EventHandler<bool>? CollectingChanged;

    /// <summary>The user decided whether an arriving job should raise the window.</summary>
    public event EventHandler<bool>? ShowOnCaptureChanged;

    /// <summary>The user picked a look under "Appearance".</summary>
    public event EventHandler<ThemePreference>? ThemeChosen;

    /// <summary>Ticks the current choice and dresses the menu to match the window.</summary>
    public void SetTheme(ThemePreference preference, bool dark)
    {
        foreach (var (choice, item) in _themeItems) item.Checked = choice == preference;

        // The tray menu is WinForms, outside WPF's resources, so it is coloured
        // here. Left alone it would be the one light thing in a dark app.
        foreach (var strip in new WinForms.ToolStrip[] { _menu, _appearanceItem.DropDown, _languageItem.DropDown })
        {
            strip.Renderer = dark ? new DarkMenuRenderer() : new WinForms.ToolStripProfessionalRenderer();
            strip.ForeColor = dark ? DarkMenuColors.Text : Drawing.SystemColors.MenuText;
        }
    }
    /// <summary>The user decided whether hiding the window should be announced with a balloon.</summary>
    public event EventHandler<bool>? NoticeOnHideChanged;

    /// <summary>Mirrors the app's state into the menu without raising <see cref="CollectingChanged"/>.</summary>
    public void SetCollecting(bool collecting)
    {
        if (_collectItem.Checked == collecting) return;

        _suppressCollectEvent = true;
        _collectItem.Checked = collecting;
        _suppressCollectEvent = false;
    }

    /// <summary>Mirrors the app's state into the menu without raising <see cref="ShowOnCaptureChanged"/>.</summary>
    public void SetShowOnCapture(bool show)
    {
        if (_popUpItem.Checked == show) return;

        _suppressPopUpEvent = true;
        _popUpItem.Checked = show;
        _suppressPopUpEvent = false;
    }

    /// <summary>Mirrors the app's state into the menu without raising <see cref="NoticeOnHideChanged"/>.</summary>
    public void SetNoticeOnHide(bool notice)
    {
        if (_hideNoticeItem.Checked == notice) return;

        _suppressHideNoticeEvent = true;
        _hideNoticeItem.Checked = notice;
        _suppressHideNoticeEvent = false;
    }

    /// <summary>Shows a balloon — used when something happens while the window is hidden.</summary>
    public void Notify(string title, string text, Action? onClick = null)
    {
        _balloonClicked = onClick;
        _icon.ShowBalloonTip(onClick is null ? 4000 : 10000, title, text, WinForms.ToolTipIcon.Info);
    }

    private static Drawing.Icon LoadIcon()
    {
        // The component form works no matter which assembly hosts the window.
        var uri = new Uri("pack://application:,,,/OpenLeanPrint;component/Assets/OpenLeanPrint.ico", UriKind.Absolute);
        using var stream = Application.GetResourceStream(uri)?.Stream
            ?? throw new InvalidOperationException("The application icon resource is missing.");
        return new Drawing.Icon(stream);
    }

    /// <summary>The dark tray menu's colours, matching Palette.Dark.xaml.</summary>
    private sealed class DarkMenuColors : WinForms.ProfessionalColorTable
    {
        public static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xE8, 0xE8, 0xEC);
        private static readonly Drawing.Color Back = Drawing.Color.FromArgb(0x2A, 0x2A, 0x2F);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x3E, 0x3E, 0x46);
        private static readonly Drawing.Color Tint = Drawing.Color.FromArgb(0x24, 0x3B, 0x63);
        private static readonly Drawing.Color Accent = Drawing.Color.FromArgb(0x4C, 0x8D, 0xFF);

        public override Drawing.Color ToolStripDropDownBackground => Back;
        public override Drawing.Color ImageMarginGradientBegin => Back;
        public override Drawing.Color ImageMarginGradientMiddle => Back;
        public override Drawing.Color ImageMarginGradientEnd => Back;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Tint;
        public override Drawing.Color MenuItemSelected => Tint;
        public override Drawing.Color MenuItemSelectedGradientBegin => Tint;
        public override Drawing.Color MenuItemSelectedGradientEnd => Tint;
        public override Drawing.Color MenuItemPressedGradientBegin => Tint;
        public override Drawing.Color MenuItemPressedGradientEnd => Tint;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Back;
        public override Drawing.Color CheckBackground => Accent;
        public override Drawing.Color CheckSelectedBackground => Accent;
        public override Drawing.Color CheckPressedBackground => Accent;
    }

    /// <summary>
    /// The professional renderer draws the tick and the submenu arrow in black
    /// whatever the colour table says, which vanishes on a dark menu. This
    /// draws both in light colours instead.
    /// </summary>
    private sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors())
        {
            RoundedEdges = false;
        }

        /// <summary>
        /// The highlight under the pointer, and on an item whose submenu is
        /// open. WinForms only takes it from the colour table when visual
        /// styles are on - and a WPF app never turns them on for its WinForms
        /// parts - so otherwise it paints Windows' light blue under this menu's
        /// light text, and the item can no longer be read. Painted here, it does
        /// not depend on that.
        /// </summary>
        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.IsOnDropDown || !e.Item.Enabled || !(e.Item.Selected || e.Item.Pressed))
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }

            // Inset like the stock highlight, so the image margin stays visible.
            var bounds = new Drawing.Rectangle(2, 0, e.Item.Width - 3, e.Item.Height);
            using var fill = new Drawing.SolidBrush(ColorTable.MenuItemSelected);
            e.Graphics.FillRectangle(fill, bounds);
        }

        protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = DarkMenuColors.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            var box = e.ImageRectangle;
            box.Inflate(1, 1);
            using (var fill = new Drawing.SolidBrush(ColorTable.CheckBackground))
                e.Graphics.FillRectangle(fill, box);

            var smoothing = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Drawing.Pen(Drawing.Color.White, Math.Max(1.5f, box.Height / 9f)))
            {
                float x = box.Left, y = box.Top, w = box.Width, h = box.Height;
                e.Graphics.DrawLines(pen, new[]
                {
                    new Drawing.PointF(x + w * 0.22f, y + h * 0.52f),
                    new Drawing.PointF(x + w * 0.42f, y + h * 0.72f),
                    new Drawing.PointF(x + w * 0.78f, y + h * 0.30f),
                });
            }
            e.Graphics.SmoothingMode = smoothing;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
