using System.Text.Json;

namespace Passenger.UnitTests.Schemas;

/// <summary>What the JSON schemas Passenger carries offer as values: completion offers a property's default as the value
/// it writes, so each default is one the schema offering it takes.</summary>
public sealed class CarriedSchemasTests
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
}
