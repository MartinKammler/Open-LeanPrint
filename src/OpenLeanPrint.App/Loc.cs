using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace OpenLeanPrint.App;

/// <summary>What the user asked for; <see cref="System"/> follows Windows' display language.</summary>
public enum LanguagePreference
{
    System,
    English,
    German,
    French,
    Spanish,
}

/// <summary>
/// The interface's words, in the language in force.
/// <para>
/// The texts live in <c>Strings.resx</c> (English, also the fallback for any
/// key a translation lacks) and <c>Strings.de/fr/es.resx</c> next to it. XAML
/// reaches them through <see cref="LocExtension"/>, which binds to this
/// object's indexer - so switching the language raises one change and every
/// open window rewrites itself. Code asks <see cref="Get"/> and
/// <see cref="Format"/>, and redraws what it wrote when <see cref="Changed"/>
/// fires.
/// </para>
/// <para>
/// Only the UI culture changes. Numbers keep following Windows' regional
/// format, because that is what people type into the margin box - someone with
/// an English interface and German settings still writes 2,5.
/// </para>
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager Texts =
        new("OpenLeanPrint.App.Strings", typeof(Loc).Assembly);

    /// <summary>The display language Windows started us with, before any choice of ours.</summary>
    private static readonly CultureInfo WindowsLanguage = CultureInfo.CurrentUICulture;

    public static Loc Instance { get; } = new();

    private Loc() { }

    public static LanguagePreference Preference { get; private set; } = LanguagePreference.System;

    /// <summary>The culture the texts come from right now.</summary>
    public static CultureInfo Culture { get; private set; } = Resolve(LanguagePreference.System);

    /// <summary>Raised after the language changed.</summary>
    public static event EventHandler? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What XAML binds to: <c>[Key]</c>.</summary>
    public string this[string key] => Get(key);

    public static string Get(string key) =>
        // A missing key shows itself rather than an empty label, so a gap in a
        // translation is visible instead of silent.
        Texts.GetString(key, Culture) ?? key;

    /// <summary>A text with placeholders. Numbers in it follow the regional format, like the input boxes.</summary>
    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>"1 page" or "3 pages": <c>Key.One</c> for one, <c>Key.Many</c> with the count otherwise.</summary>
    public static string Count(string key, int count) =>
        count == 1 ? Get(key + ".One") : Format(key + ".Many", count);

    public static void Apply(LanguagePreference preference)
    {
        Preference = preference;
        var culture = Resolve(preference);
        if (Equals(culture, Culture)) return;

        Culture = culture;
        // Dialogs and anything else that asks the thread pick it up as well.
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(Binding.IndexerName));
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static bool TryParse(string? text, out LanguagePreference preference) =>
        Enum.TryParse(text, ignoreCase: true, out preference) && Enum.IsDefined(preference);

    /// <summary>The language a choice amounts to. Windows in a language we do not have means English.</summary>
    private static CultureInfo Resolve(LanguagePreference preference) => preference switch
    {
        LanguagePreference.English => CultureInfo.GetCultureInfo("en"),
        LanguagePreference.German => CultureInfo.GetCultureInfo("de"),
        LanguagePreference.French => CultureInfo.GetCultureInfo("fr"),
        LanguagePreference.Spanish => CultureInfo.GetCultureInfo("es"),
        _ => WindowsLanguage.TwoLetterISOLanguageName switch
        {
            "de" or "fr" or "es" => CultureInfo.GetCultureInfo(WindowsLanguage.TwoLetterISOLanguageName),
            _ => CultureInfo.GetCultureInfo("en"),
        },
    };
}

/// <summary>
/// <c>{l:Loc Key}</c> in XAML: a live binding to the text, so a change of
/// language reaches the window without reopening it.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }
            .ProvideValue(serviceProvider);
}
