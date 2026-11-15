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
/// a missing step's definition is written, and the assemblies besides its own that hold its bindings. A binding culture
/// named twice is bindingCulture's, which Reqnroll reads last.</summary>
public sealed record ReqnrollConfig(string? FeatureLanguage, string? BindingCulture, SkeletonStyle Skeleton, IReadOnlyList<string> BindingAssemblies, bool IsSpecFlow)
{
    public const string ReqnrollFile = "reqnroll.json";
    public const string SpecFlowFile = "specflow.json";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>A project folder's configuration: its reqnroll.json, else its specflow.json, else Reqnroll's defaults.</summary>
    public static ReqnrollConfig In(string folder)
    {
        foreach ((string name, bool specFlow) in new[] { (ReqnrollFile, false), (SpecFlowFile, true) })
        {
            string path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                try
                {
                    return Parse(File.ReadAllText(path), specFlow);
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
            return new ReqnrollConfig(
                JsonMembers.Text(language, "feature", anyCase: true),
                JsonMembers.Text(JsonMembers.Object(root, "bindingCulture", anyCase: true), "name", anyCase: true) ?? JsonMembers.Text(language, "binding", anyCase: true),
                style is not null && Enum.TryParse(style.Replace("Attribute", "", StringComparison.Ordinal), ignoreCase: true, out SkeletonStyle named) ? named : fallback,
                [.. JsonMembers.Items(root, "bindingAssemblies", anyCase: true).Select(a => JsonMembers.Text(a, "assembly", anyCase: true)).OfType<string>()],
                specFlow);
        }
        catch (JsonException)
        {
            return new ReqnrollConfig(null, null, fallback, [], specFlow);
        }
    }
}

/// <summary>The language a project's .feature files are read in where a file names none: its configuration's.</summary>
public static class FeatureLanguage
{
    /// <summary>The language of a project folder's features (English where it names none, or with no folder), read from
    /// its configuration on disk: off the window's thread.</summary>
    public static string Of(string? projectFolder) =>
        GherkinDialects.Default(projectFolder is null ? null : ReqnrollConfig.In(projectFolder).FeatureLanguage);
}
