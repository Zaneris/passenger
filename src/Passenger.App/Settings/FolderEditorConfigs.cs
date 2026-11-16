using Passenger.Core;
using Passenger.Languages;
using Passenger.Text;

namespace Passenger.App.Settings;

/// <summary>What the open folder's .editorconfig files set over Settings' rows (<see cref="EditorConfigOver"/>): read off
/// the window's thread only once asked (Settings open, an agent's settings), from the .editorconfig files the index's walk
/// lists, never a walk of its own, and asked again once the watcher hears one written, made or deleted, or the walk lands
/// anew, which <see cref="Changed"/> tells. A UI-thread object of its <see cref="ProjectSession"/>.</summary>
public sealed class FolderEditorConfigs(ProjectSession session)
{
    private Task<EditorConfigLines>? read;

    /// <summary>What was read may have changed, so what shows it asks again.</summary>
    public event EventHandler? Changed;

    /// <summary>What the folder's .editorconfig files set, as last read, or read now where nothing has changed since.</summary>
    public Task<EditorConfigLines> ReadAsync()
    {
        UiThread.Check();
        if (read is null)
        {
            string? folder = session.Folder;
            IReadOnlyList<string> files = session.Index.Files;
            read = folder is null ? Task.FromResult(EditorConfigLines.None) : BackgroundWork.Run(() => EditorConfigLines.Read(folder, files));
        }
        return read;
    }

    /// <summary>The index's walk landed: its files are those an .editorconfig's sections are matched against.</summary>
    public void Walked() => Again();

    /// <summary>Files the index put in and took out: an .editorconfig among them is read, or no longer.</summary>
    public void CameAndWent(IReadOnlyList<string> came, IReadOnlyList<string> gone)
    {
        if (came.Concat(gone).Any(IsEditorConfig))
        {
            Again();
        }
    }

    /// <summary>Files the folder watcher saw written, made or deleted; <paramref name="every"/> where it lost what changed.</summary>
    public void FilesChanged(IEnumerable<string> paths, bool every)
    {
        if (every || paths.Any(IsEditorConfig))
        {
            Again();
        }
    }

    private static bool IsEditorConfig(string path) => Path.GetFileName(path) == EditorConfig.FileName;

    private void Again()
    {
        UiThread.Check();
        if (read is not null)
        {
            read = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>What the open folder's .editorconfig files set over each row that declares the keys saying over it, as Settings
/// and an agent's settings say it: each key a section sets, where the row's files are every file or its section's glob
/// matches theirs (one in the folder, or a name of theirs standing beside the .editorconfig).</summary>
public sealed class EditorConfigLines
{
    /// <summary>The most lines said of a row, the last of them saying how many more there are where there are more.</summary>
    public const int Most = 4;

    public static readonly EditorConfigLines None = new(new Dictionary<EditorConfigOver, IReadOnlyList<string>>());

    private readonly IReadOnlyDictionary<EditorConfigOver, IReadOnlyList<string>> said;

    private EditorConfigLines(IReadOnlyDictionary<EditorConfigOver, IReadOnlyList<string>> said) => this.said = said;

    /// <summary>Reads what the .editorconfig files among the folder's <paramref name="files"/> (by their paths from it) and
    /// those above it set over every declared row. A worker's.</summary>
    public static EditorConfigLines Read(string folder, IReadOnlyList<string> files)
    {
        string[] configs = [.. files.Where(f => Path.GetFileName(f) == EditorConfig.FileName).Select(f => Path.Join(folder, f))];
        EditorConfigFolder read = EditorConfigFolder.Read(folder, configs);
        Dictionary<Language, string[]> filesOf = [];
        Dictionary<EditorConfigOver, IReadOnlyList<string>> said = [];
        foreach (EditorConfigOver over in Declared.All.Select(s => s.Row?.Over).OfType<EditorConfigOver>())
        {
            List<string> lines = [];
            foreach (SetKey key in read.Keys.Where(k => over.Names(k.Key)))
            {
                if (over.Files is not { } language || key.Concerns(Names(language), FilesOf(language)))
                {
                    lines.Add(key.Said(folder));
                }
            }
            said[over] = lines;
        }
        return new EditorConfigLines(said);

        string[] FilesOf(Language language)
        {
            if (!filesOf.TryGetValue(language, out string[]? found))
            {
                found = [.. files.Where(f => language.Patterns.Any(p => p.Matches(f))).Select(f => Path.Join(folder, f))];
                filesOf[language] = found;
            }
            return found;
        }
    }

    /// <summary>What is set over a row, every key in full, each a line ("./.editorconfig sets max_line_length = 120 for *.gd").</summary>
    public IReadOnlyList<string> Of(EditorConfigOver over) => said.TryGetValue(over, out IReadOnlyList<string>? lines) ? lines : [];

    /// <summary>What is set over a row as a short list: at most <see cref="Most"/> lines, the last of them saying how many
    /// more there are where there are more; none where nothing is.</summary>
    public IReadOnlyList<string> Shortly(EditorConfigOver over)
    {
        IReadOnlyList<string> lines = Of(over);
        return lines.Count <= Most ? lines : [.. lines.Take(Most - 1), $"and {lines.Count - (Most - 1)} more"];
    }

    /// <summary>A file name of each of a language's patterns, standing for its files in a folder with none of them yet.</summary>
    private static IEnumerable<string> Names(Language language) => language.Patterns.Select(p => p.Glob.Replace("*", "file", StringComparison.Ordinal));
}
