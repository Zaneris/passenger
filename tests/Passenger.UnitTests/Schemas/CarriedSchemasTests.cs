using System.Text.Json;
using System.Text.RegularExpressions;

namespace Passenger.UnitTests.Schemas;

/// <summary>What the JSON schemas Passenger carries offer as values: completion offers a property's default as the value
/// it writes, so each default is one the schema offering it takes, and a path's is a path.</summary>
public sealed partial class CarriedSchemasTests
{
    [Fact]
    public void Every_default_a_carried_schema_offers_is_a_value_its_own_schema_takes()
    {
        List<string> walked = [];
        List<string> refused = [];
        foreach (string file in CarriedSchemas.All.Keys)
        {
            foreach ((string pointer, JsonElement schema) in CarriedSchemas.Parts(file))
            {
                if (schema.TryGetProperty("default", out JsonElement offered))
                {
                    walked.Add($"{file}#{pointer}");
                    refused.AddRange(CarriedSchemas.Refusals(offered, file, schema).Select(r => $"{file}#{pointer}: {r}"));
                }
            }
        }
        Assert.Contains("appsettings.json#/definitions/NLog/properties/internalLogToConsole", walked);
        Assert.True(refused.Count == 0, $"Completion offers defaults that the schema offering them marks as wrong once written:\n{string.Join("\n", refused)}");
    }

    [Fact]
    public void No_path_a_carried_schema_offers_by_default_is_a_sentence()
    {
        List<string> paths = [];
        List<string> sentences = [];
        foreach (string file in CarriedSchemas.All.Keys)
        {
            foreach ((string pointer, JsonElement schema) in CarriedSchemas.Parts(file))
            {
                string name = pointer[(pointer.LastIndexOf('/') + 1)..];
                if (pointer.EndsWith("/properties/" + name, StringComparison.Ordinal) && PathNamed().IsMatch(name))
                {
                    paths.Add($"{file}#{pointer}");
                    if (schema.TryGetProperty("default", out JsonElement offered) && offered.ValueKind == JsonValueKind.String && Words().IsMatch(offered.GetString()!))
                    {
                        sentences.Add($"{file}#{pointer}: {offered.GetString()}");
                    }
                }
            }
        }
        Assert.Contains("tsconfig.json#/definitions/tsNodeDefinition/properties/ts-node/anyOf/0/properties/scopeDir", paths);
        Assert.True(sentences.Count == 0, $"Completion offers words as a path, which the tool then takes as a folder's or file's name:\n{string.Join("\n", sentences)}");
    }

    /// <summary>A property's name that says it holds a path: a folder, file or root, alone or ending a camel-cased name.</summary>
    [GeneratedRegex("^(dir|directory|folder|path|file|root)s?$|[a-z](Dir|Directory|Folder|Path|File|Root)s?$")]
    private static partial Regex PathNamed();

    /// <summary>Words with white space between them, as a sentence has and a path seldom does.</summary>
    [GeneratedRegex(@"\w\s+\w")]
    private static partial Regex Words();
}
