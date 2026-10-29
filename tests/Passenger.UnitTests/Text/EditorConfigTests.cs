using Passenger.Core;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>An .editorconfig's sections: which files a glob matches, brackets taking each character as itself; and a
/// section whose glob cannot be used matching no file, named where it is, while the file's other sections still apply.</summary>
public sealed class EditorConfigTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_section_whose_range_runs_backwards_matches_no_file_and_the_sections_around_it_still_apply()
    {
        File.WriteAllText(Path.Join(root, EditorConfig.FileName),
            "root = true\n\n[*]\nindent_size = 2\n\n[[z-a]*.txt]\nindent_size = 8\ncharset = latin1\n\n[*.txt]\ntab_width = 6\n");

        EditorConfig notes = EditorConfig.For(Path.Join(root, "notes.txt"));
        Assert.True(notes["indent_size"] == "2", $"The section [[z-a]*.txt] gave notes.txt its indent_size {notes["indent_size"]}, not [*]'s 2");
        Assert.True(notes["tab_width"] == "6", "The section [*.txt] after one that matches no file did not apply");
        Assert.True(notes["charset"] is null, "The section [[z-a]*.txt] gave notes.txt its charset");
        Assert.Equal("2", EditorConfig.For(Path.Join(root, "z.txt"))["indent_size"]);
        Assert.False(EditorConfig.Matches("[z-a]*.txt", "/z.txt"), "A glob whose range runs backwards matched a file");
    }

    [Fact]
    public void Each_section_that_matches_no_file_is_named_by_where_its_glob_is_in_the_text_and_why()
    {
        string deep = string.Concat(Enumerable.Repeat("{a,", 65)) + "b" + new string('}', 65);
        string deepest = string.Concat(Enumerable.Repeat("{a,", 100_000)) + "b" + new string('}', 100_000);
        string text = $"root = true\r\n\r\n[[a-\\d]*.cs]\r\n  [[z-a]*.txt]\r\nindent_size = 8\r\n[*.{{cs,md}}]\r\n[{deep}]\r\n\t[{deepest}]\r\n[[a-\\]].x]\r\n";

        IReadOnlyList<EditorConfig.RefusedSection> refused = EditorConfig.Refused(text);

        Assert.Equal(["[z-a]*.txt", deep, deepest, "[a-\\]].x"], refused.Select(r => text.Substring(r.Glob.Start, r.Glob.Length)));
        Assert.Equal("its range z-a is in reverse order", refused[0].Why);
        Assert.Equal("its braces nest more than 64 deep", refused[1].Why);
        Assert.Equal("its range a-] is in reverse order", refused[3].Why);
        Assert.False(EditorConfig.Matches(deepest, "/b"), "A glob nested too deep matched a file");
    }

    [Fact]
    public void Braces_nested_as_deep_as_a_glob_may_still_match()
    {
        string deep = string.Concat(Enumerable.Repeat("{a,", 64)) + "b" + new string('}', 64);
        Assert.True(EditorConfig.Matches(deep, "/b"));
        Assert.True(EditorConfig.Matches(deep, "/src/a"));
        Assert.False(EditorConfig.Matches(deep, "/c"));
    }

    [Theory]
    [InlineData("[ab].a", "/b.a", true)]
    [InlineData("[!ab].b", "/a.b", false)]
    [InlineData("[!ab].b", "/c.b", true)]
    [InlineData("[d-g].c", "/f.c", true)]
    [InlineData("[d-g].c", "/h.c", false)]
    [InlineData("[!d-g].d", "/f.d", false)]
    [InlineData("[abd-g].e", "/e.e", true)]
    [InlineData("[-ab].f", "/-.f", true)]
    [InlineData("[\\]ab].g", "/].g", true)]
    [InlineData("[ab]].g", "/b].g", true)]
    [InlineData("[!\\]ab].g", "/].g", false)]
    [InlineData("[!\\]ab].g", "/c.g", true)]
    [InlineData("ab[e/]cd.i", "/ab[e/]cd.i", true)]
    [InlineData("ab[/c", "/ab[/c", true)]
    [InlineData("[ab*c{1..2}].j", "/*.j", true)]
    [InlineData("[ab*c{1..2}].j", "/{.j", true)]
    [InlineData("[ab*c{1..2}].j", "/3.j", false)]
    public void A_bracket_matches_one_of_its_characters_as_the_format_says(string glob, string relative, bool matches) =>
        Assert.True(EditorConfig.Matches(glob, relative) == matches, $"[{glob}] {(matches ? "did not match" : "matched")} {relative}");

    [Theory]
    [InlineData("[\\p].txt", "/p.txt", true)]
    [InlineData("[\\p].txt", "/\\.txt", false)]
    [InlineData("[a-\\d].cs", "/c.cs", true)]
    [InlineData("[a-\\d].cs", "/e.cs", false)]
    [InlineData("[\\b].md", "/b.md", true)]
    [InlineData("[\\b].md", "/\b.md", false)]
    [InlineData("[\\0].md", "/0.md", true)]
    [InlineData("[a\\-z].x", "/-.x", true)]
    [InlineData("[a\\-z].x", "/m.x", false)]
    [InlineData("[\\é].x", "/é.x", true)]
    public void A_backslash_in_a_bracket_takes_the_next_character_as_itself(string glob, string relative, bool matches) =>
        Assert.True(EditorConfig.Matches(glob, relative) == matches, $"[{glob}] {(matches ? "did not match" : "matched")} {relative}");

    [Theory]
    [InlineData("[!-a].x", "/-.x", false)]
    [InlineData("[!-a].x", "/a.x", false)]
    [InlineData("[!-a].x", "/0.x", true)]
    [InlineData("[!-a].x", "/B.x", true)]
    [InlineData("a[+-0]b", "/a.b", true)]
    [InlineData("a[+-0]b", "/a0b", true)]
    [InlineData("a[+-0]b", "/a/b", false)]
    public void A_dash_first_in_a_bracket_is_itself_and_a_range_across_a_slash_never_matches_one(string glob, string relative, bool matches) =>
        Assert.True(EditorConfig.Matches(glob, relative) == matches, $"[{glob}] {(matches ? "did not match" : "matched")} {relative}");
}
