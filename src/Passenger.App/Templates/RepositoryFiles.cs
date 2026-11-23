using System.Text;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Templates;

/// <summary>A file a Git repository is given beside its code.</summary>
public enum RepositoryFile
{
    GitIgnore,
    GitAttributes,
    EditorConfig,
}

/// <summary>The files a Git repository is given, made one way wherever Passenger makes one (New Solution, Create Git
/// Repository): .gitignore from dotnet new's own template with a section of Passenger's own, what its runs leave in a
/// project's folder (<see cref="PassengerIgnores"/>); .editorconfig from dotnet new's, every file taking LF line breaks, a
/// final one and UTF-8 (<see cref="LineEndingsSettled"/>); and .gitattributes Passenger's own text. Each is written only
/// where no file is there yet.</summary>
public static class RepositoryFiles
{
    /// <summary>The file's name, as it stands in the repository's top folder.</summary>
    public static string NameOf(RepositoryFile file) => file switch
    {
        RepositoryFile.GitIgnore => ".gitignore",
        RepositoryFile.GitAttributes => ".gitattributes",
        _ => ".editorconfig",
    };

    /// <summary>The short name of dotnet new's template the file comes from; null for one of Passenger's own text.</summary>
    public static string? TemplateOf(RepositoryFile file) => file switch
    {
        RepositoryFile.GitIgnore => "gitignore",
        RepositoryFile.EditorConfig => "editorconfig",
        _ => null,
    };

    /// <summary>The section of a .gitignore naming what Passenger's runs leave in a project's folder that no one commits,
    /// which dotnet new's own .gitignore does not name: Godot's cache, imports and build output, as Passenger runs Godot's
    /// editor headless and imports a game before it runs it, and the folder GdUnit4's test adapter writes as Passenger runs
    /// its tests. A save's temporary file ends in .user, which dotnet new's names already.</summary>
    public static string PassengerIgnores { get; } = $"""
        # Passenger
        # Left in a project's folder as Passenger runs it: Godot's cache and imports, from the Godot it runs,
        # and the test runner GdUnit4's adapter writes as the tests run.
        {IgnoredFolders.GodotCache}/
        {IgnoredFolders.GdUnit4Runner}/

        """;

    /// <summary>Passenger's .gitattributes: every text file kept with LF line breaks in the repository and in each checkout
    /// whatever the system, Windows' command scripts with CRLF, which cmd.exe reads them by; C#'s diff driver, so a hunk is
    /// headed by the member it is in; and the binary files of a .NET project, an app's assets and a Godot game marked as
    /// such, never diffed or merged as text.</summary>
    public const string GitAttributes = """
        # Text files: LF line breaks in the repository and in every checkout, whatever the system
        * text=auto eol=lf

        # Windows' command scripts, which cmd.exe reads by CRLF
        *.cmd text eol=crlf
        *.bat text eol=crlf

        # C#'s diffs headed by the member a change is in
        *.cs diff=csharp

        # Binary files, never diffed or merged as text
        *.png binary
        *.jpg binary
        *.jpeg binary
        *.gif binary
        *.bmp binary
        *.ico binary
        *.icns binary
        *.webp binary
        *.tif binary
        *.tiff binary
        *.ttf binary
        *.otf binary
        *.woff binary
        *.woff2 binary
        *.mp3 binary
        *.wav binary
        *.ogg binary
        *.mp4 binary
        *.pdf binary
        *.zip binary
        *.7z binary
        *.gz binary
        *.tar binary
        *.nupkg binary
        *.snupkg binary
        *.dll binary
        *.exe binary
        *.pdb binary
        *.so binary
        *.dylib binary
        *.wasm binary
        *.snk binary
        *.pfx binary
        *.p12 binary
        *.scn binary
        *.res binary

        """;

    /// <summary>The settings every file of a new repository takes from its .editorconfig, by key.</summary>
    private static readonly (string Key, string Value)[] Settled = [("end_of_line", "lf"), ("insert_final_newline", "true"), ("charset", "utf-8")];

    /// <summary>The file's text as Passenger writes it for a folder (<see cref="GitIgnoreAsync"/>, <see cref="EditorConfigAsync"/>,
    /// <see cref="GitAttributes"/>), a .gitignore with the lines of <paramref name="besides"/> it lacks; else why not.</summary>
    public static async Task<(string? Text, string? Why)> TextAsync(RepositoryFile file, string folder, CancellationToken cancel = default,
        (string Heading, string Text)? besides = null) => file switch
        {
            RepositoryFile.GitIgnore => await GitIgnoreAsync(folder, cancel, besides),
            RepositoryFile.EditorConfig => await EditorConfigAsync(folder, cancel),
            _ => (GitAttributes, null),
        };

    /// <summary>A .gitignore for a folder: dotnet new gitignore's, from the SDK the folder builds with, then Passenger's
    /// section (<see cref="PassengerIgnores"/>), then under its heading each line of <paramref name="besides"/> (a Godot
    /// game's own .gitignore) neither has; else why not, in dotnet's words.</summary>
    public static async Task<(string? Text, string? Why)> GitIgnoreAsync(string folder, CancellationToken cancel = default, (string Heading, string Text)? besides = null)
    {
        (string? made, string? why) = await MadeAsync(TemplateOf(RepositoryFile.GitIgnore)!, NameOf(RepositoryFile.GitIgnore), folder, cancel);
        return made is null ? (null, why) : (GitIgnoreOf(made, besides), null);
    }

    /// <summary>An .editorconfig for a folder: dotnet new editorconfig's, from the SDK the folder builds with, giving every
    /// file LF line breaks, a final one and UTF-8 (<see cref="LineEndingsSettled"/>); else why not, in dotnet's words.</summary>
    public static async Task<(string? Text, string? Why)> EditorConfigAsync(string folder, CancellationToken cancel = default)
    {
        (string? made, string? why) = await MadeAsync(TemplateOf(RepositoryFile.EditorConfig)!, NameOf(RepositoryFile.EditorConfig), folder, cancel);
        return made is null ? (null, why) : (LineEndingsSettled(made), null);
    }

    /// <summary>Writes each file into a folder, the .editorconfig first so the others take its line breaks, each only where
    /// no file is there yet: those it wrote, by name, else why one could not be. Off the window's thread.</summary>
    public static Task<(IReadOnlyList<string> Written, string? Why)> WriteAsync(string folder, IReadOnlyList<(RepositoryFile File, string Text)> files) =>
        BackgroundWork.Run(() =>
        {
            List<string> written = [];
            foreach ((RepositoryFile file, string text) in files.OrderBy(f => f.File == RepositoryFile.EditorConfig ? 0 : 1))
            {
                try
                {
                    if (FileText.SaveNewOnly(Path.Combine(folder, NameOf(file)), text) is not null)
                    {
                        written.Add(NameOf(file));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return ((IReadOnlyList<string>)written, $"{NameOf(file)} could not be written: {error.Message}");
                }
            }
            return ((IReadOnlyList<string>)written, (string?)null);
        });

    /// <summary>dotnet new's .gitignore with Passenger's section after it, then under its heading each line of
    /// <paramref name="besides"/> neither has.</summary>
    internal static string GitIgnoreOf(string made, (string Heading, string Text)? besides)
    {
        StringBuilder text = new(made.ReplaceLineEndings("\n").TrimEnd('\n'));
        text.Append("\n\n").Append(PassengerIgnores);
        if (besides is var (heading, more))
        {
            HashSet<string> named = new(text.ToString().Split('\n').Select(l => l.Trim()), StringComparer.Ordinal);
            string[] lacking = [.. more.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && named.Add(l))];
            if (lacking.Length > 0)
            {
                text.Append("\n# ").Append(heading).Append('\n');
                foreach (string line in lacking)
                {
                    text.Append(line).Append('\n');
                }
            }
        }
        return text.ToString();
    }

    /// <summary>An .editorconfig's text giving every file LF line breaks, a final one and UTF-8: each of those keys a section
    /// sets taking that value, and those its [*] section lacks added after its last setting, a [*] section of them made
    /// before the first section where it has none.</summary>
    internal static string LineEndingsSettled(string editorConfig)
    {
        List<string> lines = [.. editorConfig.ReplaceLineEndings("\n").Split('\n')];
        HashSet<string> inAll = new(StringComparer.OrdinalIgnoreCase);
        int allAt = -1;
        int firstSection = -1;
        bool inside = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                firstSection = firstSection < 0 ? i : firstSection;
                inside = line == "[*]" && allAt < 0;
                allAt = inside ? i : allAt;
                continue;
            }
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (line.Length == 0 || line[0] is '#' or ';' || equals < 0)
            {
                continue;
            }
            string key = line[..equals].Trim();
            if (Array.FindIndex(Settled, s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is >= 0 and int setting)
            {
                lines[i] = $"{lines[i][..(lines[i].Length - lines[i].TrimStart().Length)]}{key} = {Settled[setting].Value}";
                if (inside)
                {
                    inAll.Add(key);
                }
            }
            if (inside)
            {
                allAt = i;
            }
        }
        string[] missing = [.. Settled.Where(s => !inAll.Contains(s.Key)).Select(s => $"{s.Key} = {s.Value}")];
        if (allAt >= 0)
        {
            lines.InsertRange(allAt + 1, missing);
        }
        else if (firstSection >= 0)
        {
            // Before the comments heading the first section, which are its own.
            int at = firstSection;
            while (at > 0 && lines[at - 1].TrimStart() is ['#' or ';', ..])
            {
                at--;
            }
            lines.InsertRange(at, ["[*]", .. missing, string.Empty]);
        }
        else
        {
            lines.AddRange(lines[^1].Length == 0 ? ["[*]", .. missing, string.Empty] : [string.Empty, "[*]", .. missing, string.Empty]);
        }
        return string.Join('\n', lines);
    }

    /// <summary>The file dotnet new's template writes, made in a folder of the person's own temporary one and taken away
    /// after, run where the SDK the folder builds with makes it; else why not.</summary>
    private static async Task<(string? Text, string? Why)> MadeAsync(string template, string name, string folder, CancellationToken cancel)
    {
        string apart;
        try
        {
            apart = await BackgroundWork.Run(() => Path.Combine(UserTemporary.Own(UserTemporary.Of(State.StateStore.DefaultRoot, "templates")), Guid.NewGuid().ToString("N")), cancel);
        }
        catch (IOException refused)
        {
            return (null, refused.Message);
        }
        try
        {
            Task<ChildResult> run = ChildProcesses.RunAsync(ProjectTemplates.NewCommand([template, "--output", apart, "--no-update-check"], folder),
                limit: TimeSpan.FromMinutes(2), cancel: cancel);
            BackgroundWork.Track(run);
            ChildResult made = await run;
            if (!made.Succeeded)
            {
                return (null, made.Said);
            }
            return await BackgroundWork.Run(() => FileText.Load(Path.Combine(apart, name)) is { } file
                ? (file.Text, (string?)null)
                : ((string?)null, $"dotnet new {template} wrote no {name}"), cancel);
        }
        finally
        {
            await BackgroundWork.Run(() =>
            {
                try
                {
                    if (Directory.Exists(apart))
                    {
                        Directory.Delete(apart, recursive: true);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    IdeErrors.Write($"dotnet new {template}'s folder apart, {apart}, was left: {error.Message}");
                }
            });
        }
    }
}
