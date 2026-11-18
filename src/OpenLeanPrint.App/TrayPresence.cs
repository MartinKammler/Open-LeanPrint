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
    private readonly WinForms.ToolStripMenuItem _hideNoticeItem;
    private bool _suppressCollectEvent;
    private bool _suppressPopUpEvent;
    private bool _suppressHideNoticeEvent;

    public TrayPresence()
    {
        var showItem = new WinForms.ToolStripMenuItem("Show Open-LeanPrint");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        _collectItem = new WinForms.ToolStripMenuItem("Collect captured jobs") { CheckOnClick = true };
        _collectItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressCollectEvent) CollectingChanged?.Invoke(this, _collectItem.Checked);
        };

        _popUpItem = new WinForms.ToolStripMenuItem("Show the window when a job arrives") { CheckOnClick = true };
        _popUpItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressPopUpEvent) ShowOnCaptureChanged?.Invoke(this, _popUpItem.Checked);
        };

        // Radio-style: the one ticked is what is in force. Ticked here rather
        // than with CheckOnClick, so clicking the current choice cannot untick it.
        _appearanceItem = new WinForms.ToolStripMenuItem("Appearance");
        foreach (var (preference, label) in new[]
                 {
                     (ThemePreference.System, "Same as Windows"),
                     (ThemePreference.Light, "Light"),
                     (ThemePreference.Dark, "Dark"),
                 })
        {
            var item = new WinForms.ToolStripMenuItem(label);
            item.Click += (_, _) => ThemeChosen?.Invoke(this, preference);
            _themeItems[preference] = item;
            _appearanceItem.DropDownItems.Add(item);
        }
        _hideNoticeItem = new WinForms.ToolStripMenuItem("Tell me when the window goes to the tray") { CheckOnClick = true };
        _hideNoticeItem.CheckedChanged += (_, _) =>
        {
            if (!_suppressHideNoticeEvent) NoticeOnHideChanged?.Invoke(this, _hideNoticeItem.Checked);
        };

        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = _menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(showItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_collectItem);
        menu.Items.Add(_popUpItem);
        menu.Items.Add(_hideNoticeItem);
        menu.Items.Add(_appearanceItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Open-LeanPrint",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
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
        foreach (var strip in new WinForms.ToolStrip[] { _menu, _appearanceItem.DropDown })
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
    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.Info);

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
