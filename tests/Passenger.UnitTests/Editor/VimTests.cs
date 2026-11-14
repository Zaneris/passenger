using Passenger.Editor;
using Passenger.Text;
using Passenger.UnitTests.Text;

namespace Passenger.UnitTests.Editor;

/// <summary>Vim mode's keys as the carets and text they leave (<see cref="Marked"/>, visual mode's stretches as the
/// selections shown), checked against what Vim 9.1 does with the same keys (each expected text is Vim's own, settings
/// shiftwidth=4 tabstop=4 expandtab autoindent, but J's one space after a sentence's end, nojoinspaces, as Neovim and
/// code have it): motions, operators, text objects, registers and puts, . repeating a change, visual modes, entering
/// and leaving insert mode, and the keys it leaves to the keymap; then what an editor needs of it alone: every caret
/// acted at, one step of undo, the system clipboard's registers.</summary>
public class VimTests
{
    [Theory]
    [InlineData("ab|cd", "h", "a|bcd")]
    [InlineData("|abcd", "h", "|abcd")]
    [InlineData("ab|cd", "9h", "|abcd")]
    [InlineData("a|bcd", "l", "ab|cd")]
    [InlineData("abc|d", "l", "abc|d")]
    [InlineData("a|bcd\nef", "5l", "abc|d\nef")]
    [InlineData("on|e\ntwo", "l", "on|e\ntwo")]
    [InlineData("  a|bc", "0", "|  abc")]
    [InlineData("  a|bc", "^", "  |abc")]
    [InlineData("|abc\ndef", "$", "ab|c\ndef")]
    [InlineData("|abc\ndef", "2$", "abc\nde|f")]
    public void Moves_along_a_line_stay_on_its_characters(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("ab|cd\nefgh", "j", "abcd\nef|gh")]
    [InlineData("abcd\nef|gh", "k", "ab|cd\nefgh")]
    [InlineData("abc|d\nx\nabcd", "jj", "abcd\nx\nabc|d")]
    [InlineData("a|b\nc\nd", "9j", "ab\nc\n|d")]
    [InlineData("ab\nc\n|d", "99k", "|ab\nc\nd")]
    [InlineData("a|b\nc\n", "5j", "ab\n|c\n")]
    [InlineData("|abc\nx\nabcdef", "$jj", "abc\nx\nabcde|f")]
    public void Up_and_down_keep_the_column_aimed_for_and_stop_at_the_last_line(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("|one two", "w", "one |two")]
    [InlineData("|foo.bar", "w", "foo|.bar")]
    [InlineData("|foo.bar", "ww", "foo.|bar")]
    [InlineData("on|e\n\ntwo", "w", "one\n|\ntwo")]
    [InlineData("on|e\n\ntwo", "ww", "one\n\n|two")]
    [InlineData("one\n  |  two", "w", "one\n    |two")]
    [InlineData("one t|wo", "w", "one tw|o")]
    [InlineData("|a b c d", "3w", "a b c |d")]
    [InlineData("one |two", "b", "|one two")]
    [InlineData("one tw|o", "b", "one |two")]
    [InlineData("one\n\n|two", "b", "one\n|\ntwo")]
    [InlineData("foo.|bar", "b", "foo|.bar")]
    [InlineData("|one two", "e", "on|e two")]
    [InlineData("on|e two", "e", "one tw|o")]
    [InlineData("one\n\n|two", "e", "one\n\ntw|o")]
    [InlineData("|ab\n\ncd", "e", "a|b\n\ncd")]
    [InlineData("|a.b", "e", "a|.b")]
    [InlineData("one\r\n\r\n|two", "b", "one\r\n|\r\ntwo")]
    public void Word_motions_stop_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("a\n  b\nc|", "gg", "|a\n  b\nc")]
    [InlineData("|a\n  b\nc", "G", "a\n  b\n|c")]
    [InlineData("|a\n  b\nc\n", "G", "a\n  b\n|c\n")]
    [InlineData("|a\n  b\nc", "2G", "a\n  |b\nc")]
    [InlineData("a\n  b\n|c", "2gg", "a\n  |b\nc")]
    [InlineData("|a\nb", "99G", "a\n|b")]
    public void Line_jumps_land_on_the_first_non_blank(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("a|bc", "x", "a|c")]
    [InlineData("ab|c", "x", "a|b")]
    [InlineData("a|bcd\nef", "9x", "|a\nef")]
    [InlineData("|\nab", "x", "|\nab")]
    [InlineData("a|b|c", "x", "|a")]
    [InlineData("one\n  t|wo\nthree", "dd", "one\n|three")]
    [InlineData("one\ntwo\nth|ree", "dd", "one\n|two")]
    [InlineData("o|ne\ntwo\nthree", "2dd", "|three")]
    [InlineData("o|ne\ntwo", "9dd", "|")]
    [InlineData("one\ntw|o\n", "dd", "|one\n")]
    public void X_and_dd_delete_and_leave_the_caret_where_vim_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("ab\n|cd", "<BS>", "a|b\ncd")]
    [InlineData("a|b", "<BS>", "|ab")]
    [InlineData("ab\n\n|cd", "3<BS>", "|ab\n\ncd")]
    [InlineData("a|b\n  cd", "<CR>", "ab\n  |cd")]
    [InlineData("a|b\nc\n  d", "2<CR>", "ab\nc\n  |d")]
    [InlineData("ab\n  c|d", "<CR>", "ab\n  c|d")]
    [InlineData("a|bc", "<Del>", "a|c")]
    [InlineData("a|bcd", "2<Del>x", "a|cd")]
    [InlineData("a|bc", "<Tab>", "a|bc")]
    public void Keys_that_would_edit_in_the_keymap_move_or_delete_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("a|bc", "iX<Esc>", "a|Xbc")]
    [InlineData("a|bc", "aX<Esc>", "ab|Xc")]
    [InlineData("  a|bc", "IX<Esc>", "  |Xabc")]
    [InlineData("a|bc", "AX<Esc>", "abc|X")]
    [InlineData("a|bc", "i<Esc>", "|abc")]
    [InlineData("|abc", "i<Esc>", "|abc")]
    public void Insert_mode_types_and_escape_steps_back_onto_the_text(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("|one two-three four", "W", "one |two-three four")]
    [InlineData("|one two-three four", "2W", "one two-three |four")]
    [InlineData("one two-three |four", "B", "one |two-three four")]
    [InlineData("|one two-three four", "E", "on|e two-three four")]
    [InlineData("one two-three |four", "ge", "one two-thre|e four")]
    [InlineData("one two-three |four", "gE", "one two-thre|e four")]
    [InlineData("|a, b, c", "f,", "a|, b, c")]
    [InlineData("|a, b, c", "2f,", "a, b|, c")]
    [InlineData("|a, b, c", "t,", "|a, b, c")]
    [InlineData("a, b, |c", "F,", "a, b|, c")]
    [InlineData("a, b, |c", "T,", "a, b,| c")]
    [InlineData("|a, b, c", "f,;", "a, b|, c")]
    [InlineData("|a, b, c", "f,;,", "a|, b, c")]
    [InlineData("|a, b, c", "t,;", "a, |b, c")]
    [InlineData("|x (a [b] c) y", "%", "x (a [b] c|) y")]
    [InlineData("x (a [b] c|) y", "%", "x |(a [b] c) y")]
    [InlineData("x (a [|b] c) y", "%", "x (a |[b] c) y")]
    [InlineData("|a\nb\n\nc\nd\n\ne", "}", "a\nb\n|\nc\nd\n\ne")]
    [InlineData("|a\nb\n\nc\nd\n\ne", "2}", "a\nb\n\nc\nd\n|\ne")]
    [InlineData("a\nb\n\nc\nd\n\n|e", "{", "a\nb\n\nc\nd\n|\ne")]
    [InlineData("|a\nb\n\nc", "3}", "|a\nb\n\nc")]
    [InlineData("|One two. Three four.  Five", ")", "One two. |Three four.  Five")]
    [InlineData("One two. Three |four.  Five", "(", "One two. |Three four.  Five")]
    [InlineData("|a\n\tb", "j", "a\n|\tb")]
    [InlineData("\t|b\nabcdefgh", "k", "\t|b\nabcdefgh")]
    [InlineData("abcdefgh|ij\n\tb", "j", "abcdefghij\n\t|b")]
    [InlineData("|a\nb\nc", "+", "a\n|b\nc")]
    [InlineData("a\nb\n|c", "-", "a\n|b\nc")]
    [InlineData("  a|bc", "g_", "  ab|c")]
    public void Word_find_bracket_paragraph_sentence_and_line_motions_go_where_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("one t|wo three", "dw", "one t|three")]
    [InlineData("one two |three\nfour", "dw", "one two| \nfour")]
    [InlineData("one t|wo three", "d2w", "one |t")]
    [InlineData("one t|wo three", "2d2w", "one |t")]
    [InlineData("one t|wo three", "de", "one t| three")]
    [InlineData("one t|wo three", "db", "one |wo three")]
    [InlineData("one t|wo three", "d$", "one |t")]
    [InlineData("one t|wo three", "d0", "|wo three")]
    [InlineData("one t|wo three", "dfe", "one t|e")]
    [InlineData("one t|wo three", "dte", "one t|ee")]
    [InlineData("one t|wo three", "dFo", "|wo three")]
    [InlineData("a\n|b\nc\nd", "dj", "a\n|d")]
    [InlineData("a\nb\n|c\nd", "dk", "a\n|d")]
    [InlineData("a\nb\n|c\nd", "dG", "a\n|b")]
    [InlineData("a\nb\n|c\nd", "dgg", "|d")]
    [InlineData("x (a [b] c|) y", "d%", "x | y")]
    [InlineData("|a\nb\n\nc", "d}", "|\nc")]
    [InlineData("  |a\n  b\n\nc", "d}", "|\nc")]
    [InlineData("one t|wo three", "cwX<Esc>", "one t|X three")]
    [InlineData("one tw|o three", "cwX<Esc>", "one tw|X three")]
    [InlineData("one t|wo three", "c2wX<Esc>", "one t|X")]
    [InlineData("one t|wo three", "yw", "one t|wo three")]
    [InlineData("one t|wo three", "yb", "one |two three")]
    [InlineData("a\n  b|c\nd", "yk", "|a\n  bc\nd")]
    [InlineData("a\n  b|c\nd", "yj", "a\n  b|c\nd")]
    [InlineData("a\n  b|c\nd", ">j", "a\n      |bc\n    d")]
    [InlineData("a\n  b|c\nd", "<<", "a\n|bc\nd")]
    [InlineData("a\n  b|c\nd", "3>>", "a\n      |bc\n    d")]
    [InlineData("one t|wo three", "g~w", "one t|WO three")]
    [InlineData("one t|wo three", "gUiw", "one |TWO three")]
    [InlineData("ONE T|WO THREE", "guu", "|one two three")]
    [InlineData("one t|wo three", "gUU", "|ONE TWO THREE")]
    [InlineData("one t|wo three", "2gUU", "one t|wo three")]
    [InlineData("a|bc def", "x", "a|c def")]
    [InlineData("a|bc def", "3x", "a|def")]
    [InlineData("a|bc def", "X", "|bc def")]
    [InlineData("a|bc def", "D", "|a")]
    [InlineData("a|bc def", "CX<Esc>", "a|X")]
    [InlineData("a|bc def", "sX<Esc>", "a|Xc def")]
    [InlineData("  a|bc\ndef", "SX<Esc>", "  |X\ndef")]
    [InlineData("a|bc\ndef", "Yjp", "abc\ndef\n|abc")]
    [InlineData("a|bcd", "2rx", "ax|xd")]
    [InlineData("a|bcd", "5rx", "a|bcd")]
    [InlineData("a|b.\n   cd\n)x", "3J", "ab. cd|)x")]
    [InlineData("a|b \n   cd", "J", "ab |cd")]
    [InlineData("a|bCd", "3~", "aBc|D")]
    public void Operators_and_the_commands_that_edit_alone_change_what_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("one t|wo three", "diw", "one | three")]
    [InlineData("one t|wo three", "daw", "one |three")]
    [InlineData("one two |three", "daw", "one tw|o")]
    [InlineData("one t|wo  three", "d2aw", "on|e")]
    [InlineData("one t|wo three", "diW", "one | three")]
    [InlineData("a.b t|wo three", "daW", "a.b |three")]
    [InlineData("x (a [|b] c) y", "di(", "x (|) y")]
    [InlineData("x (a [|b] c) y", "da(", "x | y")]
    [InlineData("x (a [|b] c) y", "di[", "x (a [|] c) y")]
    [InlineData("x (a [|b] c) y", "2di(", "x (a [|b] c) y")]
    [InlineData("x {\n  a|b\n} y", "di{", "x {\n|} y")]
    [InlineData("x {\n  a|b\n} y", "da{", "x | y")]
    [InlineData("s = \"a|b c\";", "di\"", "s = \"|\";")]
    [InlineData("s = \"a|b c\";", "da\"", "s =|;")]
    [InlineData("s = 'a|b' + x;", "ci'X<Esc>", "s = '|X' + x;")]
    [InlineData("<a><b>x|y</b></a>", "dit", "<a><b>|</b></a>")]
    [InlineData("<a><b>x|y</b></a>", "dat", "<a>|</a>")]
    [InlineData("<a><b>x|y</b></a>", "d2it", "<a>|</a>")]
    [InlineData("a\n|b\nc\n\nd", "dip", "|\nd")]
    [InlineData("a\n|b\nc\n\nd", "dap", "|d")]
    [InlineData("a\n\n|b\nc", "dap", "|a")]
    [InlineData("One two. Three |four.  Five", "dis", "One two. |  Five")]
    [InlineData("One two. Three |four.  Five", "das", "One two. |Five")]
    [InlineData("x |foo(a) y", "di(", "x foo(|) y")]
    public void Text_objects_take_what_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("a|bc\ndef", "\"ayy\"ap", "abc\n|abc\ndef")]
    [InlineData("a|bc\ndef", "\"ayy\"Ayy\"ap", "abc\n|abc\nabc\ndef")]
    [InlineData("a|bc\ndef", "\"ayw\"Ayy\"ap", "abc\n|bc\nabc\ndef")]
    [InlineData("a|bc\ndef", "\"_ddp", "|def")]
    [InlineData("a|bc\ndef\nghi", "dddd\"2p", "ghi\n|abc")]
    [InlineData("a|bc\ndef\nghi", "dddd\"1p", "ghi\n|def")]
    [InlineData("a|bc def", "dw\"-P", "abc| def")]
    [InlineData("a|bc def", "yw\"0P", "abc| bc def")]
    [InlineData("a|bc def", "ywdw\"0p", "adbc| ef")]
    [InlineData("a|bc def", "xp", "ac|b def")]
    [InlineData("a|bc def", "x3p", "acbb|b def")]
    [InlineData("a|bc\ndef", "yyP", "|abc\nabc\ndef")]
    [InlineData("a|bc\ndef", "yy2p", "abc\n|abc\nabc\ndef")]
    [InlineData("a|bc\ndef", "ddp", "def\n|abc")]
    public void Registers_keep_and_put_back_what_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("one |two three four", "dw.", "one |four")]
    [InlineData("one |two three four", "dw2.", "one| ")]
    [InlineData("one |two three four", "cwX<Esc>w.", "one X |X four")]
    [InlineData("a|bc\nabc", "AX<Esc>j.", "abcX\nabc|X")]
    [InlineData("a|bc\nabc", "ix<Esc>j.", "axbc\na|xbc")]
    [InlineData("a|bc\nd", "ox<Esc>.", "abc\nx\n|x\nd")]
    [InlineData("a|bc\ndef", ">>j.", "    abc\n    |def")]
    [InlineData("a|bc\nd", "3ix<Esc>", "axx|xbc\nd")]
    [InlineData("a|bc\nd", "2ox<Esc>", "abc\nx\n|x\nd")]
    public void Dot_repeats_the_last_change_its_typing_included(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("\t|x\nabcdefgh", "j", "\tx\nabcd|efgh")]
    [InlineData("|\tx\nabcdefgh", "j", "\tx\nabc|defgh")]
    [InlineData("abc|defgh\n\tx", "j", "abcdefgh\n|\tx")]
    [InlineData("ab|cd\n\u4E00\u4E8Cx", "j", "abcd\n\u4E00|\u4E8Cx")]
    [InlineData("\u4E00|\u4E8Cx\nabcdef", "j", "\u4E00\u4E8Cx\nab|cdef")]
    [InlineData("ab|cd\nx\n\u4E00\u4E8Cx", "jj", "abcd\nx\n\u4E00|\u4E8Cx")]
    public void Up_and_down_keep_the_display_column_across_tabs_and_wide_characters(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("a|bc\na|bc", "dw", "|a\n|a")]
    [InlineData("one |two\none |two", "ciwX<Esc>", "one |X\none |X")]
    [InlineData("|a b\n|c d", "yiwwp", "a b|a\nc d|c")]
    [InlineData("a|b\nc|d", ">>", "    |ab\n    |cd")]
    [InlineData("|ab\n|cd", "yyP", "|ab\nab\n|cd\ncd")]
    public void Each_caret_takes_the_command_and_a_register_keeps_a_piece_for_each(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("one t|wo three\nfour", "v", "one t«w|o three\nfour")]
    [InlineData("one t|wo three\nfour", "vl", "one t«wo| three\nfour")]
    [InlineData("one t|wo three\nfour", "vlh", "one t«w|o three\nfour")]
    [InlineData("one t|wo three\nfour", "vh", "one |tw»o three\nfour")]
    [InlineData("one t|wo three\nfour", "ve", "one t«wo| three\nfour")]
    [InlineData("one t|wo three\nfour", "vb", "one |tw»o three\nfour")]
    [InlineData("one t|wo three\nfour", "v$", "one t«wo three\n|four")]
    [InlineData("one t|wo three\nfour", "vj", "one t«wo three\nfour|")]
    [InlineData("one t|wo three\nfour", "vjo", "one t|wo three\nfour»")]
    [InlineData("one t|wo three\nfour", "v3l", "one t«wo t|hree\nfour")]
    [InlineData("one t|wo three\nfour", "viw", "one «two| three\nfour")]
    [InlineData("one t|wo three\nfour", "vaw", "one «two |three\nfour")]
    [InlineData("one t|wo three\nfour", "viwiw", "one «two |three\nfour")]
    [InlineData("one t|wo three\nfour", "vfe", "one t«wo thre|e\nfour")]
    [InlineData("one t|wo three\nfour", "vG", "one t«wo three\nf|our")]
    [InlineData("one t|wo three\nfour", "V", "«one two three\n|four")]
    [InlineData("one t|wo three\nfour", "Vj", "«one two three\nfour|")]
    [InlineData("one t|wo three\nfour", "Vjo", "|one two three\nfour»")]
    [InlineData("one\nt|wo\nthree", "Vk", "|one\ntwo\n»three")]
    [InlineData("one t|wo three\nfour", "2v", "one t«wo| three\nfour")]
    [InlineData("one t|wo three\nfour", "vjV", "«one two three\nfour|")]
    [InlineData("one t|wo three\nfour", "Vjv", "one t«wo three\nfour|")]
    [InlineData("one t|wo three\nfour", "vl<Esc>", "one tw|o three\nfour")]
    [InlineData("one t|wo three\nfour", "vlv", "one tw|o three\nfour")]
    [InlineData("one t|wo three\nfour", "vjoV", "|one two three\nfour»")]
    [InlineData("x (a [b|] c) y", "vi(", "x («a [b] c|) y")]
    [InlineData("x (a [b|] c) y", "va[", "x (a «[b]| c) y")]
    [InlineData("a\n|b\nc\n\nd", "vip", "«a\nb\nc\n|\nd")]
    public void Visual_mode_selects_from_where_it_began_to_its_cursor_as_vims_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("one t|wo three\nfour", "vld", "one t| three\nfour")]
    [InlineData("one t|wo three\nfour", "vjd", "one |t")]
    [InlineData("one t|wo three\nfour", "v$d", "one t|four")]
    [InlineData("one t|wo three\nfour", "vex", "one t| three\nfour")]
    [InlineData("one t|wo three\nfour", "vjX", "|")]
    [InlineData("one t|wo three\nfour", "vlD", "|four")]
    [InlineData("one t|wo three\nfour", "vecX<Esc>", "one t|X three\nfour")]
    [InlineData("one t|wo three\nfour", "vjcX<Esc>", "one t|X")]
    [InlineData("one t|wo three\nfour", "VcX<Esc>", "|X\nfour")]
    [InlineData("  one t|wo\n  three\nfour", "VjcX<Esc>", "  |X\nfour")]
    [InlineData("one t|wo three\nfour", "vlsX<Esc>", "one t|X three\nfour")]
    [InlineData("one t|wo three\nfour", "vlSX<Esc>", "|X\nfour")]
    [InlineData("one t|wo three\nfour", "vlCX<Esc>", "|X\nfour")]
    [InlineData("one t|wo three\nfour", "vly", "one t|wo three\nfour")]
    [InlineData("one t|wo three\nfour", "vjy", "one t|wo three\nfour")]
    [InlineData("one t|wo three\nfour", "Vjy", "|one two three\nfour")]
    [InlineData("one t|wo three\nfour", "vlY", "|one two three\nfour")]
    [InlineData("one t|wo three\nfour", "veyP", "one tw|owo three\nfour")]
    [InlineData("one t|wo three\nfour", "vey$p", "one two threew|o\nfour")]
    [InlineData("one t|wo three\nfour", "Vjyp", "one two three\n|one two three\nfour\nfour")]
    [InlineData("one t|wo three\nfour", "vjU", "one t|WO THREE\nFOUR")]
    [InlineData("one t|wo three\nfour", "veU", "one t|WO three\nfour")]
    [InlineData("one t|wo three\nfour", "VU", "|ONE TWO THREE\nfour")]
    [InlineData("ONE T|WO three\nfour", "veu", "ONE T|wo three\nfour")]
    [InlineData("one t|wo three\nfour", "ve~", "one t|WO three\nfour")]
    [InlineData("one t|wo three\nfour", "veg~", "one t|WO three\nfour")]
    [InlineData("one t|wo three\nfour", "vjgU", "one t|WO THREE\nFOUR")]
    [InlineData("one t|wo three\nfour", "ver-", "one t|-- three\nfour")]
    [InlineData("one t|wo three\nfour", "vjr-", "one t|--------\n----")]
    [InlineData("one t|wo three\nfour", "Vr-", "|-------------\nfour")]
    [InlineData("one t|wo three\nfour", "vj>", "    |one two three\n    four")]
    [InlineData("one t|wo three\nfour", "V2>", "        |one two three\nfour")]
    [InlineData("    one t|wo three\nfour", "vj<", "|one two three\nfour")]
    [InlineData("one t|wo three\nfour", "vjJ", "one two three| four")]
    [InlineData("one t|wo three\n  four", "vjgJ", "one two three|  four")]
    [InlineData("one t|wo three\nfour", "VJ", "one two three| four")]
    [InlineData("one t|wo three\nfour", "yiwwviwp", "one two tw|o\nfour")]
    [InlineData("one t|wo three\nfour", "yiwwviwP", "one two tw|o\nfour")]
    [InlineData("one t|wo three\nfour", "yiwwviwp$p", "one two twothre|e\nfour")]
    [InlineData("one t|wo three\nfour", "yyjviwp", "one two three\n\n|one two three\n")]
    [InlineData("one t|wo three\nfour", "yiwjVp", "one two three\n|two")]
    [InlineData("one t|wo three\nfour", "yyjVp", "one two three\n|one two three")]
    [InlineData("one t|wo three\nfour", "yyVp", "|one two three\nfour")]
    [InlineData("one t|wo three\nfour", "vlIX<Esc>", "|Xone two three\nfour")]
    [InlineData("one t|wo three\nfour", "vlAX<Esc>", "one two|X three\nfour")]
    [InlineData("one t|wo three\nfour", "VAX<Esc>", "one tw|Xo three\nfour")]
    [InlineData("one t|wo three\nfour", "vjAX<Esc>", "one two three\nfour|X")]
    [InlineData("one t|wo three\nfour", "vld.", "one t|hree\nfour")]
    [InlineData("one t|wo three\nfour", "Vd.", "|")]
    [InlineData("one t|wo three\nfour", "vlyjgvd", "one t| three\nfour")]
    [InlineData("one t|wo three\nfour", "vl<Esc>gvd", "one t| three\nfour")]
    [InlineData("one t|wo three\nfour", "vl\"ad\"ap", "one t w|othree\nfour")]
    [InlineData("one t|wo three\nfour", "vlvd", "one tw|o three\nfour")]
    public void Visual_modes_operators_take_what_is_selected_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>", "ab«c|d\nefgh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jl", "ab«cd|\nef«gh|\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jh", "a|bc»d\ne|fg»h\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>j$", "ab«cd|\nef«gh|\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlo", "ab|cd»\nef|gh»\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlO", "ab|cd»\nef|gh»\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jld", "a|b\nef\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlx", "a|b\nef\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlD", "a|b\nef\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlcX<Esc>", "ab|X\nefX\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlCX<Esc>", "ab|X\nefX\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jIX<Esc>", "ab|Xcd\nefXgh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jAX<Esc>", "ab|cXd\nefgXh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>j$AX<Esc>", "ab|cdX\nefghX\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jly", "ab|cd\nefgh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlyP", "ab|cdcd\nefghgh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlyp", "abc|cdd\nefgghh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlyGp", "abcd\nefgh\ni|cdjkl\n gh")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jly$p", "abcd|cd\nefghgh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jly2P", "ab|cdcdcd\nefghghgh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlU", "ab|CD\nefGH\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlr-", "ab|--\nef--\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jl>", "ab|    cd\nef    gh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlJ", "abcd| efgh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jl<Esc>", "abcd\nefg|h\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlv", "ab«cd\nefgh|\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jld.", "|a\ne\nijkl")]
    [InlineData("ab|cd\nx\nijkl", "<C-v>jjIX<Esc>", "ab|Xcd\nx\nijXkl")]
    [InlineData("    |{\n        int\n    }", "<C-v>jjl", "    «{|\n    «  |  int\n    «}|")]
    [InlineData("ab|cd\nx\nijkl", "<C-v>jjAX<Esc>", "ab|cXd\nx  X\nijkXl")]
    public void A_block_is_a_selection_on_each_line_and_its_operators_act_line_by_line_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("one t|wo three\nfour five\nsix", "qaxq", "one t|o three\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "qaxqj@a", "one to three\nfour |ive\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "qaxqj2@a", "one to three\nfour |ve\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "qaxqj@a@@", "one to three\nfour |ve\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "qaA!<Esc>jqj@a", "one two three!\nfour five\nsix|!")]
    [InlineData("one t|wo three\nfour five\nsix", "qaA!<Esc>jq@a@@", "one two three!\nfour five!\nsix|!")]
    [InlineData("one t|wo three\nfour five\nsix", "qaxqqAxq@a", "one t|hree\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "qaciwX<Esc>wq@a", "one X X\n|four five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "qaviwUq0@a", "|ONE TWO three\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "qaxq\"ap", "one to|x three\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "qaRXY<Esc>jq@a", "one tXY three\nfour fXYe\nsi|x")]
    public void Q_records_keys_into_a_register_and_at_presses_them_again_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("one t|wo three\nfour five\nsix", "RXY<Esc>", "one tX|Y three\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "RXYZWVU<Esc>", "one tXYZWV|Uee\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "$RXYZ<Esc>", "one two threXY|Z\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "RXY<BS><Esc>", "one t|Xo three\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "RXY<BS><BS><BS><Esc>", "one| two three\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "2RXY<Esc>", "one tXYX|Yhree\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "RXY<Esc>j.", "one tXY three\nfour fX|Ye\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "RX<CR>Y<Esc>", "one tX\n|Y three\nfour five\nsix")]
    [InlineData("one t|wo three\nfour five\nsix", "RX<CR><BS><Esc>", "one t|Xo three\nfour five\nsix")]
    public void Replace_mode_types_over_the_text_and_backspace_puts_it_back_as_vims_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two<CR>", "  one two\nfoo |two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two<CR>n", "  one two\nfoo two\n  |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two<CR>nn", "  one two\nfoo two\n  two three |two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two<CR>nnn", "  one |two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two<CR>N", "  one |two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "?two<CR>", "  one |two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "?two<CR>n", "  one two\nfoo two\n  two three |two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "?two<CR>N", "  one two\nfoo |two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two/e<CR>", "  one tw|o\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two/e<CR>n", "  one two\nfoo tw|o\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two/s+1<CR>", "  one two\nfoo t|wo\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two/+1<CR>", "  one two\nfoo two\n|  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "2/two<CR>", "  one two\nfoo two\n  |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/t\\(w\\|h\\)<CR>n", "  one two\nfoo two\n  |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/\\vt(w|h)<CR>n", "  one two\nfoo two\n  |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/\\<two\\><CR>", "  one two\nfoo |two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/o\\zsn<CR>", "  o|ne two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/^f<CR>", "  one two\n|foo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two$<CR>n", "  one two\nfoo two\n  two three |two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/[fl]<CR>n", "  one two\nfoo two\n  two three two\n|last")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "*", "  one two\nfoo |two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "**", "  one two\nfoo two\n  |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "#", "  one two\nfoo two\n  two three |two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "jjj*", "  one two\nfoo two\n  two three two\n|last")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "g*", "  one two\nfoo |two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "d/foo<CR>", "  one |t\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "d/two<CR>", "  one t|two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "y/two<CR>P", "  one t|wo\nfoo wo\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "c/foo<CR>X<Esc>", "  one t|X\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "v/three<CR>d", "  one t|hree two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two<CR>dn", "  one two\nfoo |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two<CR>x.", "  one two\nfoo |o\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two<CR>/<CR>", "  one two\nfoo two\n  |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":set ic<CR>/TWO<CR>", "  one two\nfoo |two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":set ic scs<CR>/two<CR>", "  one two\nfoo |two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/Two\\c<CR>", "  one two\nfoo |two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "ma:3<CR>'a", "  |one two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "majj`a", "  one t|wo\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "majjd'a", "|last")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "majjy`aP", "  one t|wo\nfoo two\n  two two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "G''", "  |one two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "G``", "  one t|wo\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/three<CR>''", "  |one two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "3G''", "  |one two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":3<CR>''", "  |one two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "mxjmyG'x", "  |one two\nfoo two\n  two three two\nlast")]
    public void Searches_and_marks_go_where_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":s/two/2/<CR>", "  |one 2\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/two/2/<CR>", "  one 2\nfoo 2\n  |2 three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/two/2/g<CR>", "  one 2\nfoo 2\n  |2 three 2\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":2,3s/two/2/g<CR>", "  one two\nfoo 2\n  |2 three 2\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/two/2/gn<CR>", "  |one two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":.,+1s/o/0/g<CR>", "  0ne tw0\n|f00 tw0\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":.,$s/o/0/g<CR>", "  0ne tw0\nf00 tw0\n  |tw0 three tw0\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":3<CR>", "  one two\nfoo two\n  |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":$<CR>", "  one two\nfoo two\n  two three two\n|last")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":+2<CR>", "  one two\nfoo two\n  |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/\\(t\\)\\(w\\)o/\\2\\1/g<CR>", "  one wt\nfoo wt\n  |wt three wt\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/two/\\U&/<CR>", "  one TWO\nfoo TWO\n  |TWO three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/two/\\u&/g<CR>", "  one Two\nfoo Two\n  |Two three Two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/o\\zsn/N/g<CR>", "  |oNe two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/two/a\\rb/<CR>", "  one a\nb\nfoo a\nb\n  a\n|b three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/ two/!/g<CR>", "  one!\nfoo!\n |! three!\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":s#two#2#<CR>", "  |one 2\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/two/2/<CR>:%&&<CR>", "  one 2\nfoo 2\n  |2 three 2\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":s/two/2/<CR>j:s<CR>", "  one 2\n|foo 2\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "/two<CR>:%s//2/g<CR>", "  one 2\nfoo 2\n  |2 three 2\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/^/> /<CR>", ">   one two\n> foo two\n>   two three two\n|> last")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/$/;/<CR>", "  one two;\nfoo two;\n  two three two;\n|last;")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/\\s\\+/ /g<CR>", " one two\nfoo two\n |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/[ot]\\{2}/X/g<CR>", "  one two\n|fX two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/two\\|foo/X/g<CR>", "  one X\nX X\n  |X three X\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/\\vt(w|h)/X/g<CR>", "  one Xo\nfoo Xo\n  |Xo Xree Xo\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%s/O/X/gi<CR>", "  Xne twX\nfXX twX\n  |twX three twX\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":g/two/d<CR>", "|last")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":v/two/d<CR>", "  one two\nfoo two\n  |two three two")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":g!/foo/d<CR>", "|foo two")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":g/two/s/o/0/g<CR>", "  0ne tw0\nf00 tw0\n  |tw0 three tw0\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":2,3g/two/d<CR>", "  one two\n|last")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":g/two/normal Ax<CR>", "  one twox\nfoo twox\n  two three two|x\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%d<CR>", "|")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":2,3d<CR>", "  one two\n|last")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":2d<CR>p", "  one two\n  two three two\n|foo two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":2y<CR>P", "|foo two\n  one two\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":1,2j<CR>", "  |one two foo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":2>><CR>", "  one two\n        |foo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":%<<CR>", "one two\nfoo two\ntwo three two\n|last")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "ma:'a,'a+1s/o/0/g<CR>", "  0ne tw0\n|f00 tw0\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":/foo/s/two/2/<CR>", "  one two\n|foo 2\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "Vj:s/o/0/g<CR>", "  0ne tw0\n|f00 tw0\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", "Vj:d<CR>", "  |two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":set js<CR>J", "  one two| foo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":norm Ax<CR>", "  one two|x\nfoo two\n  two three two\nlast")]
    [InlineData("  one t|wo\nfoo two\n  two three two\nlast", ":2,3norm Ax<CR>", "  one two\nfoo twox\n  two three two|x\nlast")]
    public void Ex_commands_change_and_move_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Theory]
    [InlineData(@"t\(w\|h\)o", "t(w|h)o")]
    [InlineData(@"\vt(w|h)o", "t(w|h)o")]
    [InlineData(@"a+b?", @"a\+b\?")]
    [InlineData(@"a\+b\=", "a+b?")]
    [InlineData(@"\<two\>", @"\b(?=\w)two\b(?<=\w)")]
    [InlineData(@"x\{2,3}y\{-}", "x{2,3}y*?")]
    [InlineData(@"\s\d\w", @"[ \t][0-9][0-9A-Za-z_]")]
    [InlineData(@"o\zsn", "(?<=o)n")]
    [InlineData(@"o\zen", "o(?=n)")]
    [InlineData(@"[[:alpha:]]", "[A-Za-z]")]
    [InlineData(@"\Va.b", @"a\.b")]
    [InlineData("a^b$", @"a\^b$")]
    public void Vims_patterns_read_as_dotnets(string pattern, string regex) =>
        Assert.Equal(regex, VimPatterns.Translate(pattern, ignoreCase: false, smartCase: false).Regex);

    [Fact]
    public void A_pattern_with_no_counterpart_is_said_and_case_follows_its_flags_and_smartcase()
    {
        Assert.NotNull(VimPatterns.Translate(@"a\@<=b", false, false).Problem);
        Assert.NotNull(VimPatterns.Translate(@"a\&b", false, false).Problem);
        Assert.False(VimPatterns.Translate(@"two\c", false, false).MatchCase);
        Assert.True(VimPatterns.Translate("Two", ignoreCase: true, smartCase: true).MatchCase);
        Assert.False(VimPatterns.Translate("two", ignoreCase: true, smartCase: true).MatchCase);
    }

    [Fact]
    public void A_search_shows_its_matches_says_its_wrap_and_failure_and_noh_clears_them()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("one t|wo\ntwo");
        VimResult found = Press(vim, "/two<CR>", text, carets);
        Assert.True(found.Highlight is { IsEmpty: false, Regex: true }, "a search asked the editor to show no matches");
        VimResult wrapped = Press(vim, "n", text, SelectionSet.Single(8));
        Assert.Equal(("search hit BOTTOM, continuing at TOP", false), (wrapped.Message, wrapped.MessageIsError));
        VimResult missing = Press(vim, "/xyz<CR>", text, carets);
        Assert.Equal(("E486: Pattern not found: xyz", true), (missing.Message, missing.MessageIsError));
        Assert.True(Press(vim, ":noh<CR>", text, carets).Highlight is { IsEmpty: true }, ":noh left the matches shown");
        Assert.Equal("/fo", Press(vim, "/fo", text, carets) is { Taken: true } ? vim.CommandLine : null);
    }

    [Theory]
    [InlineData(":w<CR>", VimRequestKind.Save, null)]
    [InlineData(":q<CR>", VimRequestKind.Close, null)]
    [InlineData(":wq<CR>", VimRequestKind.SaveAndClose, null)]
    [InlineData(":x<CR>", VimRequestKind.SaveAndClose, null)]
    [InlineData(":wa<CR>", VimRequestKind.SaveAll, null)]
    [InlineData(":e src/Other.cs<CR>", VimRequestKind.Open, "src/Other.cs")]
    [InlineData("<C-o>", VimRequestKind.Older, null)]
    [InlineData("<Tab>", VimRequestKind.Newer, null)]
    public void Saving_closing_opening_and_the_ides_places_are_asked_of_the_host(string keys, VimRequestKind kind, string? path)
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|b");
        Assert.Equal(new VimRequest(kind, path), Press(new Vim(), keys, text, carets).Request);
    }

    [Theory]
    [InlineData(":frobnicate<CR>", "Vim mode does not do :frobnicate")]
    [InlineData(":qa<CR>", "Vim mode does not do :qa")]
    [InlineData(":%s/a/b/c<CR>", ":s's c flag (asking at each match) is not done; Find and Replace asks match by match")]
    [InlineData(":set sw=2<CR>", "sw is the file's indent, which Settings › Editor and an .editorconfig set")]
    [InlineData(":set nosuch<CR>", "E518: Unknown option: nosuch")]
    public void A_command_vim_mode_does_not_do_is_said(string keys, string said)
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|b");
        Assert.Equal(said, Press(new Vim(), keys, text, carets).Message);
    }

    [Fact]
    public void A_substitution_over_many_lines_is_one_edit()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|a\na\na\n");
        VimResult result = Press(new Vim(), ":%s/a/b/<CR>", text, carets);
        Assert.Equal(("b\nb\n|b\n", 1), (Marked.After(text, result.Edit), result.Edits?.Count));
        Assert.Equal("3 substitutions on 3 lines", result.Message);
    }

    [Fact]
    public void A_repeated_change_is_edits_of_one_step_and_a_plain_change_one_edit()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("one |two three");
        (text, carets) = Drive(vim, text, carets, "cwX<Esc>w");
        VimResult again = vim.Press(".", text, carets);
        Assert.True(again.Edits is { Count: > 1 } edits && edits.All(e => e.Kind == EditKind.Joined), "A repeated change was not one step of undo");
        Assert.Equal(EditKind.Other, vim.Press("x", text, carets).Edit?.Kind);
    }

    [Fact]
    public void The_clipboards_registers_ask_the_editor_to_write_and_read_the_system_clipboard()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|bc\ndef");
        VimResult yank = Press(vim, "\"+yy", text, carets);
        Assert.Equal(("abc\n", true), (yank.Clipboard?.Text, yank.Clipboard?.Linewise ?? false));
        VimResult put = Press(vim, "\"*p", text, carets);
        Assert.True(put.ReadsClipboard, "p from * did not ask for the clipboard");
        VimResult landed = vim.PutClipboard("xyz\n", text, carets);
        Assert.Equal("abc\nxyz\ndef", text.Apply(landed.Edit!.Edits).ToString());
    }

    [Fact]
    public void Registers_are_shared_by_every_vim_given_them_and_named_ones_append_with_a_capital()
    {
        VimRegisters shared = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|bc\ndef");
        Press(new Vim(shared), "\"ayy", text, carets);
        Press(new Vim(shared), "j\"Ayy", text, SelectionSet.Single(5));
        Assert.Equal("abc\ndef\n", shared.Get('a')?.Text);
        Assert.Equal("abc\ndef\n", shared.Unnamed?.Text);
    }

    [Fact]
    public void A_count_with_d_waits_and_escape_forgets_it()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|b");
        Assert.True(vim.Press("2", text, carets).Taken);
        Assert.True(vim.Press("d", text, carets).Taken);
        Assert.Equal("2d", vim.Pending);
        Assert.True(vim.Press("<Esc>", text, carets).Taken);
        Assert.Equal(string.Empty, vim.Pending);
    }

    [Fact]
    public void Keys_normal_mode_does_not_take_go_to_the_keymap()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|b");
        Assert.False(vim.Press("<D-s>", text, carets).Taken);
        Assert.False(vim.Press("<Esc>", text, carets).Taken);
        Assert.True(vim.Press("Q", text, carets).Taken);
        vim.Press("i", text, carets);
        Assert.False(vim.Press("x", text, carets).Taken);
    }

    [Theory]
    [InlineData("u", EditorActions.Undo, 1)]
    [InlineData("3u", EditorActions.Undo, 3)]
    [InlineData("<C-r>", EditorActions.Redo, 1)]
    public void Undo_and_redo_run_the_editors_own_actions(string keys, string action, int repeat)
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|b");
        VimResult result = VimResult.Left;
        foreach (string key in Keys(keys))
        {
            result = vim.Press(key, text, carets);
        }
        Assert.Equal((action, repeat), (result.Action, result.Repeat));
        Assert.Equal(VimMode.Normal, vim.Mode);
    }

    [Theory]
    [InlineData("  a|b\nc", "ox<Esc>", "  ab\n  |x\nc")]
    [InlineData("  a|b\nc", "Ox<Esc>", "  |x\n  ab\nc")]
    [InlineData("  a|b\nc", "o<Esc>", "  ab\n|\nc")]
    public void O_opens_a_line_with_its_indent_taken_back_where_nothing_was_typed(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    [Fact]
    public void Each_x_is_an_edit_of_its_own_kind_so_undo_takes_back_one_at_a_time()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|bc");
        Assert.Equal(EditKind.Other, new Vim().Press("x", text, carets).Edit?.Kind);
    }

    [Fact]
    public void Carets_settle_on_a_character_keeping_their_aim_and_a_selection_stays_whole()
    {
        (TextSnapshot text, _) = Marked.Parse("a|b\n\ncd");
        SelectionSet put = SelectionSet.Create([new Selection(2, 2, 40), new Selection(3, 3), new Selection(4, 6)], 0);
        SelectionSet settled = Vim.Settled(text, put);
        Assert.Equal([new Selection(1, 1, 40), new Selection(3, 3), new Selection(4, 6)], settled.Items);
        Assert.Same(settled, Vim.Settled(text, settled));
    }

    [Fact]
    public void An_insert_sessions_edits_are_one_step_of_undo_whatever_they_type_until_a_break()
    {
        TextDocument document = new("ab");
        SelectionSet carets = SelectionSet.Single(2);
        foreach (string typed in new[] { " one", "\n", "two  ", "three" })
        {
            TextEdit edit = new(document.Snapshot.Length, 0, typed);
            SelectionSet after = SelectionSet.Single(document.Snapshot.Length + typed.Length);
            document.Edit([edit], carets, after, EditKind.Joined);
            carets = after;
        }
        document.BreakUndoGroup();
        document.Edit([new TextEdit(document.Snapshot.Length, 0, "!")], carets, SelectionSet.Single(document.Snapshot.Length + 1), EditKind.Joined);
        Assert.NotNull(document.Undo());
        Assert.Equal("ab one\ntwo  three", document.Snapshot.ToString());
        Assert.Equal(SelectionSet.Single(2).Items, document.Undo()!.Items);
        Assert.Equal("ab", document.Snapshot.ToString());
    }

    [Theory]
    [InlineData("|one two three four five six seven\neight nine", "gqq", "one two three four\n|five six seven\neight nine")]
    [InlineData("|one two three four five six seven\neight nine\n\nten eleven", "gqap", "one two three four\nfive six seven eight\nnine\n|\nten eleven")]
    [InlineData("  |one two three four five six seven\neight nine\n\nten eleven", "gqip", "  one two three four\n  five six seven\n  |eight nine\n\nten eleven")]
    [InlineData("one two three fo|ur five six seven\neight nine\n\nten eleven", "gwip", "one two three fo|ur\nfive six seven eight\nnine\n\nten eleven")]
    [InlineData("    // |one two three four five six seven\n    // eight nine\nx", "gqip", "    // one two three\n    // four five six\n    // seven eight\n    // nine\n|x")]
    [InlineData("# |one two three four five six seven\n# eight nine", "gqip", "# one two three four\n# five six seven\n|# eight nine")]
    [InlineData("// |one two three\n# four five six seven", "gqip", "// one two three\n# four five six\n|# seven")]
    [InlineData("|one two.  three four five six seven", "gqq", "one two.  three four\n|five six seven")]
    [InlineData("|averyveryverylongwordthatgoeson two", "gqq", "averyveryverylongwordthatgoeson\n|two")]
    [InlineData("|one\ntwo\nthree", "gqj", "|one two\nthree")]
    [InlineData("|one\ntwo\nthree", "3gqq", "|one two three")]
    [InlineData("|one two three four\n    five six seven eight", "gqip", "one two three four\n|five six seven eight")]
    [InlineData("- |one two three four five six\n  seven", "gqip", "- one two three four\n  |five six seven")]
    [InlineData("/*\n * |one two three four five six\n * seven\n */", "gqip", "/*\n * one two three\n * four five six\n * seven\n |*/")]
    [InlineData("> |one two three four five six\n> seven", "gqip", "> one two three four\n|> five six seven")]
    [InlineData("|one two three four five six    \nseven", "gqip", "one two three four\n|five six    seven")]
    [InlineData("|one two three four five six\n\n\nseven", "gqG", "one two three four\nfive six\n\n\n|seven")]
    [InlineData("// a\n//\n// |b c d e f g h i j k l m n o p q r s t", "gqip", "// a\n//\n// b c d e f g h i j\n// k l m n o p q r s\n|// t")]
    [InlineData("|one two three four five six\nseven eight", "Vjgq", "one two three four\n|five six seven eight")]
    [InlineData("|one two three four five six\nseven eight", "vgq", "one two three four\n|five six\nseven eight")]
    [InlineData("|one two three four five six\nseven eight", "gqgq", "one two three four\n|five six\nseven eight")]
    [InlineData("|one two three four five six\nseven eight", "gqq.", "one two three four\n|five six\nseven eight")]
    [InlineData("|a\nb\n\nc d e f g h i j k l m n o p", "gqipj.", "a b\n|\nc d e f g h i j k l m n o p")]
    [InlineData("#include |one two three four five six", "gqq", "#include one two\n|three four five six")]
    public void Gq_wraps_each_paragraph_at_textwidth_keeping_its_comment_marks(string before, string keys, string after)
    {
        Vim vim = new();
        vim.Registers.Options.TextWidth = 20;
        Assert.Equal(after, Run(vim, before, keys));
    }

    [Fact]
    public void Gq_wraps_at_the_width_the_editor_gives_where_textwidth_is_not_set_and_the_languages_own_comment_mark()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|-- one two three four five six seven");
        VimTextView view = new(new IndentOptions()) { LineComment = "--", LineLength = 16 };
        Vim vim = new();
        foreach (string key in Keys("gqq"))
        {
            VimResult result = vim.Press(key, text, carets, view);
            foreach (EditPlan edit in result.Edits ?? [])
            {
                (text, carets) = (text.Apply(edit.Edits), edit.Selections);
            }
        }
        Assert.Equal("-- one two three\n-- four five six\n|-- seven", Marked.Show(text, carets));
    }

    [Theory]
    [InlineData("a\n|b\nc\nd", "==", 1, 1, "a\n|b\nc\nd")]
    [InlineData("a\n|b\nc\nd", "=j", 1, 2, "a\n|b\nc\nd")]
    [InlineData("a\n  |b\nc\nd", "=k", 0, 1, "|a\n  b\nc\nd")]
    [InlineData("a\n|b\nc\nd", "2==", 1, 2, "a\n|b\nc\nd")]
    [InlineData("a\n|b\nc\nd", "=G", 1, 3, "a\n|b\nc\nd")]
    [InlineData("a\n|b\nc\nd", "Vj=", 1, 2, "a\n|b\nc\nd")]
    [InlineData("|a\nb\n\nc", "=ip", 0, 1, "|a\nb\n\nc")]
    public void Equals_asks_the_host_to_lay_out_its_lines_and_lands_on_the_first_lines_first_non_blank(string before, string keys, int first, int last, string after)
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse(before);
        Vim vim = new();
        VimResult result = VimResult.Left;
        foreach (string key in Keys(keys))
        {
            result = vim.Press(key, text, carets);
            carets = result.Selections ?? carets;
        }
        Assert.Equal(VimRequestKind.Reformat, result.Request?.Kind);
        Assert.Equal([(first, last)], result.Request!.Lines);
        Assert.Equal(after, Marked.Show(text, carets));
    }

    [Fact]
    public void Dot_after_equals_asks_again_for_as_many_lines_from_the_caret()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|a\nb\nc\nd\ne");
        Vim vim = new();
        Press(vim, "=j", text, carets);
        Assert.Equal([(2, 3)], vim.Press(".", text, SelectionSet.Single(text.GetLineStart(2))).Request?.Lines);
    }

    [Fact]
    public void Equals_at_every_caret_names_each_stretch_of_lines_once()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|a\n|b\nc\nd\n|e\nf");
        VimResult result = Press(new Vim(), "=j", text, carets);
        Assert.Equal([(0, 2), (4, 5)], result.Request!.Lines);
    }

    [Fact]
    public void Action_asks_the_host_for_the_action_named_and_alone_says_how_it_is_used()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|a");
        VimResult result = Press(new Vim(), ":action Reformat Code<CR>", text, carets);
        Assert.Equal((VimRequestKind.Action, "Reformat Code"), (result.Request?.Kind, result.Request?.Path));
        VimResult alone = Press(new Vim(), ":action<CR>", text, carets);
        Assert.Null(alone.Request);
        Assert.True(alone.MessageIsError);
    }

    [Fact]
    public void Set_textwidth_is_said_back_and_refuses_a_value_that_is_no_number()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|a");
        Vim vim = new();
        Assert.Equal("textwidth=0", Press(vim, ":set tw?<CR>", text, carets).Message);
        Press(vim, ":set textwidth=72<CR>", text, carets);
        Assert.Equal("textwidth=72", Press(vim, ":set tw<CR>", text, carets).Message);
        Assert.StartsWith("E521", Press(vim, ":set tw=wide<CR>", text, carets).Message);
    }

    [Theory]
    [InlineData("nnoremap ,d dd", "|a\nb", ",d", "|b")]
    [InlineData("let mapleader = \",\"\nnnoremap <leader>x x", "|ab", ",x", "|b")]
    [InlineData("nnoremap <Leader>x x", "|ab", "\\x", "|b")]
    [InlineData("let mapleader=\"\\<Space>\"\nnnoremap <leader>d dd", "|a\nb", " d", "|b")]
    [InlineData("nmap Q dd\nnmap X Q", "|a\nb", "X", "|b")]
    [InlineData("nnoremap Q dd\nnnoremap X Q", "|a\nb", "X", "|a\nb")]
    [InlineData("nnoremap ,d dd", "|a\nb\nc", "2,d", "|c")]
    [InlineData("nnoremap x dd", "|abxc", "fx", "ab|xc")]
    [InlineData("nnoremap x <Nop>", "|ab", "x", "|ab")]
    [InlineData("nnoremap ,a x\nnnoremap ,ab dd", "|abc\nd", ",al", "b|c\nd")]
    [InlineData("nnoremap ,a x\nnnoremap ,ab dd", "|abc\nd", ",ab", "|d")]
    [InlineData("nnoremap w b", "a |bc de", "dw", "a |de")]
    [InlineData("onoremap w iw", "a b|cd e", "dw", "a | e")]
    [InlineData("vnoremap ,y d", "|abc", "vl,y", "|c")]
    [InlineData("inoremap jk <Esc>", "|ab", "ijkx", "|b")]
    [InlineData("inoremap jk <Esc>", "|ab", "ijxy<Esc>", "jx|yab")]
    [InlineData("nmap j j", "|a\nb", "j", "a\n|b")]
    [InlineData("\" a comment\n\n:nnoremap <silent> ,d dd", "|a\nb", ",d", "|b")]
    public void Mappings_press_what_they_map_to_in_their_modes(string file, string before, string keys, string after)
    {
        VimMappings mappings = VimMappings.Read(file);
        Assert.Empty(mappings.Problems);
        Vim vim = new();
        vim.Registers.Mappings = mappings;
        Assert.Equal(after, Run(vim, before, keys));
    }

    [Fact]
    public void A_mappings_files_bad_lines_are_passed_over_with_why_and_its_set_lines_set_options()
    {
        VimMappings read = VimMappings.Read("nnoremap\nfoo bar\nlet x = 1\nnnoremap ,d dd\nset ignorecase tw=72\nset nosuch\nnmap <Plug>(x) y\nnunmap ,q");
        Assert.Equal([1, 2, 3, 7, 8], read.Problems.Select(p => p.Line));
        Assert.Single(read.All);
        VimRegisters registers = new();
        IReadOnlyList<string> refused = Vim.SetAll(registers, read.Settings);
        Assert.True(registers.Options.IgnoreCase && registers.Options.TextWidth == 72, "set ignorecase tw=72 set neither");
        Assert.Equal(["E518: Unknown option: nosuch"], refused);
    }

    [Theory]
    [InlineData("inoremap jk <Esc>", "|ab", "ij<Wait>k<Esc>", "j|kab")]
    [InlineData("inoremap jk <Esc>", "|ab", "ijk<Wait>x", "|b")]
    [InlineData("nnoremap ,a x\nnnoremap ,ab dd", "|abc\nd", ",a<Wait>", "|bc\nd")]
    [InlineData("nnoremap ,dd x", "|ab cd", ",d<Wait>w", "|cd")]
    [InlineData("inoremap jj X\ninoremap jjk Y", "|ab", "ijj<Wait><Esc>", "|Xab")]
    [InlineData("inoremap jj X\ninoremap jjk Y", "|ab", "ijj<Wait>k<Esc>", "X|kab")]
    public void Keys_held_toward_a_mapping_mean_themselves_once_timeoutlen_passes(string file, string before, string keys, string after)
    {
        Vim vim = new();
        vim.Registers.Mappings = VimMappings.Read(file);
        Assert.Equal(after, Run(vim, before, keys));
        Assert.False(vim.AwaitsMapping, "Keys were still held toward a mapping once the wait had passed");
    }

    [Fact]
    public void Timeout_and_timeoutlen_are_set_and_said_and_ttimeoutlen_is_taken()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|a");
        Vim vim = new();
        Assert.Equal("timeout  timeoutlen=1000", Press(vim, ":set to? tm?<CR>", text, carets).Message);
        Assert.Empty(Vim.SetAll(vim.Registers, ["notimeout timeoutlen=200", "ttimeout ttimeoutlen=50"]));
        Assert.True(vim.Registers.Options is { Timeout: false, TimeoutLength: 200 }, "set notimeout timeoutlen=200 set neither");
        Assert.Equal("ttimeout  ttimeoutlen=50", Press(vim, ":set ttimeout? ttm?<CR>", text, carets).Message);
        Assert.StartsWith("E487", Press(vim, ":set tm=-1<CR>", text, carets).Message);
    }

    [Fact]
    public void Keys_held_toward_a_mapping_show_as_pending()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|ab");
        Vim vim = new();
        vim.Registers.Mappings = VimMappings.Read("nnoremap ,dd x");
        Assert.True(Press(vim, ",d", text, carets).Taken);
        Assert.Equal(",d", vim.Pending);
    }

    [Fact]
    public void A_macro_keeps_a_ctrl_key_it_recorded_as_one_key()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|ab");
        Vim vim = new();
        Press(vim, "qa<C-o>q", text, carets);
        Assert.Equal(["<C-o>"], Press(vim, "@a", text, carets).Replay);
    }

    /// <summary>The words a read-only text's refusals say in these tests.</summary>
    private const string ReadOnlyWords = "One.txt is read-only";

    [Theory]
    [InlineData("|one two\nthree", "x", "|one two\nthree")]
    [InlineData("o|ne two\nthree", "X", "|one two\nthree")]
    [InlineData("one |two\nthree", "db", "|one two\nthree")]
    [InlineData("one |two\nthree", "dw", "one |two\nthree")]
    [InlineData("on|e two\nthree", "dd", "|one two\nthree")]
    [InlineData("on|e two\nthree", "dj", "on|e two\nthree")]
    [InlineData("on|e two\nthree", "cc", "|one two\nthree")]
    [InlineData("on|e two\nthree", ">>", "|one two\nthree")]
    [InlineData("on|e two\nthree", "gUU", "|one two\nthree")]
    [InlineData("on|e two\nthree", "~", "on|e two\nthree")]
    [InlineData("|one two\nthree", "J", "|one two\nthree")]
    [InlineData("one |two\nthree", "vhd", "one| two\nthree")]
    [InlineData("on|e two\nthree", "Vd", "|one two\nthree")]
    [InlineData("one two\nt|hree", "<C-v>kld", "o|ne two\nthree")]
    [InlineData("on|e two\nthree", "A", "on|e two\nthree")]
    [InlineData("on|e two\nthree", "o", "on|e two\nthree")]
    [InlineData("on|e two\nthree", "u", "on|e two\nthree")]
    public void A_read_only_text_refuses_each_change_leaving_the_caret_where_vim_leaves_it(string before, string keys, string after)
    {
        Vim vim = new();
        vim.Registers.Yank(null, new VimRegister(["REG"], false));
        Assert.Equal(after, Run(vim, before, keys, new VimTextView(new IndentOptions()) { Refuses = ReadOnlyWords }));
        Assert.Equal(VimMode.Normal, vim.Mode);
        Assert.Equal("REG", vim.Registers.Get(null)!.Text);
    }

    [Fact]
    public void A_read_only_text_says_why_it_refuses_and_lets_motions_and_yanks_be()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|one two\nthree");
        Vim vim = new();
        VimTextView readOnly = new(new IndentOptions()) { Refuses = ReadOnlyWords };
        VimResult refused = vim.Press("x", text, carets, readOnly);
        Assert.True(refused is { Taken: true, Edits: null, Message: ReadOnlyWords, MessageIsError: true }, $"x on a read-only text came to {refused}");
        Assert.Equal(EditorActions.Undo, vim.Press("u", text, carets).Action);
        Assert.Null(vim.Press("u", text, carets, readOnly).Action);
        Assert.Null(vim.Press("<C-r>", text, carets, readOnly).Action);
        // A change made where the text took it is refused again by . where it refuses.
        Assert.NotNull(vim.Press("x", text, carets).Edits);
        Assert.Equal(ReadOnlyWords, vim.Press(".", text, carets, readOnly).Message);
        Assert.Equal("one |two\nthree", Run(vim, "|one two\nthree", "wyw", readOnly));
        Assert.Equal("two", vim.Registers.Get(null)!.Text);
        foreach ((string command, string after) in new[] { (":s/one/1/", "|one two\nthree"), (":%d", "|one two\nthree"), (":g/t/d", "one two\n|three"),
            (":1,2j", "|one two\nthree"), (":normal x", "|one two\nthree") })
        {
            Assert.Equal(after, Run(vim, "|one two\nthree", command + "<CR>", readOnly));
        }
    }

    [Theory]
    [InlineData("%s/one/1/ge | update", new[] { "%s/one/1/ge", "update" })]
    [InlineData("s/a\\|b/X/g | normal Ay|z", new[] { "s/a\\|b/X/g", "normal Ay|z" })]
    [InlineData("'<,'>normal A|", new[] { "'<,'>normal A|" })]
    [InlineData("g/x|y/s/a/b/ | w", new[] { "g/x|y/s/a/b/ | w" })]
    [InlineData("/a|b/,$d|w", new[] { "/a|b/,$d", "w" })]
    [InlineData("w||", new[] { "w" })]
    [InlineData("s/a|b/X/ | s/c/d/", new[] { "s/a|b/X/", "s/c/d/" })]
    [InlineData("%s/a/x|y/g|$d", new[] { "%s/a/x|y/g", "$d" })]
    [InlineData("s#a|b#c|d#|w", new[] { "s#a|b#c|d#", "w" })]
    [InlineData("e One\\|Two.cs | w", new[] { "e One|Two.cs", "w" })]
    [InlineData("cdo s/a/b/ | update", new[] { "cdo s/a/b/ | update" })]
    [InlineData("cfdo normal x | update", new[] { "cfdo normal x | update" })]
    [InlineData("argdo %s/a/b/ge | update", new[] { "argdo %s/a/b/ge | update" })]
    public void A_command_line_parts_at_each_bar_but_where_its_command_takes_the_rest(string line, string[] commands)
    {
        Assert.Equal(commands, Vim.Bars(line));
    }

    /// <summary>A line of several commands asks the host to run them in turn (the harness runs each as the window does),
    /// and comes to what Vim 9.1 comes to: :s takes its pattern and replacement whole, a command that fails ends the line,
    /// :normal takes the rest as its keys.</summary>
    [Theory]
    [InlineData("|ab c\nc a", ":s/a|b/X/ | s/c/d/<CR>", "|ab c\nc a")]
    [InlineData("|a b c", ":s/\\va|b/X/g | s/c/d/<CR>", "|X X d")]
    [InlineData("|ab c\nc a", ":s/zzz/y/ | s/c/d/<CR>", "|ab c\nc a")]
    [InlineData("|ab c\nc a", ":%s/a/b/g|$d<CR>", "|bb c")]
    [InlineData("|one\ntwo\nthree", ":2d | 1s/o/0/<CR>", "|0ne\nthree")]
    [InlineData("|one\ntwo\nthree", ":s/o/0/|2|s/w/W/<CR>", "0ne\n|tWo\nthree")]
    [InlineData("|one two\nthree", ":normal x | x<CR>", "n| two\nthree")]
    public void Commands_joined_by_a_bar_on_any_command_line_come_to_what_vims_do(string before, string keys, string after)
    {
        Assert.Equal(after, Run(before, keys));
    }

    [Fact]
    public void A_bar_in_a_substitutions_replacement_is_its_own_and_one_after_it_runs_the_next_command()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Drive(vim, TextSnapshot.From("a b"), SelectionSet.Single(0), ":s/a/x|y/ | s/b/z/<CR>");
        Assert.Equal(("x|y z", 0), (text.ToString(), carets.Primary.Caret));
        Assert.Equal((VimRequestKind.Commands, ":s/a/x|y/ | s/b/z/"), Press(vim, ":s/a/x|y/ | s/b/z/<CR>", text, carets).Request is { } asked ? (asked.Kind, ":" + asked.Path) : default);
        Assert.Equal("s/a/x|y/ | s/b/z/", vim.Registers.LastCommand);
    }

    [Fact]
    public void Bufdo_and_windo_ask_the_host_to_run_their_command_line_in_each_file_and_update_saves()
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|a");
        Vim vim = new();
        Assert.Equal((VimRequestKind.EachFile, "%s/a/b/ge | update"), Press(vim, ":bufdo %s/a/b/ge | update<CR>", text, carets).Request is { } each ? (each.Kind, each.Path) : default);
        Assert.Equal((VimRequestKind.EachEditor, "normal x"), Press(vim, ":windo normal x<CR>", text, carets).Request is { } split ? (split.Kind, split.Path) : default);
        Assert.Equal("E471: Argument required", Press(vim, ":bufdo<CR>", text, carets).Message);
        foreach ((string typed, VimRequestKind kind) in new[] { ("argdo", VimRequestKind.EachTab), ("cdo", VimRequestKind.EachPlace), ("cfd", VimRequestKind.EachPlaceFile),
            ("cfdo", VimRequestKind.EachPlaceFile) })
        {
            Assert.Equal((kind, "s/a/b/ | update"), Press(vim, $":{typed} s/a/b/ | update<CR>", text, carets).Request is { } place ? (place.Kind, place.Path) : default);
            Assert.Equal("E471: Argument required", Press(vim, $":{typed}<CR>", text, carets).Message);
        }
        Assert.Equal(VimRequestKind.Save, Press(vim, ":up<CR>", text, carets).Request?.Kind);
        Assert.Equal(("|b", "up"), (Marked.Show(text.Apply(vim.RunCommand("s/a/b/", text, carets).Edit!.Edits), carets), vim.Registers.LastCommand));
    }

    [Fact]
    public void A_huge_count_ends_at_the_texts_edge()
    {
        Assert.Equal("a b |c", Run("|a b c", "99999999999w"));
    }

    private static VimResult Press(Vim vim, string keys, TextSnapshot text, SelectionSet carets)
    {
        VimResult result = VimResult.Left;
        foreach (string key in Keys(keys))
        {
            result = vim.Press(key, text, carets);
        }
        return result;
    }

    /// <summary>The keys pressed in turn on the marked text, insert mode's typing standing in for the editor's.</summary>
    private static string Run(string before, string keys) => Run(new Vim(), before, keys);

    private static string Run(Vim vim, string before, string keys, IVimView? view = null)
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse(before);
        (text, carets) = Drive(vim, text, carets, Keys(keys), stopAtFailure: false, view);
        return Marked.Show(text, carets);
    }

    /// <summary>The keys through Vim as an editor takes them: what Vim does done, and in insert mode each key typed as
    /// the editor types it, Enter carrying its line's indent and Backspace taking a character back.</summary>
    public static (TextSnapshot Text, SelectionSet Carets) Drive(Vim vim, TextSnapshot text, SelectionSet carets, string keys, bool stopAtFailure = false) =>
        Drive(vim, text, carets, Keys(keys), stopAtFailure);

    private static (TextSnapshot Text, SelectionSet Carets) Drive(Vim vim, TextSnapshot text, SelectionSet carets, IEnumerable<string> keys, bool stopAtFailure,
        IVimView? view = null)
    {
        foreach (string key in keys)
        {
            VimResult result = key == Waited ? vim.TimedOut() : vim.Press(key, text, carets, view);
            if (result.Replay is { } replay)
            {
                (text, carets) = Drive(vim, text, carets, replay, stopAtFailure, view);
                continue;
            }
            if (result.Request is { Kind: VimRequestKind.Commands, Path: { } line })
            {
                // As the window runs them: each in turn on what the one before left, the first that fails ending the line.
                foreach (string command in Vim.Bars(line))
                {
                    VimResult ran = vim.RunCommand(command, text, carets, view);
                    if (ran.Replay is { } keysOf)
                    {
                        (text, carets) = Drive(vim, text, carets, keysOf, stopAtFailure, view);
                    }
                    foreach (EditPlan edit in ran.Edits ?? [])
                    {
                        (text, carets) = (text.Apply(edit.Edits), edit.Selections);
                    }
                    if (ran.Edits is not { Count: > 0 } && ran.Selections is { } at)
                    {
                        carets = at;
                    }
                    carets = Vim.Settled(text, carets);
                    if (ran.MessageIsError)
                    {
                        break;
                    }
                }
                continue;
            }
            if (stopAtFailure && ReferenceEquals(result, VimResult.Held) && vim.Pending.Length == 0 && vim.Mode == VimMode.Normal && key != Vim.Escape)
            {
                // A command that fails ends the keys, as Vim's beep flushes what was typed ahead.
                break;
            }
            if (!result.Taken && vim.Mode == VimMode.Insert)
            {
                EditPlan? typed = key switch
                {
                    "<CR>" => TextEditing.NewLine(text, carets, "\n", new IndentOptions()),
                    "<BS>" => TextEditing.Backspace(text, carets, new IndentOptions()),
                    "<Tab>" => TextEditing.Insert(text, carets, "\t"),
                    _ => TextEditing.Insert(text, carets, key),
                };
                if (typed is not null)
                {
                    (text, carets) = (text.Apply(typed.Edits), typed.Selections);
                }
                continue;
            }
            foreach (EditPlan edit in result.Edits ?? [])
            {
                (text, carets) = (text.Apply(edit.Edits), edit.Selections);
            }
            if (result.Edits is not { Count: > 0 } && result.Selections is { } moved)
            {
                carets = moved;
            }
            if (vim.Mode == VimMode.Normal)
            {
                carets = Vim.Settled(text, carets);
            }
        }
        return (text, carets);
    }

    /// <summary>Not a key: timeoutlen passing with no key pressed, as the editor tells Vim mode.</summary>
    private const string Waited = "<Wait>";

    /// <summary>The keys of Vim's notation one by one: a named key ("&lt;Esc&gt;") whole, any other character alone.</summary>
    public static IEnumerable<string> Keys(string keys)
    {
        string[] named = ["<Esc>", "<CR>", "<BS>", "<Tab>", "<Del>", "<C-r>", "<C-v>", "<C-o>", "<C-i>", "<C-u>", "<C-w>", "<Up>", "<Down>", "<D-s>", Waited];
        for (int i = 0; i < keys.Length; i++)
        {
            string? name = keys[i] == '<' ? Array.Find(named, n => string.CompareOrdinal(keys, i, n, 0, n.Length) == 0) : null;
            yield return name ?? keys[i].ToString();
            i += name is null ? 0 : name.Length - 1;
        }
    }
}
