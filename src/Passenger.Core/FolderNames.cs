namespace Passenger.Core;

/// <summary>Folders' names as the folder holding each reads its entries' names: a name written as one of these is it, and
/// so is one differing from it only in letter case where that folder ignores case, as a program writing the one there
/// writes into the other (a build's obj into an Obj on macOS or Windows). Framework-free, since the project host compiles
/// it in.</summary>
public sealed class FolderNames
{
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> exactly;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> anyCase;

    public FolderNames(IEnumerable<string> names)
    {
        HashSet<string> written = new(names, StringComparer.Ordinal);
        Names = written;
        exactly = written.GetAlternateLookup<ReadOnlySpan<char>>();
        anyCase = new HashSet<string>(written, StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The names as written.</summary>
    public IReadOnlySet<string> Names { get; }

    /// <summary>Whether a folder, by its path, is named one of these.</summary>
    public bool Is(string folder)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(folder);
        return Holds(Path.GetFileName(trimmed.AsSpan()), Path.GetDirectoryName(trimmed) ?? trimmed, []);
    }

    /// <summary>Whether a name is one of these, read in the folder <paramref name="above"/> leads to from
    /// <paramref name="root"/> (the root itself when it is empty), which is asked of its case only for a name differing in
    /// case alone, so a name read in place costs no string.</summary>
    public bool Holds(ReadOnlySpan<char> name, string root, ReadOnlySpan<char> above) =>
        exactly.Contains(name) || anyCase.Contains(name) && PathNames.CaseIgnoredIn(Folder(root, above));

    /// <summary>Whether a name is <paramref name="named"/>, read where <see cref="Holds"/> reads one.</summary>
    public static bool Same(ReadOnlySpan<char> name, string named, string root, ReadOnlySpan<char> above) =>
        name.SequenceEqual(named) || name.Equals(named, StringComparison.OrdinalIgnoreCase) && PathNames.CaseIgnoredIn(Folder(root, above));

    private static string Folder(string root, ReadOnlySpan<char> above) => above.IsEmpty ? root : Path.Join(root, above);
}
