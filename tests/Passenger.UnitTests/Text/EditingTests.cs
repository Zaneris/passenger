using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Editing commands as the plans they make, at the text's start and end, with several carets and with CRLF breaks:
/// the text and carets each leaves (<see cref="Marked"/>), its edits as a document takes them.</summary>
public class EditingTests
{
    private static readonly IndentOptions Spaces = new(4, UseTabs: false, 4);

    [Theory]
    [InlineData("a| b", "(", "a(|) b")]
    [InlineData("a|b", "(", "a(|b")]
    [InlineData("|", "[", "[|]")]
    [InlineData("f(|)", ")", "f()|")]
    [InlineData("«word|", "\"", "\"«word|\"")]
    [InlineData("|word»", "(", "(«word|)")]
    [InlineData("it|s", "'", "it'|s")]
    [InlineData("x = |;", "\"", "x = \"|\";")]
    [InlineData("a|\r\nb|", "(", "a(|)\r\nb(|)")]
    [InlineData("|a|b|", "x", "x|ax|bx|")]
    public void Typing_pairs_brackets_and_quotes_steps_over_a_closer_and_wraps_a_selection(string before, string typed, string after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Type(text, selections, typed)));
    }

    [Theory]
    [InlineData("(|)", "|")]
    [InlineData("\"|\"", "|")]
    [InlineData("one\r\n|two", "one|two")]
    [InlineData("one\n|two", "one|two")]
    [InlineData("|abc", null)]
    [InlineData("        |x", "    |x")]
    [InlineData("      |x", "    |x")]
    [InlineData("  a  |x", "  a |x")]
    [InlineData("x\U0001F600|", "x|")]
    [InlineData("a|b|c", "|c")]
    [InlineData("«abc|d|", "|")]
    public void Backspace_takes_a_pair_a_break_whole_an_indentation_level_or_a_character_and_merges_carets_that_meet(string before, string? after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Backspace(text, selections, Spaces)));
    }

    [Theory]
    [InlineData("abc|", null)]
    [InlineData("one|\r\ntwo", "one|two")]
    [InlineData("|\U0001F600x", "|x")]
    [InlineData("|éx", "|x")]
    [InlineData("|ab|cd", "|b|d")]
    public void Delete_takes_the_character_or_grapheme_after_each_caret_and_a_CRLF_break_whole(string before, string? after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Delete(text, selections)));
    }

    [Theory]
    [InlineData("    if (x) {|}", "\r\n", "    if (x) {\r\n        |\r\n    }")]
    [InlineData("    call(|", "\n", "    call(\n        |")]
    [InlineData("    a|   b", "\n", "    a\n    |b")]
    [InlineData("|", "\r\n", "\r\n|")]
    [InlineData("a|\nb|", "\n", "a\n|\nb\n|")]
    [InlineData("    one|  |  two", "\n", "    one\n    |\n    |two")]
    [InlineData("first« |« |second", "\n", "first\n|\n|second")]
    [InlineData("{\n    first();\n|    second();\n}", "\n", "{\n    first();\n\n    |second();\n}")]
    [InlineData("{\n\n  |  second();\n}", "\n", "{\n\n  \n    |second();\n}")]
    [InlineData("\t«x|y", "\n", "\t\n\t|y")]
    public void Enter_breaks_the_line_in_the_documents_ending_keeping_its_indentation_and_one_level_more_after_an_opener(string before, string eol, string after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.NewLine(text, selections, eol, Spaces)));
    }

    [Theory]
    [InlineData("    <StackPanel>\n        <Border>|</Border>", MarkupKind.Xml, "    <StackPanel>\n        <Border>\n            |\n        </Border>")]
    [InlineData("        <Button Content=\"One\"|", MarkupKind.Xml, "        <Button Content=\"One\"\n                |")]
    [InlineData("<Grid>\n    <Button Content=\"One\"\n            Width=\"80\" />|", MarkupKind.Xml, "<Grid>\n    <Button Content=\"One\"\n            Width=\"80\" />\n    |")]
    [InlineData("<head>\n    <meta charset=\"utf-8\">|\n</head>", MarkupKind.Html, "<head>\n    <meta charset=\"utf-8\">\n    |\n</head>")]
    [InlineData("<ul>\n    <li>One\n    <li>Two</li>|", MarkupKind.Html, "<ul>\n    <li>One\n    <li>Two</li>\n    |")]
    [InlineData("<ul>\n    <li>Four|</ul>", MarkupKind.Html, "<ul>\n    <li>Four\n|</ul>")]
    public void Enter_in_markup_indents_by_the_tags_in_from_a_start_tag_under_a_first_attribute_and_beside_a_closed_element(string before, MarkupKind markup,
        string after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.NewLine(text, selections, "\n", Spaces, markup)));
    }

    [Theory]
    [InlineData("one\ntw|o", "on|e")]
    [InlineData("|one\r\ntwo", "|two")]
    [InlineData("one\n|two\nthree", "one\n|three")]
    [InlineData("a|a|a\nbbb", "b|b|b")]
    [InlineData("«one\ntwo\n|three", "|three")]
    [InlineData("on|ly", "|")]
    public void Deleting_lines_takes_each_touched_line_once_with_its_break_and_the_last_the_break_before_it(string before, string after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.DeleteLines(text, selections)));
    }

    [Theory]
    [InlineData("one\n|two", true, "|two\none")]
    [InlineData("o|ne\ntwo", false, "two\no|ne")]
    [InlineData("|one\ntwo", true, null)]
    [InlineData("one\ntw|o", false, null)]
    [InlineData("a\r\n|b\r\nc", false, "a\r\nc\r\n|b")]
    [InlineData("a\n|b\nc|\nd", true, "|b\nc|\na\nd")]
    public void Moving_lines_swaps_them_with_their_neighbour_carets_going_with_them_and_does_nothing_at_an_edge(string before, bool up, string? after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.MoveLines(text, selections, before.Contains('\r') ? "\r\n" : "\n", up)));
    }

    [Theory]
    [InlineData("one\ntw|o", "one\ntwo\ntw|o")]
    [InlineData("|one\r\ntwo", "one\r\n|one\r\ntwo")]
    [InlineData("a«bc|d", "abc«bc|d")]
    public void Duplicating_copies_the_caret_line_below_it_or_a_selection_after_itself(string before, string after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        string eol = before.Contains('\r') ? "\r\n" : "\n";
        Assert.Equal(after, Marked.After(text, TextEditing.Duplicate(text, selections, eol)));
    }

    [Fact]
    public void Commenting_lines_puts_the_prefix_at_their_shallowest_indentation_and_again_takes_it_back_off()
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse("«    a\r\n\r\n  b|");
        EditPlan commented = TextEditing.ToggleLineComment(text, selections, "//")!;
        Assert.Equal("«  //   a\r\n\r\n  // b|", Marked.After(text, commented));
        TextSnapshot after = text.Apply(commented.Edits);
        Assert.Equal("«    a\r\n\r\n  b|", Marked.After(after, TextEditing.ToggleLineComment(after, commented.Selections, "//")));
        Assert.Null(TextEditing.ToggleLineComment(after, commented.Selections, "//", add: true));
        (TextSnapshot blank, SelectionSet caret) = Marked.Parse("x\n  |\ny");
        Assert.Null(TextEditing.ToggleLineComment(blank, caret, "//"));
    }

    [Theory]
    [InlineData("a «bc| d", "a «/*bc*/| d")]
    [InlineData("x|y", "x/*|*/y")]
    [InlineData("/* |copies src/*.cs and docs/*|.md */\nint x;\n", "|copies src/*.cs and docs/*|.md\nint x;\n")]
    [InlineData("a /* b| */ c", "a b| c")]
    public void Block_comment_wraps_a_selection_or_takes_the_ends_off_the_comment_a_caret_is_in_once_for_all_its_carets(string before, string after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.ToggleBlockComment(text, selections, "/*", "*/")));
    }

    [Fact]
    public void A_line_copied_with_nothing_selected_pastes_back_whole_above_the_caret_line()
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse("first\nsec|ond");
        (string copied, bool wholeLines) = TextEditing.Copy(text, selections, "\n");
        Assert.Equal(("second\n", true), (copied, wholeLines));
        Assert.Equal("first\nsecond\nsec|ond", Marked.After(text, TextEditing.Paste(text, selections, copied, wholeLines, "\n")));
        (TextSnapshot two, SelectionSet carets) = Marked.Parse("«ab|c\nd«e|f");
        Assert.Equal(("ab\ne", false), TextEditing.Copy(two, carets, "\n"));
    }

    [Fact]
    public void An_XML_line_comment_wraps_each_line_and_unwraps_it_again()
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse("  <a />|");
        EditPlan commented = TextEditing.ToggleLineComment(text, selections, "<!--", "-->")!;
        TextSnapshot after = text.Apply(commented.Edits);
        Assert.Equal("  <!--<a />-->", after.ToString());
        Assert.Equal("  <a />", after.Apply(TextEditing.ToggleLineComment(after, commented.Selections, "<!--", "-->")!.Edits).ToString());
    }

    [Theory]
    [InlineData("|\n|", "a\nb", false, "a|\nb|")]
    [InlineData("|\r\n|", "a\nb", false, "a|\r\nb|")]
    [InlineData("x|y", "1\r2", false, "x1\n2|y")]
    [InlineData("one\nt|wo", "new\n", true, "one\nnew\nt|wo")]
    [InlineData("|a|b", "z", false, "z|az|b")]
    public void Paste_deals_a_line_to_each_caret_puts_whole_lines_above_and_takes_any_line_break_as_the_documents(string before, string clipboard, bool wholeLines, string after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        string eol = before.Contains('\r') ? "\r\n" : "\n";
        Assert.Equal(after, Marked.After(text, TextEditing.Paste(text, selections, clipboard, wholeLines, eol)));
    }

    [Fact]
    public void Saving_trims_trailing_space_but_on_a_caret_line_ends_with_a_break_and_makes_every_break_one()
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse("a  \r\nb  |\nc\t");
        EditPlan plan = TextEditing.Saving(text.ToString(), selections, trimTrailing: true, finalNewline: true, convertTo: "\n", eol: "\r\n")!;
        Assert.Equal("a\nb  |\nc\n", Marked.After(text, plan));
        Assert.Null(TextEditing.Saving("tidy\n", selections.Clamp(5), trimTrailing: true, finalNewline: true, convertTo: "\n", eol: "\n"));
        EditPlan ended = TextEditing.Saving("x", SelectionSet.Single(0), trimTrailing: false, finalNewline: true, convertTo: null, eol: "\r\n")!;
        Assert.Equal("x\r\n", TextSnapshot.From("x").Apply(ended.Edits).ToString());
    }

    [Theory]
    [InlineData("a|\n    b", "a |b")]
    [InlineData("a|\n\n   \nb", "a|\n   \nb")]
    [InlineData("«a\n\n   \n  b|", "«a b|")]
    [InlineData("«a\nb\nc|", "«a b c|")]
    [InlineData("last|", null)]
    public void Joining_lines_meets_the_next_lines_text_with_one_space_and_drops_blank_lines(string before, string? after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.JoinLines(text, selections)));
    }

    [Theory]
    [InlineData("o|ne\r\ntwo", "o|\r\ntwo", "ne")]
    [InlineData("one|\r\ntwo", "one|two", "\r\n")]
    [InlineData("one|", null, "")]
    public void Cutting_to_the_line_end_takes_the_rest_of_the_line_or_at_its_end_the_whole_break(string before, string? after, string removed)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        (EditPlan? plan, string taken) = TextEditing.CutToLineEnd(text, selections);
        Assert.Equal(after, Marked.After(text, plan));
        Assert.Equal(removed, taken);
    }

    [Fact]
    public void Carets_at_one_place_merge_and_a_caret_at_a_selections_edge_joins_it_but_two_selections_that_meet_stay_apart()
    {
        SelectionSet same = SelectionSet.Create([Selection.At(3), Selection.At(3)], 1);
        Assert.Equal(1, same.Count);
        SelectionSet edge = SelectionSet.Create([new Selection(0, 3), Selection.At(3)], 1);
        Assert.Equal([new Selection(0, 3)], edge.Items);
        SelectionSet meeting = SelectionSet.Create([new Selection(0, 3), new Selection(3, 5)], 0);
        Assert.Equal(2, meeting.Count);
        SelectionSet backwards = SelectionSet.Create([new Selection(5, 2), Selection.At(4)], 0);
        Assert.Equal([new Selection(5, 2)], backwards.Items);
    }

    [Fact]
    public void Offsets_carried_through_edits_go_before_or_after_text_inserted_at_them_and_to_a_removals_start()
    {
        TextEdit[] edits = [new(2, 0, "xx"), new(5, 3, "y")];
        Assert.Equal(2, TextEditing.MapOffset(2, edits, afterInsertions: false));
        Assert.Equal(4, TextEditing.MapOffset(2, edits, afterInsertions: true));
        Assert.Equal(7, TextEditing.MapOffset(6, edits, afterInsertions: false));
        Assert.Equal(8, TextEditing.MapOffset(6, edits, afterInsertions: true));
        Assert.Equal(9, TextEditing.MapOffset(9, edits, afterInsertions: false));
        Assert.Equal(TextRange.FromBounds(7, 8), TextEditing.MapRange(TextRange.FromBounds(5, 8), edits));
    }
}
