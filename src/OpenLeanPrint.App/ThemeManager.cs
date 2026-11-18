using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace OpenLeanPrint.App;

/// <summary>What the user asked for; <see cref="System"/> follows Windows.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Switches between the light and the dark look.
/// <para>
/// The light palette lives in <c>Theme.xaml</c> and is always loaded. Dark is
/// <c>Palette.Dark.xaml</c> merged in after it, which overrides the same keys;
/// light is that dictionary taken out again. Everything that draws refers to
/// the palette with DynamicResource, so the change reaches open windows at once.
/// </para>
/// </summary>
internal static class ThemeManager
{
    private static readonly Uri DarkPaletteUri =
        new("pack://application:,,,/OpenLeanPrint;component/Palette.Dark.xaml", UriKind.Absolute);

    private static ResourceDictionary? _darkPalette;
    private static bool _watchingSystem;

    public static ThemePreference Preference { get; private set; } = ThemePreference.System;

    /// <summary>Whether the dark palette is in force right now.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Raised after the look changed, on the UI thread.</summary>
    public static event EventHandler? Changed;

    public static void Apply(ThemePreference preference)
    {
        Preference = preference;

        // Only listen while it matters. The event is static and outlives
        // everything, so a handler left behind would be a leak by design.
        bool watch = preference == ThemePreference.System;
        if (watch != _watchingSystem)
        {
            if (watch) SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            else SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _watchingSystem = watch;
        }

        Refresh();
    }

    /// <summary>Keeps a window's title bar in step with the theme, now and on every change.</summary>
    public static void Track(Window window)
    {
        // The handle does not exist before SourceInitialized, and setting the
        // attribute then means the frame is never drawn light first.
        window.SourceInitialized += (_, _) => ApplyTitleBar(window);
        Changed += (_, _) => ApplyTitleBar(window);
    }

    public static bool TryParse(string? text, out ThemePreference preference) =>
        Enum.TryParse(text, ignoreCase: true, out preference) && Enum.IsDefined(preference);

    private static void Refresh()
    {
        bool dark = Preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemPrefersDark(),
        };

        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var merged = resources.MergedDictionaries;
        _darkPalette ??= new ResourceDictionary { Source = DarkPaletteUri };

        bool loaded = merged.Contains(_darkPalette);
        if (dark == loaded && dark == IsDark) return;

        if (dark && !loaded) merged.Add(_darkPalette);
        else if (!dark && loaded) merged.Remove(_darkPalette);

        IsDark = dark;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Switching Windows between light and dark arrives as "General". It is
        // raised on a thread of its own, not the one that owns the resources.
        if (e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(Refresh);
    }

    /// <summary>
    /// The "Choose your app mode" switch in Windows' colour settings. There is
    /// no managed API for it; this registry value is what Windows itself
    /// documents for apps that draw their own look.
    /// </summary>
    private static bool SystemPrefersDark()
    {
        // High contrast brings its own colours, and they are not ours to replace.
        if (SystemParameters.HighContrast) return false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false; // unreadable: the look the app always had
        }
    }

    // ---------- title bar ----------

    // DWMWA_USE_IMMERSIVE_DARK_MODE. Windows 10 before 20H1 knew it as 19.
    private const int DarkModeAttribute = 20;
    private const int DarkModeAttributeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// WPF draws the inside of the window; the frame and title bar belong to
    /// Windows, which only draws them dark when asked to. Where it cannot, the
    /// call fails quietly and the frame stays light - a cosmetic loss.
    /// </summary>
    private static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        int value = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DarkModeAttribute, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DarkModeAttributeBefore20H1, ref value, sizeof(int));
    }
}
