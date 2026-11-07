using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A file's indentation found from its own lines: the step most changes between neighbouring lines take, a step of 8
/// read as two of 4 where the file's lines stand at 4's levels too, as a continuation indented 8 in a file of 4 has them, and
/// in Python none taken to or from a line whose indentation is no block's (continued, or inside a string).</summary>
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

    /// <summary>googletest's shape: Python's blocks indented by 2, each call's arguments continued 4 deeper, so a step of 4
    /// outnumbers one of 2 unless the lines inside brackets, whose indentation is no block's, take no step.</summary>
    [Fact]
    public void Python_continued_4_deep_in_a_file_of_2_is_2()
    {
        const string Test = "class GMockLeakTest(gmock_test_utils.TestCase):\n\n  def testCatchesLeakedMockByDefault(self):\n    self.assertNotEqual(\n"
            + "        0,\n        gmock_test_utils.Subprocess(\n            TEST_WITH_EXPECT_CALL, env=environ\n        ).exit_code,\n    )\n"
            + "    self.assertNotEqual(\n        0, gmock_test_utils.Subprocess(TEST_WITH_ON_CALL, env=environ).exit_code\n    )\n\n"
            + "  def testDoesNotCatchLeakedMockWhenDisabled(self):\n    self.assertEqual(\n        0,\n        gmock_test_utils.Subprocess(\n"
            + "            TEST_WITH_EXPECT_CALL + ['--gmock_catch_leaked_mocks=0'],\n            env=environ,\n        ).exit_code,\n    )\n";
        Assert.Equal("4", Detected(Test));
        Assert.Equal("2", Python(Test));
    }

    /// <summary>A docstring's lines are a string's text, whose indentation is no block's: parameters whose words go on a column
    /// deeper, as AutoRest writes them, outnumber the steps of a short file's code of 4 unless they take no step.</summary>
    [Fact]
    public void Python_docstring_lines_take_no_step()
    {
        const string Configuration = "class Configuration:\n    \"\"\"Configuration for the service.\n\n    :param url: The URL of the service account, container, or blob\n"
            + "     that is the target of the desired operation. Required.\n    :type url: str\n    :param version: Specifies the version of the operation to use\n"
            + "     for this request. Default value is None.\n    :type version: str\n    :keyword resource: The value must be filesystem for all\n"
            + "     filesystem operations.\n    :paramtype resource: str\n    \"\"\"\n\n    def __init__(self, url: str, **kwargs: Any) -> None:\n"
            + "        self.url = url\n        self._configure(**kwargs)\n\n    def _configure(self, **kwargs: Any) -> None:\n"
            + "        self.retry_policy = kwargs.get(\"retry_policy\")\n";
        Assert.Equal("1", Detected(Configuration));
        Assert.Equal("4", Python(Configuration));
    }

    /// <summary>A Python file whose only indented lines are continued, or inside its strings, shows no block's indentation of its
    /// own, so it takes the language's.</summary>
    [Fact]
    public void A_Python_file_with_no_block_detects_none()
    {
        Assert.Null(Python("helps['ams'] = \"\"\"\ntype: group\nshort-summary: Manage Azure Media Services resources.\n  long: words\n  more: words\n\"\"\"\n"));
        Assert.Null(Python("TABLE = (\n    1, 2, 3,\n    4, 5, 6,\n    7, 8, 9,\n)\n"));
    }

    /// <summary>As Python's files are read: their lines whose indentation is no block's taking no step.</summary>
    private static string? Python(string text) => Indentation.Detect(text, 4, blockless: ContinuedLines.PythonBlockless(text)) is { } found
        ? found.UseTabs ? "tabs" : found.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : null;

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
