using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A file's indentation found from its own lines: the step most changes between neighbouring lines take, a step of 8
/// read as two of 4 where the file's lines stand at 4's levels too, as a continuation indented 8 in a file of 4 has them.</summary>
public sealed class IndentDetectionTests
{
    private static string? Detected(string text) => Indentation.Detect(text, 4) is { } found ? found.UseTabs ? "tabs" : found.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>PyYAML's dumper.py, its shape: methods at 4, bodies at 8, each call's arguments continued 8 deeper, so a step of 8
    /// outnumbers one of 4.</summary>
    [Fact]
    public void Continuations_8_deep_in_a_file_of_4_leave_it_4()
    {
        const string Dumper = "class Dumper(Emitter, Serializer, Representer, Resolver):\n\n"
            + "    def __init__(self, stream,\n            default_style=None, default_flow_style=False,\n            canonical=None, indent=None, width=None,\n"
            + "            allow_unicode=None, line_break=None):\n        Emitter.__init__(self, stream, canonical=canonical,\n"
            + "                indent=indent, width=width,\n                allow_unicode=allow_unicode, line_break=line_break)\n"
            + "        Serializer.__init__(self, encoding=encoding,\n                explicit_start=explicit_start, explicit_end=explicit_end)\n"
            + "        Representer.__init__(self, default_style=default_style,\n                default_flow_style=default_flow_style)\n"
            + "        Resolver.__init__(self)\n";
        Assert.Equal("4", Detected(Dumper));
        Assert.Equal("4", Detected(Dumper + "\nclass SafeDumper(Dumper):\n\n" + Dumper[Dumper.IndexOf("    def", StringComparison.Ordinal)..]));
    }

    /// <summary>A file of 8 stays 8: one whose lines stand only at 8's levels, one aligning a few lines halfway, and one at 4's
    /// levels that never steps by 4.</summary>
    [Fact]
    public void A_file_of_8_stays_8()
    {
        Assert.Equal("8", Detected("int f(int a)\n{\n        if (a)\n        {\n                return 1;\n        }\n        return 0;\n}\n"));
        Assert.Equal("8", Detected("int f(int a)\n{\n        if (a)\n                return g(1,\n                    2);\n        if (b)\n                return 2;\n"
            + "        if (c)\n                return 3;\n        return 0;\n}\n"));
        Assert.Equal("8", Detected("int x = f(a,\n            b,\n                    c);\nint y = g(d,\n            e,\n                    f);\n"));
    }

    /// <summary>What no rule here changes: a file of 4, of 2, of tabs, and one with no step standing out.</summary>
    [Fact]
    public void Other_files_read_as_before()
    {
        Assert.Equal("4", Detected("class A:\n    def f(self):\n        return 1\n"));
        Assert.Equal("2", Detected("a:\n  b:\n    c: 1\n  d: 2\n"));
        Assert.Equal("tabs", Detected("a {\n\tb;\n\t\tc;\n}\n"));
        Assert.Null(Detected("a\n    b\n  c\n        d\n"));
    }
}
