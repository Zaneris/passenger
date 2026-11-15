using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Gherkin;
using Passenger.Workspace.Gherkin;
using Xunit;

namespace Passenger.UnitTests.Gherkin;

/// <summary>The languages a .feature file is offered and read in, held to those Cucumber's Gherkin reader knows: its
/// languages are the strings its assembly holds that it takes as a language, so an update adding, dropping or keeping its
/// languages otherwise fails here.</summary>
public sealed class GherkinDialectsTests
{
    private static readonly Reader Asked = new();

    [Fact]
    public void The_languages_offered_are_exactly_those_the_gherkin_reader_knows()
    {
        HashSet<string> held = [.. StringsIn(typeof(GherkinDialectProvider).Assembly.Location).Where(Asked.Knows)];
        IReadOnlyList<string> offered = GherkinDialects.Languages;
        List<string> wrong = [];
        wrong.AddRange(held.Except(offered).Order(StringComparer.Ordinal)
            .Select(l => $"{l}: Gherkin's reader knows it, but # language: completion never offers it and a project naming it reads in English"));
        wrong.AddRange(offered.Where(l => !Asked.Knows(l))
            .Select(l => $"{l}: offered, but Gherkin's reader no longer knows it, so a file naming it fails to read"));
        wrong.AddRange(offered.Where(l => Asked.Knows(l) && !held.Contains(l))
            .Select(l => $"{l}: Gherkin's reader knows it but its assembly no longer names it, so a language an update adds would go unoffered"));
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.True(offered.SequenceEqual(offered.Order(StringComparer.Ordinal)), "# language: completion offers the languages out of order");
    }

    /// <summary>Every string an assembly's code writes, from its metadata's heap of them.</summary>
    private static List<string> StringsIn(string assembly)
    {
        using FileStream stream = File.OpenRead(assembly);
        using PEReader image = new(stream);
        MetadataReader metadata = image.GetMetadataReader();
        List<string> strings = [];
        for (UserStringHandle handle = metadata.GetNextHandle(MetadataTokens.UserStringHandle(0)); !handle.IsNil; handle = metadata.GetNextHandle(handle))
        {
            strings.Add(metadata.GetUserString(handle));
        }
        return strings;
    }

    /// <summary>The reader's own answer to whether it knows a language, without the exception its GetDialect throws for
    /// each it doesn't.</summary>
    private sealed class Reader() : GherkinDialectProvider(GherkinDialects.English)
    {
        public bool Knows(string language) => TryGetDialect(language, null, out GherkinDialect dialect) && dialect.Language == language;
    }
}
