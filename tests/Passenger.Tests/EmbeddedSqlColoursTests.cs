using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>SQL written in C# takes the editor's SQL colours where its C# context marks it as SQL.</summary>
public sealed class EmbeddedSqlColoursTests : FileColoursFolder
{
    private const string Code = """"
        using System.Diagnostics.CodeAnalysis;

        static class Queries
        {
            [StringSyntax("Sql")]
            private static string Query { get; set; } = "select 7";

            private static void Run([StringSyntax("Sql")] string command) { }

            private static void Read(int userId)
            {
                Run(command: "SELECT customer FROM users WHERE id = 71");
                Run("s\u0065lect 36");

                // language=SQL
                string marked = @"UPDATE customers SET active = 1";
                string block = /* lang=sql */ "TRUNCATE TABLE staging";
                Run($"""
                    /* multiline
                    SQL comment */
                    select customer
                    from customers
                    where id = {userId}
                    order by customer
                    """);
                string later = "DELETE FROM logs";
                Run(later);
                Query = "INSERT INTO logs VALUES (34)";

                // lang=sql
                string around = $"SELECT {("select 86")} FROM purchases";
                string plain = "select 99";
                string joined = /* lang=sql */ "select 91" + " from logs";
                // lang=unknown
                string unknown = "select 92";
            }
        }
        """";

    [AvaloniaFact]
    public void Marked_CSharp_strings_are_coloured_like_SQL_files_and_follow_edits()
    {
        File.WriteAllText(Path.Combine(folder, "Queries.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>""");
        MainWindow window = Window();
        try
        {
            CodeEditor file = Open(window, "Query.sql", "SELECT customer FROM users WHERE id = 71");
            TokenKind? keyword = KindOf(file, "SELECT");
            TokenKind? identifier = KindOf(file, "customer");
            Assert.Equal(TokenKind.Keyword, keyword);

            window.OpenFolder(folder);
            CodeEditor editor = Open(window, "Queries.cs", Code);
            Ui.WaitFor(() => KindOf(editor, "SELECT", settle: false) == keyword,
                () => $"the named SQL argument's keyword to be coloured ({window.CSharpService?.State})", TimeSpan.FromMinutes(2));

            foreach (string word in new[] { "select", "FROM", @"s\u0065lect", "UPDATE", "TRUNCATE", "order by", "DELETE", "INSERT INTO" })
            {
                Assert.True(KindOf(editor, word) == keyword, $"'{word}' should use the SQL keyword colour.");
            }
            foreach (string word in new[] { "customer", "purchases" })
            {
                Assert.True(KindOf(editor, word) == identifier, $"'{word}' should use the SQL identifier colour.");
            }
            Assert.Equal(TokenKind.Number, KindOf(editor, "71"));
            Assert.Equal(TokenKind.Comment, KindOf(editor, "SQL comment"));
            foreach (string text in new[] { "select 86", "select 99", "select 91", "select 92" })
            {
                Assert.True(KindOf(editor, text) == TokenKind.String, $"'{text}' should keep the C# string colour.");
            }

            int at = Code.IndexOf("language=SQL", StringComparison.Ordinal) + "language=".Length;
            editor.Selections = SelectionSet.Single(new Selection(at, at + 3));
            editor.Focus();
            window.KeyTextInput("text");
            Ui.WaitFor(() => KindOf(editor, "UPDATE", settle: false) == TokenKind.String,
                "removing the SQL marker to restore the string's C# colour");
        }
        finally
        {
            window.Close();
        }
    }
}
