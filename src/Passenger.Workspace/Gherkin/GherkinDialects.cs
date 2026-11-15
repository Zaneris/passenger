using System.Reflection;
using Gherkin;

namespace Passenger.Workspace.Gherkin;

/// <summary>The languages Gherkin's keywords are written in, as Cucumber's own reader knows them, and the one a file is
/// read in: its <c># language:</c> line, else the project's (reqnroll.json's <c>language.feature</c>), a culture with a
/// region falling back to its language as Reqnroll's does ("de-AT" to "de"), else English.</summary>
public static class GherkinDialects
{
    public const string English = "en";

    /// <summary>Every language's code ("de", "en-old"), read from the reader's own dialect factory, whose methods are named
    /// after them: it lists them nowhere else.</summary>
    public static IReadOnlyList<string> Languages { get; } = [.. typeof(GherkinDialectProvider)
        .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        .Select(m => m.Name)
        .Where(n => n.StartsWith(DialectFactoryPrefix, StringComparison.Ordinal))
        .Select(n => n[DialectFactoryPrefix.Length..].Replace('_', '-'))
        .Distinct(StringComparer.Ordinal)
        .Where(Knows)
        .Order(StringComparer.Ordinal)];

    private const string DialectFactoryPrefix = "CreateGherkinDialectFor_";

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

    private static bool Knows(string language)
    {
        try
        {
            return new GherkinDialectProvider(English).GetDialect(language, null) is not null;
        }
        catch (NoSuchLanguageException)
        {
            return false;
        }
    }
}
