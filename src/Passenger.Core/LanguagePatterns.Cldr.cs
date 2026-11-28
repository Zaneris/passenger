// Written by tools/cldr.py from the Unicode CLDR 49 (main/<language>.xml, under the Unicode License v3; see
// THIRD_PARTY_NOTICES.md). Run it again rather than edit this.
namespace Passenger.Core;

public static partial class LanguagePatterns
{
    /// <summary>A language's list patterns by its tag, joining by and then by or: each a start, a middle, an end and a pair's;
    /// null for a language CLDR's patterns are not carried of.</summary>
    private static string[]? Lists(string language) => language switch
    {
        "de" => ["{0}, {1}", "{0}, {1}", "{0} und {1}", "{0} und {1}", "{0}, {1}", "{0}, {1}", "{0} oder {1}", "{0} oder {1}"],
        "es" => ["{0}, {1}", "{0}, {1}", "{0} y {1}", "{0} y {1}", "{0}, {1}", "{0}, {1}", "{0} o {1}", "{0} o {1}"],
        "fr" => ["{0}, {1}", "{0}, {1}", "{0} et {1}", "{0} et {1}", "{0}, {1}", "{0}, {1}", "{0} ou {1}", "{0} ou {1}"],
        "hi" => ["{0}, {1}", "{0}, {1}", "{0} और {1}", "{0} और {1}", "{0}, {1}", "{0}, {1}", "{0} या {1}", "{0} या {1}"],
        "it" => ["{0}, {1}", "{0}, {1}", "{0} e {1}", "{0} e {1}", "{0}, {1}", "{0}, {1}", "{0} o {1}", "{0} o {1}"],
        "pa" => ["{0}, {1}", "{0}, {1}", "{0} ਅਤੇ {1}", "{0} ਅਤੇ {1}", "{0}, {1}", "{0}, {1}", "{0} ਜਾਂ {1}", "{0} ਜਾਂ {1}"],
        "pt" => ["{0}, {1}", "{0}, {1}", "{0} e {1}", "{0} e {1}", "{0}, {1}", "{0}, {1}", "{0} ou {1}", "{0} ou {1}"],
        "zh" => ["{0}、{1}", "{0}、{1}", "{0}和{1}", "{0}和{1}", "{0}、{1}", "{0}、{1}", "{0}或{1}", "{0}或{1}"],
        _ => null,
    };

    /// <summary>A language's patterns of a unit by its tag, each plural category before its pattern; null for a language
    /// CLDR's patterns are not carried of.</summary>
    private static string[]? Units(string language, string unit) => (language, unit) switch
    {
        ("de", "millisecond") => ["other", "{0} ms"],
        ("de", "second") => ["other", "{0} Sek."],
        ("de", "minute") => ["one", "{0} Minute", "other", "{0} Minuten"],
        ("de", "hour") => ["one", "{0} Stunde", "other", "{0} Stunden"],
        ("de", "byte") => ["one", "{0}\u00A0Byte", "other", "{0} Byte"],
        ("de", "kilobyte") => ["other", "{0} kB"],
        ("de", "megabyte") => ["other", "{0} MB"],
        ("de", "gigabyte") => ["other", "{0}\u00A0GB"],
        ("es", "millisecond") => ["other", "{0} ms"],
        ("es", "second") => ["other", "{0} s"],
        ("es", "minute") => ["one", "{0} minuto", "other", "{0} minutos"],
        ("es", "hour") => ["one", "{0} hora", "other", "{0} horas"],
        ("es", "byte") => ["other", "{0} B"],
        ("es", "kilobyte") => ["other", "{0} kB"],
        ("es", "megabyte") => ["other", "{0} MB"],
        ("es", "gigabyte") => ["other", "{0} GB"],
        ("fr", "millisecond") => ["other", "{0}\u202Fms"],
        ("fr", "second") => ["other", "{0}\u202Fs"],
        ("fr", "minute") => ["one", "{0} minute", "other", "{0} minutes"],
        ("fr", "hour") => ["one", "{0}\u00A0heure", "other", "{0}\u00A0heures"],
        ("fr", "byte") => ["other", "{0}\u202Fo"],
        ("fr", "kilobyte") => ["other", "{0}\u202Fko"],
        ("fr", "megabyte") => ["other", "{0}\u202FMo"],
        ("fr", "gigabyte") => ["other", "{0}\u202FGo"],
        ("hi", "millisecond") => ["other", "{0} मि॰से॰"],
        ("hi", "second") => ["other", "{0} से॰"],
        ("hi", "minute") => ["one", "{0} मिनट", "other", "{0} मिनट"],
        ("hi", "hour") => ["one", "{0} घंटा", "other", "{0} घंटे"],
        ("hi", "byte") => ["other", "{0} बाइट"],
        ("hi", "kilobyte") => ["other", "{0} kB"],
        ("hi", "megabyte") => ["other", "{0} MB"],
        ("hi", "gigabyte") => ["other", "{0} GB"],
        ("it", "millisecond") => ["other", "{0} ms"],
        ("it", "second") => ["other", "{0} s"],
        ("it", "minute") => ["one", "{0} minuto", "other", "{0} minuti"],
        ("it", "hour") => ["one", "{0} ora", "other", "{0} ore"],
        ("it", "byte") => ["other", "{0} byte"],
        ("it", "kilobyte") => ["other", "{0} kB"],
        ("it", "megabyte") => ["other", "{0} MB"],
        ("it", "gigabyte") => ["other", "{0} GB"],
        ("pa", "millisecond") => ["other", "{0} ਮਿਲੀਸਕਿੰਟ"],
        ("pa", "second") => ["other", "{0} ਸਕਿੰਟ"],
        ("pa", "minute") => ["other", "{0} ਮਿੰਟ"],
        ("pa", "hour") => ["one", "{0} ਘੰਟਾ", "other", "{0} ਘੰਟੇ"],
        ("pa", "byte") => ["other", "{0} ਬਾਇਟ"],
        ("pa", "kilobyte") => ["other", "{0} kB"],
        ("pa", "megabyte") => ["other", "{0} MB"],
        ("pa", "gigabyte") => ["other", "{0} GB"],
        ("pt", "millisecond") => ["other", "{0} ms"],
        ("pt", "second") => ["other", "{0} s"],
        ("pt", "minute") => ["one", "{0} minuto", "other", "{0} minutos"],
        ("pt", "hour") => ["one", "{0} hora", "other", "{0} horas"],
        ("pt", "byte") => ["other", "{0} bytes"],
        ("pt", "kilobyte") => ["other", "{0} kB"],
        ("pt", "megabyte") => ["other", "{0} MB"],
        ("pt", "gigabyte") => ["other", "{0} GB"],
        ("zh", "millisecond") => ["other", "{0}毫秒"],
        ("zh", "second") => ["other", "{0}秒"],
        ("zh", "minute") => ["other", "{0}分钟"],
        ("zh", "hour") => ["other", "{0}小时"],
        ("zh", "byte") => ["other", "{0} B"],
        ("zh", "kilobyte") => ["other", "{0} kB"],
        ("zh", "megabyte") => ["other", "{0} MB"],
        ("zh", "gigabyte") => ["other", "{0} GB"],
        _ => null,
    };
}
