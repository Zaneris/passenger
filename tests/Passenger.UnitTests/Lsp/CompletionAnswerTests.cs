using System.Text;
using System.Text.Json;
using Passenger.Lsp;

namespace Passenger.UnitTests.Lsp;

/// <summary>A completion answer read once from its bytes, in either of the protocol's shapes, its items given what the
/// list's defaults say of them, so a list Roslyn's server sends with its range and data said once reads as the same list
/// sent whole.</summary>
public class CompletionAnswerTests
{
    private const string Uri = "file:///work/src/Program.cs";

    [Fact]
    public void A_list_with_its_range_format_and_data_said_once_reads_as_the_same_list_sent_whole()
    {
        string range = """{"start":{"line":4,"character":8},"end":{"line":4,"character":10}}""";
        string data = $$$"""{"ResultId":3,"TextDocument":{"uri":"{{{Uri}}}"}}""";
        string whole = $$$"""
            {"isIncomplete":true,"itemDefaults":{"commitCharacters":[".",";"]},"items":[
              {"label":"StringBuilder","kind":7,"sortText":"0001","insertTextFormat":1,"textEdit":{"range":{{{range}}},"newText":"StringBuilder"},"data":{{{data}}}},
              {"label":"Stream","kind":7,"sortText":"0002","insertTextFormat":1,"textEdit":{"range":{{{range}}},"newText":"Stream"},"data":{{{data}}}},
              {"label":"override …","kind":2,"insertTextFormat":1,"textEdit":{"range":{{{range}}},"newText":"override "},"data":{{{data}}}},
              {"label":"Other","kind":5,"insertTextFormat":2,"textEdit":{"range":{"start":{"line":4,"character":0},"end":{"line":4,"character":10}},"newText":"Other"},"data":{"ResultId":4}}
            ]}
            """;
        string defaults = $$$"""
            {"isIncomplete":true,"itemDefaults":{"commitCharacters":[".",";"],"editRange":{{{range}}},"insertTextFormat":1,"data":{{{data}}}},"items":[
              {"label":"StringBuilder","kind":7,"sortText":"0001"},
              {"label":"Stream","kind":7,"sortText":"0002"},
              {"label":"override …","kind":2,"textEditText":"override "},
              {"label":"Other","kind":5,"insertTextFormat":2,"textEdit":{"range":{"start":{"line":4,"character":0},"end":{"line":4,"character":10}},"newText":"Other"},"data":{"ResultId":4}}
            ]}
            """;
        CompletionList sent = Read(whole);
        CompletionList said = Read(defaults);
        Assert.True(sent.IsIncomplete && said.IsIncomplete);
        Assert.Equal(sent.Items.Count, said.Items.Count);
        for (int i = 0; i < sent.Items.Count; i++)
        {
            CompletionItem a = sent.Items[i];
            CompletionItem b = said.Items[i];
            Assert.True(a.TextEdit == b.TextEdit, $"{a.Label}'s edit read as {b.TextEdit} from the list's defaults where the whole item says {a.TextEdit}: it would go in over another stretch");
            Assert.Equal(a.InsertTextFormat, b.InsertTextFormat);
            Assert.True(a.Data is { } sentData && b.Data is { } saidData && sentData.GetRawText().Replace(" ", "", StringComparison.Ordinal) == saidData.GetRawText().Replace(" ", "", StringComparison.Ordinal),
                $"{a.Label}'s data read as {b.Data} from the list's defaults where the whole item says {a.Data}: resolving it would find nothing");
        }
        Assert.Equal([".", ";"], said.ItemDefaults?.CommitCharacters);
    }

    [Fact]
    public void Each_item_reads_how_well_it_fits_from_the_runs_beside_the_list_and_never_sends_it_back()
    {
        int[] priorities = [0, 0, 600, 600, 600, 0, int.MaxValue / 2, int.MinValue / 2, 0];
        int[] runs = Passenger.Roslyn.Extension.CompletionPriorities.Runs(priorities.Length, i => priorities[i])!;
        Assert.Equal([0, 2, 600, 3, 0, 1, int.MaxValue / 2, 1, int.MinValue / 2, 1, 0, 1], runs);
        Assert.Null(Passenger.Roslyn.Extension.CompletionPriorities.Runs(4, _ => 0));
        string items = string.Join(",", priorities.Select((_, i) => $$"""{"label":"Item{{i}}"}"""));
        CompletionList list = Read($$"""{"isIncomplete":false,"matchPriorities":[{{string.Join(",", runs)}}],"items":[{{items}}]}""");
        Assert.Equal(priorities, list.Items.Select(i => i.MatchPriority));
        Assert.DoesNotContain("600", JsonSerializer.Serialize(list.Items[2], Json.Options), StringComparison.Ordinal);

        // Runs that say more or fewer items than the list holds say nothing of any.
        CompletionList miscounted = Read($$"""{"isIncomplete":false,"matchPriorities":[600,2],"items":[{{items}}]}""");
        Assert.All(miscounted.Items, i => Assert.Equal(0, i.MatchPriority));
        Assert.All(Read($$"""{"isIncomplete":false,"items":[{{items}}]}""").Items, i => Assert.Equal(0, i.MatchPriority));
    }

    /// <summary>A list asked again as a name is typed lists its items anew, each with that answer's sort text, range and
    /// data: an item is the one chosen before by what it is, and two of one name are told apart by what else the server
    /// says of them.</summary>
    [Fact]
    public void An_item_answered_again_is_the_same_item_whatever_its_answers_own_and_one_of_its_name_another()
    {
        CompletionList first = Read("""
            {"isIncomplete":true,"itemDefaults":{"editRange":{"start":{"line":4,"character":15},"end":{"line":4,"character":16}},"data":{"ResultId":3}},"items":[
              {"label":"Thrice","kind":2,"sortText":"0002"},
              {"label":"Twice","kind":2,"sortText":"0003"},
              {"label":"Stream","kind":7,"labelDetails":{"description":"System.IO"}}
            ]}
            """);
        CompletionList again = Read("""
            {"isIncomplete":true,"itemDefaults":{"editRange":{"start":{"line":4,"character":15},"end":{"line":4,"character":17}},"data":{"ResultId":4}},"items":[
              {"label":"Twice","kind":2,"sortText":"0001"},
              {"label":"Twice","kind":10,"sortText":"0002"},
              {"label":"Stream","kind":7,"labelDetails":{"description":"System.IO"}},
              {"label":"Stream","kind":7,"labelDetails":{"description":"Pipes"}}
            ]}
            """);
        Assert.True(CompletionItem.Same(first.Items[1], again.Items[0]),
            "Twice, the method, answered again with another sort text, range and data is another item: a choice on it would go to the list's first row");
        Assert.True(CompletionItem.Same(first.Items[2], again.Items[2]), "Stream of System.IO answered again is another item");
        Assert.False(CompletionItem.Same(first.Items[1], again.Items[1]), "Twice the method is the same item as a property named Twice");
        Assert.False(CompletionItem.Same(first.Items[2], again.Items[3]), "Stream of System.IO is the same item as a Stream of another namespace");
        Assert.False(CompletionItem.Same(first.Items[0], again.Items[0]), "Thrice is the same item as Twice");
        Assert.False(CompletionItem.Same(first.Items[0], null), "An item is the same as none");
        Assert.True(CompletionItem.Same(null, null), "No item is not the same as no item");
    }

    [Fact]
    public void An_answer_of_items_alone_or_of_nothing_reads_as_the_protocol_says()
    {
        CompletionList items = Read("""[{"label":"Main","kind":2}]""");
        Assert.False(items.IsIncomplete);
        Assert.Equal("Main", Assert.Single(items.Items).Label);
        Assert.Null(JsonSerializer.Deserialize<CompletionAnswer>("null", Json.Options));
    }

    private static CompletionList Read(string json) =>
        JsonSerializer.Deserialize<CompletionAnswer>(Encoding.UTF8.GetBytes(json), Json.Options)?.List ?? throw new InvalidOperationException("No list read");
}
