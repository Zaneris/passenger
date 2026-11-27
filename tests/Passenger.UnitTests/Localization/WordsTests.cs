using System.Globalization;
using Passenger.Core;

namespace Passenger.UnitTests.Localization;

/// <summary>Words said in a language (<see cref="Words"/>) over this project's own TestWords.resx, translated into French
/// and, its greeting alone, Punjabi in Gurmukhi (xlf/TestWords.*.xlf, made satellites as the project builds): the nearest
/// carried language of .NET's chain, never across a change of script, else English; the system's languages followed to
/// the first that reaches one; a message's arguments, plurals by CLDR's rules and numbers in the asked culture; a
/// translation waiting or naming other arguments giving way to the English; and the pseudo-locale.</summary>
public class WordsTests
{
    private static readonly IReadOnlyList<CultureInfo> Carried = UiLanguage.CarriedBy(typeof(WordsTests).Assembly);

    private static UiLanguage In(string tag) => UiLanguage.Of(tag, Carried) ?? throw new InvalidOperationException($"{tag} reaches no carried language");

    [Fact]
    public void Words_are_said_in_the_nearest_carried_language_of_their_chain_with_its_plurals_and_numbers() => Cases.All(
        ("the build carries each language its translations name", () => Assert.True(Carried.Select(c => c.Name).SequenceEqual(["fr", "pa"]),
            $"The test project carries [{string.Join(", ", Carried.Select(c => c.Name))}], where its xlf files name fr and pa: the build's satellites or the generator's attribute missed one")),
        ("a culture reaches the nearest carried language of its chain, never across a change of script, else English", A_culture_reaches_the_nearest_carried_language_never_across_a_script),
        ("the system's languages are followed to the first that reaches a carried one", The_systems_languages_are_followed_to_the_first_that_reaches_a_carried_one),
        ("a message says its arguments, plurals and numbers in its language", A_message_says_its_arguments_plurals_and_numbers_in_its_language),
        ("a translation waiting or naming other arguments gives way to the English", A_translation_waiting_or_naming_other_arguments_gives_way_to_the_English),
        ("the pseudo-locale accents, lengthens and brackets a resource's words and leaves names alone", The_pseudo_locale_accents_lengthens_and_brackets_words_and_leaves_names_alone));

    private static void A_culture_reaches_the_nearest_carried_language_never_across_a_script() => Cases.Each(
    [
        ("fr-CA", "Bonjour"),
        ("fr", "Bonjour"),
        ("pa-IN", "ਸਤ ਸ੍ਰੀ ਅਕਾਲ"),
        ("pa-Guru-IN", "ਸਤ ਸ੍ਰੀ ਅਕਾਲ"),
        ("pa-Arab-PK", "Hello"),
        ("zh-Hant-TW", "Hello"),
        ("ja-JP", "Hello"),
        ("en-GB", "Hello"),
    ], row =>
    {
        (string tag, string greeting) = row;
        UiLanguage language = UiLanguage.Choose([tag], Carried);
        Assert.True(TestWords.Greeting.In(language) == greeting, $"{tag} reads the greeting as \"{TestWords.Greeting.In(language)}\", where its nearest "
            + $"carried language of the same script says \"{greeting}\": a person whose system asks {tag} reads another language's words, or none of their own");
    }, row => row.Item1);

    private static void The_systems_languages_are_followed_to_the_first_that_reaches_a_carried_one()
    {
        UiLanguage french = UiLanguage.Choose(["ja-JP", "fr-CA", "en-US"], Carried);
        Assert.True(french.Reached?.Name == "fr" && french.Asked.Name == "fr-CA",
            $"A system preferring Japanese, then Canadian French, chose {french.Tag} reaching {french.Reached?.Name ?? "English"}, where Japanese is not carried and fr-CA reaches fr");
        UiLanguage english = UiLanguage.Choose(["en-US", "fr"], Carried);
        Assert.True(english.IsEnglish, $"A system preferring English before French chose {english.Reached?.Name}, where its first choice is English");
        UiLanguage none = UiLanguage.Choose(["ja"], Carried);
        Assert.True(none.IsEnglish && none.Asked.Name == "ja",
            $"A system preferring only Japanese chose {none.Reached?.Name ?? "English"} asked as {none.Asked.Name}: English words, its asked culture Japanese for its fonts");
    }

    private static void A_message_says_its_arguments_plurals_and_numbers_in_its_language()
    {
        UiLanguage canada = In("fr-CA");
        string Grouped(double number) => number.ToString("#,0.###", canada.Asked);
        Cases.Each(
        [
            ("no file, in English", TestWords.FilesFound(0).English, "No file found"),
            ("one file, in English", TestWords.FilesFound(1).English, "1 file found"),
            ("1,500 files, in English", TestWords.FilesFound(1500).English, "1,500 files found"),
            ("no file, in French", TestWords.FilesFound(0).In(canada), "Aucun fichier trouvé"),
            ("1.5 files, singular in French", TestWords.FilesFound(1.5).In(canada), $"{Grouped(1.5)} fichier trouvé"),
            ("a million files, French's many", TestWords.FilesFound(1_000_000).In(canada), $"{Grouped(1_000_000)} de fichiers trouvés"),
            ("a name and words nested, in French", TestWords.Stopped(TestWords.NotAProject("Api"), "disk full").In(canada), "Api n'est pas un projet s'est arrêté : disk full"),
            ("a select's case", TestWords.Moved("folder").In(canada), "Le dossier a été déplacé"),
            ("a select's other", TestWords.Moved("link").English, "It moved"),
            ("numbers in English", TestWords.Measured(1234.5, 7.6).English, "1,234.5 of 8"),
            ("numbers in Canadian French", TestWords.Measured(1234.5, 7.6).In(canada), $"{Grouped(1234.5)} sur 8"),
            ("apostrophes quoting and standing for themselves", TestWords.Quoted.English, "The folder's {braces} and it's quoted"),
            ("verbatim words in French", ((Words)"Api.csproj").In(canada), "Api.csproj"),
        ], row => Assert.True(row.Item2 == row.Item3, $"{row.Item1} reads \"{row.Item2}\", not \"{row.Item3}\""), row => row.Item1);
    }

    private static void A_translation_waiting_or_naming_other_arguments_gives_way_to_the_English()
    {
        UiLanguage french = In("fr");
        Cases.Each(
        [
            ("a unit not yet translated", TestWords.NotYetTranslated.In(french), "Not yet translated"),
            ("a unit translated as its English", TestWords.EnglishOnly.In(french), "Only in English"),
            ("a translation older than its English, still shown until reviewed", TestWords.EnglishChanged.In(french), "Mots dont l'anglais a changé"),
            ("a translation older than its English and naming another argument", TestWords.Renamed("Api").In(french), "Api was renamed"),
        ], row => Assert.True(row.Item2 == row.Item3, $"{row.Item1} reads \"{row.Item2}\" in French, not \"{row.Item3}\""), row => row.Item1);
    }

    private static void The_pseudo_locale_accents_lengthens_and_brackets_words_and_leaves_names_alone()
    {
        UiLanguage pseudo = In(UiLanguage.PseudoTag);
        string greeting = TestWords.Greeting.In(pseudo);
        Assert.True(greeting.StartsWith("[Ĥéļļö", StringComparison.Ordinal) && greeting.EndsWith(']') && greeting.Length >= "[Hello]".Length + 2,
            $"The pseudo-locale says \"Hello\" as \"{greeting}\", where it accents each letter, makes the words 40% longer and brackets them");
        string named = TestWords.NotAProject("Api").In(pseudo);
        Assert.True(named.StartsWith("[Api ", StringComparison.Ordinal), $"The pseudo-locale says a project's name as \"{named}\": a name is said as written");
        Assert.True(In(UiLanguage.MirroredTag).RightToLeft && !pseudo.RightToLeft, "The mirrored pseudo-locale lays windows out right to left, and the other left to right");
    }

    [Fact]
    public void Each_languages_plural_categories_are_CLDRs() => Cases.Each(
    [
        ("en", 1.0, 0, "one"), ("en", 0.0, 0, "other"), ("en", 1.0, 1, "other"),
        ("fr", 0.0, 0, "one"), ("fr", 1.5, 1, "one"), ("fr", 2.0, 0, "other"), ("fr", 1_000_000.0, 0, "many"),
        ("pt", 0.0, 0, "one"), ("pt-PT", 0.0, 0, "other"), ("hi", 0.0, 0, "one"), ("pa", 1.0, 0, "one"), ("pa", 2.0, 0, "other"),
        ("zh-Hans", 1.0, 0, "other"), ("ru", 2.0, 0, "few"), ("ru", 5.0, 0, "many"), ("ru", 21.0, 0, "one"),
        ("ar", 0.0, 0, "zero"), ("ar", 2.0, 0, "two"), ("ar", 103.0, 0, "few"), ("ar", 11.0, 0, "many"), ("ar", 100.0, 0, "other"),
    ], row =>
    {
        (string tag, double count, int places, string category) = row;
        string found = PluralRules.Of(CultureInfo.GetCultureInfo(tag), count, places);
        Assert.True(found == category, $"{tag} takes {count} written with {places} places as {found}, where CLDR's rules say {category}");
    }, row => $"{row.Item1} {row.Item2}");
}
