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

    /// <summary>An é written as e and a combining acute.</summary>
    private const string Cafe = "cafe\u0301";

    /// <summary>Hindi's namaste, two of its letters carrying a nonspacing mark (U+094D, U+0947).</summary>
    private const string Namaste = "नमस्ते";

    /// <summary>Hindi's namaskar, its ा a spacing mark (U+093E).</summary>
    private const string Namaskar = "नमस्कार";

    /// <summary>A heart and the selector asking for its emoji look, a nonspacing mark that is no letter's.</summary>
    private const string Heart = "\u2764\uFE0F";

    /// <summary>A letter's marks go with it: words, word objects and * take them whole, an end lands on the letter they
    /// mark, and a mark on an emoji keeps it out of the word beside it, as Vim 9.1's do.</summary>
    [Theory]
    [InlineData($"|{Cafe} x", "w", $"{Cafe} |x")]
    [InlineData($"|{Cafe} x", "e", "caf|e\u0301 x")]
    [InlineData($"{Cafe} |x", "b", $"|{Cafe} x")]
    [InlineData($"{Cafe} |x", "ge", "caf|e\u0301 x")]
    [InlineData($"|{Cafe}", "e", "caf|e\u0301")]
    [InlineData($"|{Cafe}.b", "w", $"{Cafe}|.b")]
    [InlineData($"|{Cafe}.b", "e", "caf|e\u0301.b")]
    [InlineData($"x |{Cafe}y", "E", $"x {Cafe}|y")]
    [InlineData($"|{Cafe} x", "ve", "«cafe\u0301| x")]
    [InlineData($"|{Namaste} x", "w", $"{Namaste} |x")]
    [InlineData($"|{Namaste} x", "e", "नमस्|ते x")]
    [InlineData($"{Namaste} |x", "b", $"|{Namaste} x")]
    [InlineData($"{Namaste} |x", "ge", "नमस्|ते x")]
    [InlineData($"|{Namaskar} x", "e", "नमस्का|र x")]
    [InlineData($"{Namaskar} |x", "ge", "नमस्का|र x")]
    [InlineData($"|{Heart}abc x", "w", $"{Heart}|abc x")]
    [InlineData($"|{Heart}abc x", "e", $"{Heart}ab|c x")]
    [InlineData($"a {Heart}|abc", "b", $"a |{Heart}abc")]
    [InlineData($"x {Heart}|abc", "ge", $"x |{Heart}abc")]
    [InlineData($"|{Cafe} {Cafe}", "cwX<Esc>", $"|X {Cafe}")]
    [InlineData("ca|fe\u0301 x", "diw", "| x")]
    [InlineData("ca|fe\u0301 x", "daw", "|x")]
    [InlineData($"|{Namaste} x {Namaste}", "*", $"{Namaste} x |{Namaste}")]
    [InlineData($"{Namaste} x |{Namaste}", "#", $"|{Namaste} x {Namaste}")]
    [InlineData($"|{Namaskar} {Namaskar}", "*", $"{Namaskar} |{Namaskar}")]
    [InlineData($"|{Cafe} x {Cafe}", "*", $"{Cafe} x |{Cafe}")]
    [InlineData($"|cafe x {Cafe} cafe", "*", $"cafe x {Cafe} |cafe")]
    [InlineData($"|{Cafe} x cafe {Cafe}", "*", $"{Cafe} x cafe |{Cafe}")]
    [InlineData($"|x {Cafe}y {Cafe}", "/\\k\\+y<CR>", $"x |{Cafe}y {Cafe}")]
    public void Word_motions_objects_and_star_take_a_letters_marks_with_it_as_vims_do(string before, string keys, string after) =>
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

    /// <summary>The less common corners: gI inserting at column 1, a visual put from an empty register deleting what is
    /// shown, a sentence motion or object running to the end of a text with no line break after it taking its last
    /// character, a block found after the caret in visual line and block mode turning them charwise (and the block a longer
    /// stretch is taken to), a count's blocks ahead until a bracket closes, and a count of J past the last line going to
    /// its start.</summary>
    [Theory]
    [InlineData("a b c d |e f\ng h", "gI7<Esc>", "|7a b c d e f\ng h")]
    [InlineData("  a b c d |e f\ng h", "3gI78<Esc>", "78787|8  a b c d e f\ng h")]
    [InlineData("  a |b\n  c d", "gIx<Esc>j.", "x  a b\n|x  c d")]
    [InlineData("ab|c def\nxyz", "vlp", "ab|def\nxyz")]
    [InlineData("ab|c def\nxyz", "Vjp", "|")]
    [InlineData("ab|c def\nxyz", "<C-v>jp", "ab| def\nxy")]
    [InlineData("ab|c def\nxyz", "vlpp", "abdc| ef\nxyz")]
    [InlineData("ab|c def\nxyz", "vlPp", "ab|def\nxyz")]
    [InlineData("ab|c def\nxyz", "vlp.", "ab|f\nxyz")]
    [InlineData("ab|c def\nxyz", "v\"ap", "ab| def\nxyz")]
    [InlineData("a b c d e f\ng h i j k l\n|m n o p q r", "gU)", "a b c d e f\ng h i j k l\n|M N O P Q R")]
    [InlineData("one. tw|o three", "d)", "one. t|w")]
    [InlineData("a. |b", "d)", "a.| ")]
    [InlineData("o|ne. two three", "d2)", "|o")]
    [InlineData("one. two |three", "das", "one|.")]
    [InlineData("one. two |three", "dis", "one.| ")]
    [InlineData("one. tw|o three", "y)P", "one. two thre|eo three")]
    [InlineData("f|oo(a, b) x\nnext", "Vi(d", "foo(|) x\nnext")]
    [InlineData("f|oo(a, b) x\nnext", "<C-v>i(d", "foo(|) x\nnext")]
    [InlineData("f|oo(a, b) x\nnext", "Va(d", "foo| x\nnext")]
    [InlineData("f|oo \"a b\" x\nnext", "Vi\"d", "foo \"|\" x\nnext")]
    [InlineData("f|oo bar\nnext", "Viwd", "| bar\nnext")]
    [InlineData("f|oo(a) bar\nnext\nx", "Vji(d", "foo(|) bar\nnext\nx")]
    [InlineData("f|oo(ab) bar\nnext\nx", "Vji(d", "|x")]
    [InlineData("f|oo(a, b) x\nnext", "vlli(d", "foo(|) x\nnext")]
    [InlineData("foo(a, |b) x\nnext", "vlli(d", "foo(|) x\nnext")]
    [InlineData("x (a [b|] c) y", "vi(i(d", "x (|) y")]
    [InlineData("f|oo(a, (b)) x\nnext", "d2i(", "foo(a, (|)) x\nnext")]
    [InlineData("f|oo(a, (b)) x\nnext", "V2i(d", "foo(a, (|)) x\nnext")]
    [InlineData("|d foo(a) (b)", "d2i(", "|d foo(a) (b)")]
    [InlineData("|d) foo(a) (b)", "di(", "|d) foo(a) (b)")]
    [InlineData("a b\n  c |d", "3J", "a b\n|  c d")]
    [InlineData("a b\nc |d", "3gJ", "a b\n|c d")]
    [InlineData("a b\nc |d", "2J", "a b\nc |d")]
    [InlineData("a |b\nc d", "3J.", "a b| c d")]
    [InlineData("a |b\nc d\ne\nf", "5Jk.", "|a b c d e f")]
    [InlineData("a b\nc |d", "3Jk.", "a b| c d")]
    [InlineData("a b\nc |d", "2Jk.", "a |b\nc d")]
    [InlineData("a |b\nc d\ne\nf\ng\nh", "5Jj.", "a b c d e f g\n|h")]
    [InlineData("a |b\nc d\ne f\ng\nh", "4Jk.", "a b c d e f g| h")]
    public void Less_common_corners_do_what_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>Visual block mode over lines of other lengths: a line too short for the column aimed at puts the block's
    /// cursor just past its end, an operator leaves the caret at the block's top left, I, c and C reach a line ending
    /// where the block begins, and whole lines put over a block go above or below it.</summary>
    [Theory]
    [InlineData("int main(void)\n{\n   | foo(a, b);\n    if (x) { return \"hi\"; }\n}", "<C-v>krk", "int main(void)\n|{\n kkkfoo(a, b);\n    if (x) { return \"hi\"; }\n}")]
    [InlineData("def f(x):\n   | return x + 1\n\n\nclass A:\n    pass", "<C-v>jrk3l", "def f(x):\nkkk|kreturn x + 1\n\n\nclass A:\n    pass")]
    [InlineData("foo_bar = baz(qux[0], {k: v})\n\n | // comment here\nend", "<C-v>kox3l", "foo_bar = baz(qux[0], {k: v})\n|\n// comment here\nend")]
    [InlineData("foo_bar = baz(qux[0], {k: v})\n\n  // co|mment here\nend", "<C-v>jy", "foo_bar = baz(qux[0], {k: v})\n\n  /|/ comment here\nend")]
    [InlineData("int main(void)\n{\n    foo(a, b);\n    if (x) { retu|rn \"hi\"; }\n}", "yy<C-v>koU4|", "int main(void)\n{\n   | foo(a, b);\n    if (x) { rETURn \"hi\"; }\n}")]
    [InlineData("abcdef\nab\nabc|def", "<C-v>kkd", "abc|ef\nab\nabcef")]
    [InlineData("abcdef\nab\nabc|def", "<C-v>kd", "abcdef\na|b\nabef")]
    [InlineData("abcdef\nab\nabc|def", "<C-v>kkjd", "abcdef\na|b\nabef")]
    [InlineData("abcdef\n\nabc|def", "<C-v>kkd", "abc|ef\n\nabcef")]
    [InlineData("ab\n\n|cd", "<C-v>kkIQ<Esc>", "|Qab\nQ\nQcd")]
    [InlineData("ab\n\n|cd", "<C-v>kIQ<Esc>", "ab\n|Q\nQcd")]
    [InlineData("abc\nab\n|abcd", "<C-v>kklIQ<Esc>", "|Qabc\nQab\nQabcd")]
    [InlineData("abc|d\nab\nxy", "<C-v>jjCQ<Esc>", "ab|Q\nabQ\nxyQ")]
    [InlineData("ab|cd\nab\nxy", "<C-v>jjCQ<Esc>", "ab|Q\nabQ\nxyQ")]
    [InlineData("ab|cd\nab\nxy", "<C-v>jjcQ<Esc>", "ab|Qd\nabQ\nxyQ")]
    [InlineData("ab|cd\nab\nxy", "<C-v>jjAQ<Esc>", "ab|cQd\nab Q\nxy Q")]
    [InlineData("ab|cd\nab\nxy", "<C-v>jj$AQ<Esc>", "ab|cdQ\nabQ\nxyQ")]
    [InlineData("ab|cd\na\nxyz", "<C-v>jjIQ<Esc>", "ab|Qcd\na\nxyQz")]
    [InlineData("a b c d e f\ng h i j k l\nm n o p| q r", "yy<C-v>hPd$", "a b c d e f\ng h i j k l\n|\nm n o q r")]
    [InlineData("def f(x):\n    return x + 1\n\n\ncl|ass A:\n    pass", "yG<C-v>2lp", "def f(x):\n    return x + 1\n\n\ncl A:\n|class A:\n    pass\n    pass")]
    [InlineData("  alpha.beta(gamma, delta)\n  [1|, 2, 3]\n  'quoted text' and \"more\"", "<C-v>h$dy_", "  alpha.beta(gamma, delta)\n  [|1\n  'quoted text' and \"more\"")]
    [InlineData("ab|c\nxyz", "<C-v>$d", "a|b\nxyz")]
    [InlineData("ab|c\nxyz", "<C-v>h$d", "a|b\nxyz")]
    [InlineData("ab|c\nxyz", "<C-v>h$dy_", "a|b\nxyz")]
    [InlineData("abcd\nab\nab|cd", "<C-v>kkIQ<Esc>", "ab|Qcd\nabQ\nabQcd")]
    [InlineData("abcd\na\nab|cd", "<C-v>kkIQ<Esc>", "ab|Qcd\na\nabQcd")]
    [InlineData("abcd|e\nab\nxyzw", "<C-v>jjcQ<Esc>", "abcd|Q\nab\nxyzwQ")]
    [InlineData("abcd|e\nab\nxyzwv", "<C-v>jjlcQ<Esc>", "abcd|Q\nab\nxyzwQ")]
    [InlineData("abcd|e\nab\nxyzwv", "<C-v>jjCQ<Esc>", "abcd|Q\nab\nxyzwQ")]
    [InlineData("ab|cd\nab\nxy", "<C-v>jjsQ<Esc>", "ab|Qd\nabQ\nxyQ")]
    [InlineData("ab|cd\n\nxy", "<C-v>jjcQ<Esc>", "ab|Qd\n\nxyQ")]
    [InlineData("|abcd\n\nxy", "<C-v>jjcQ<Esc>", "|Qbcd\nQ\nQy")]
    [InlineData("|abcd\n\nxy", "<C-v>jjCQ<Esc>", "|Q\nQ\nQ")]
    [InlineData("abcd|e\nab\nxyzwv", "<C-v>jjcQ<Esc>j.", "abcdQ\na|Q\nxQzwQ")]
    [InlineData("ab|cd\nxy\nzz", "yy<C-v>jP", "|abcd\nabd\nxy\nzz")]
    [InlineData("ab|cd\nxy\nzz", "yy<C-v>jp", "abd\nxy\n|abcd\nzz")]
    [InlineData("ab|cd\nxy", "yy<C-v>jp", "abd\nxy\n|abcd")]
    [InlineData("|abcd\nxy", "jyyk<C-v>lP", "|xy\ncd\nxy")]
    [InlineData("one two three\nfour five six\n\nseven eigh|t", "<C-v>$kP", "one two three\nfour five six\n|\n")]
    [InlineData("one two three\nfour five six\n\nseven eigh|t", "<C-v>$kd", "one two three\nfour five six\n|\n")]
    [InlineData("ab\n\nab|cd", "<C-v>k$d", "ab\n|\n")]
    [InlineData("ab\n\nab|cd", "<C-v>kd", "ab\n|\nd")]
    public void A_block_over_lines_of_other_lengths_acts_as_vims_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A yank of nothing (yh at a line's start, y0 there, y` to the caret's own mark) still yanks, leaving the
    /// unnamed register and 0 empty, so a put after it puts nothing, as Vim's does.</summary>
    [Theory]
    [InlineData("|abc def", "ylyhP", "|abc def")]
    [InlineData("|abc def", "ylyhp", "|abc def")]
    [InlineData("|abc def", "yly0P", "|abc def")]
    [InlineData("|abc def", "yly0\"0P", "|abc def")]
    [InlineData("|abc def", "yly0\"1P", "|abc def")]
    [InlineData("abc\n|\ndef", "kylj0ylP", "abc\n|\ndef")]
    [InlineData("abc\n|\ndef", "kylj0ylkp", "|abc\n\ndef")]
    [InlineData("|abc def", "ylwmby`bP", "abc |def")]
    [InlineData("|abc def", "ylwmby`bvp", "abc |ef")]
    public void A_yank_of_nothing_empties_its_register(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A yank over _ leaves the caret where it was, as yy does.</summary>
    [Theory]
    [InlineData("a b|c\nxyz", "yy", "a b|c\nxyz")]
    [InlineData("a b|c\nxyz", "y_", "a b|c\nxyz")]
    [InlineData("a b|c\nxyz", "yj", "a b|c\nxyz")]
    [InlineData("a b\nxy|z", "yk", "a |b\nxyz")]
    [InlineData("  a b|c\nxyz", "y_", "  a b|c\nxyz")]
    public void A_yank_of_the_caret_line_leaves_the_caret(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A change of case over nothing at a line's start takes the first character on the text's first line and
    /// the whole line on any other, as Vim's does.</summary>
    [Theory]
    [InlineData("f|oo_bar = baz(qux[0], {k: v})\n\n  // comment here\nend", "bg~|", "|Foo_bar = baz(qux[0], {k: v})\n\n  // comment here\nend")]
    [InlineData("one two three\nfour five six\n\n|seven eight", "Gg~0", "one two three\nfour five six\n\n|SEVEN EIGHT")]
    [InlineData("x = (a + (b * c)) - d;\ny = [e, f];\n|z = \"g (h) i\";", "gU03~", "x = (a + (b * c)) - d;\ny = [e, f];\nz =| \"G (H) I\";")]
    [InlineData("|seven eight", "g~0", "|Seven eight")]
    [InlineData("a\n|seven eight", "gu0", "a\n|seven eight")]
    public void A_change_of_case_over_nothing_at_a_lines_start_does_what_vims_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A sentence ending its line where the next one begins the next line, or an empty line does, ends there:
    /// an operator from the line's start takes whole lines, from inside it the text to the line's end, and visual mode
    /// selects to the line's end; as takes the blanks before where only a line break stands after.</summary>
    [Theory]
    [InlineData("foo_bar = baz(|qux[0], {k: v})\n\n  // comment here\nend", "das", "|\n  // comment here\nend")]
    [InlineData("foo_ba|r = baz(qux[0], {k: v})\n\n  // comment here\nend", "dis", "|\n  // comment here\nend")]
    [InlineData("one two |three\nfour five six\n\nseven eight", "das", "|\nseven eight")]
    [InlineData("one two |three\nfour five six\n\nseven eight", "dis", "|\nseven eight")]
    [InlineData("one two |three\nfour five six\n\nseven eight", "yisP", "|one two three\nfour five six\none two three\nfour five six\n\nseven eight")]
    [InlineData("one. two |three\n\nfour", "das", "one|.\n\nfour")]
    [InlineData("one. two |three\n\nfour", "vas<Esc>", "one. two thre|e\n\nfour")]
    [InlineData("fo|o.\nbar", "das", "|bar")]
    [InlineData("fo|o.\nbar", "dis", "|bar")]
    [InlineData("a. fo|o.\nbar", "das", "a|.\nbar")]
    [InlineData("fo|o.\n\nbar", "vasd", "|\n\nbar")]
    [InlineData("fo|o.\n\nbar", "yasP", "|foo.\nfoo.\n\nbar")]
    [InlineData("One two. Three |four.  Five", "das", "One two. |Five")]
    [InlineData("one. |two\nthree. four", "dis", "one. | four")]
    public void A_sentence_ending_its_line_ends_there_as_vims_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>( from an empty line goes back over the empty lines before it to the sentence above them, and from text
    /// just after empty lines to the last of them, as Vim's does.</summary>
    [Theory]
    [InlineData("a b\n\n|\nc d", "(", "|a b\n\n\nc d")]
    [InlineData("a b. c d\n\n|\nx", "(", "a b. |c d\n\n\nx")]
    [InlineData("a b. c d\n\n|\nx", "((", "|a b. c d\n\n\nx")]
    [InlineData("a b\n\n\nc |d", "((", "a b\n\n|\nc d")]
    [InlineData("a b\n\n\nc |d", "(((", "|a b\n\n\nc d")]
    [InlineData("\n\n|\nc d", "(", "|\n\n\nc d")]
    [InlineData("a b\n|\nc d", "d(", "|\nc d")]
    [InlineData("foo_bar = baz(qux[0], {k: v})\n\n\n  // |comment here\nend", "(", "foo_bar = baz(qux[0], {k: v})\n\n\n  |// comment here\nend")]
    [InlineData("foo_bar = baz(qux[0], {k: v})\n\n|\n  // comment here\nend", "(", "|foo_bar = baz(qux[0], {k: v})\n\n\n  // comment here\nend")]
    [InlineData("a b\n\n\n|\nc d", "(", "|a b\n\n\n\nc d")]
    [InlineData("a b\n\n\n|\nc d", "2(", "a b\n\n\n|\nc d")]
    [InlineData("a b.\n\n\nc |d", "(", "a b.\n\n\n|c d")]
    public void Back_a_sentence_over_empty_lines_goes_where_vims_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A count of ( or ) goes sentence by sentence as Vim's does: ) from a sentence's end or the white after it
    /// to the next sentence, from the white before a paragraph's first sentence past it, and on to the text's last
    /// character after the last sentence's start, even where that is the same character; ( reaching the start of a text
    /// that begins with an empty line counts it twice.</summary>
    [Theory]
    [InlineData("one.| two three", ")", "one. |two three")]
    [InlineData("one.| two three", "2)", "one. two thre|e")]
    [InlineData("one.| two three", "d)", "one.|two three")]
    [InlineData("a (b.|) c. \"d\" e!  f? g", "3)", "a (b.) c. \"d\" e!  |f? g")]
    [InlineData("a.\n |  \nb. c", ")", "a.\n   \n|b. c")]
    [InlineData("a.\n |  \nb. c", "3)", "a.\n   \nb. |c")]
    [InlineData("a. b\n\n\n|  c d", ")", "a. b\n\n\n  c |d")]
    [InlineData("one.  two.\n\n|  three. four\nfive.\n\n\n\nsix", "d2)", "one.  two.\n\n|\n\n\nsix")]
    [InlineData("a b\n\n\n\nc d. |e", "2)", "a b\n\n\n\nc d. |e")]
    [InlineData("a b\n\n\n\nc d.| e", "2)", "a b\n\n\n\nc d. |e")]
    [InlineData("a b\n\n\n\nc d.| e", "3)", "a b\n\n\n\nc d.| e")]
    [InlineData("a b\n  \n\n|c", "2)", "a b\n  \n\n|c")]
    [InlineData("|a b\n  \n\nc", "3)", "a b\n  \n\n|c")]
    [InlineData("\n\nc d. e |f", "5(", "|\n\nc d. e f")]
    [InlineData("\n\nc d. e |f", "6(", "\n\nc d. e |f")]
    [InlineData("\n|\nc d. e f", "2(", "|\n\nc d. e f")]
    [InlineData("\nc |d", "3(", "|\nc d")]
    [InlineData("x.\n\na b\n\n\nc |d", "5(", "|x.\n\na b\n\n\nc d")]
    public void Counts_of_sentences_go_where_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>Text objects in visual mode carry a stretch on as Vim's do: words a run (or white and a word) at a time,
    /// forward or back, over empty lines and line breaks as Vim steps over them; paragraphs by runs of lines, a line that
    /// is a run alone in linewise mode held already; sentences to their own line's end; an inner block over whole lines
    /// to its last line's break; a quoted string or a block around or after the stretch, linewise mode keeping its start.</summary>
    [Theory]
    [InlineData("a1\na2\n \n  \nb|1\n   \nc1\nc2\nc3\n    \nd1", "Vapd", "a1\na2\n \n  \n   | \nd1")]
    [InlineData("a1\na2\n \n  \nb|1\n   \nc1\nc2\nc3\n    \nd1", "Vipd", "a1\na2\n \n  \n|c1\nc2\nc3\n    \nd1")]
    [InlineData("a1\na2\n \n  \nb1\n   \nc1\nc2\nc3\n  |  \nd1", "V2apd", "a1\na2\n \n  \nb1\n   \nc1\nc2\nc3\n|d1")]
    [InlineData("a b\nc d\n\ne f\ng h\n\ni| j", "Vipipd", "a b\nc d\n\ne f\ng h\n|")]
    [InlineData("ab, cd.ef\n  g|h  ij\n\n\n  kl", "veiwd", "ab, cd.ef\n  g|  kl")]
    [InlineData("ab, cd.ef\n  g|h  ij\n\n\n  kl", "vjiwd", "ab, cd.ef\n  g| kl")]
    [InlineData("one two three\nfour five six\n\ns|even eight", "v0iwd", "one two three\nfour five six\n|ven eight")]
    [InlineData("one two three\nfour five six\n\ns|even eight", "vb3iwd", "one two three\nfour five|ven eight")]
    [InlineData("ab, cd.ef\n  |gh  ij\n\n\n  kl", "vhawd", "ab, cd.|h  ij\n\n\n  kl")]
    [InlineData("ab, cd.ef\n  |gh  ij\n\n\n  kl", "vkawd", "a|h  ij\n\n\n  kl")]
    [InlineData("ab cd|e fg\nhi jkl mn\nop qrs tu", "<C-v>jiprx", "|xxxxxx fg\nxxxxxx mn\nxxxxxx tu")]
    [InlineData("if (x) {\n    call(|1, 2);\n}\nend", "vi{d", "if (x) {\n|}\nend")]
    [InlineData("if (x) {\n    call(|1, 2);\n}\nend", "vi{<Esc>", "if (x) {\n    call(1, 2)|;\n}\nend")]
    [InlineData("foo(bar, {k: [1, 2]},| 'q s')\nnext line here", "V2la'd", "foo(bar, {k: [1, 2]},|)\nnext line here")]
    [InlineData("foo(bar, {k:| [1, 2]}, 'q s')\nnext line here", "v2li'd", "foo(bar, {k: [1, 2]}, '|')\nnext line here")]
    [InlineData("<a><b>text</b> more</a>\n<c x=\"|1\">y</c>", "<C-v>li\"d", "<a><b>text</b> more</a>\n<c x=\"|>y</c>")]
    [InlineData("  |alpha.beta(gamma, delta)\n  [1, 2, 3]", "Vwi(y", "  alpha.beta(|gamma, delta)\n  [1, 2, 3]")]
    [InlineData("One two. Three four!  Five |six?\nSeven eight.\n\nNine ten.", "v2lisd", "One two. Three four!  Five| \nSeven eight.\n\nNine ten.")]
    [InlineData("One |two. Three four!  Five six?\nSeven eight.\n\nNine ten.", "Vhisd", "|Seven eight.\n\nNine ten.")]
    [InlineData("if (x) {\n    cal|l(1, 2);\n}\nend", "<C-v>wasd", "if (x) {\n  |(1, 2);\n}\nen")]
    [InlineData("if (x) {\n    call(1, 2);\n|}\nend", "Vw2iwd", "if (x) {\n    |call(1, 2);")]
    [InlineData("def f(x):\n    return x + 1\n\n\nclass A|:\n    pass", "<C-v>kiwd", "def f(x):\n    return x + 1\n|\n\n\n    pass")]
    public void Visual_text_objects_carry_a_stretch_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A text whose file keeps no final line break: where a change leaves its last line empty, that line is a
    /// line, as Vim's buffer holds it, which the caret stands on and j, G and dd reach.</summary>
    [Theory]
    [InlineData("a\n|b", "S<Esc>", "a\n|")]
    [InlineData("a\n|b", "cc<Esc>", "a\n|")]
    [InlineData("a\n|b", "o<Esc>", "a\nb\n|")]
    [InlineData("a\n|b", "o<Esc>ix<Esc>", "a\nb\n|x")]
    [InlineData("a\n|b", "S<Esc>kj", "a\n|")]
    [InlineData("a\n|b", "S<Esc>gg G", "a\n|")]
    [InlineData("a\nb\n\nse|ven eight", "ddp", "a\nb\n\n|seven eight")]
    [InlineData("a\n|b", "d)", "a\n|")]
    [InlineData("a\n|b", "S<Esc>ix<Esc>", "a\n|x")]
    [InlineData("a\n|b", "S<Esc>dd", "|a")]
    [InlineData("a\n|b", "o<Esc>k$j", "a\nb\n|")]
    public void A_file_without_a_final_line_break_keeps_an_emptied_last_line_as_vims_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A visual J with nothing below to join ends visual mode all the same, the caret at the stretch's start.</summary>
    [Theory]
    [InlineData("|a b", "vlJ", "|a b")]
    [InlineData("a\n|b c", "vlJ", "a\n|b c")]
    [InlineData("a\n|b c", "VJ", "a\n|b c")]
    [InlineData("a\n|b c", "<C-v>lJ", "a\n|b c")]
    [InlineData("a\n|b c", "vlJx", "a\n| c")]
    [InlineData("x\na b| c", "vhJ", "x\na |b c")]
    public void A_visual_join_on_the_last_line_ends_visual_mode_at_the_stretchs_start(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A count of sentences running into the text's edge with sentences still to go fails, as Vim's does, and (
    /// reaching the text's first sentence goes to the text's very start, its leading blanks too.</summary>
    [Theory]
    [InlineData("o|ne two", "2(", "o|ne two")]
    [InlineData("one. two |three", "2(", "|one. two three")]
    [InlineData("one. tw|o three", "2)", "one. tw|o three")]
    [InlineData("one. tw|o three", "2d)", "one. tw|o three")]
    [InlineData("one. two. th|ree", "2(", "one. |two. three")]
    [InlineData("one. two. th|ree", "d2(", "one. |ree")]
    [InlineData("  al|pha beta", "d(", "|pha beta")]
    [InlineData("  al|pha beta", "(", "|  alpha beta")]
    [InlineData("x\n  al|pha beta", "d(", "|pha beta")]
    public void Sentence_counts_fail_at_the_texts_edge_and_the_first_sentence_starts_at_the_text(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>Words and paragraphs over a longer visual stretch: iw in linewise mode carried forward turns it charwise,
    /// back carries the cursor back word by word; ip on one line takes the paragraph whole, linewise, and over several
    /// lines carries the cursor on by paragraphs, up or down, as far as the text's edge.</summary>
    [Theory]
    [InlineData("f|oo(a) bar\nnext\nx", "Vjiwd", "|f\nx")]
    [InlineData("one t|wo three\nfour", "Vjiwd", "|")]
    [InlineData("one t|wo three\nfour five", "Vjawd", "one |t")]
    [InlineData("a b c d e f\ng h |i j k l\nm n o p q r", "Vliw~", "a b c d e f\ng h |I J k l\nm n o p q r")]
    [InlineData("a b c d e f\ng h |i j k l\nm n o p q r", "Vl2iwd", "a b c d e f\ng h |k l\nm n o p q r")]
    [InlineData("a b c d e f\ng h |i j k l\nm n o p q r", "VhiwP", "a b c d e f\ng | j k l\nm n o p q r")]
    [InlineData("a b c d e f\ng h |i j k l\nm n o p q r", "Vhiwd", "a b c d e f\ng | j k l\nm n o p q r")]
    [InlineData("one t|wo three\nfour", "ViWd", "one | three\nfour")]
    [InlineData("a b c d e f\ng h |i j k l\nm n o p q r", "Vk2iwd", "a | j k l\nm n o p q r")]
    [InlineData("a b c d e f\n|g h i j k l\nm n o p q r", "Vkiwd", "|m n o p q r")]
    [InlineData("a b c d e f\ng h |i j k l\nm n o p q r", "vhiwd", "a b c d e f\ng | j k l\nm n o p q r")]
    [InlineData("a b c d e f\ng h |i j k l\nm n o p q r", "vh3iwd", "a b c d e f\n| j k l\nm n o p q r")]
    [InlineData("a b\n\n|c d", "Vkiwd", "a | d")]
    [InlineData("a b\n\n|c d", "Vk2iwd", "a| d")]
    [InlineData("a b\nc |d\n\ne f\ng h\n\ni j", "vlipd", "|\ne f\ng h\n\ni j")]
    [InlineData("a b\nc |d\n\ne f\ng h\n\ni j", "vjipd", "a b\nc | h\n\ni j")]
    [InlineData("a b\nc |d\n\ne f\ng h\n\ni j", "vjapd", "a b\nc |i j")]
    [InlineData("a b\nc d\n\ne |f\ng h\n\ni j", "vkipd", "|\ng h\n\ni j")]
    [InlineData("a b\nc d\n\ne |f\ng h\n\ni j", "Vkipd", "|g h\n\ni j")]
    [InlineData("a b\nc d\n\ne |f\ng h\n\ni j", "Vk2ipd", "|g h\n\ni j")]
    [InlineData("a b\n\n|c d\n\ne f", "Vipd", "a b\n\n|e f")]
    [InlineData("a b\n\n|c d\n\ne f", "Vipipd", "a b\n|")]
    [InlineData("a b\n|c d\n\ne f", "veipY", "|a b\nc d\n\ne f")]
    [InlineData("a b\n|c d\n\ne f", "vlipy", "|a b\nc d\n\ne f")]
    [InlineData("a b\nc d\n\ne |f\ng h\n\ni j", "Vjjjipd", "a b\nc d\n|")]
    [InlineData("|a b\nc d", "Vjipd", "|")]
    [InlineData("|a b\nc d", "vjipd", "| d")]
    public void Words_and_paragraphs_over_a_longer_visual_stretch_go_where_vims_do(string before, string keys, string after) =>
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
    [InlineData("ab |cd", "veiw", "ab «cd|")]
    [InlineData("ab |cd", "veaW", "ab «cd|")]
    [InlineData("ab |cd", "viwiwd", "ab| ")]
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
    [InlineData("ab|cd\nefgh\nijkl", VimKeys.CtrlV, "ab«c|d\nefgh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jl", "ab«cd|\nef«gh|\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jh", "a|bc»d\ne|fg»h\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>j$", "ab«cd|\nef«gh|\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlo", "ab|cd»\nef|gh»\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlO", "ab|cd»\nef|gh»\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jld", "a|b\nef\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlx", "a|b\nef\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlD", "a|b\nef\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jX", "ab|d\nefh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jlYP", "ab|cdcd\nefghgh\nijkl")]
    [InlineData("ab|cd\nefgh\nijkl", "<C-v>jSX<Esc>", "|X\nijkl")]
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
    public void Vims_word_edges_and_keyword_class_read_the_editors_words() =>
        Assert.Equal(WordCharacters.NotAfterWord + WordCharacters.BeforeWord + "two" + WordCharacters.AfterWord + WordCharacters.NotBeforeWord + WordCharacters.Class,
            VimPatterns.Translate(@"\<two\>\k", ignoreCase: false, smartCase: false).Regex);

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
    [InlineData(VimKeys.CtrlO, VimRequestKind.Older, null)]
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

    /// <summary>:s says what it counted as Vim 9.1's does: with the n flag its matches and their lines, one of either
    /// named alone, and without it its substitutions over more than two lines.</summary>
    [Theory]
    [InlineData(":%s/o/0/gn<CR>", "7 matches on 3 lines")]
    [InlineData(":%s/one/1/n<CR>", "1 match on 1 line")]
    [InlineData(":1s/o/0/gn<CR>", "2 matches on 1 line")]
    [InlineData(":%s/w/W/g<CR>", "4 substitutions on 3 lines")]
    public void Substitute_says_what_it_counted_as_vims_does(string keys, string said)
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("|one two\nfoo two\ntwo three two\nlast");
        Assert.Equal(said, Press(new Vim(), keys, text, carets).Message);
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
        Assert.True(vim.Press(VimKeys.Escape, text, carets).Taken);
        Assert.Equal(string.Empty, vim.Pending);
    }

    [Fact]
    public void Keys_normal_mode_does_not_take_go_to_the_keymap()
    {
        Vim vim = new();
        (TextSnapshot text, SelectionSet carets) = Marked.Parse("a|b");
        Assert.False(vim.Press("<D-s>", text, carets).Taken);
        Assert.False(vim.Press(VimKeys.Escape, text, carets).Taken);
        Assert.True(vim.Press("Q", text, carets).Taken);
        vim.Press("i", text, carets);
        Assert.False(vim.Press("x", text, carets).Taken);
    }

    [Theory]
    [InlineData("u", EditorActions.Undo, 1)]
    [InlineData("3u", EditorActions.Undo, 3)]
    [InlineData(VimKeys.CtrlR, EditorActions.Redo, 1)]
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
        SelectionSet settled = Vim.Settled(text, put, breakEnds: true);
        Assert.Equal([new Selection(1, 1, 40), new Selection(3, 3), new Selection(4, 6)], settled.Items);
        Assert.Same(settled, Vim.Settled(text, settled, breakEnds: true));
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

    /// <summary>gq and | count a character's cells alike, as Vim 9.1 draws them: two for a wide one, none for a mark,
    /// each of a flag's two letters on its own.</summary>
    [Theory]
    [InlineData("|日本語 日本語 日本語", "gqq", 10, "日本語\n日本語\n|日本語")]
    [InlineData("|日本語 日本語 日本語", "gqq", 13, "日本語 日本語\n|日本語")]
    [InlineData($"|{Cafe} {Cafe} {Cafe} {Cafe}", "gqq", 9, $"{Cafe} {Cafe}\n|{Cafe} {Cafe}")]
    [InlineData($"|{Cafe} {Cafe} {Cafe} {Cafe}", "gqq", 14, $"{Cafe} {Cafe} {Cafe}\n|{Cafe}")]
    [InlineData($"|{Namaste} {Namaste} {Namaste}", "gqq", 9, $"{Namaste} {Namaste}\n|{Namaste}")]
    [InlineData("|日本語 xyz", "6|", 0, "日本|語 xyz")]
    [InlineData($"|{Cafe} x", "6|", 0, $"{Cafe} |x")]
    [InlineData($"|{Namaste} xyz", "6|", 0, $"{Namaste} |xyz")]
    [InlineData("|\U0001F1FA\U0001F1F8 xyz", "6|", 0, "\U0001F1FA\U0001F1F8 |xyz")]
    [InlineData("日|本語\nabcdef", "<C-v>jd", 0, "日|語\nabef")]
    public void Gq_and_columns_count_a_characters_cells_as_vims_do(string before, string keys, int textWidth, string after)
    {
        Vim vim = new();
        vim.Registers.Options.TextWidth = textWidth;
        Assert.Equal(after, Run(vim, before, keys));
    }

    /// <summary>J and gq join lines alike, as Vim 9.1's do: with joinspaces two spaces after a sentence's end, and one
    /// more where a single space already ends it; one space after an empty line, whatever came before it; none before a
    /// ')' or after a blank, a ')' so joined staying one word with what it follows, which gq breaks before whole.</summary>
    [Theory]
    [InlineData(true, "|One. \nTwo.", "J", "One. | Two.")]
    [InlineData(false, "|One. \nTwo.", "J", "One. |Two.")]
    [InlineData(true, "|One.\n\nTwo.", "3J", "One.| Two.")]
    [InlineData(true, "|One. \n\nTwo.", "3J", "One. | Two.")]
    [InlineData(false, "|One. \n\nTwo.", "3J", "One. | Two.")]
    [InlineData(false, "|   \n\nOne", "3J", "   | One")]
    [InlineData(true, "|One. \n)Two", "J", "One. |)Two")]
    [InlineData(true, "|One.  \nTwo", "J", "One.  |Two")]
    [InlineData(true, "|One.\t\nTwo", "J", "One.\t|Two")]
    [InlineData(true, "|One?\n    Two!", "J", "One?|  Two!")]
    [InlineData(true, "|One two.\nThree four.\nFive.", "gqip", "One two.  Three\n|four.  Five.")]
    [InlineData(false, "|One two.\nThree four.\nFive.", "gqip", "One two. Three four.\n|Five.")]
    [InlineData(true, "|One two. \nThree four?\nFive.", "gqip", "One two.  Three\n|four?  Five.")]
    [InlineData(true, "|// One two.\n// Three four.\n// Five.", "gqip", "// One two.  Three\n|// four.  Five.")]
    [InlineData(true, "|One two.\n)Three four.\nFive.", "gqip", "One two.)Three four.\n|Five.")]
    [InlineData(true, "one two.\nthree| four.", "gqk", "one two.  three\n|four.")]
    [InlineData(false, "|One two three four\n)five six seven.", "gqip", "One two three\n|four)five six seven.")]
    [InlineData(false, "|One two three\nfour\n)five six seven.", "gqip", "One two three\n|four)five six seven.")]
    public void J_and_gq_join_lines_alike_as_vims_do(bool joinSpaces, string before, string keys, string after)
    {
        Vim vim = new();
        vim.Registers.Options.JoinSpaces = joinSpaces;
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
        Assert.Equal([VimKeys.CtrlO], Press(vim, "@a", text, carets).Replay);
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
        Assert.Null(vim.Press(VimKeys.CtrlR, text, carets, readOnly).Action);
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

    /// <summary>Words, WORDs and their objects move as Vim's cursor does, a place at a time: an empty line is a word, an
    /// operator's last word stops at its line's end, a count running out of words fails leaving the caret where it ran
    /// out, and a visual stretch carried on by iw or aw goes on over a line's end, or back where its cursor stands first.</summary>
    [Theory]
    [InlineData("if (x) { return \"hi\";| }\n}", "VwiwcQ<Esc>", "if (x) { return \"hi\";|Q")]
    [InlineData("one two three\nfour five |six\n\nseven eight", "v$iwd", "one two three\nfour five |even eight")]
    [InlineData("a b c\nd e| f", "2daw", "a b c\nd e |f")]
    [InlineData("last-line, he|re.", "2yaWx", "last-line, her|e")]
    [InlineData("x = |1\n\n\nclass A:\n    pass", "j2daW", "x = 1\n|A:\n    pass")]
    [InlineData("one two |three four", "vhawd", "one|hree four")]
    [InlineData("one two |three four", "vhiwd", "one |hree four")]
    [InlineData("one two |three four", "vh2awd", "o|hree four")]
    [InlineData("one two thr|ee four", "vhiwiwd", "one two|e four")]
    [InlineData("one\n  two |three", "vkawd", "o|hree")]
    [InlineData("foo bar.\n  b|az qux", "k$2daw", "foo bar| qux")]
    [InlineData("one |two.\n  baz qux", "2daw", "on|e\n  baz qux")]
    [InlineData("|one\n\ntwo three", "vjjiwd", "| three")]
    [InlineData("one\n\n|two three", "vkkawd", "|wo three")]
    [InlineData("ab cd|\n", "viwiwd", "ab| \n")]
    [InlineData("ab c|d", "vawawd", "a|b")]
    [InlineData("   |   \nab", "daw", "|")]
    [InlineData("ab   |   \ncd", "2daw", "ab      \nc|d")]
    [InlineData("|word\n    indented line", "dw.a7<Esc>", "    i|7ndented line")]
    [InlineData("a b c\nm n o p q| r", "wgUe", "a b c\nm n o p q |R")]
    [InlineData("a b c\nm n o p q |r", "dw", "a b c\nm n o p q| ")]
    [InlineData("a |b c d", "2dge3l", "a b| c d")]
    [InlineData("one two |three", "3dbx", "|hree")]
    [InlineData("one |two", "wx", "one t|w")]
    [InlineData("one |two", "vwd", "one| ")]
    [InlineData("one t|wo", "ved", "one |t")]
    [InlineData("one two\n\nthree |four", "3gex", "one t|w\n\nthree four")]
    [InlineData("|one two\n\nthree four", "3e", "one two\n\nthre|e four")]
    [InlineData("|one two\n\n  three four", "d3w", "  |three four")]
    [InlineData("|one two\n\n  three four", "c3w7<Esc>", "|7 four")]
    [InlineData("on|e two\nthree", "2cwX<Esc>", "on|X\nthree")]
    [InlineData("|a; b.c d", "wdW", "a|b.c d")]
    [InlineData("|\n\nab", "2dw", "|ab")]
    public void Words_and_their_objects_move_as_vims_cursor_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>Visual A and I over characters or lines act on the stretch's lines as Vim's do: I where Vim's operator
    /// begins, A after the character where it ends, or on it where the columns that operator keeps (a block's before
    /// it, after $) come out equal; j and k onto a line too short for the column aimed at put the cursor past its end;
    /// and an operator over lines whose cursor stands above leaves the caret at the cursor.</summary>
    [Theory]
    [InlineData("a|bcdef\nghijkl\nmnopqr", "vllAQ<Esc>", "abcd|Qef\nghijkl\nmnopqr")]
    [InlineData("abcd|ef\nghijkl\nmnopqr", "VjAQ<Esc>", "abcdef\nghijk|Ql\nmnopqr")]
    [InlineData("abcd|ef\nghijkl\nmnopqr", "Vj$AQ<Esc>", "abcdef\nghijk|Ql\nmnopqr")]
    [InlineData("abcdef\nghij|kl\nmnopqr", "VkAQ<Esc>", "abcdef\n|Qghijkl\nmnopqr")]
    [InlineData("abcdef\nghij|kl\nmnopqr", "VkIQ<Esc>", "abcd|Qef\nghijkl\nmnopqr")]
    [InlineData("abcdef\nghij|kl\nmnopqr", "vkIQ<Esc>", "abcd|Qef\nghijkl\nmnopqr")]
    [InlineData("abcdef\nghij|kl\nmnopqr", "vAQ<Esc>", "abcdef\nghijk|Ql\nmnopqr")]
    [InlineData("ab|c", "v0AQ<Esc>", "|Qabc")]
    [InlineData("a b c |d e f\ng h i j k l\nm n o p q r", "<C-v>iphAQ<Esc>", "a b c d e f\ng h i j k l\n|Qm n o p q r")]
    [InlineData("foo_bar = baz(qux[0], {k: v}|)", "<C-v>i($AQ<Esc>", "foo_bar = baz(qux[0], {k: v}|Q)")]
    [InlineData("abcdef\nghij|kl\nmnopqr", "<C-v>jlxggVjAQ<Esc>", "abcdef\ng|Qhij\nmnop")]
    [InlineData("abcdef\nghij|kl\nmnopqr", "<C-v>jlx0vlAQ<Esc>", "abcdef\ngh|Qij\nmnop")]
    [InlineData("abc\nab|cdef", "vkd", "ab|def")]
    [InlineData("abc\nab|cdef", "vkkd", "ab|def")]
    [InlineData("abc\nab|cdef", "Vkd", "|")]
    [InlineData("abcdef\nab|c", "vjd", "abcdef\na|b")]
    [InlineData("abcdef\nab\nab|c", "vkkd", "a|b")]
    [InlineData("abc\nab|cdef", "vk$jd", "abc\na|b")]
    [InlineData("abc\nab|cdef", "Vky", "ab|c\nabcdef")]
    [InlineData("abc\nab|cdef", "Vk~", "AB|C\nABCDEF")]
    [InlineData("abc\nab|cdef", "vkU", "ab|C\nABCdef")]
    [InlineData("abc\nab|cdef", "Vjy", "abc\n|abcdef")]
    [InlineData("abcdef\nab|cdef", "Vk~", "AB|CDEF\nABCDEF")]
    [InlineData("abcdef\nab|cdef", "VkJ", "abcdef| abcdef")]
    [InlineData("  abcdef\nab|cdef", "Vk>", "      |abcdef\n    abcdef")]
    public void Visual_inserts_and_operators_over_lines_land_where_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A visual cursor moved by j or k onto a line too short for the column aimed for stands on that line's end,
    /// so the stretch takes its line break, as Vim's does; and a stretch reaching the last line's end leaves the final
    /// line break of a file that keeps one, as there is no line after it to join.</summary>
    [Theory]
    [InlineData("ab\nabcd|ef", "vkd", "ab|f")]
    [InlineData("ab\nab|cd", "vkd", "ab|d")]
    [InlineData("ab\nabcd|ef", "vkhd", "a|f")]
    [InlineData("abcd|ef\nab\nabcdef", "vjd", "abcd|abcdef")]
    [InlineData("ab\nabcd ef|gh", "bvkp", "ab|fgh")]
    [InlineData("ab\nabcd|ef", "vk~", "a|b\nABCDEf")]
    [InlineData("ab\nabcd|ef", "vkIX<Esc>", "ab|X\nabcdef")]
    [InlineData("ab\nabcd|ef", "vkcX<Esc>", "ab|Xf")]
    [InlineData("one\nab|cd\n", "v$d", "one\na|b\n")]
    [InlineData("one\nab|cd\n", "v$y$p", "one\nabcdc|d\n")]
    [InlineData("one\nab|cd\n", "v$cX<Esc>", "one\nab|X\n")]
    [InlineData("one\nab|cd\n", "kyyjv$p", "one\nab\n|one\n\n")]
    [InlineData("one\n|\n", "kyyjvp", "one\n\n|one\n\n")]
    [InlineData("ab|c\n\n", "jvlx", "abc\n|\n")]
    [InlineData("ab|c\n\n", "jvwd", "abc\n|\n")]
    [InlineData("ab|c\n", "vwx", "a|b\n")]
    public void A_visual_stretch_takes_a_short_lines_break_but_never_the_files_final_one(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>* and # on a line with no keyword from the caret on search for the run of other non-blanks under the
    /// caret, or the first after it on the line, as Vim's do: no word edges, g* and g# alike.</summary>
    [Theory]
    [InlineData("x f(y|);\nz f(y);", "*", "x f(y);\nz f(y|);")]
    [InlineData("x f(y|);\nz f(y);", "g*", "x f(y);\nz f(y|);")]
    [InlineData("x f(y);\nz f(y|);", "#", "x f(y|);\nz f(y);")]
    [InlineData("x f(y);\nz f(y|);", "g#", "x f(y|);\nz f(y);")]
    [InlineData("a |:= b\nc := d", "*", "a := |b\nc := d")]
    [InlineData("a :|= \nc := d", "*", "a := \nc |:= d")]
    [InlineData("a | := \nc := d", "*", "a  := \nc |:= d")]
    [InlineData("|  ;;\nx ;;;;", "*", "  ;;\nx |;;;;")]
    [InlineData("|  ;;\nx ;;;;", "g*", "  ;;\nx |;;;;")]
    [InlineData("|  ;;\nx ;; ;;", "2*", "  ;;\nx ;; |;;")]
    [InlineData("x ;;\n  |;;", "#", "x |;;\n  ;;")]
    [InlineData("a.b|.;\na.b.;", "*", "a.b.;\na.b|.;")]
    [InlineData("a ;|; b ;;\n;;", "*", "a ;; |b ;;\n;;")]
    [InlineData("a\t|;;", "*", "a\t|;;")]
    [InlineData("|   \nab", "*", "|   \nab")]
    [InlineData("x |\"y\" \"y\"", "*", "x \"y\" \"|y\"")]
    [InlineData("x y|\"\n\"y\"", "*", "x y\"\n|\"y\"")]
    [InlineData("a+|+ b\nc ++", "*", "a++ |b\nc ++")]
    [InlineData("a+|+\nc++", "*", "a++\nc|++")]
    public void Star_and_hash_with_no_keyword_search_for_the_run_of_other_non_blanks(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A block with nothing inside, around the caret or ahead of it: an operator takes nothing but leaves the
    /// caret inside it, as Vim's does; in visual mode an inner block fails, keeping the stretch, an inner string takes
    /// its quotes, and an inner tag block takes its tags from its end tag's first character.</summary>
    [Theory]
    [InlineData("|f()", "di(", "f(|)")]
    [InlineData("|x\nf()", "di(", "x\nf(|)")]
    [InlineData("|x\nf()", "ci(X<Esc>", "x\nf(|X)")]
    [InlineData("|x\nf()", "vi(d", "|\nf()")]
    [InlineData("|x []", "di[", "x [|]")]
    [InlineData("|x\n{\n}\n", "di{", "x\n{\n|}\n")]
    [InlineData("x\n{|\n}\n", "di{", "x\n{\n|}\n")]
    [InlineData("|f()", "vi(d", "|()")]
    [InlineData("f(|)", "vi(d", "f|(")]
    [InlineData("|f()", "yi(x", "f|(")]
    [InlineData("|x\nf()", "yi(x", "x\nf|(")]
    [InlineData("|f()", "gUi(x", "f|(")]
    [InlineData("|x\n{\n}\n", "vi{d", "|\n{\n}\n")]
    [InlineData("|x \"\"", "vi\"d", "x| ")]
    [InlineData("x <a>|</a>", "vitd", "x| ")]
    [InlineData("x <a|></a>", "vitd", "x <a>|/a>")]
    [InlineData("x f|()", "vi(d", "x f|)")]
    [InlineData("x f|()", "vlli(d", "x |f")]
    [InlineData("b <p><a></a></|p>", "vitd", "b <p>|</p>")]
    [InlineData("x |\"\"", "vi\"y$p", "x \"\"\"|\"")]
    [InlineData("x \"|\"", "vi\"lx", "x |\"")]
    public void Objects_with_nothing_inside_do_what_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>is and as on a run of empty lines take its last line as the sentence, the white after it too for as, and
    /// one ( from the text's very start stays there, an operator taking nothing, where a count more fails, as Vim's do.</summary>
    [Theory]
    [InlineData("a b\n|\n\nc d", "dis", "a b\n\n|c d")]
    [InlineData("a b\n|\n\nc d", "das", "a b\n\n|c d")]
    [InlineData("a b\n\n|\nc d", "dis", "a b\n\n|c d")]
    [InlineData("a b\n|\n\nc d", "2das", "a b\n|")]
    [InlineData("|\n\nc d. e f", "dis", "\n|c d. e f")]
    [InlineData("\n|\nc d. e f", "das", "\n|c d. e f")]
    [InlineData("one.  two.\n\n  three. four\nfive.\n|\n\n\nsix", "das", "one.  two.\n\n  three. four\nfive.\n\n\n|six")]
    [InlineData("one.  two.\n\n  three. four\nfive.\n\n\n|\nsix", "dis", "one.  two.\n\n  three. four\nfive.\n\n\n|six")]
    [InlineData("a.\n|\n  b. c", "dis", "a.\n  |b. c")]
    [InlineData("a.\n|\n  b. c", "das", "a.\n|b. c")]
    [InlineData("a.\n|\n  b. c", "2das", "a.\n|c")]
    [InlineData("one two three\nfour five six\n|", "gUas", "|ONE TWO THREE\nFOUR FIVE SIX\n")]
    [InlineData("|abc. def", "c(X<Esc>", "|Xabc. def")]
    [InlineData("|abc. def", "2c(X<Esc>", "|abc. def")]
    [InlineData("|ABC. def", "gu(", "|aBC. def")]
    [InlineData("|\nabc", "c(X<Esc>", "|X\nabc")]
    [InlineData("\na|bc", "3c(X<Esc>", "|Xbc")]
    [InlineData("\na|bc", "4c(X<Esc>", "\n|bc")]
    public void Sentences_on_empty_lines_and_back_from_the_texts_start_do_what_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>An inner quoted string with a count of two or more takes its quotes too but no white, as Vim's help says
    /// and Vim 9.1 does, in visual mode as well.</summary>
    [Theory]
    [InlineData("x \"a|b\" y", "2di\"", "x | y")]
    [InlineData("x \"a|b\" y", "3di\"", "x | y")]
    [InlineData("x \"a|b\" y", "2da\"", "x |y")]
    [InlineData("x \"a|b\" y", "v2i\"d", "x | y")]
    [InlineData("x |\"ab\" y", "2ci\"Q<Esc>", "x |Q y")]
    [InlineData("|x \"ab\" y", "2di\"", "x | y")]
    [InlineData("x 'a|b' y", "2di'", "x | y")]
    [InlineData("x \"a|b\" y", "2yi\"P", "x \"ab|\"\"ab\" y")]
    [InlineData("x \"a|b\" y", "1di\"", "x \"|\" y")]
    public void A_counted_inner_string_takes_its_quotes(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A count of word objects walks the text a place at a time as Vim's cursor does: iw's step onto an empty
    /// line goes on to the line after, which an operator then stops before, taking whole lines from an indent; aw's
    /// white runs over line breaks to a word or an empty line; in visual mode the walk carries a stretch either way,
    /// reaches the last line's end past its last character (as e does there), and back stops at the text's second
    /// character where Vim's does.</summary>
    [Theory]
    [InlineData("x| = 1\n\n\nclass A:\n    pass", "3daw", "|x\nclass A:\n    pass")]
    [InlineData("x =| 1\n\n\nclass A:\n    pass", "3daw", "x =|A:\n    pass")]
    [InlineData("x = 1\n|\n\nclass A:\n    pass", "2diw", "x = 1\n|class A:\n    pass")]
    [InlineData("x = 1\n|\n\nclass A:\n    pass", "4diw", "x = 1\n|A:\n    pass")]
    [InlineData("x = 1\n\n|\nclass A:\n    pass", "daw", "x = 1\n\n| A:\n    pass")]
    [InlineData("x = 1\n\n|\nclass A:\n    pass", "3daw", "x = 1\n\n    |pass")]
    [InlineData("x = 1\n\n\nclass| A:\n    pass", "3daw", "x = 1\n\n\nclas|s")]
    [InlineData("|  ab  \n\nx.y .\n", "4diw", "|x.y .\n")]
    [InlineData("  ab| \n\nx.y .\n", "2yiwP", "  ab| \n \n\nx.y .\n")]
    [InlineData("\t\n|\n ,;ab  ", "daw", "\t\n|ab  ")]
    [InlineData("\n .  |ab x.y", "2daw", "\n .|.y")]
    [InlineData("ab\n|\n", "diw", "|a\n")]
    [InlineData("def f(x):\n    7\n\n\n|class A:", "2daW", "def f(x):\n    7\n\n\n|")]
    [InlineData("one two three\nfour five six\n\nse|ven eight", "v2h2awd", "one two three\nfour |en eight")]
    [InlineData(" ,;  c \n\n  de|ab ,; ", "vh2awd", " ,;  |b ,; ")]
    [InlineData("one\n\n|two three", "vkiwd", "|wo three")]
    [InlineData("ab c|d", "veiwd", "ab |c")]
    [InlineData(".  c  \na|b ", "vj2awd", ".  c  \n|")]
    [InlineData("ab c|d\n", "v3iwd", "ab| \n")]
    [InlineData("if (x) {\n    call(1, 2);\n|}\nend", "Vw2iwd", "if (x) {\n    |call(1, 2);")]
    [InlineData("foo\nen|d", "<C-v>eA7<Esc>", "foo\nen|d 7")]
    public void Counts_of_word_objects_walk_over_empty_lines_and_line_ends_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A block shifted right adds its white on each line it reaches and at the end of a line ending just where
    /// it begins, but never on an empty line, and leaves the caret at its top left, as Vim's does.</summary>
    [Theory]
    [InlineData("ab|cdef\nab\nabcdef", "<C-v>j>", "ab|    cdef\nab    \nabcdef")]
    [InlineData("ab|cdef\nab\nabcdef", "<C-v>jj>", "ab|    cdef\nab    \nab    cdef")]
    [InlineData("ab|cdef\nab\nabcdef", "<C-v>j2>", "ab|        cdef\nab        \nabcdef")]
    [InlineData("abc|def\nab\nabcdef", "<C-v>jj>", "abc|    def\nab\nabc    def")]
    [InlineData("|abcdef\n\nabcdef", "<C-v>jj>", "|    abcdef\n\n    abcdef")]
    [InlineData("  ab|cdef\n  ab\n  abcdef", "<C-v>j$>", "  ab|    cdef\n  ab    \n  abcdef")]
    [InlineData("one two three\n|four five six\n\nseven eight", "C<Esc><C-v>ej>", "one two three\n|\n\n    seven eight")]
    [InlineData("foo_bar\n\n  /|/ comment here\nend", "<C-v>j>", "foo_bar\n\n  /|    / comment here\nend    ")]
    [InlineData("ab|cdef\nab\nabcdef", "<C-v>j<", "ab|cdef\nab\nabcdef")]
    public void A_block_shifted_right_pads_a_line_ending_where_it_begins(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A put over a visual block does what Vim's does: text of one line goes in each line's part, a line ending
    /// where the block begins (an empty one too) taking it at its end; a block goes in from the block's top left; lines
    /// go below the block, or above for P, below its top line where P's part there ran to that line's end past text it
    /// leaves; and a count puts the register that many times over, in every visual mode.</summary>
    [Theory]
    [InlineData("abc|def\nab\nabcdef\nxyz", "ma$yiw`a<C-v>jp", "ababcde|fef\nababcdef\nabcdef\nxyz")]
    [InlineData("abc|def\nab\nabcdef\nxyz", "ma$yiw`a<C-v>j2p", "ababcdefabcde|fef\nababcdefabcdef\nabcdef\nxyz")]
    [InlineData("abc|def\nab\nabcdef\nxyz", "maG0y2l`a<C-v>jjP", "abcx|yef\nab\nabcxyef\nxyz")]
    [InlineData("a|bcdef\n\nabcdef\nxyz", "maG0y2l`a<C-v>jp", "x|ycdef\nxy\nabcdef\nxyz")]
    [InlineData("abc|def\nab\nabcdef\nxyz", "maG0<C-v>ly`a<C-v>jj2p", "abc|xyxyef\nab\nabcef\nxyz")]
    [InlineData("abc|def\nab\nabcdef\nxyz", "maGyy`a<C-v>jj2p", "abcef\nab\nabcef\n|xyz\nxyz\nxyz")]
    [InlineData("a|bcdef\nab\nabcdef\nxyz", "maGyy`a<C-v>j$P", "a\n|xyz\na\nabcdef\nxyz")]
    [InlineData("a|bcdef\n\nabcdef\nxyz", "maGyy`a<C-v>j$P", "|xyz\n\n\nabcdef\nxyz")]
    [InlineData("a|bcdef\nab\nabcdef\nxyz\npq", "maG0kvjy`a<C-v>j2p", "a|xyz\npxyz\npcdef\na\nabcdef\nxyz\npq")]
    [InlineData("ab|cdef\nxyz", "jyiwkv2p", "xyzxy|zbcdef\nxyz")]
    [InlineData("ab|cdef\nxyz", "jyykV2p", "|xyz\nxyz\nxyz")]
    [InlineData("ab|cdef\nxyz", "jyiwkV2p", "|xyz\nxyz\nxyz")]
    public void A_put_over_a_block_fills_each_line_and_a_count_repeats_it(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A block's cursor moved by j or k onto a tab takes the tab's columns, and after $ onto a shorter line
    /// stands past that line's end, so I and A go where Vim's do.</summary>
    [Theory]
    [InlineData("word\n\tnot\nla|st-line, here.", "<C-v>kI7<Esc>", "word\n|7\tnot\n7last-line, here.")]
    [InlineData("word\n\tnot\nla|st-line, here.", "<C-v>kd", "word\n|not\n-line, here.")]
    [InlineData("word\n\tnot\nla|st-line, here.", "<C-v>kA7<Esc>", "word\n|\t7not\nlast7-line, here.")]
    [InlineData("word\n\tnot\nlast-|line, here.", "<C-v>kI7<Esc>", "word\n\tn|7ot\nlast-7line, here.")]
    [InlineData("    indented li|ne\n\tnot\nlast", "<C-v>$jI7<Esc>", "    ind|7ented line\n\tnot7\nlast")]
    [InlineData("    indented li|ne\n\tnot\nlast", "<C-v>$jd", "    in|d\n\tnot\nlast")]
    [InlineData("    indented li|ne\nnot\nlast", "<C-v>$jI7<Esc>", "   |7 indented line\nnot7\nlast")]
    [InlineData("    indented li|ne\nnot\nlast", "<C-v>$jA7<Esc>", "   | indented line7\nnot7\nlast")]
    [InlineData("    indented li|ne\nnot\nlast", "<C-v>$jjI7<Esc>", "    |7indented line\nnot\nlast7")]
    [InlineData("abcdef|g\nabc\nabcdefgh", "<C-v>$jI7<Esc>", "abc|7defg\nabc7\nabcdefgh")]
    [InlineData("abcdef|g\nabc\nabcdefgh", "<C-v>$jjI7<Esc>", "abcdef|7g\nabc\nabcdef7gh")]
    [InlineData("abcdef|g\nabc\nabcdefgh", "<C-v>$jjd", "abcde|f\nabc\nabcdef")]
    [InlineData("abc|defg\nabc\nabcdefgh", "<C-v>$jI7<Esc>", "abc|7defg\nabc7\nabcdefgh")]
    public void A_blocks_cursor_on_a_tab_or_after_dollar_takes_the_columns_vims_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>Where a closing '.', '!' or '?' ends the text, ) counts the place past it as one more sentence's start,
    /// standing on the last character, so a count and an operator with one reach there as Vim's do.</summary>
    [Theory]
    [InlineData("ab cd. |ef gh.", "2)x", "ab cd. ef g|h")]
    [InlineData("ab cd. |ef gh.", "2d)", "ab cd.| ")]
    [InlineData("ab cd. |ef gh.", "2g~)", "ab cd. |EF GH.")]
    [InlineData("ab cd. ef |gh.", "2)x", "ab cd. ef g|h")]
    [InlineData("ab cd. ef |gh.", "3)x", "ab cd. ef |h.")]
    [InlineData("|ab cd. ef gh.", "3d)", "|")]
    [InlineData("ab cd. ef gh|.", "2d)", "ab cd. ef g|h")]
    [InlineData("ab cd. |ef gh", "2)x", "ab cd. |f gh")]
    [InlineData("word\n    ind|ented line\n\tnot\nlast-line, here.", "2g~)", "word\n    ind|ENTED LINE\n\tNOT\nLAST-LINE, HERE.")]
    [InlineData("word\n    indented lin|e\n\tnot\nlast-line, here.", "2<)", "word\n|indented line\nnot\nlast-line, here.")]
    public void A_counted_sentence_motion_counts_the_end_past_a_closing_mark(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>Setting a mark keeps the column j and k aim for, where a yank or any change sets it afresh, as Vim's do.</summary>
    [Theory]
    [InlineData("def f(x):\n    return |x + 1", "kmaj", "def f(x):\n    return |x + 1")]
    [InlineData("def f(x):\n    return |x + 1", "kmbmaj", "def f(x):\n    return |x + 1")]
    [InlineData("def f(x):\n    return |x + 1", "kyyj", "def f(x):\n    retu|rn x + 1")]
    [InlineData("def f(x):\n    return |x + 1", "kyiwj", "def f(x):\n    ret|urn x + 1")]
    public void A_mark_keeps_the_column_j_and_k_aim_for(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A block's change leaves its cursor where typing ended on the first line it changed, a step back from
    /// there as Escape steps, so . from a line below changes the block's width from that column as Vim's does.</summary>
    [Theory]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jlc78<Esc>", "o7|8 two\nt78ee\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jlc<Esc>", "|o two\ntee\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jlC78<Esc>", "o7|8\nt78\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jls78<Esc>jj.", "o78 two\nt78ee\nfo7|8 five six\nse78n")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jlc78<Esc>j.", "o78 two\nt77|8e\nfo78 five six\nseven")]
    [InlineData("|\nthree two\nfour five six\nseven", "j<C-v>kjjlc78<Esc>", "\n7|8ree two\n78ur five six\nseven")]
    [InlineData("|ab\nthree two\nfour five six\nseven", "jj4l<C-v>kklc78<Esc>", "ab7|8\nth78 two\nfo78five six\nseven")]
    public void A_blocks_change_leaves_its_cursor_where_typing_ended(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A block's cursor moved by j or k onto a line too short for its column stands past that line's end, so h
    /// from there and a shift's blanks go where Vim's do.</summary>
    [Theory]
    [InlineData("y = [e, f];\nz = \"g (h) i|\";", "<C-v>khd", "y = [e, f|]\nz = \"g (h);")]
    [InlineData("y = [e, f];\nz = \"g (h) i|\";", "<C-v>khhd", "y = [e, |f\nz = \"g (h;")]
    [InlineData("ab\nz = \"g (h) i|\";", "<C-v>khd", "|a\nz;")]
    [InlineData("y = [e, f];\nz = \"g (h) i|\";", "<C-v>khA7<Esc>", "y = [e, f]|;  7\nz = \"g (h) i\"7;")]
    [InlineData("y = [e, f];\nz = \"g (h) i|\";", "<C-v>kh>", "y = [e, f]|    ;\nz = \"g (h)     i\";")]
    [InlineData("y = [e, f];\nz = \"g (h) i|\";", "<C-v>k>", "y = [e, f];|    \nz = \"g (h)     i\";")]
    [InlineData("ab\nz = \"g (h) i|\";", "<C-v>k>", "ab|    \nz     = \"g (h) i\";")]
    [InlineData("|ab\nthree two\nfour five six\nseven", "jj4l<C-v>kkhc78<Esc>", "a7|8\nt78 two\nf78five six\nseven")]
    public void A_blocks_cursor_moved_onto_a_shorter_line_stands_past_its_end(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A tab a block's edge cuts in two keeps its part outside the block as spaces, and what the block takes of
    /// it is spaces too: deleted, changed, yanked, inserted before or after, or replaced cell by cell, as Vim's does.</summary>
    [Theory]
    [InlineData("|abc\n\tx\ty", "l<C-v>jld", "|a\n \ty")]
    [InlineData("|abcdef\n\tx\ty", "ll<C-v>jlld", "a|b\n  y")]
    [InlineData("|abcdefghij\n\tx\ty", "lllll<C-v>jhd", "abcd|ghij\n\t  y")]
    [InlineData("|abcdef\n\tx\ty", "ll<C-v>jlllD", "a|b\n  ")]
    [InlineData("|abcdef\n\tx\ty", "ll<C-v>jlllc78<Esc>", "ab7|8\n  78")]
    [InlineData("|abcdef\n\tx\ty", "ll<C-v>jlllI7<Esc>", "ab|7cdef\n  7  x\ty")]
    [InlineData("|abcdefghij\n\tx\ty", "lllll<C-v>jhA7<Esc>", "abcd|ef7ghij\n\tx 7  y")]
    [InlineData("|abcdefghij\n\tx\ty", "lllll<C-v>jhrk", "abcd|kkghij\n\tkk  y")]
    [InlineData("|abcdef\n\tx\ty", "l<C-v>jrk", "|kkkkef\nkkkkx\ty")]
    [InlineData("|abc\n\tx\ty", "l<C-v>jlyGo<Esc>p", "abc\n\tx\ty\n|bc\n   x")]
    [InlineData("|abc\n\tx\ty", "l<C-v>jly", "a|bc\n\tx\ty")]
    public void A_tab_a_blocks_edge_cuts_keeps_its_outside_part_as_spaces(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A block's &gt; lays the blanks around its left edge out again as wide again as the shift, and &lt; brings
    /// what follows the blanks back by it, no further than the edge, keeping the blanks that still fit, as Vim's do.</summary>
    [Theory]
    [InlineData("|abcdefghij\n\tx\ty", "lllll<C-v>jh>", "abcd|    efghij\n        x\ty")]
    [InlineData("|abcdefghij\n\tx\ty", "lllll<C-v>j>", "abcde|    fghij\n\tx       y")]
    [InlineData("|abcdefghij\nab\t\tx", "lll<C-v>j>", "ab|    cdefghij\nab          x")]
    [InlineData("|abcdefghij\nx  \t  y", "l<C-v>j2>", "a|        bcdefghij\nx             y")]
    [InlineData("|abcdefghij\na\tbc", "lllll<C-v>j>", "abcde|    fghij\na\tb    c")]
    [InlineData("|abcdefghij\nab\t\tx", "lll<C-v>j<", "ab|cdefghij\nab\tx")]
    [InlineData("|abcdefghij\nab  \tx", "lll<C-v>j<", "abc|defghij\nab  x")]
    [InlineData("|abcdefghij\n\t\tx", "lll<C-v>j<", "|abcdefghij\n\tx")]
    [InlineData("|abcdefghij\nab\t   x", "ll<C-v>j<", "ab|cdefghij\nab x")]
    [InlineData("|abcdefghij\n\t x", "ll<C-v>j<", "|abcdefghij\n x")]
    [InlineData("|abcdefghij\n        x", "lllll<C-v>j<", "abcde|fghij\n     x")]
    public void A_blocks_shift_counts_its_blanks_by_columns(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>iw in visual mode on an empty last line reaches back: the line above's last place first, its cursor
    /// there, then a word further back for each more, as Vim's does.</summary>
    [Theory]
    [InlineData("one\ntwo\n|x", "xviw<Esc>", "one\ntw|o\n")]
    [InlineData("one\ntwo\n|x", "xviwo<Esc>", "one\ntwo\n|")]
    [InlineData("one\ntwo\n|x", "xv2iwd", "one\n|")]
    [InlineData("one\ntwo\n|x", "xv3iw<Esc>", "|one\ntwo\n")]
    [InlineData("one\ntwo\n|x", "xviwiwd", "one\n|")]
    [InlineData("one   \n|x", "xv2iwd", "on|e")]
    [InlineData("one\n  \n|x", "xv2iw<Esc>", "one\n|  \n")]
    [InlineData("foo\n\n  // comment here\ne|nd", "S<Esc>viw$p", "foo\n\n  // comment here\n|end\n")]
    public void Visual_iw_on_an_empty_last_line_reaches_back_a_word_at_a_time(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>An operator from an empty line fills the registers as Vim's does: w from an empty last line takes
    /// nothing, cw changing and yw yanking it; a change by an inclusive motion or iw leaves its register empty; and an
    /// operator over a text of one empty line leaves the registers as they were.</summary>
    [Theory]
    [InlineData("a|b\nx", "xjxcw7<Esc>p", "a\n7|x")]
    [InlineData("a|b\nx", "xjxc2w7<Esc>", "a\n|7")]
    [InlineData("a|b", "xddcw7<Esc>p", "7\n|a")]
    [InlineData("a|b\nx", "xjxywP", "a\n|")]
    [InlineData("a|b", "xddywP", "|")]
    [InlineData("a b| c d e f\ng h i j k l\nm n o p q r", "2djcw7<Esc>", "|7")]
    [InlineData("a|b\nx", "xjxce7<Esc>p", "a\n|7")]
    [InlineData("a|b\nx", "xjxc$7<Esc>p", "a\n|7")]
    [InlineData("a|b\nx", "xjxcl7<Esc>p", "a\n7|x")]
    [InlineData("a|b\nx", "xjxc)7<Esc>p", "a\n7|x")]
    [InlineData("a|b\nx\nc d", "xjxciw7<Esc>p", "a\n|7\nc d")]
    [InlineData("a|b\nx\nc d", "xjxC7<Esc>p", "a\n|7\nc d")]
    [InlineData("a|b\nx", "xjxD\"-p", "a\n|x")]
    [InlineData("a|b", "xddddp", "\n|a")]
    [InlineData("a|b", "xddS7<Esc>p", "7\n|a")]
    [InlineData("a|b", "xddC7<Esc>p", "7\n|a")]
    [InlineData("a|b", "xdddGp", "\n|a")]
    public void An_operator_from_an_empty_line_fills_the_registers_as_vims_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>Whole lines put over a block go below the line its cursor stands on with p, above its top with P.</summary>
    [Theory]
    [InlineData("|aaa\nbbb\nccc\nddd\neee", "yy<C-v>jop", "aa\n|aaa\nbb\nccc\nddd\neee")]
    [InlineData("|aaa\nbbb\nccc\nddd\neee", "yy<C-v>jp", "aa\nbb\n|aaa\nccc\nddd\neee")]
    [InlineData("aaa\nbbb\nc|cc\nddd\neee", "yy<C-v>kp", "aaa\nbb\n|ccc\ncc\nddd\neee")]
    [InlineData("aaa\nbbb\nc|cc\nddd\neee", "yj<C-v>kl2p", "aaa\nb\n|ccc\nddd\nccc\nddd\nc\nddd\neee")]
    [InlineData("aaa\nbbb\nc|cc\nddd\neee", "yy<C-v>kP", "aaa\n|ccc\nbb\ncc\nddd\neee")]
    [InlineData("def f(x):\n    return x + 1\n\n\nclass A:\n   | pass", "V$d<C-v>hkp", "def f(x):\n    return x + 1\n\n\n    |pass\nlass A:")]
    public void Lines_put_over_a_block_go_below_its_cursors_line(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>. after I or A over characters or lines types again from the caret, taking no stretch, as Vim's does: I
    /// at the caret; A as many lines below as the stretch had, at the caret's place in its line, after its character
    /// where A went after the cursor's, on it where A went on it or before the stretch, and on the line's last character
    /// after $; its count unused.</summary>
    [Theory]
    [InlineData("|one two\nthree\nfour five six\nseven", "vlA7<Esc>jj.", "on7e two\nthree\nfou|7r five six\nseven")]
    [InlineData("|one two\nthree\nfour five six\nseven", "vlA7<Esc>.", "on7|7e two\nthree\nfour five six\nseven")]
    [InlineData("o|ne two\nthree four\nfive six seven\neight nine ten\neleven twelve", "vjA7<Esc>j0.", "one two\nth7ree four\nfive six seven\ne|7ight nine ten\neleven twelve")]
    [InlineData("o|ne two\nthree four\nfive six seven\neight nine ten\neleven twelve", "vjA7<Esc>jjjjl.", "one two\nth7ree four\nfive six seven\neight nine ten\nelev|7en twelve")]
    [InlineData("o|ne two\nthree four\nfive six seven\neight nine ten\neleven twelve", "VjA7<Esc>jl.", "one two\nth7ree four\nfive six seven\neigh|7t nine ten\neleven twelve")]
    [InlineData("one two\nthree\nfour f|ive six\nseven", "vkA78<Esc>j.", "one two\nthree\n78four five six\ns7|8even")]
    [InlineData("one two\nthree four\nfive six seven\neight nine ten\nelev|en twelve", "vkkA7<Esc>kkl.", "one two\nthree four\nfive six seven\neight nine ten\n7|7eleven twelve")]
    [InlineData("one two\nthree four\nfive six seven\neight nine ten\nelev|en twelve", "vkI7<Esc>kl.", "one two\nthree four\nfive |7six seven\neigh7t nine ten\neleven twelve")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "vlI7<Esc>jjl.", "7one two\nthree\nf|7our five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "vjjI78<Esc>jl.", "78one two\nth7|8ree\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "v$A7<Esc>jj.", "one tw7o\nthree\nfour five si|7x\nseven")]
    [InlineData("|one two\nthree four\nfive six seven\neight nine ten\neleven twelve", "vj$A78<Esc>jjk.", "one two\nthree fou78r\nfive six seven\neight nine te7|8n\neleven twelve")]
    [InlineData("|one two\nthree four\nfive six seven\neight nine ten\neleven twelve", "0vA78<Esc>jj..", "78one two\nthree four\nf77|88ive six seven\neight nine ten\neleven twelve")]
    [InlineData("|one two\nthree four\nfive six seven\neight nine ten\neleven twelve", "vjjA78<Esc>jj2.", "one two\nthree four\n78five six seven\neight nine ten\ne7|8leven twelve")]
    [InlineData("|\tab\tcd\n  \tef gh\n\t\tij kl\nmn op qr st", "vjA7<Esc>j.", "\tab\tcd\n  \t7ef gh\n\t\tij kl\nmn|7 op qr st")]
    public void Dot_after_visual_insert_or_append_types_again_from_the_caret(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A over characters after a block went on the cursor's character in its first column on the stretch's first
    /// line, and on a later line only where that line reaches the column the block began at, as Vim's does.</summary>
    [Theory]
    [InlineData("|abcdefghij\nabcdefghij\nab\nabcdefghij\nabcdefghij", "ll<C-v>jyj0lvlA7<Esc>", "abcdefghij\nabc|7defghij\nab\nabcdefghij\nabcdefghij")]
    [InlineData("|abcdefghij\nabcdefghij\nab\nabcdefghij\nabcdefghij", "ll<C-v>jyj0lvhA7<Esc>", "abcdefghij\n|7abcdefghij\nab\nabcdefghij\nabcdefghij")]
    [InlineData("|abcdefghij\nabcdefghij\nab\nabcdefghij\nabcdefghij", "ll<C-v>jyj0lllvjA7<Esc>", "abcdefghij\nabcdefghij\na|7b\nabcdefghij\nabcdefghij")]
    [InlineData("|abcdefghij\nabcdefghij\nab\nabcdefghij\nabcdefghij", "lll<C-v>jyj0lllvjA7<Esc>", "abcdefghij\nabcdefghij\nab|7\nabcdefghij\nabcdefghij")]
    [InlineData("|abcdefghij\nabcdefghij\nab\nabcdefghij\nabcdefghij", "lll<C-v>jyj0lllvjjA7<Esc>", "abcdefghij\nabcdefghij\nab\nabc|7defghij\nabcdefghij")]
    [InlineData("|abcdefghij\nabcdefghij\nab\nabc\nabcd\n\nabcdefghij", "lll<C-v>jyj0llvjjlA7<Esc>", "abcdefghij\nabcdefghij\nab\nab|7c\nabcd\n\nabcdefghij")]
    public void Visual_append_after_a_block_lands_where_vims_does(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>is takes a sentence or the white after it a count at a time and as both; a sentence ending its line takes
    /// its line break, the white after it beginning on the next line, a line of blanks included; as with no white after
    /// takes all the white before, over lines of blanks; and visual is goes on from the line after, as Vim's do.</summary>
    [Theory]
    [InlineData("|One two. Three four. Five six. Seven eight.", "2dis", "|Three four. Five six. Seven eight.")]
    [InlineData("|One two. Three four. Five six. Seven eight.", "3dis", "| Five six. Seven eight.")]
    [InlineData("|One two. Three four. Five six. Seven eight.", "4dis", "|Five six. Seven eight.")]
    [InlineData("One two.| Three four. Five six. Seven eight.", "2dis", "One two.| Five six. Seven eight.")]
    [InlineData("One two.| Three four. Five six. Seven eight.", "3dis", "One two.|Five six. Seven eight.")]
    [InlineData("One two.| Three four. Five six. Seven eight.", "2das", "One two.| Seven eight.")]
    [InlineData("One t|wo. Three four. Five six. Seven eight.", "c2is7<Esc>", "|7Three four. Five six. Seven eight.")]
    [InlineData("One two.| Three four. Five six. Seven eight.", "y2isP", "One two. Three four|. Three four. Five six. Seven eight.")]
    [InlineData("|One two.\nThree four.\nFive six.", "2dis", "|Three four.\nFive six.")]
    [InlineData("|One two.\n   \nThree four.\nFive six.", "dis", "  | \nThree four.\nFive six.")]
    [InlineData("|One two.\n   \nThree four.\nFive six.", "2dis", "|Three four.\nFive six.")]
    [InlineData("|One two.\n\t\nThree four.\nFive six.", "dis", "|\t\nThree four.\nFive six.")]
    [InlineData("One two.\n | \nThree four.\nFive six.", "dis", "One two.\n|Three four.\nFive six.")]
    [InlineData("One two.\n | \nThree four.\nFive six.", "das", "One two.\n|Five six.")]
    [InlineData("One two.\n | \nThree four.\nFive six.", "2das", "|One two.")]
    [InlineData("One two.\n   \n|Three four.\nFive six.", "das", "One two.\n|Five six.")]
    [InlineData("One two.\n   \n|Three four.\nFive six.", "2das", "|One two.")]
    [InlineData("One two.  \n   \n|Three four.\nFive six.", "das", "One two|.\nFive six.")]
    [InlineData("|One two.\n   Three four.\nFive six.", "dis", "   |Three four.\nFive six.")]
    [InlineData("One two.\n|   Three four.\nFive six.", "dis", "One two.\n|Three four.\nFive six.")]
    [InlineData("One two.\n|   Three four.\nFive six.", "vasd", "One two.\n|\nFive six.")]
    [InlineData("One two.\n|   \nThree four.\nFive six.", "visd", "One two.\n|\nThree four.\nFive six.")]
    [InlineData("Zero. One two.\n   \n  |Three four. Five.\nSix.", "2das", "Zero. One two.\n|Six.")]
    [InlineData("x.\n  two Fi|ve.\n \n\nfour.", "g~is", "x.\n  |TWO fIVE.\n \n\nfour.")]
    [InlineData("x.\n  two Fi|ve.\n \n\nfour.", "yisP", "x.\n  |two Five.\n  two Five.\n \n\nfour.")]
    [InlineData("two Five| two.\n  Three yes!\none Three yes?", "visisd", "|Three yes!\none Three yes?")]
    [InlineData("Three |Five.\n   \nyes.  \nfour.)", "visisd", "|\nyes.  \nfour.)")]
    [InlineData("one x|.\"\n\t\none four.) Three two!", "vlisd", "one |x\none four.) Three two!")]
    [InlineData("Three six.)\n  Three four x! |yes x yes?\nx?", "vasasd", "Three six.)\n  Three four x|!")]
    public void Counted_sentence_objects_and_lines_of_blanks_between_take_what_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A command that can't act (. with nothing to repeat, a put with nothing in its register, ~ or r with no
    /// character, h, l, f, t, ;, , or * with nowhere to go) keeps the column j and k aim for, where x, D or a search
    /// that finds nothing sets it afresh, as Vim's do.</summary>
    [Theory]
    [InlineData("abcd|ef\n\nabcdefgh", "j.j", "abcdef\n\nabcd|efgh")]
    [InlineData("abcd|ef\n\nabcdefgh", "jpj", "abcdef\n\nabcd|efgh")]
    [InlineData("abcd|ef\n\nabcdefgh", "j~j", "abcdef\n\nabcd|efgh")]
    [InlineData("abcd|ef\n\nabcdefgh", "jrxj", "abcdef\n\nabcd|efgh")]
    [InlineData("abcd|ef\n\nabcdefgh", "jhj", "abcdef\n\nabcd|efgh")]
    [InlineData("abcd|ef\nab\nabcdefgh", "jlj", "abcdef\nab\nabcd|efgh")]
    [InlineData("abcd|ef\n\nabcdefgh", "jfxj", "abcdef\n\nabcd|efgh")]
    [InlineData("abcd|ef\nab\nabcdefgh", "j,j", "abcdef\nab\nabcd|efgh")]
    [InlineData("abcd|ef\n\nabcdefgh", "j*j", "abcdef\n\nabcd|efgh")]
    [InlineData("abcd|ef\n\nabcdefgh", "jxj", "abcdef\n\n|abcdefgh")]
    [InlineData("abcd|ef\n\nabcdefgh", "jDj", "abcdef\n\n|abcdefgh")]
    [InlineData("abcd|ef\n\nabcdefgh", "j/zzz<CR>j", "abcdef\n\n|abcdefgh")]
    public void A_command_that_cannot_act_keeps_the_column_j_and_k_aim_for(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>. after a visual J on the last line, with nothing to join, leaves the caret where it stood, where the J
    /// itself took a linewise stretch's caret to its line's start, as Vim's do.</summary>
    [Theory]
    [InlineData("ab cd\nef| gh\nij kl", "VJ.", "ab cd\nef gh| ij kl")]
    [InlineData("ab cd\nef| gh\nij kl", "VJl.", "ab cd\nef gh |ij kl")]
    [InlineData("ab cd\nef| gh\nij kl", "vJ.", "ab cd\nef gh| ij kl")]
    [InlineData("ab cd\nef| gh\nij kl", "VJ$.", "ab cd\nef gh ij k|l")]
    [InlineData("ab cd\nef| gh\nij kl", "J.", "ab cd\nef gh| ij kl")]
    [InlineData("ab cd\nef| gh\nij kl", "VjJk.j.", "ab cd| ef gh ij kl")]
    [InlineData("ab cd\nef gh\nij| kl", "VJ", "ab cd\nef gh\n|ij kl")]
    public void Dot_after_a_visual_join_with_nothing_to_join_leaves_the_caret(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A bracket object over a visual stretch takes the block around its start, one further out from an open
    /// bracket it starts on, and an inner block the stretch holds already gives way to the one around the character
    /// before it, or fails, keeping the stretch; a takes the first block ahead of a start outside any, as Vim's do.</summary>
    [Theory]
    [InlineData("x |= (a + (b * c)) - d;\ny = [e, f];", "vji(d", "x | [e, f];")]
    [InlineData("x |= (a + (b * c)) - d;\ny = [e, f];", "vja(d", "x = | - d;\ny = [e, f];")]
    [InlineData("x = |(a + (b * c)) - d;", "vli(d", "x = (a + (|)) - d;")]
    [InlineData("x = |(a + (b * c)) - d;", "vla(d", "x = (a + |) - d;")]
    [InlineData("x = (a + |(b * c)) - d;", "vli(d", "x = (|) - d;")]
    [InlineData("x = |(a + (b * c)) - d;", "vl2i(d", "x = | + (b * c)) - d;")]
    [InlineData("a (b\nc)| d (e\nf) g", "vji(d", "a (|) d (e\nf) g")]
    [InlineData("a (b\nc)| d (e\nf) g", "vja(d", "a (b\nc) d | g")]
    [InlineData("(a)| (b c) d", "v5li(d", "(|) (b c) d")]
    [InlineData("(|(a b) c (d e))", "v4li(d", "(|)")]
    [InlineData("(|(a b) c (d e))", "v12li(d", "(|)")]
    [InlineData("f|(a,\n  g(b, c),\n  d)\nend", "vji(d", "f(a,\n  g(|),\n  d)\nend")]
    [InlineData("a| (b (c d) e) f", "v12la(d", "a | f")]
    [InlineData("a| (b (c d) e) f", "v10li(d", "a|) f")]
    [InlineData("x = |(a + [b * c]) - d;", "vli[d", "x = (a + [|]) - d;")]
    [InlineData("x = |{a + {b * c}} - d;", "vla{d", "x = {a + |} - d;")]
    [InlineData("(a b) |(c d)", "vli(d", "(a b) | d)")]
    public void Bracket_objects_over_a_visual_stretch_go_out_from_its_start_as_vims_do(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>. after a visual change whose stretch $ took to the line's end takes the line from the cursor to its end,
    /// its line break with it, however long the line, as Vim's help has it.</summary>
    [Theory]
    [InlineData("ab|cdefgh\nijklmnop\nqrstuvwx\nyzABCDEF\nGHIJKLMN", "v$djj0.", "abijklmnop\nqrstuvwx\n|GHIJKLMN")]
    [InlineData("ab|cdefgh\nijklmnop\nqrstuvwx\nyzABCDEF\nGHIJKLMN", "v$djj01l.", "abijklmnop\nqrstuvwx\ny|GHIJKLMN")]
    [InlineData("ab|cdefgh\nijklmnop\nqrstuvwx\nyzABCDEF\nGHIJKLMN", "v$~jj0.", "abCDEFGH\nijklmnop\n|QRSTUVWX\nyzABCDEF\nGHIJKLMN")]
    [InlineData("ab|cdefgh\nijklmnop\nqrstuvwx\nyzABCDEF\nGHIJKLMN", "v$c7<Esc>jj01l.", "ab7ijklmnop\nqrstuvwx\ny|7GHIJKLMN")]
    [InlineData("ab|c\ndefgh\nij\nklmno\npq\nrstu", "v$dG.", "abdefgh\nij\nklmno\npq\n|")]
    [InlineData("|abcdefgh\nij\nklmnopqrstu\nvw\nxyzABCDEFGHI\nJK\nLMNOPQRS", "v$hdjj01l.", "\nij\nk|tu\nvw\nxyzABCDEFGHI\nJK\nLMNOPQRS")]
    [InlineData("|abcdefgh\nij\nklmnopqrstu\nvw\nxyzABCDEFGHI\nJK\nLMNOPQRS", "vj$djj01l.", "klmnopqrstu\nvw\nx|LMNOPQRS")]
    [InlineData("|abcdefgh\nij\nklmnopqrstu\nvw\nxyzABCDEFGHI\nJK\nLMNOPQRS", "v2$djj01l.", "klmnopqrstu\nvw\nx|LMNOPQRS")]
    public void Dot_after_a_visual_change_to_the_lines_end_takes_the_line_to_its_end(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>Escape from S, cc, o or O with nothing typed takes the indent back and leaves j and k aiming for the
    /// column it took the caret to, as Vim's do.</summary>
    [Theory]
    [InlineData("ab\n|    cdef ghij\nklmnopqrstuv\nwx", "S<Esc>j", "ab\n\nklmn|opqrstuv\nwx")]
    [InlineData("ab\n|\tcdef ghij\nklmnopqrstuv\nwx", "cc<Esc>2j", "ab\n\nklmnopqrstuv\nw|x")]
    [InlineData("ab\n|  cdef\n\nklmnopqrstuv\nwx", "S<Esc>jj", "ab\n\n\nkl|mnopqrstuv\nwx")]
    [InlineData("ab\n|    cdef ghij\nklmnopqrstuv\nwx", "o<Esc>j", "ab\n    cdef ghij\n\nklmn|opqrstuv\nwx")]
    [InlineData("ab\n|    cdef ghij\nklmnopqrstuv\nwx", "S<Esc>k", "a|b\n\nklmnopqrstuv\nwx")]
    [InlineData("ab\n|    cdef ghij\nklmnopqrstuv\nwx", "S<Esc>3lj", "ab\n\nklmn|opqrstuv\nwx")]
    [InlineData("ab\n|\tcdef ghij\nklmnopqrstuv\nwx", "S<Esc>xj", "ab\n\n|klmnopqrstuv\nwx")]
    [InlineData("ab\n|\tcdef ghij\nklmnopqrstuv\nwx", "S<Esc>vjd", "ab\n|pqrstuv\nwx")]
    public void An_emptied_indent_leaves_j_and_k_aiming_for_its_column(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>Visual mode begins with j and k aiming for the column normal mode's did, a block after $ reaching each
    /// line's end, as Vim's does.</summary>
    [Theory]
    [InlineData("abc|defgh\nij\nklmnopqrstu\nvw\nxyzABCDEFGHI", "jvjd", "abcdefgh\ni|opqrstu\nvw\nxyzABCDEFGHI")]
    [InlineData("abcdef|gh\nij\nklmnopqrstu\nvw\nxyzABCDEFGHI", "j<C-v>jd", "abcdefgh\n|i\nkrstu\nvw\nxyzABCDEFGHI")]
    [InlineData("|abcdefgh\nij\nklmnopqrstu\nvw\nxyzABCDEFGHI", "$<C-v>jjd", "abcdef|g\nij\nklmnopq\nvw\nxyzABCDEFGHI")]
    [InlineData("|abcdefgh\nij\nklmnopqrstu\nvw\nxyzABCDEFGHI", "$vjjd", "abcdefg|vw\nxyzABCDEFGHI")]
    [InlineData("|abcdefgh\nij\nklmnopqrstu\nvw\nxyzABCDEFGHI", "jj$k<C-v>jd", "abcdefgh\n|i\nk\nvw\nxyzABCDEFGHI")]
    [InlineData("abcdef|gh\nijklm\nno\npqrstuvwx\nyz", "jj<C-v>kA7<Esc>", "abcdefgh\ni|jklm 7\nno    7\npqrstuvwx\nyz")]
    [InlineData("abc|defgh\nijklm\nno\npqrstuvwx\nyz", "jjvkd", "abcdefgh\nij|k\npqrstuvwx\nyz")]
    public void Visual_mode_takes_the_column_j_and_k_aim_for(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A block's c, s, I or A whose text has a line break types it on the block's first line alone, the other
    /// lines left as the change found them, with no padding, the cursor where typing ended, as Vim's do.</summary>
    [Theory]
    [InlineData("|one two\nthree\nfour five six\nseven", "<C-v>jlc7<CR>8<Esc>", "7\n|8e two\nree\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jlc7<CR>8<Esc>", "o7\n|8two\ntee\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jjc7<CR>8<Esc>", "o7\n|8e two\ntree\nfur five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jls7<CR>8<Esc>", "o7\n|8two\ntee\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jlc7<CR>8<CR>9<Esc>", "o7\n8\n|9two\ntee\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jI7<CR>8<Esc>", "o7\n|8ne two\nthree\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jA7<CR>8<Esc>", "on7\n|8e two\nthree\nfour five six\nseven")]
    [InlineData("one t|wo\nthree\nfour five six\nseven", "<C-v>jA7<CR>8<Esc>", "one tw7\n|8o\nthree\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>j$A7<CR>8<Esc>", "one two7\n|8\nthree\nfour five six\nseven")]
    [InlineData("o|ne two\nthree\nfour five six\nseven", "<C-v>jlc7<CR>8<Esc>j.", "o7\n8two\n7\n|8e\nur five six\nseven")]
    [InlineData("    a|b cd\n    ef gh\n    ij kl", "<C-v>jjc7<CR>8<Esc>", "    a7\n    |8cd\n    e gh\n    i kl")]
    [InlineData("abcdefgh\nij\nklmnopqrstu\nvw\nx|yzABCDEFGHI", "<C-v>j$I7<CR>8<Esc>", "abcdefgh\nij\nklmnopqrstu\nvw\nx7\n|8yzABCDEFGHI")]
    [InlineData("f(a,\n  g(b, c),\n  d)\nen|d", "<C-v>jl$A7<CR>8<Esc>", "f(a,\n  g(b, c),\n  d)\nend7\n|8")]
    public void A_blocks_text_with_a_line_break_goes_on_its_first_line_alone(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>A block yanked or deleted keeps a piece for each of its lines, a line short of it as many spaces as the
    /// block is wide (one more after $), and a put pads each line out to its column, as Vim's do.</summary>
    [Theory]
    [InlineData("|abcdef\n\nghijkl\nmn", "<C-v>$jyp", "a|abcdefbcdef\n \nghijkl\nmn")]
    [InlineData("|abcdef\n\nghijkl\nmn", "<C-v>$jyjp", "abcdef\n|abcdef\n      ghijkl\nmn")]
    [InlineData("|abcdef\n\nghijkl\nmn", "<C-v>$jy$p", "abcdef|abcdef\n      \nghijkl\nmn")]
    [InlineData("a|bcdef\n\nghijkl\nmn\nopqrstuvwx\ny", "<C-v>$jjyp", "ab|bcdefcdef\n        \nghhijklijkl\nmn\nopqrstuvwx\ny")]
    [InlineData("a|bcdef\n\nghijkl\nmn\nopqrstuvwx\ny", "<C-v>lljjyp", "ab|bcdcdef\n     \nghhijijkl\nmn\nopqrstuvwx\ny")]
    [InlineData("a|bcdef\n\nghijkl\nmn\nopqrstuvwx\ny", "<C-v>$jjyjjjp", "abcdef\n\nghijkl\nmn|bcdef\nop      qrstuvwx\ny hijkl")]
    [InlineData("abc|def\nab\nghijkl\n\nopqrstuvwx\ny", "<C-v>$jjyP", "abc|defdef\nab     \nghijkljkl\n\nopqrstuvwx\ny")]
    [InlineData("abc|def\n\nghijkl\nmn\nopqrstuvwx\ny", "<C-v>$jjyGp", "abcdef\n\nghijkl\nmn\nopqrstuvwx\ny|def\n     \n jkl")]
    [InlineData("a|bcdef\n\nghijkl\nmn\nopq", "<C-v>$jjdjjp", "a\n\ng|bcdef\nm      n\nohijklpq")]
    [InlineData("abc|def\nab\nghijkl\nmnopqrs", "<C-v>lljjdGp", "abc\nab\nghi\nm|defnopqrs\n    \n jkl")]
    public void A_block_keeps_a_piece_for_each_line_and_puts_each_out_to_its_column(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

    /// <summary>o in a block $ took to each line's end leaves the corner it moves from past its own line's end, the block
    /// no longer reaching each line's end, as Vim's does.</summary>
    [Theory]
    [InlineData("|abcdef\nab\nghijklmn\nop", "<C-v>$oA7<Esc>", "|abcdef 7\nab\nghijklmn\nop")]
    [InlineData("|abcdef\nab\nghijklmn\nop", "<C-v>j$oA7<Esc>", "|abc7def\nab 7\nghijklmn\nop")]
    [InlineData("|abcdef\nab\nghijklmn\nop", "<C-v>$jod", "|def\n\nghijklmn\nop")]
    [InlineData("|abcdef\nab\nghijklmn\nop", "<C-v>j$ojd", "abcdef\n|\nghijklmn\nop")]
    [InlineData("|ab cd\nefghij\nk", "<C-v>j$oA7<Esc>", "|ab cd  7\nefghij 7\nk")]
    [InlineData("|ab cd\nefghij\nk", "<C-v>$ooA7<Esc>", "|ab cd 7\nefghij\nk")]
    public void O_in_a_block_after_dollar_keeps_its_corner_past_the_lines_end(string before, string keys, string after) =>
        Assert.Equal(after, Run(before, keys));

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

    /// <summary>The keys pressed in turn on the marked text, insert mode's typing standing in for the editor's, the
    /// text keeping a final line break as the file it stands for would (<see cref="FileOf"/>).</summary>
    private static string Run(string before, string keys) => Run(new Vim(), before, keys);

    /// <summary>A view of a text as a file read with it: one ending in a line break keeps a final line break, as Vim's
    /// 'eol' is set reading it, one ending without keeps none.</summary>
    public static VimTextView FileOf(string marked)
    {
        string text = Marked.Parse(marked).Text.ToString();
        return new VimTextView(new IndentOptions()) { KeepsFinalLineBreak = text.Length == 0 || text.EndsWith('\n') };
    }

    private static string Run(Vim vim, string before, string keys, IVimView? view = null)
    {
        (TextSnapshot text, SelectionSet carets) = Marked.Parse(before);
        (text, carets) = Drive(vim, text, carets, Keys(keys), stopAtFailure: false, view ?? FileOf(before));
        return Marked.Show(text, carets);
    }

    /// <summary>The keys through Vim as an editor takes them: what Vim does done, and in insert mode each key typed as
    /// the editor types it, Enter carrying its line's indent and Backspace taking a character back.</summary>
    public static (TextSnapshot Text, SelectionSet Carets) Drive(Vim vim, TextSnapshot text, SelectionSet carets, string keys, bool stopAtFailure = false,
        IVimView? view = null) =>
        Drive(vim, text, carets, Keys(keys), stopAtFailure, view);

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
                    carets = Vim.Settled(text, carets, view?.KeepsFinalLineBreak ?? true);
                    if (ran.MessageIsError)
                    {
                        break;
                    }
                }
                continue;
            }
            if (stopAtFailure && ReferenceEquals(result, VimResult.Held) && vim.Pending.Length == 0 && vim.Mode == VimMode.Normal && key != VimKeys.Escape)
            {
                // A command that fails ends the keys, as Vim's beep flushes what was typed ahead.
                break;
            }
            if (!result.Taken && vim.Mode == VimMode.Insert)
            {
                EditPlan? typed = key switch
                {
                    VimKeys.Enter => TextEditing.NewLine(text, carets, "\n", new IndentOptions()),
                    VimKeys.Backspace => TextEditing.Backspace(text, carets, new IndentOptions()),
                    VimKeys.Tab => TextEditing.Insert(text, carets, "\t"),
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
                carets = Vim.Settled(text, carets, view?.KeepsFinalLineBreak ?? true);
            }
        }
        return (text, carets);
    }

    /// <summary>Not a key: timeoutlen passing with no key pressed, as the editor tells Vim mode.</summary>
    private const string Waited = "<Wait>";

    /// <summary>The keys of Vim's notation one by one: a named key ("&lt;Esc&gt;") whole, any other character alone.</summary>
    public static IEnumerable<string> Keys(string keys)
    {
        string[] named = [VimKeys.Escape, VimKeys.Enter, VimKeys.Backspace, VimKeys.Tab, VimKeys.Delete, VimKeys.CtrlR, VimKeys.CtrlV, VimKeys.CtrlO, VimKeys.CtrlI,
            VimKeys.CtrlU, VimKeys.CtrlW, VimKeys.Up, VimKeys.Down, "<D-s>", Waited];
        for (int i = 0; i < keys.Length; i++)
        {
            string? name = keys[i] == '<' ? Array.Find(named, n => string.CompareOrdinal(keys, i, n, 0, n.Length) == 0) : null;
            yield return name ?? keys[i].ToString();
            i += name is null ? 0 : name.Length - 1;
        }
    }
}
