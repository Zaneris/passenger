using Passenger.Host;

namespace Passenger.UnitTests.Workspace;

/// <summary>Whether a C# file of no directive begins with top-level statements, which makes it a program as <c>dotnet run</c>
/// takes it: its first member past using directives, extern aliases, attributes, modifiers, comments and preprocessor lines
/// is code, never a type's or namespace's declaration; in a file a merge left in conflict, past its markers and the sides
/// after ||||||| and ======= too, which the compiler reads as no code.</summary>
public class TopLevelStatementsTests
{
    [Fact]
    public void Code_that_begins_with_a_statement_is_a_programs_and_code_declaring_types_or_nothing_is_not() => Cases.All(
        ("code that begins with a statement is a program's", () => Cases.Each(
        [
            "Console.WriteLine(\"Hello\");\n",
            "\uFEFF// A greeting.\n/* Two lines\n   of comment. */\nusing System;\nusing System.Text;\n\nConsole.WriteLine(1);\n",
            "using var reader = new StreamReader(\"a\");\n",
            "using (var reader = new StreamReader(\"a\")) { }\n",
            "#if DEBUG\nConsole.WriteLine(\"debug\");\n#endif\n",
            "<<<<<<< HEAD\nConsole.WriteLine(1);\n=======\nclass Shop { short count; }\n>>>>>>> feature\n",
        ], code => Assert.True(ProgramFiles.BeginsWithStatement(code, 0), $"Code beginning with a statement was taken for declarations alone:\n{code}"))),
        ("code that declares types or nothing is no program's", () => Cases.Each(
        [
            "",
            "namespace Tools;\n\npublic static class Helper { }\n",
            "using System;\n\n[Serializable]\npublic sealed partial class Shape { }\n",
            "/* using Fake; Console.WriteLine(); */\nstruct Point { }\n",
            "<<<<<<< HEAD\nclass Shop { long count; }\n||||||| base\nConsole.WriteLine(1);\n=======\nConsole.WriteLine(2);\n>>>>>>> feature\n",
        ], code => Assert.False(ProgramFiles.BeginsWithStatement(code, 0), $"Code declaring types or nothing was taken for a program:\n{code}"))));
}
