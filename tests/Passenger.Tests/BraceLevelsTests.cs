using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Passenger.App;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Rainbow braces in as many colours as the theme gives, three to six: a theme of five draws a bracket at the sixth
/// depth in the first level's colour and one at the fifth in the fifth's, and Passenger's own theme keeps its three.</summary>
public sealed class BraceLevelsTests
{
    private const string Source = "class C\n{\n    void M()\n    {\n        var x = F(G(H(I(1))));\n    }\n}\n";

    private static readonly string[] Five = ["#FF2020", "#20E020", "#3060FF", "#F0E000", "#E020E0"];

    [AvaloniaFact]
    public void A_theme_of_five_brace_colours_draws_the_sixth_depth_in_the_first_level_s_colour()
    {
        string tokens = string.Join(", ", Five.Select((c, i) => $"\"Syntax.Brace{i + 1}\": \"{c}\""));
        ThemeRead<ThemeFile> read = ThemeFiles.ReadTheme("Five.json", Encoding.UTF8.GetBytes($$$"""
            {"format": 1, "name": "Five", "tokens": {"Editor.Background": "#000000", "Editor.CurrentLine": "#000000", {{{tokens}}}}}
            """), ThemeResources.Kinds);
        Assert.True(read.Read is not null, read.Refused);
        string folder = TestFolders.Create("brace-levels-");
        File.WriteAllText(Path.Combine(folder, "Nested.cs"), Source);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.OpenFile("Nested.cs");
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            int sixth = Source.IndexOf("I(", StringComparison.Ordinal) + 1;
            int fifth = Source.IndexOf("H(", StringComparison.Ordinal) + 1;
            Assert.Equal(TokenKind.Brace3, KindAt(editor, sixth));

            Themes.Wear(Application.Current!, read.Read, null);
            Ui.Settle();
            Assert.Equal(TokenKind.Brace5, KindAt(editor, fifth));
            Assert.Equal(TokenKind.Brace1, KindAt(editor, sixth));
            Assert.Equal(Color.Parse(Five[0]), Drawn(window, editor, sixth));
            Assert.Equal(Color.Parse(Five[4]), Drawn(window, editor, fifth));

            Themes.Wear(Application.Current!, null, null);
            Ui.Settle();
            Assert.Equal(TokenKind.Brace3, KindAt(editor, sixth));
        }
        finally
        {
            Themes.Wear(Application.Current!, null, null);
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    private static TokenKind? KindAt(CodeEditor editor, int offset) =>
        editor.Highlighter!.GetSpans(editor.Document.Snapshot, offset, 1).Where(s => s.Start <= offset && offset < s.End).Cast<SyntaxSpan?>().FirstOrDefault()?.Kind;

    /// <summary>Which of the five brace colours a bracket is drawn in: the one nearest the pixel of its cell furthest from the
    /// black ground, so a glyph's softened edges never decide it.</summary>
    private static Color Drawn(MainWindow window, CodeEditor editor, int offset)
    {
        Point top = editor.PointOf(offset);
        double width = editor.PointOf(offset + 1).X - top.X;
        List<Point> cell = [];
        for (double y = top.Y + 1; y < top.Y + editor.LineHeight - 1; y += 1)
        {
            for (double x = top.X + 0.5; x < top.X + width; x += 1)
            {
                cell.Add(editor.TranslatePoint(new Point(x, y), window) ?? throw new InvalidOperationException("The editor is not in the window."));
            }
        }
        Color brightest = Ui.Pixels(window, cell).MaxBy(c => c.R + c.G + c.B);
        return Five.Select(Color.Parse).MinBy(c => Math.Abs(c.R - brightest.R) + Math.Abs(c.G - brightest.G) + Math.Abs(c.B - brightest.B));
    }
}
