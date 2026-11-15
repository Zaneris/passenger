using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A command's program found as a terminal finds it, by one rule for every program Passenger runs: on Windows a
/// bare name is tried with each extension PATHEXT lists of what Windows starts, in its order, in each folder of the PATH in
/// turn, and never alone.</summary>
public sealed class ProgramsTests
{
    private const string Default = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC";

    [Fact]
    public void A_bare_name_on_Windows_is_tried_with_each_extension_of_what_Windows_starts_in_PATHEXTs_order()
    {
        Assert.Equal(["func.com", "func.exe", "func.bat", "func.cmd"], Programs.WindowsNames("func", Default));
        Assert.Equal(["func.cmd", "func.exe"], Programs.WindowsNames("func", ".CMD;.PY;.EXE"));
        Assert.Equal(["func.com", "func.exe", "func.bat", "func.cmd"], Programs.WindowsNames("func", null));
        Assert.Equal(["python3.11.com", "python3.11.exe", "python3.11.bat", "python3.11.cmd"], Programs.WindowsNames("python3.11", ".js;.vbs"));
    }

    [Fact]
    public void A_name_given_with_an_extension_of_what_Windows_starts_is_looked_for_as_it_is()
    {
        Assert.Equal(["pwsh.exe"], Programs.WindowsNames("pwsh.exe", Default));
        Assert.Equal(["pnpm.CMD"], Programs.WindowsNames("pnpm.CMD", Default));
    }

    [Fact]
    public void Each_folder_on_the_PATH_is_searched_whole_before_the_next_and_a_file_with_no_extension_is_never_taken()
    {
        string npm = Path.Combine("C:", "npm");
        string tools = Path.Combine("C:", "tools");
        string[] folders = [npm, tools];
        IReadOnlyList<string> names = Programs.WindowsNames("func", Default);

        // npm's folder holds the shell's script of no extension beside its .cmd; Core Tools' folder its program.
        HashSet<string> files = [Path.Combine(npm, "func"), Path.Combine(npm, "func.cmd"), Path.Combine(tools, "func.exe")];
        Assert.True(Programs.First(folders, names, files.Contains) == Path.Combine(npm, "func.cmd"),
            "The first folder's func.cmd was not what a terminal runs as func: a later folder's, or a script Windows cannot start, came first");

        files = [Path.Combine(npm, "func"), Path.Combine(tools, "func.exe")];
        Assert.True(Programs.First(folders, names, files.Contains) == Path.Combine(tools, "func.exe"),
            "A file of no extension, which Windows cannot start, was taken for func");

        files = [Path.Combine(tools, "npm.exe")];
        Assert.True(Programs.First(folders, Programs.WindowsNames("npm", Default), files.Contains) == Path.Combine(tools, "npm.exe"),
            "An npm.exe shim was missed, where a terminal runs it as npm");
    }
}
