using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace.Gherkin;

/// <summary>How a step definition is written for a step that has none, as reqnroll.json's
/// <c>trace.stepDefinitionSkeletonStyle</c> names it.</summary>
public enum SkeletonStyle
{
    CucumberExpression,
    Regex,
    AsyncCucumberExpression,
    AsyncRegex,
}

/// <summary>What a Reqnroll project's reqnroll.json (or a SpecFlow project's specflow.json) says that the editor follows:
/// the language its features are written in where a file names none, the culture its bindings convert arguments in, how
/// a missing step's definition is written, and the assemblies besides its own that hold its bindings (reqnroll.json's
/// bindingAssemblies, specflow.json's stepAssemblies). A binding culture
/// named twice is bindingCulture's, which Reqnroll reads last.</summary>
public sealed record ReqnrollConfig(string? FeatureLanguage, string? BindingCulture, SkeletonStyle Skeleton, IReadOnlyList<string> BindingAssemblies, bool IsSpecFlow)
{
    /// <summary>Whether it was read from a file, rather than the defaults of a folder with none.</summary>
    public bool Written { get; init; }

    /// <summary>The culture a feature's steps' arguments are converted in, as Reqnroll's runtime takes it: the configuration's
    /// binding culture, else the language its generator writes the feature in, the one its own <c># language:</c> line names
    /// as Gherkin's reader names it ("de", "en-au"), else the project's feature language as written ("de-AT"), else en-US.</summary>
    public string BindingCultureOf(FeatureReading reading) => BindingCulture ?? (reading.NamesItsLanguage ? reading.Dialect.Language : FeatureLanguage ?? DefaultFeatureLanguage);

    /// <summary>The feature language Reqnroll takes where its configuration names none.</summary>
    public const string DefaultFeatureLanguage = "en-US";

    /// <summary>Why a file's reading, in this configuration's language or the one its <c># language:</c> line names, found no
    /// feature (<see cref="FeatureError.NoFeature"/>), as Reqnroll's generator refuses it: the language and whence, the words
    /// a feature begins with there, and how a file is read in the language it is written in.</summary>
    public string NoFeatureWhy(FeatureReading reading)
    {
        string reader = IsSpecFlow ? "SpecFlow" : "Reqnroll";
        string file = IsSpecFlow ? SpecFlowFile : ReqnrollFile;
        string begins = $"where a feature begins with {Plural.Either([.. reading.Dialect.FeatureKeywords.Select(k => $"{k}:")])}, and finds none";
        const string Codes = "as # language: en names English and # language: de German";
        if (reading.NamesItsLanguage)
        {
            return $"{reader} reads this file in {reading.Dialect.Language}, as its # language: line names, {begins}: "
                + $"name the language it is written in on that line, {Codes}";
        }
        string whence = FeatureLanguage is { } named ? $"{named}, the language.feature of its project's {file}"
            : Written ? $"{DefaultFeatureLanguage}, as its project's {file} names no language.feature"
            : $"{DefaultFeatureLanguage}, as its project has no {file} naming a language.feature";
        return $"{reader} reads this file in {whence}, {begins}: name the language it is written in on its first line, {Codes}, "
            + $"or the language the project's features are written in as language.feature in its {file}";
    }

    /// <summary>How a project's missing step is written: as its file says, else as its framework writes one by default
    /// (Reqnroll a Cucumber Expression, SpecFlow a regular expression).</summary>
    public SkeletonStyle SkeletonFor(bool specFlow) => Written ? Skeleton : specFlow ? SkeletonStyle.Regex : SkeletonStyle.CucumberExpression;

    public const string ReqnrollFile = "reqnroll.json";
    public const string SpecFlowFile = "specflow.json";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Each configuration file read, by its path, with the write time and length it was read at.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (FileStamp Stamp, ReqnrollConfig Read)> Kept = new(PathNames.Comparer);

    /// <summary>A project folder's configuration: its reqnroll.json, else its specflow.json, else Reqnroll's defaults; a file
    /// read again only once it changed, as every typing pause in a feature asks. For any thread.</summary>
    public static ReqnrollConfig In(string folder)
    {
        foreach ((string name, bool specFlow) in new[] { (ReqnrollFile, false), (SpecFlowFile, true) })
        {
            string path = Path.Combine(folder, name);
            if (FileStamp.Of(path) is not { } stamp)
            {
                continue;
            }
            if (Kept.TryGetValue(path, out (FileStamp Stamp, ReqnrollConfig Read) kept) && kept.Stamp == stamp)
            {
                return kept.Read;
            }
            try
            {
                ReqnrollConfig read = Parse(File.ReadAllText(path), specFlow) with { Written = true };
                Kept[path] = (stamp, read);
                return read;
            }
            catch (IOException)
            {
                return Parse("{}", specFlow);
            }
            catch (UnauthorizedAccessException)
            {
                return Parse("{}", specFlow);
            }
        }
        return Parse("{}", specFlow: false);
    }

    /// <summary>A configuration file's text; one that does not read as JSON says nothing.</summary>
    public static ReqnrollConfig Parse(string json, bool specFlow)
    {
        SkeletonStyle fallback = specFlow ? SkeletonStyle.Regex : SkeletonStyle.CucumberExpression;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, Lenient);
            JsonElement root = document.RootElement;
            JsonElement? language = JsonMembers.Object(root, "language", anyCase: true);
            string? style = JsonMembers.Text(JsonMembers.Object(root, "trace", anyCase: true), "stepDefinitionSkeletonStyle", anyCase: true);
            IEnumerable<JsonElement> assemblies = JsonMembers.Items(root, "bindingAssemblies", anyCase: true)
                .Concat(JsonMembers.Items(JsonMembers.Object(root, "stepAssemblies", anyCase: true), "stepAssemblies", anyCase: true))
                .Concat(JsonMembers.Items(root, "stepAssemblies", anyCase: true));
            return new ReqnrollConfig(
                Named(JsonMembers.Text(language, "feature", anyCase: true)),
                Named(JsonMembers.Text(JsonMembers.Object(root, "bindingCulture", anyCase: true), "name", anyCase: true)) ?? Named(JsonMembers.Text(language, "binding", anyCase: true)),
                style is not null && Enum.TryParse(style.Replace("Attribute", "", StringComparison.Ordinal), ignoreCase: true, out SkeletonStyle named) ? named : fallback,
                [.. assemblies.Select(a => JsonMembers.Text(a, "assembly", anyCase: true)).OfType<string>()],
                specFlow);
        }
        catch (JsonException)
        {
            return new ReqnrollConfig(null, null, fallback, [], specFlow);
        }
    }

    /// <summary>A culture's name as Reqnroll takes one: blank is none.</summary>
    private static string? Named(string? written) => string.IsNullOrWhiteSpace(written) ? null : written.Trim();
}

/// <summary>The language a project's .feature files are read in where a file names none: its configuration's.</summary>
public static class FeatureLanguage
{
    /// <summary>The language of a project folder's features (English where it names none, or with no folder), read from
    /// its configuration on disk: off the window's thread.</summary>
    public static string Of(string? projectFolder) =>
        GherkinDialects.Default(projectFolder is null ? null : ReqnrollConfig.In(projectFolder).FeatureLanguage);
}
