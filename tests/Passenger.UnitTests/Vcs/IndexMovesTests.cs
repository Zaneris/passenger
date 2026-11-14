using System.Text;
using Passenger.Vcs;

namespace Passenger.UnitTests.Vcs;

/// <summary>Moves made in an index as git mv makes them, planned from what ls-files wrote: each moved path's entry, or
/// each below a moved folder, goes with its mode and object (its staged content), in the moves' order, while what the
/// index lacks and what is in conflict stay as they are.</summary>
public class IndexMovesTests
{
    private const string Shop = "1111111111111111111111111111111111111111";
    private const string Line = "2222222222222222222222222222222222222222";
    private const string Price = "3333333333333333333333333333333333333333";
    private const string Run = "4444444444444444444444444444444444444444";
    private const string Removed = "0 0000000000000000000000000000000000000000";

    private static readonly string Index = Listed(
        $"100644 {Shop} 0\tsrc/Shop.cs",
        $"100644 {Line} 0\tsrc/Lines/Line.cs",
        $"100644 {Price} 0\tsrc/Lines/Price.cs",
        $"100755 {Run} 0\tsrc/Lines2/run.sh",
        $"100644 {Price} 0\tsrc/Lines.cs");

    [Fact]
    public void A_moved_file_takes_its_mode_and_object_to_its_new_path()
    {
        Assert.Equal([$"{Removed}\tsrc/Shop.cs", $"100644 {Shop}\tsrc/Store.cs"], Plan(Index, ("src/Shop.cs", "src/Store.cs")));
    }

    [Fact]
    public void A_moved_folder_takes_every_entry_below_it_and_none_of_a_sibling_whose_name_begins_alike()
    {
        Assert.Equal(
        [
            $"{Removed}\tsrc/Lines/Line.cs",
            $"{Removed}\tsrc/Lines/Price.cs",
            $"100644 {Line}\tsrc/Rows/Line.cs",
            $"100644 {Price}\tsrc/Rows/Price.cs",
        ], Plan(Index, ("src/Lines", "src/Rows")));
    }

    [Fact]
    public void Moves_are_taken_in_order_so_a_later_one_moves_what_an_earlier_one_brought()
    {
        Assert.Equal([$"{Removed}\tsrc/Shop.cs", $"100644 {Shop}\tsrc/Shelves/Till.cs"],
            Plan(Index, ("src/Shop.cs", "src/Store.cs"), ("src/Store.cs", "src/Shelf/Store.cs"), ("src/Shelf", "src/Shelves"), ("src/Shelves/Store.cs", "src/Shelves/Till.cs")));
    }

    [Fact]
    public void Two_files_trading_places_are_each_given_the_others_entry()
    {
        Assert.Equal([$"100644 {Shop}\tsrc/Lines.cs", $"100644 {Price}\tsrc/Shop.cs"],
            Plan(Index, ("src/Shop.cs", "src/Swap.cs"), ("src/Lines.cs", "src/Shop.cs"), ("src/Swap.cs", "src/Lines.cs")));
    }

    [Fact]
    public void A_path_the_index_lacks_plans_nothing()
    {
        Assert.Empty(Plan(Index, ("src/Draft.cs", "src/Kept.cs"), ("notes", "docs")));
    }

    [Fact]
    public void A_folder_with_a_file_in_conflict_stays_where_it_is_while_the_other_moves_go_on()
    {
        string conflicted = Index + Listed($"100644 {Line} 1\tsrc/Lines/Both.cs", $"100644 {Price} 2\tsrc/Lines/Both.cs");

        Assert.Equal([$"{Removed}\tsrc/Shop.cs", $"100644 {Shop}\tsrc/Store.cs"],
            Plan(conflicted, ("src/Lines", "src/Rows"), ("src/Shop.cs", "src/Store.cs")));
    }

    [Fact]
    public void A_name_beyond_ASCII_is_planned_as_the_index_writes_it()
    {
        string index = Listed($"100644 {Shop} 0\tsrc/Café.cs");

        Assert.Equal([$"{Removed}\tsrc/Café.cs", $"100644 {Shop}\tsrc/Crêpe.cs"], Plan(index, ("src/Café.cs", "src/Crêpe.cs")));
    }

    [Fact]
    public void Twelve_hundred_files_moved_one_by_one_are_planned_together()
    {
        string index = Listed([.. Enumerable.Range(0, 1200).Select(i => $"100644 {Shop} 0\tMany/F{i:D4}.cs")]);

        string[] plan = Plan(index, [.. Enumerable.Range(0, 1200).Select(i => ($"Many/F{i:D4}.cs", $"Moved/F{i:D4}.cs"))]);

        Assert.Equal(2400, plan.Length);
        Assert.Equal(1200, plan.Count(line => line.StartsWith($"100644 {Shop}\tMoved/F", StringComparison.Ordinal)));
    }

    private static string Listed(params string[] entries) => string.Concat(entries.Select(e => e + "\0"));

    private static string[] Plan(string index, params (string From, string To)[] moves) =>
        IndexMoves.Plan(Encoding.UTF8.GetBytes(index), moves).Split('\0', StringSplitOptions.RemoveEmptyEntries);
}
