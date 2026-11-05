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
    [InlineData("<StackPanel>\n    <Grid>|\n</StackPanel>", ">", MarkupKind.Xml, "<StackPanel>\n    <Grid>|</Grid>\n</StackPanel>")]
    [InlineData("<Grid.RowDefinitions>|", ">", MarkupKind.Xml, "<Grid.RowDefinitions>|</Grid.RowDefinitions>")]
    [InlineData("<local:Card Title=\"a > b\">|", ">", MarkupKind.Xml, "<local:Card Title=\"a > b\">|</local:Card>")]
    [InlineData("<StackPanel><Grid>|<Grid></Grid></StackPanel>", ">", MarkupKind.Xml, "<StackPanel><Grid>|</Grid><Grid></Grid></StackPanel>")]
    [InlineData("<a>|\n<b>|", ">", MarkupKind.Xml, "<a>|</a>\n<b>|</b>")]
    [InlineData("<div>|", ">", MarkupKind.Html, "<div>|</div>")]
    [InlineData("<ul>\n    <li>|\n    <li>Two</li>\n</ul>", ">", MarkupKind.Html, "<ul>\n    <li>|</li>\n    <li>Two</li>\n</ul>")]
    [InlineData("<StackPanel>\n    <Grid>\n    </|", "/", MarkupKind.Xml, "<StackPanel>\n    <Grid>\n    </Grid>|")]
    [InlineData("<Grid><Button /></|>", "/", MarkupKind.Xml, "<Grid><Button /></Grid>|")]
    [InlineData("<Grid><Border></|</Border></Grid>", "/", MarkupKind.Xml, "<Grid><Border></|Border></Grid>")]
    [InlineData("<div><p>One</|", "/", MarkupKind.Html, "<div><p>One</p>|")]
    public void Typing_a_start_tags_close_writes_its_end_tag_after_the_caret_and_an_end_tags_slash_the_open_elements_name(string before, string typed,
        MarkupKind markup, string after)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.CloseTag(text, selections, typed, markup)));
    }

    [Theory]
    [InlineData("<Grid />|", ">", MarkupKind.Xml)]
    [InlineData("<StackPanel></Grid>|", ">", MarkupKind.Xml)]
    [InlineData("<Grid>|\n    <Button />\n</Grid>", ">", MarkupKind.Xml)]
    [InlineData("<!-- <Grid>| -->", ">", MarkupKind.Xml)]
    [InlineData("<Grid Tag=\"<b>|\"", ">", MarkupKind.Xml)]
    [InlineData("<Root><![CDATA[ <Grid>| ]]></Root>", ">", MarkupKind.Xml)]
    [InlineData("<?xml version=\"1.0\"?>|", ">", MarkupKind.Xml)]
    [InlineData("<Grid>|", ">", MarkupKind.None)]
    [InlineData("<br>|", ">", MarkupKind.Html)]
    [InlineData("<!DOCTYPE html>|", ">", MarkupKind.Html)]
    [InlineData("<script>if (a <b>|", ">", MarkupKind.Html)]
    [InlineData("<div>|<p>Text</div>", ">", MarkupKind.Html)]
    [InlineData("</|", "/", MarkupKind.Xml)]
    [InlineData("<Grid></|Grid>", "/", MarkupKind.Xml)]
    [InlineData("<Grid>|", "x", MarkupKind.Xml)]
    public void No_end_tag_is_written_for_a_self_closed_or_empty_element_one_closed_already_or_a_comment_value_cdata_or_script(string before, string typed, MarkupKind markup)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Null(Marked.After(text, TextEditing.CloseTag(text, selections, typed, markup)));
    }

    [Theory]
    [InlineData("<div>x|</div>", "</div>", MarkupKind.Html, "<div>x</div>|")]
    [InlineData("<div>|</div>", "</div>", MarkupKind.Html, "<div></div>|")]
    [InlineData("<div><p>|</p></div>", "</p></div>", MarkupKind.Html, "<div><p></p></div>|")]
    [InlineData("<Grid.Row>x|</Grid.Row>", "</Grid.Row>", MarkupKind.Xml, "<Grid.Row>x</Grid.Row>|")]
    [InlineData("<a>|</a>\n<a>|</a>", "</a>", MarkupKind.Xml, "<a></a>|\n<a></a>|")]
    [InlineData("<a>|", "</a>", MarkupKind.Xml, "<a></a>|")]
    [InlineData("<div>x\n</", "div>", MarkupKind.Html, "<div>x\n</div>|", "|")]
    public void An_end_tag_typed_key_by_key_over_the_one_written_leaves_one_end_tag_the_caret_after_it(string before, string keys, MarkupKind markup, string after, string caretAt = "")
    {
        Assert.Equal(after, TypedKeyByKey(before + caretAt, caretAt.Length > 0 ? "/" + keys : keys, markup, caretAt.Length > 0));
    }

    [Theory]
    [InlineData("<div>x|</div>", "</span", "<div>x</span|</div>")]
    [InlineData("<div>x|</div>", "</dix", "<div>x</dix|</div>")]
    [InlineData("<div>x|</div>", "<span", "<div>x<span|</div>")]
    [InlineData("<div>|</div>", "</divx>", "<div></divx>|</div>")]
    public void A_key_that_differs_from_the_end_tag_being_typed_over_lands_as_typed_the_tag_left_whole(string before, string keys, string after)
    {
        Assert.Equal(after, TypedKeyByKey(before, keys, MarkupKind.Html));
    }

    [Theory]
    [InlineData("<p>hi</", "/ and", "<p>hi</p> and|")]
    [InlineData("<p>hi</", "/pal", "<p>hi</p>pal|")]
    public void Words_typed_after_an_end_tag_a_slash_wrote_go_after_it_a_letter_shared_with_its_name_included(string before, string keys, string after)
    {
        Assert.Equal(after, TypedKeyByKey(before + "|", keys, MarkupKind.Html, slashFirst: true));
    }

    [Fact]
    public void Backspace_while_an_end_tag_is_typed_over_takes_back_a_key_and_never_a_letter_of_the_tag()
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse("<div>x</|</div>");
        EditPlan joined = TextEditing.CloseTag(text, selections, "/", MarkupKind.Html)!;
        EndTagTyping typing = joined.EndTags!;
        TextSnapshot over = text.Apply(joined.Edits);
        Assert.Equal("<div>x</|div>", Marked.Show(over, joined.Selections));
        SelectionSet past = typing.Past(joined.Selections, "d");
        EndTagTyping passed = typing.After("d")!;
        Assert.Equal("<div>x</d|iv>", Marked.Show(over, past));
        Assert.True(passed.CanStepBack);
        SelectionSet back = passed.Back(over, past);
        Assert.Equal("<div>x</|div>", Marked.Show(over, back));
        Assert.False(passed.StepBack(over, past).CanStepBack);
    }

    /// <summary>Keys typed one at a time as the editor takes them: passing the end tag typed over, else that tag left whole
    /// and the key typed, closing what it closes. With <paramref name="slashFirst"/> the text ends in the "&lt;" of an end tag
    /// typed already, and the first key is its '/'.</summary>
    private static string TypedKeyByKey(string before, string keys, MarkupKind markup, bool slashFirst = false)
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        EndTagTyping? typing = null;
        foreach (char key in keys)
        {
            string typed = key.ToString();
            if (typing is not null && typing.Passes(typed))
            {
                selections = typing.Past(selections, typed);
                typing = typing.After(typed);
                continue;
            }
            if (typing?.PutBack(selections) is { } back)
            {
                text = text.Apply(back.Edits);
                selections = back.Selections;
            }
            typing = null;
            if (!(slashFirst && key == '/' && text[selections.Primary.Caret - 1] == '/'))
            {
                EditPlan plan = TextEditing.Type(text, selections, typed);
                text = text.Apply(plan.Edits);
                selections = plan.Selections;
            }
            if (TextEditing.CloseTag(text, selections, typed, markup) is { } closing)
            {
                text = text.Apply(closing.Edits);
                selections = closing.Selections;
                typing = closing.EndTags;
            }
        }
        return Marked.Show(text, selections);
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
