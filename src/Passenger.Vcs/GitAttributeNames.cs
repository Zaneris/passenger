namespace Passenger.Vcs;

/// <summary>An attribute a .gitattributes sets: its name, who reads it, what it does, the values it takes where they are
/// few with what each does, and whether its values are drivers git's configuration declares (<c>diff=</c>, <c>merge=</c>,
/// <c>filter=</c>) or a number.</summary>
public sealed record GitAttribute(string Name, string From, string Said, IReadOnlyList<(string Value, string Said)> Values, string? Driver = null, bool Number = false);

/// <summary>The attributes a .gitattributes sets that git reads, as its documentation (gitattributes(5)) names them, and
/// those GitHub's Linguist reads, each in Passenger's words; the diff and merge drivers git has built in; and a file's
/// lines as git reads them: a pattern, then attributes set (<c>text</c>), unset (<c>-text</c>), left unspecified
/// (<c>!text</c>) or given a value (<c>eol=lf</c>), a <c>[attr]</c> line defining a macro, <c>#</c> a comment.</summary>
public static class GitAttributeNames
{
    public const string Git = "git";
    public const string Linguist = "GitHub's Linguist";

    /// <summary>The configuration sections that declare each kind of driver.</summary>
    public const string Diff = "diff";
    public const string Merge = "merge";
    public const string Filter = "filter";

    /// <summary>The diff drivers git has built in, which find the function or section a change is in.</summary>
    public static readonly IReadOnlyList<(string Value, string Said)> DiffDrivers =
    [
        ("ada", "Ada"), ("bash", "Bash and POSIX shell"), ("bibtex", "BibTeX references"), ("cpp", "C and C++"), ("csharp", "C#"), ("css", "CSS"),
        ("dts", "devicetree sources"), ("elixir", "Elixir"), ("fortran", "Fortran"), ("fountain", "Fountain screenplays"), ("golang", "Go"),
        ("html", "HTML and XHTML"), ("java", "Java"), ("kotlin", "Kotlin"), ("markdown", "Markdown"), ("matlab", "MATLAB and Octave"),
        ("objc", "Objective-C"), ("pascal", "Pascal and Delphi"), ("perl", "Perl"), ("php", "PHP"), ("python", "Python"), ("ruby", "Ruby"),
        ("rust", "Rust"), ("scheme", "Scheme"), ("tex", "LaTeX"),
    ];

    /// <summary>The merge drivers git has built in.</summary>
    public static readonly IReadOnlyList<(string Value, string Said)> MergeDrivers =
        [("text", "a three-way merge of lines, conflicts marked in the file"), ("binary", "the branch's own version kept, the merge marked a conflict"),
            ("union", "both sides' lines kept, no conflict marked")];

    /// <summary>The attributes, git's first.</summary>
    public static readonly IReadOnlyList<GitAttribute> All =
    [
        new("text", Git, "Whether Git keeps the file's line breaks as LF in the repository: set, it does; unset, never; auto, where Git takes the file for text.",
            [("auto", "Git decides by the file's content whether it is text")]),
        new("eol", Git, "The line break Git writes the file with in the working tree; the file is text.", [("lf", "LF line breaks"), ("crlf", "CRLF line breaks")]),
        new("binary", Git, "Git's own macro: the file is neither text, nor shown as a diff of lines, nor merged as text (-text -diff -merge).", []),
        new("diff", Git, "How Git shows the file's changes: set, as lines; unset, as a binary file; a driver, by that driver's hunk headers or conversion.", DiffDrivers, Diff),
        new("merge", Git, "How Git merges the file: set, as text; unset, as a binary file kept as the branch has it; a driver, by that driver.", MergeDrivers, Merge),
        new("filter", Git, "A driver git's configuration declares that changes the file as it is checked in and out (Git LFS's lfs).", [], Filter),
        new("working-tree-encoding", Git, "The encoding Git writes the file in in the working tree, the repository keeping it UTF-8.",
            [("UTF-16", "UTF-16 with a byte order mark"), ("UTF-16LE-BOM", "UTF-16, little-endian, with a byte order mark"), ("UTF-16BE", "UTF-16, big-endian"),
                ("UTF-16LE", "UTF-16, little-endian"), ("SHIFT-JIS", "Shift JIS")]),
        new("ident", Git, "Git writes the file's object name into $Id$ as it checks the file out.", []),
        new("whitespace", Git, "Which white space problems git diff and git apply point out in the file, as core.whitespace names them.",
            [("blank-at-eol", "white space at a line's end"), ("space-before-tab", "a space before a tab in the indent"), ("indent-with-non-tab", "an indent of eight spaces or more"),
                ("tab-in-indent", "a tab in the indent"), ("blank-at-eof", "blank lines at the file's end"), ("trailing-space", "both blank-at-eol and blank-at-eof"),
                ("cr-at-eol", "a CR at a line's end is no problem")]),
        new("conflict-marker-size", Git, "How long the conflict markers are that a merge writes in the file.", [], Number: true),
        new("export-ignore", Git, "git archive leaves the file out.", []),
        new("export-subst", Git, "git archive expands $Format:…$ placeholders in the file.", []),
        new("delta", Git, "Unset, Git stores the file whole rather than as a change of another.", []),
        new("encoding", Git, "The encoding gitk and git gui show the file's text in.", [("UTF-8", "UTF-8")]),
        new("crlf", Git, "Older form of text and eol: set, as text; unset, as -text; input, as eol=lf.", [("input", "LF line breaks")]),
        new("linguist-vendored", Linguist, "GitHub counts the file as vendored code, left out of the repository's languages.", [("true", "vendored"), ("false", "not vendored")]),
        new("linguist-generated", Linguist, "GitHub counts the file as generated: left out of the languages and folded in diffs.", [("true", "generated"), ("false", "not generated")]),
        new("linguist-documentation", Linguist, "GitHub counts the file as documentation, left out of the repository's languages.", [("true", "documentation"), ("false", "not documentation")]),
        new("linguist-detectable", Linguist, "Whether GitHub counts the file among the repository's languages.", [("true", "counted"), ("false", "not counted")]),
        new("linguist-language", Linguist, "The language GitHub colours and counts the file as, by Linguist's name for it.", []),
    ];

    /// <summary>An attribute by name; null for one neither git nor Linguist reads.</summary>
    public static GitAttribute? Of(string name) => All.FirstOrDefault(a => a.Name == name);

    /// <summary>An attribute a line sets: whether it is unset (<c>-</c>) or left unspecified (<c>!</c>), where its name
    /// starts and ends, and where its value starts and ends after its <c>=</c> (none for one with no value).</summary>
    public readonly record struct Written(char Mark, int Start, int End, int ValueStart, int ValueEnd)
    {
        public bool Valued => ValueStart >= 0;
    }

    /// <summary>A line of a .gitattributes as git reads it: where its pattern is (a macro's name for <c>[attr]</c>), whether
    /// it defines a macro, and its attributes; null for a blank line or a comment.</summary>
    public sealed record Line(int PatternStart, int PatternEnd, bool Macro, IReadOnlyList<Written> Attributes);

    /// <summary>The line of a .gitattributes's text from <paramref name="start"/> to <paramref name="end"/> as git reads it.</summary>
    public static Line? Read(string text, int start, int end)
    {
        int at = start;
        while (at < end && text[at] is ' ' or '\t')
        {
            at++;
        }
        if (at >= end || text[at] == '#')
        {
            return null;
        }
        int patternStart = at;
        if (text[at] == '"')
        {
            at++;
            while (at < end && text[at] != '"')
            {
                at += text[at] == '\\' ? 2 : 1;
            }
            at = Math.Min(at + 1, end);
        }
        else
        {
            while (at < end && text[at] is not (' ' or '\t'))
            {
                at++;
            }
        }
        int patternEnd = at;
        bool macro = text.AsSpan(patternStart, patternEnd - patternStart).StartsWith("[attr]", StringComparison.Ordinal);
        List<Written> attributes = [];
        while (at < end)
        {
            while (at < end && text[at] is ' ' or '\t')
            {
                at++;
            }
            if (at >= end)
            {
                break;
            }
            int wordStart = at;
            while (at < end && text[at] is not (' ' or '\t'))
            {
                at++;
            }
            char mark = text[wordStart] is '-' or '!' ? text[wordStart] : '\0';
            int nameStart = mark == '\0' ? wordStart : wordStart + 1;
            int equals = text.IndexOf('=', nameStart, at - nameStart);
            attributes.Add(equals < 0 ? new Written(mark, nameStart, at, -1, -1) : new Written(mark, nameStart, equals, equals + 1, at));
        }
        return new Line(macro ? patternStart + "[attr]".Length : patternStart, patternEnd, macro, attributes);
    }

    /// <summary>Why a value a line gives an attribute is no value git reads it by: an <c>eol</c> other than lf or crlf, or a
    /// diff or merge driver neither built in nor declared in git's configuration (<paramref name="declared"/>, each by its
    /// kind and name, null where it could not be read); null where git reads it.</summary>
    public static string? Problem(string name, string value, IReadOnlySet<(string Kind, string Name)>? declared)
    {
        if (name == "eol")
        {
            return Of(name)!.Values.Any(v => v.Value == value) ? null : $"eol takes lf or crlf, not {value}";
        }
        if (name is Diff or Merge && declared is not null)
        {
            IReadOnlyList<(string Value, string Said)> builtIn = name == Diff ? DiffDrivers : MergeDrivers;
            return builtIn.Any(d => d.Value == value) || declared.Contains((name, value)) ? null
                : $"No {name} driver {value}: git has none built in, and its configuration declares no [{name} \"{value}\"]";
        }
        return null;
    }
}
