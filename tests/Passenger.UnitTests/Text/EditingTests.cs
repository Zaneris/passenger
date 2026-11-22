using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Editing commands as the plans they make, at the text's start and end, with several carets and with CRLF breaks:
/// the text and carets each leaves (<see cref="Marked"/>), its edits as a document takes them.</summary>
public class EditingTests
{
    private static readonly IndentOptions Spaces = new(4, UseTabs: false, 4);

    [Fact]
    public void Typing_pairs_brackets_and_quotes_and_steps_over_a_closer() => Cases.All(
        ("Typing pairs brackets and quotes steps over a closer and wraps a selection", Typing_pairs_brackets_and_quotes_steps_over_a_closer_and_wraps_a_selection),
        ("A closer typed where only white space and a line break stand before the codes own types over it", A_closer_typed_where_only_white_space_and_a_line_break_stand_before_the_codes_own_types_over_it),
        ("Enter between a pair says where its brackets stand and elsewhere says none", Enter_between_a_pair_says_where_its_brackets_stand_and_elsewhere_says_none));

    private void Typing_pairs_brackets_and_quotes_steps_over_a_closer_and_wraps_a_selection() => Cases.Each<(string before, string typed, string after)>(
    [
        ("a| b", "(", "a(|) b"),
        ("|", "[", "[|]"),
        ("«word|", "\"", "\"«word|\""),
        ("|word»", "(", "(«word|)"),
        ("x = |;", "\"", "x = \"|\";"),
        ("|a|b|", "x", "x|ax|bx|"),
    ], row =>
    {
        (string before, string typed, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Type(text, selections, typed)));
    });

    private void A_closer_typed_where_only_white_space_and_a_line_break_stand_before_the_codes_own_types_over_it() => Cases.Each<(string before, string typed, bool whole, string after)>(
    [
        ("{\n    x();\n    |\n}", "}", true, "{\n    x();\n}|"),
        ("{\n    x();\n    |\n\n  }", "}", true, "{\n    x();\n  }|"),
        ("call(\n    a,\n    |\n)", ")", true, "call(\n    a,\n)|"),
        ("{\n    x();\n    |\n}", "}", false, "{\n    x();\n    }|\n}"),
        ("{ x(); |  }", "}", true, "{ x(); }|  }"),
        ("{\r\n    x();\r\n    |\r\n}", "}", true, "{\r\n    x();\r\n}|"),
    ], row =>
    {
        (string before, string typed, bool whole, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Type(text, selections, typed, _ => whole)));
    });

    private void Enter_between_a_pair_says_where_its_brackets_stand_and_elsewhere_says_none()
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse("  {|}");
        Assert.Equal([new SplitPair(2, 13)], TextEditing.NewLine(text, selections, "\n", Spaces).Split);
        (text, selections) = Marked.Parse("  {|x}");
        Assert.Empty(TextEditing.NewLine(text, selections, "\n", Spaces).Split);
    }

    [Fact]
    public void Backspace_and_delete_take_a_pair_a_break_an_indentation_level_or_a_grapheme_whole() => Cases.All(
        ("Backspace takes a pair a break whole an indentation level or a character and merges carets that meet", Backspace_takes_a_pair_a_break_whole_an_indentation_level_or_a_character_and_merges_carets_that_meet),
        ("Delete takes the character or grapheme after each caret and a CRLF break whole", Delete_takes_the_character_or_grapheme_after_each_caret_and_a_CRLF_break_whole));

    private void Backspace_takes_a_pair_a_break_whole_an_indentation_level_or_a_character_and_merges_carets_that_meet() => Cases.Each<(string before, string? after)>(
    [
        ("(|)", "|"),
        ("one\r\n|two", "one|two"),
        ("|abc", null),
        ("      |x", "    |x"),
        ("x\U0001F600|", "x|"),
        ("«abc|d|", "|"),
    ], row =>
    {
        (string before, string? after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Backspace(text, selections, Spaces)));
    });

    private void Delete_takes_the_character_or_grapheme_after_each_caret_and_a_CRLF_break_whole() => Cases.Each<(string before, string? after)>(
    [
        ("abc|", null),
        ("one|\r\ntwo", "one|two"),
        ("|\U0001F600x", "|x"),
        ("|éx", "|x"),
        ("|ab|cd", "|b|d"),
    ], row =>
    {
        (string before, string? after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Delete(text, selections)));
    });

    [Fact]
    public void Enter_breaks_the_line_keeping_its_indentation_and_in_markup_by_the_tags_in() => Cases.All(
        ("Enter breaks the line in the documents ending keeping its indentation and one level more after an opener", Enter_breaks_the_line_in_the_documents_ending_keeping_its_indentation_and_one_level_more_after_an_opener),
        ("Enter in markup indents by the tags in from a start tag under a first attribute and beside a closed element", Enter_in_markup_indents_by_the_tags_in_from_a_start_tag_under_a_first_attribute_and_beside_a_closed_element));

    private void Enter_breaks_the_line_in_the_documents_ending_keeping_its_indentation_and_one_level_more_after_an_opener() => Cases.Each<(string before, string eol, string after)>(
    [
        ("    if (x) {|}", "\r\n", "    if (x) {\r\n        |\r\n    }"),
        ("    a|   b", "\n", "    a\n    |b"),
        ("a|\nb|", "\n", "a\n|\nb\n|"),
        ("    one|  |  two", "\n", "    one\n    |\n    |two"),
        ("{\n    first();\n|    second();\n}", "\n", "{\n    first();\n\n    |second();\n}"),
        ("\t«x|y", "\n", "\t\n\t|y"),
    ], row =>
    {
        (string before, string eol, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.NewLine(text, selections, eol, Spaces)));
    });

    private void Enter_in_markup_indents_by_the_tags_in_from_a_start_tag_under_a_first_attribute_and_beside_a_closed_element() => Cases.Each<(string before, MarkupKind markup, string after)>(
    [
        ("    <StackPanel>\n        <Border>|</Border>", MarkupKind.Xml, "    <StackPanel>\n        <Border>\n            |\n        </Border>"),
        ("        <Button Content=\"One\"|", MarkupKind.Xml, "        <Button Content=\"One\"\n                |"),
        ("<Grid>\n    <Button Content=\"One\"\n            Width=\"80\" />|", MarkupKind.Xml, "<Grid>\n    <Button Content=\"One\"\n            Width=\"80\" />\n    |"),
        ("<head>\n    <meta charset=\"utf-8\">|\n</head>", MarkupKind.Html, "<head>\n    <meta charset=\"utf-8\">\n    |\n</head>"),
        ("<ul>\n    <li>One\n    <li>Two</li>|", MarkupKind.Html, "<ul>\n    <li>One\n    <li>Two</li>\n    |"),
        ("<ul>\n    <li>Four|</ul>", MarkupKind.Html, "<ul>\n    <li>Four\n|</ul>"),
    ], row =>
    {
        (string before, MarkupKind markup, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.NewLine(text, selections, "\n", Spaces, markup)));
    });

    [Fact]
    public void A_start_tags_close_writes_its_end_tag_and_one_typed_over_or_joined_leaves_one() => Cases.All(
        ("Typing a start tags close writes its end tag after the caret and an end tags slash the open elements name", Typing_a_start_tags_close_writes_its_end_tag_after_the_caret_and_an_end_tags_slash_the_open_elements_name),
        ("No end tag is written for a self closed or empty element one closed already or a comment value cdata or script", No_end_tag_is_written_for_a_self_closed_or_empty_element_one_closed_already_or_a_comment_value_cdata_or_script),
        ("An end tag typed key by key over the one written leaves one end tag the caret after it", An_end_tag_typed_key_by_key_over_the_one_written_leaves_one_end_tag_the_caret_after_it),
        ("A key that differs from the end tag being typed over lands as typed the tag left whole", A_key_that_differs_from_the_end_tag_being_typed_over_lands_as_typed_the_tag_left_whole),
        ("A slash above the elements written end tag joins it and undo puts back what was typed", A_slash_above_the_elements_written_end_tag_joins_it_and_undo_puts_back_what_was_typed),
        ("An end tag an element of its name holding the open one needs is left to it and the open one gets its own", An_end_tag_an_element_of_its_name_holding_the_open_one_needs_is_left_to_it_and_the_open_one_gets_its_own),
        ("An end tag whose element is the one open is joined with elements of its name around it", An_end_tag_whose_element_is_the_one_open_is_joined_with_elements_of_its_name_around_it),
        ("Backspace while an end tag is typed over takes back a key and never a letter of the tag", Backspace_while_an_end_tag_is_typed_over_takes_back_a_key_and_never_a_letter_of_the_tag),
        ("Backspace over an end tag joined from above steps back to the slash and then puts back what stood there", Backspace_over_an_end_tag_joined_from_above_steps_back_to_the_slash_and_then_puts_back_what_stood_there));

    private void Typing_a_start_tags_close_writes_its_end_tag_after_the_caret_and_an_end_tags_slash_the_open_elements_name() => Cases.Each<(string before, string typed, MarkupKind markup, string after)>(
    [
        ("<StackPanel>\n    <Grid>|\n</StackPanel>", ">", MarkupKind.Xml, "<StackPanel>\n    <Grid>|</Grid>\n</StackPanel>"),
        ("<local:Card Title=\"a > b\">|", ">", MarkupKind.Xml, "<local:Card Title=\"a > b\">|</local:Card>"),
        ("<a>|\n<b>|", ">", MarkupKind.Xml, "<a>|</a>\n<b>|</b>"),
        ("<ul>\n    <li>|\n    <li>Two</li>\n</ul>", ">", MarkupKind.Html, "<ul>\n    <li>|</li>\n    <li>Two</li>\n</ul>"),
        ("<Grid><Button /></|>", "/", MarkupKind.Xml, "<Grid><Button /></Grid>|"),
        ("<div><p>One</|", "/", MarkupKind.Html, "<div><p>One</p>|"),
    ], row =>
    {
        (string before, string typed, MarkupKind markup, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.CloseTag(text, selections, typed, markup)));
    });

    private void No_end_tag_is_written_for_a_self_closed_or_empty_element_one_closed_already_or_a_comment_value_cdata_or_script() => Cases.Each<(string before, string typed, MarkupKind markup)>(
    [
        ("<Grid />|", ">", MarkupKind.Xml),
        ("<!-- <Grid>| -->", ">", MarkupKind.Xml),
        ("<?xml version=\"1.0\"?>|", ">", MarkupKind.Xml),
        ("<br>|", ">", MarkupKind.Html),
        ("<div>|<p>Text</div>", ">", MarkupKind.Html),
        ("<Grid>|", "x", MarkupKind.Xml),
    ], row =>
    {
        (string before, string typed, MarkupKind markup) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Null(Marked.After(text, TextEditing.CloseTag(text, selections, typed, markup)));
    });

    private void An_end_tag_typed_key_by_key_over_the_one_written_leaves_one_end_tag_the_caret_after_it() => Cases.Each<(string before, string keys, MarkupKind markup, string after, string caretAt)>(
    [
        ("<div>x|</div>", "</div>", MarkupKind.Html, "<div>x</div>|", ""),
        ("<Grid.Row>x|</Grid.Row>", "</Grid.Row>", MarkupKind.Xml, "<Grid.Row>x</Grid.Row>|", ""),
        ("<a>|", "</a>", MarkupKind.Xml, "<a></a>|", ""),
        ("<div>\r\n    x\r\n    |  \r\n\r\n</div>\r\n", "</div>", MarkupKind.Html, "<div>\r\n    x\r\n</div>|\r\n", ""),
        ("<div>x|\n</div>", "</div>", MarkupKind.Html, "<div>x</div>|", ""),
        ("<a>\n  |\n</a>\n<a>\n    |\n</a>", "</a>", MarkupKind.Xml, "<a>\n</a>|\n<a>\n</a>|", ""),
    ], row =>
    {
        (string before, string keys, MarkupKind markup, string after, string caretAt) = row;
        Assert.Equal(after, TypedKeyByKey(before + caretAt, caretAt.Length > 0 ? "/" + keys : keys, markup, caretAt.Length > 0));
    });

    private void A_key_that_differs_from_the_end_tag_being_typed_over_lands_as_typed_the_tag_left_whole() => Cases.Each<(string before, string keys, string after)>(
    [
        ("<div>x|</div>", "</span", "<div>x</span|</div>"),
        ("<div>x|</div>", "<span", "<div>x<span|</div>"),
        ("<div>\n    x\n    |\n</div>", "</span", "<div>\n    x\n    </span|\n</div>"),
        ("<div>\n    x\n    |\n</div>", "</dix", "<div>\n    x\n    </dix|\n</div>"),
        ("<div>\n|\n    </div>", "</dix", "<div>\n</dix|\n    </div>"),
        ("<a>\n  |\n</a>\n<a>\n    |\n</a>", "</ax", "<a>\n  </ax|\n</a>\n<a>\n    </ax|\n</a>"),
    ], row =>
    {
        (string before, string keys, string after) = row;
        Assert.Equal(after, TypedKeyByKey(before, keys, MarkupKind.Html));
    });

    private void A_slash_above_the_elements_written_end_tag_joins_it_and_undo_puts_back_what_was_typed() => Cases.Each<(string typed, string joined)>(
    [
        ("<div>\n    </|\n</div>", "<div>\n</|div>"),
        ("<div>\n        </|\n\n    </div>", "<div>\n    </|div>"),
        ("<div>\n</|\n    </div>", "<div>\n    </|div>"),
        ("<div>x</|\n</div>", "<div>x</|div>"),
        ("<div>    </|\n</div>", "<div>    </|div>"),
    ], row =>
    {
        (string typed, string joined) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(typed);
        TextDocument document = new(text.ToString());
        EditPlan plan = TextEditing.CloseTag(document.Snapshot, selections, "/", MarkupKind.Html)!;
        document.Edit(plan.Edits, selections, plan.Selections, plan.Kind);
        Assert.Equal(joined, Marked.Show(document.Snapshot, plan.Selections));
        Assert.True(plan.EndTags!.Ahead);
        SelectionSet undone = document.Undo()!;
        Assert.Equal(typed, Marked.Show(document.Snapshot, undone));
    });

    private void An_end_tag_an_element_of_its_name_holding_the_open_one_needs_is_left_to_it_and_the_open_one_gets_its_own() => Cases.Each<(string typed, string after)>(
    [
        ("<div>\n    <div>\n        x\n        </|\n</div>", "<div>\n    <div>\n        x\n        </div>|\n</div>"),
        ("<div><div>x</|</div>", "<div><div>x</div>|</div>"),
        ("<section><div><div>x</|\n</div></section>", "<section><div><div>x</div>|\n</div></section>"),
        ("<div><div><div>x</|</div>\n</div>", "<div><div><div>x</div>|</div>\n</div>"),
    ], row =>
    {
        (string typed, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(typed);
        Assert.Equal(after, Marked.After(text, TextEditing.CloseTag(text, selections, "/", MarkupKind.Html)));
    });

    private void An_end_tag_whose_element_is_the_one_open_is_joined_with_elements_of_its_name_around_it() => Cases.Each<(string typed, string after)>(
    [
        ("<div>\n    <div>\n        </|\n    </div>\n</div>", "<div>\n    <div>\n    </|div>\n</div>"),
        ("<div><div>x</|</div></div>", "<div><div>x</|div></div>"),
        ("<div>\n    </|\n</div>\n<div>", "<div>\n</|div>\n<div>"),
        ("<div><section><div>x</|\n</div></section></div>", "<div><section><div>x</|div></section></div>"),
        ("<div><div><p>x</p>\n    </|\n</div><b><i></i></b></div>", "<div><div><p>x</p>\n</|div><b><i></i></b></div>"),
    ], row =>
    {
        (string typed, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(typed);
        Assert.Equal(after, Marked.After(text, TextEditing.CloseTag(text, selections, "/", MarkupKind.Html)));
    });

    private void Backspace_while_an_end_tag_is_typed_over_takes_back_a_key_and_never_a_letter_of_the_tag()
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

    private void Backspace_over_an_end_tag_joined_from_above_steps_back_to_the_slash_and_then_puts_back_what_stood_there() => Cases.Each<(string typed, string joined, string putBack)>(
    [
        ("<div>\n    </|\n</div>", "<div>\n</|div>", "    </|\n</div>"),
        ("<div>x</|\n  </div>", "<div>x</|div>", "x</|\n  </div>"),
    ], row =>
    {
        (string typed, string joined, string putBack) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(typed);
        EditPlan plan = TextEditing.CloseTag(text, selections, "/", MarkupKind.Html)!;
        TextSnapshot over = text.Apply(plan.Edits);
        EndTagTyping typing = plan.EndTags!;
        SelectionSet past = typing.Past(plan.Selections, "d");
        EndTagTyping passed = typing.After("d")!;
        SelectionSet back = passed.Back(over, past);
        Assert.Equal(joined, Marked.Show(over, back));
        EndTagTyping stepped = passed.StepBack(over, past);
        Assert.False(stepped.CanStepBack);
        EditPlan left = stepped.PutBack(back)!;
        Assert.EndsWith(putBack, Marked.Show(over.Apply(left.Edits), left.Selections));
    });

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

    [Fact]
    public void Line_commands_delete_move_duplicate_join_and_cut_whole_lines() => Cases.All(
        ("Deleting lines takes each touched line once with its break and the last the break before it", Deleting_lines_takes_each_touched_line_once_with_its_break_and_the_last_the_break_before_it),
        ("Moving lines swaps them with their neighbour carets going with them and does nothing at an edge", Moving_lines_swaps_them_with_their_neighbour_carets_going_with_them_and_does_nothing_at_an_edge),
        ("Duplicating copies the caret line below it or a selection after itself", Duplicating_copies_the_caret_line_below_it_or_a_selection_after_itself),
        ("Joining lines meets the next lines text with one space and drops blank lines", Joining_lines_meets_the_next_lines_text_with_one_space_and_drops_blank_lines),
        ("Cutting to the line end takes the rest of the line or at its end the whole break", Cutting_to_the_line_end_takes_the_rest_of_the_line_or_at_its_end_the_whole_break));

    private void Deleting_lines_takes_each_touched_line_once_with_its_break_and_the_last_the_break_before_it() => Cases.Each<(string before, string after)>(
    [
        ("one\ntw|o", "on|e"),
        ("|one\r\ntwo", "|two"),
        ("one\n|two\nthree", "one\n|three"),
        ("a|a|a\nbbb", "b|b|b"),
        ("«one\ntwo\n|three", "|three"),
        ("on|ly", "|"),
    ], row =>
    {
        (string before, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.DeleteLines(text, selections)));
    });

    private void Moving_lines_swaps_them_with_their_neighbour_carets_going_with_them_and_does_nothing_at_an_edge() => Cases.Each<(string before, bool up, string? after)>(
    [
        ("one\n|two", true, "|two\none"),
        ("o|ne\ntwo", false, "two\no|ne"),
        ("|one\ntwo", true, null),
        ("one\ntw|o", false, null),
        ("a\r\n|b\r\nc", false, "a\r\nc\r\n|b"),
        ("a\n|b\nc|\nd", true, "|b\nc|\na\nd"),
    ], row =>
    {
        (string before, bool up, string? after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.MoveLines(text, selections, before.Contains('\r') ? "\r\n" : "\n", up)));
    });

    private void Duplicating_copies_the_caret_line_below_it_or_a_selection_after_itself() => Cases.Each<(string before, string after)>(
    [
        ("one\ntw|o", "one\ntwo\ntw|o"),
        ("|one\r\ntwo", "one\r\n|one\r\ntwo"),
        ("a«bc|d", "abc«bc|d"),
    ], row =>
    {
        (string before, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        string eol = before.Contains('\r') ? "\r\n" : "\n";
        Assert.Equal(after, Marked.After(text, TextEditing.Duplicate(text, selections, eol)));
    });

    [Fact]
    public void Comments_go_on_at_the_shallowest_indentation_and_come_off_again() => Cases.All(
        ("Commenting lines puts the prefix at their shallowest indentation and again takes it back off", Commenting_lines_puts_the_prefix_at_their_shallowest_indentation_and_again_takes_it_back_off),
        ("Block comment wraps a selection or takes the ends off the comment a caret is in once for all its carets", Block_comment_wraps_a_selection_or_takes_the_ends_off_the_comment_a_caret_is_in_once_for_all_its_carets),
        ("An XML line comment wraps each line and unwraps it again", An_XML_line_comment_wraps_each_line_and_unwraps_it_again));

    private void Commenting_lines_puts_the_prefix_at_their_shallowest_indentation_and_again_takes_it_back_off()
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

    private void Block_comment_wraps_a_selection_or_takes_the_ends_off_the_comment_a_caret_is_in_once_for_all_its_carets() => Cases.Each<(string before, string after)>(
    [
        ("a «bc| d", "a «/*bc*/| d"),
        ("x|y", "x/*|*/y"),
        ("/* |copies src/*.cs and docs/*|.md */\nint x;\n", "|copies src/*.cs and docs/*|.md\nint x;\n"),
        ("a /* b| */ c", "a b| c"),
    ], row =>
    {
        (string before, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.ToggleBlockComment(text, selections, "/*", "*/")));
    });

    [Fact]
    public void Copy_and_paste_carry_whole_lines_and_deal_a_line_to_each_caret() => Cases.All(
        ("A line copied with nothing selected pastes back whole above the caret line", A_line_copied_with_nothing_selected_pastes_back_whole_above_the_caret_line),
        ("Paste deals a line to each caret puts whole lines above and takes any line break as the documents", Paste_deals_a_line_to_each_caret_puts_whole_lines_above_and_takes_any_line_break_as_the_documents));

    private void A_line_copied_with_nothing_selected_pastes_back_whole_above_the_caret_line()
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse("first\nsec|ond");
        (string copied, bool wholeLines) = TextEditing.Copy(text, selections, "\n");
        Assert.Equal(("second\n", true), (copied, wholeLines));
        Assert.Equal("first\nsecond\nsec|ond", Marked.After(text, TextEditing.Paste(text, selections, copied, wholeLines, "\n")));
        (TextSnapshot two, SelectionSet carets) = Marked.Parse("«ab|c\nd«e|f");
        Assert.Equal(("ab\ne", false), TextEditing.Copy(two, carets, "\n"));
    }

    private void An_XML_line_comment_wraps_each_line_and_unwraps_it_again()
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse("  <a />|");
        EditPlan commented = TextEditing.ToggleLineComment(text, selections, "<!--", "-->")!;
        TextSnapshot after = text.Apply(commented.Edits);
        Assert.Equal("  <!--<a />-->", after.ToString());
        Assert.Equal("  <a />", after.Apply(TextEditing.ToggleLineComment(after, commented.Selections, "<!--", "-->")!.Edits).ToString());
    }

    private void Paste_deals_a_line_to_each_caret_puts_whole_lines_above_and_takes_any_line_break_as_the_documents() => Cases.Each<(string before, string clipboard, bool wholeLines, string after)>(
    [
        ("|\n|", "a\nb", false, "a|\nb|"),
        ("|\r\n|", "a\nb", false, "a|\r\nb|"),
        ("x|y", "1\r2", false, "x1\n2|y"),
        ("one\nt|wo", "new\n", true, "one\nnew\nt|wo"),
        ("|a|b", "z", false, "z|az|b"),
    ], row =>
    {
        (string before, string clipboard, bool wholeLines, string after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        string eol = before.Contains('\r') ? "\r\n" : "\n";
        Assert.Equal(after, Marked.After(text, TextEditing.Paste(text, selections, clipboard, wholeLines, eol)));
    });

    [Fact]
    public void Saving_trims_trailing_space_and_writes_the_final_line_break_as_asked() => Cases.All(
        ("Saving trims trailing space but on a caret line ends with a break and makes every break one", Saving_trims_trailing_space_but_on_a_caret_line_ends_with_a_break_and_makes_every_break_one),
        ("Saving with no final line break takes away the breaks ending the text and the blank lines they end", Saving_with_no_final_line_break_takes_away_the_breaks_ending_the_text_and_the_blank_lines_they_end));

    private void Saving_trims_trailing_space_but_on_a_caret_line_ends_with_a_break_and_makes_every_break_one()
    {
        (TextSnapshot text, SelectionSet selections) = Marked.Parse("a  \r\nb  |\nc\t");
        EditPlan plan = TextEditing.Saving(text.ToString(), selections, trimTrailing: true, finalNewline: true, convertTo: "\n", eol: "\r\n")!;
        Assert.Equal("a\nb  |\nc\n", Marked.After(text, plan));
        Assert.Null(TextEditing.Saving("tidy\n", selections.Clamp(5), trimTrailing: true, finalNewline: true, convertTo: "\n", eol: "\n"));
        EditPlan ended = TextEditing.Saving("x", SelectionSet.Single(0), trimTrailing: false, finalNewline: true, convertTo: null, eol: "\r\n")!;
        Assert.Equal("x\r\n", TextSnapshot.From("x").Apply(ended.Edits).ToString());
    }

    /// <summary>A save asked for no final line break takes away every line break the text ends with, and the lines they
    /// leave empty, those of spaces and tabs the save trims too, but never one holding a caret's spaces; a break kept
    /// before them is made <c>convertTo</c>'s like any other.</summary>
    private void Saving_with_no_final_line_break_takes_away_the_breaks_ending_the_text_and_the_blank_lines_they_end() => Cases.Each<(string before, bool trimTrailing, string? convertTo, string? after)>(
    [
        ("kept\n\n|", false, null, "kept|"),
        ("kept\r\n|", false, null, "kept|"),
        ("|a\n  \n\t\n", true, null, "|a"),
        ("|a\n  \n\t\n", false, null, "|a\n  \n\t"),
        ("a  \n|b\n", true, "\r\n", "a\r\n|b"),
        ("|kept", true, "\n", null),
    ], row =>
    {
        (string before, bool trimTrailing, string? convertTo, string? after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.Saving(text.ToString(), selections, trimTrailing, finalNewline: false, convertTo, eol: "\n")));
    });

    private void Joining_lines_meets_the_next_lines_text_with_one_space_and_drops_blank_lines() => Cases.Each<(string before, string? after)>(
    [
        ("a|\n    b", "a |b"),
        ("a|\n\n   \nb", "a|\n   \nb"),
        ("«a\n\n   \n  b|", "«a b|"),
        ("«a\nb\nc|", "«a b c|"),
        ("last|", null),
    ], row =>
    {
        (string before, string? after) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        Assert.Equal(after, Marked.After(text, TextEditing.JoinLines(text, selections)));
    });

    private void Cutting_to_the_line_end_takes_the_rest_of_the_line_or_at_its_end_the_whole_break() => Cases.Each<(string before, string? after, string removed)>(
    [
        ("o|ne\r\ntwo", "o|\r\ntwo", "ne"),
        ("one|\r\ntwo", "one|two", "\r\n"),
        ("one|", null, ""),
    ], row =>
    {
        (string before, string? after, string removed) = row;
        (TextSnapshot text, SelectionSet selections) = Marked.Parse(before);
        (EditPlan? plan, string taken) = TextEditing.CutToLineEnd(text, selections);
        Assert.Equal(after, Marked.After(text, plan));
        Assert.Equal(removed, taken);
    });

    [Fact]
    public void Carets_merge_where_they_meet_and_offsets_follow_the_edits() => Cases.All(
        ("Carets at one place merge and a caret at a selections edge joins it but two selections that meet stay apart", Carets_at_one_place_merge_and_a_caret_at_a_selections_edge_joins_it_but_two_selections_that_meet_stay_apart),
        ("Offsets carried through edits go before or after text inserted at them and to a removals start", Offsets_carried_through_edits_go_before_or_after_text_inserted_at_them_and_to_a_removals_start));

    private void Carets_at_one_place_merge_and_a_caret_at_a_selections_edge_joins_it_but_two_selections_that_meet_stay_apart()
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

    private void Offsets_carried_through_edits_go_before_or_after_text_inserted_at_them_and_to_a_removals_start()
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
