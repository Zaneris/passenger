using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A language server's edits made the file's own, as a reformat's are: <see cref="LineEndings.Fit"/>, then
/// <see cref="Indentation.Fit(TextSnapshot, IReadOnlyList{TextEdit}, IndentOptions)"/>. What an edit writes is kept in the
/// file's line breaks and indentation, and an edit that then writes what the file has is left out.</summary>
public class ServerEditsFitTests
{
    private const string Sixes = "static class Sixes\n{\n      public static int Value()\n      {\n            int total = 1;\n                  return total;\n      }\n}\n";

    private static readonly IndentOptions SixSpaces = new(6, UseTabs: false, TabWidth: 4);

    private static List<TextEdit> Fitted(TextSnapshot text, string eol, IndentOptions indent, params TextEdit[] edits) =>
        Indentation.Fit(text, LineEndings.Fit(text, edits, eol), indent);

    [Fact]
    public void A_six_space_files_line_put_back_a_level_is_written_as_the_server_wrote_it()
    {
        TextSnapshot text = TextSnapshot.From(Sixes);
        int ragged = Sixes.IndexOf("                  return", StringComparison.Ordinal);
        string formatted = Sixes.Replace("                  return", "            return", StringComparison.Ordinal);

        TextEdit whole = new(ragged, 18, new string(' ', 12));
        Assert.Equal([whole], Fitted(text, "\n", SixSpaces, whole));
        TextEdit tail = new(ragged + 12, 6, string.Empty);
        Assert.Equal(formatted, text.Apply(Fitted(text, "\n", SixSpaces, tail)).ToString());
        TextEdit broken = new(ragged - 1, 19, "\n" + new string(' ', 12));
        Assert.Equal(formatted, text.Apply(Fitted(text, "\n", SixSpaces, broken)).ToString());
    }

    [Fact]
    public void Edits_that_write_what_the_file_has_in_its_line_breaks_and_indentation_come_to_nothing()
    {
        TextSnapshot sixes = TextSnapshot.From(Sixes);
        int indent = Sixes.IndexOf("      public", StringComparison.Ordinal);
        Assert.Empty(Fitted(sixes, "\n", SixSpaces, new TextEdit(indent, 6, new string(' ', 6))));

        string crlf = Sixes.Replace("\n", "\r\n", StringComparison.Ordinal);
        int line = crlf.IndexOf("\r\n      public", StringComparison.Ordinal);
        Assert.Empty(Fitted(TextSnapshot.From(crlf), "\r\n", SixSpaces, new TextEdit(line, 8, "\n      ")));

        string tabbed = "static class Tabbed\n{\n\tpublic static int Value()\n\t{\n\t\treturn 1;\n\t}\n}\n";
        int body = tabbed.IndexOf("\t\treturn", StringComparison.Ordinal);
        Assert.Empty(Fitted(TextSnapshot.From(tabbed), "\n", new IndentOptions(4, UseTabs: true, TabWidth: 4), new TextEdit(body, 2, new string(' ', 8))));
    }
}
