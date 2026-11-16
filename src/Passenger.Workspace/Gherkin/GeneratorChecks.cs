namespace Passenger.Workspace.Gherkin;

/// <summary>What Reqnroll's code-behind generator refuses a feature for once Gherkin's reader reads it without a mistake, in
/// its words and at the places it names: two scenarios of one name (at the second, a rule's scenarios among the feature's),
/// two of an outline's examples of one name (at the second), an outline with an examples block of no rows, and a column
/// header written twice in an examples table (each at the outline).</summary>
internal static class GeneratorChecks
{
    public static IEnumerable<FeatureError> Of(FeatureReading reading)
    {
        if (reading.Feature is null)
        {
            yield break;
        }
        FeatureScenario[] scenarios = [.. reading.Scenarios.Where(s => s.Kind != ScenarioKind.Background)];
        foreach (IGrouping<string, FeatureScenario> named in scenarios.GroupBy(s => s.Title, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            FeatureScenario second = named.ElementAt(1);
            yield return Refused(second.Line, second.KeywordColumn, $"Feature file already contains a scenario with name '{named.Key}'");
        }
        FeatureScenario[] outlines = [.. scenarios.Where(s => s.Kind == ScenarioKind.Outline)];
        foreach (FeatureScenario outline in outlines)
        {
            foreach (IGrouping<string, FeatureExamples> named in outline.Examples.Where(e => !string.IsNullOrWhiteSpace(e.Title))
                .GroupBy(e => e.Title, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                FeatureExamples second = named.ElementAt(1);
                yield return Refused(second.Line, second.KeywordColumn, $"Scenario Outline '{outline.Title}' already contains an example with name '{named.Key}'");
            }
        }
        foreach (FeatureScenario outline in outlines)
        {
            if (outline.Examples.Any(e => e.Rows.Count == 0))
            {
                yield return Refused(outline.Line, outline.KeywordColumn, $"Scenario Outline '{outline.Title}' has no examples defined");
            }
        }
        foreach (FeatureScenario outline in outlines)
        {
            foreach (FeatureExamples examples in outline.Examples)
            {
                foreach (IGrouping<string, FeatureCell> header in (examples.Header?.Cells ?? []).GroupBy(c => c.Value, StringComparer.Ordinal).Where(g => g.Count() > 1))
                {
                    yield return Refused(outline.Line, outline.KeywordColumn, $"Scenario Outline '{outline.Title}' already contains an example column with header '{header.Key}'");
                }
            }
        }
    }

    private static FeatureError Refused(int line, int column, string message) => new(line, column, message) { ByGenerator = true };
}
