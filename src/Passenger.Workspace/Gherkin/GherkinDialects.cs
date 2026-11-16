using Gherkin;
using Gherkin.Ast;

namespace Passenger.Workspace.Gherkin;

/// <summary>The languages Gherkin's keywords are written in, as Cucumber's own reader knows them, and the one a file is
/// read in: its <c># language:</c> line, else the project's (reqnroll.json's <c>language.feature</c>), a culture with a
/// region falling back to its language as Reqnroll's does ("de-AT" to "de"), else English.</summary>
public static class GherkinDialects
{
    public const string English = "en";

    /// <summary>Every language's code the reader knows ("de", "en-old"), in ordinal order: written out, since the reader
    /// lists them nowhere, and held to the reader's own by GherkinDialectsTests.</summary>
    public static IReadOnlyList<string> Languages { get; } =
    [
        "af", "am", "amh", "an", "ar", "ast", "az", "be", "bg", "bm", "bs", "ca", "cs", "cy-GB", "da", "de", "el", "em", "en",
        "en-Scouse", "en-au", "en-lol", "en-old", "en-pirate", "en-tx", "eo", "es", "et", "fa", "fi", "fr", "ga", "gj", "gl",
        "he", "hi", "hr", "ht", "hu", "id", "is", "it", "ja", "jv", "ka", "kn", "ko", "lt", "lu", "lv", "mk-Cyrl", "mk-Latn",
        "ml", "mn", "mr", "ne", "nl", "no", "pa", "pl", "pt", "ro", "ru", "sk", "sl", "sr-Cyrl", "sr-Latn", "sv", "ta", "te",
        "th", "tlh", "tr", "tt", "uk", "ur", "uz", "vi", "zh-CN", "zh-TW",
    ];

    /// <summary>The language a file with no <c># language:</c> line is read in, from what the project names.</summary>
    public static string Default(string? named)
    {
        if (string.IsNullOrWhiteSpace(named))
        {
            return English;
        }
        string language = named.Trim();
        if (Knows(language))
        {
            return language;
        }
        int dash = language.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && Knows(language[..dash]) ? language[..dash] : English;
    }

    /// <summary>A language's keywords; English's for one Gherkin doesn't know.</summary>
    public static GherkinDialect Of(string language) => Knows(language) ? new GherkinDialectProvider(language).DefaultDialect : new GherkinDialectProvider(English).DefaultDialect;

    private static bool Knows(string language) => Languages.Contains(language, StringComparer.Ordinal);

    /// <summary>The languages a file's <c># language:</c> line names, as Reqnroll's reader takes them: one with a region the
    /// reader doesn't know in its language's keywords under the name written ("de-AT" as "de"); one it doesn't know at all
    /// refused as the reader refuses it.</summary>
    public sealed class ReqnrollProvider(string defaultLanguage) : GherkinDialectProvider(defaultLanguage)
    {
        public override GherkinDialect GetDialect(string language, Location? location)
        {
            if (!language.Contains('-', StringComparison.Ordinal))
            {
                return base.GetDialect(language, location);
            }
            if (TryGetDialect(language, location, out GherkinDialect regional))
            {
                return regional;
            }
            GherkinDialect d = base.GetDialect(language.Split('-')[0], location);
            return new GherkinDialect(language, d.FeatureKeywords, d.RuleKeywords, d.BackgroundKeywords, d.ScenarioKeywords, d.ScenarioOutlineKeywords,
                d.ExamplesKeywords, d.GivenStepKeywords, d.WhenStepKeywords, d.ThenStepKeywords, d.AndStepKeywords, d.ButStepKeywords);
        }
    }
}
