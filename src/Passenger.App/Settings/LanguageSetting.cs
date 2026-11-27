using System.Globalization;
using Avalonia.Controls;
using Passenger.Core;

namespace Passenger.App.Settings;

/// <summary>The language a person reads Passenger in: the system's (null: its preferred languages followed to the first
/// Passenger carries, else English), English ("en"), or a language Passenger carries by its tag, each choice named in its
/// own language. A tag this build does not carry follows the system again.</summary>
public sealed class LanguageSetting(Func<AppSettings, string?> get, Func<AppSettings, string?, AppSettings> set, SettingRow row) : Setting(row)
{
    /// <summary>The tag English is kept as.</summary>
    public const string English = "en";

    /// <summary>The languages Passenger carries beside English, as its build found their translations.</summary>
    public static IReadOnlyList<CultureInfo> Carried { get; } = UiLanguage.CarriedBy(typeof(LanguageSetting).Assembly);

    /// <summary>The values a person may choose, in Settings' order: the system's, English, then each carried language.</summary>
    public static IReadOnlyList<string?> Values { get; } = [null, English, .. Carried.Select(c => c.Name)];

    /// <summary>The language a value puts in force, the system's languages read now for null.</summary>
    public static UiLanguage InForce(string? value) =>
        value is null ? UiLanguage.Choose(SystemLanguages.Read(), Carried) : UiLanguage.Of(value, Carried) ?? UiLanguage.English;

    /// <summary>A language's name in itself ("Deutsch", "हिन्दी"), English's own for English.</summary>
    public static Words NameOf(UiLanguage language) => language.Reached is { } reached
        ? Words.Verbatim(reached.TextInfo.ToTitleCase(reached.NativeName))
        : SettingsWords.LanguageEnglish;

    public override Control Editor(AppSettings now, Action<Func<AppSettings, AppSettings>> change)
    {
        Words[] names = [SettingsWords.LanguageSystem(NameOf(InForce(null))), .. Values.Skip(1).Select(v => NameOf(InForce(v)))];
        FormDropDown box = new()
        {
            ItemsSource = names.Select(n => n.Shown).ToArray(),
            SelectedIndex = Math.Max(0, Values.ToList().IndexOf(get(now))),
        };
        box.SelectionChanged += (_, _) => change(s => set(s, Values[Math.Max(0, box.SelectedIndex)]));
        return box;
    }

    public override AppSettings Clamp(AppSettings settings) => Values.Contains(get(settings)) ? settings : set(settings, null);

    public override AppSettings Other(AppSettings settings) => set(settings, get(settings) is null ? English : null);
}
