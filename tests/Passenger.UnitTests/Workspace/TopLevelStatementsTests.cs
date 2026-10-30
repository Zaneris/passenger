using Passenger.Host;

namespace Passenger.UnitTests.Workspace;

/// <summary>Whether a C# file of no directive begins with top-level statements, which makes it a program as <c>dotnet run</c>
/// takes it: its first member past using directives, extern aliases, attributes, modifiers, comments and preprocessor lines
/// is code, never a type's or namespace's declaration.</summary>
public class TopLevelStatementsTests
{
    [Theory]
    [InlineData("Console.WriteLine(\"Hello\");\n")]
    [InlineData("var n = 2;\nConsole.WriteLine($\"é {n}\");\n")]
    [InlineData("﻿// A greeting.\n/* Two lines\n   of comment. */\nusing System;\nusing System.Text;\n\nConsole.WriteLine(1);\n")]
    [InlineData("global using System.Linq;\nusing static System.Math;\nusing Text = System.Text;\nusing global::System.IO;\nreturn Abs(-1);\n")]
    [InlineData("using var reader = new StreamReader(\"a\");\n")]
    [InlineData("using (var reader = new StreamReader(\"a\")) { }\n")]
    [InlineData("using StreamReader reader = new(\"a\");\n")]
    [InlineData("#if DEBUG\nConsole.WriteLine(\"debug\");\n#endif\n")]
    [InlineData("#region Main\nawait Task.Delay(1);\n#endregion\n")]
    [InlineData("[System.Diagnostics.CodeAnalysis.SuppressMessage(\"a]b\", \"c\")]\nstatic void Main() { }\n")]
    [InlineData("static int Twice(int x) => x * 2;\nConsole.WriteLine(Twice(2));\n")]
    [InlineData("global::System.Console.WriteLine(1);\n")]
    [InlineData("record.Save();\n")]
    [InlineData("Action a = delegate { };\n")]
    [InlineData("delegate { }.Invoke();\n")]
    [InlineData("if (args.Length > 0) { return 1; }\nreturn 0;\nclass Helper { }\n")]
    public void Code_that_begins_with_a_statement_is_a_programs(string code) =>
        Assert.True(ProgramFiles.BeginsWithStatement(code, 0), $"Code beginning with a statement was taken for declarations alone:\n{code}");

    [Theory]
    [InlineData("")]
    [InlineData("// Nothing but a comment.\n")]
    [InlineData("using System;\nusing System.Text;\n")]
    [InlineData("namespace Tools;\n\npublic static class Helper { }\n")]
    [InlineData("namespace Tools\n{\n    class Helper { }\n}\n")]
    [InlineData("using System;\n\n[Serializable]\npublic sealed partial class Shape { }\n")]
    [InlineData("[assembly: System.Reflection.AssemblyTitle(\"Tools\")]\ninternal static class Helper { }\n")]
    [InlineData("extern alias Old;\nfile record Point(int X, int Y);\n")]
    [InlineData("public readonly record struct Size(int Width, int Height);\n")]
    [InlineData("interface IShape { }\n")]
    [InlineData("enum Colour { Red }\n")]
    [InlineData("public delegate void Changed(object sender);\n")]
    [InlineData("/* using Fake; Console.WriteLine(); */\nstruct Point { }\n")]
    public void Code_that_declares_types_or_nothing_is_no_programs(string code) =>
        Assert.False(ProgramFiles.BeginsWithStatement(code, 0), $"Code declaring types or nothing was taken for a program:\n{code}");
}
