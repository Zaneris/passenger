using System.Collections.Concurrent;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.WinForms;

/// <summary>The namespaces a project's own C# files bring to all of it (<c>global using Shop.Controls;</c>), which a form's
/// designer resolves its types by as the compiler does; each file read again only once its stamp changed. Any thread.</summary>
internal static class SourceGlobalUsings
{
    /// <summary>What each file read declared, by its path, with its stamp as read.</summary>
    private static readonly ConcurrentDictionary<string, (FileStamp Stamp, IReadOnlyList<string> Namespaces)> Read = new(PathNames.Comparer);

    /// <summary>The namespaces a project's C# files declare global usings of, in no set order.</summary>
    public static IReadOnlyList<string> Of(ProjectModel project)
    {
        HashSet<string> found = new(StringComparer.Ordinal);
        foreach (string file in FolderWalk.ProjectFiles(project, ".cs"))
        {
            if (FileStamp.Of(file) is not { } stamp)
            {
                continue;
            }
            if (!Read.TryGetValue(file, out (FileStamp Stamp, IReadOnlyList<string> Namespaces) kept) || kept.Stamp != stamp)
            {
                try
                {
                    kept = (stamp, [.. File.ReadLines(file).Select(Named).OfType<string>()]);
                }
                catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                Read[file] = kept;
            }
            found.UnionWith(kept.Namespaces);
        }
        return [.. found];
    }

    /// <summary>The namespace a line's global using names (<c>global using global::System.Linq;</c>), null for any other
    /// line: a static one, or an alias, names a type.</summary>
    public static string? Named(string line)
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith("global using ", StringComparison.Ordinal) || !trimmed.EndsWith(';') || trimmed.Contains('=', StringComparison.Ordinal))
        {
            return null;
        }
        string named = trimmed["global using ".Length..^1].Trim();
        if (named.StartsWith("static ", StringComparison.Ordinal))
        {
            return null;
        }
        named = named.StartsWith("global::", StringComparison.Ordinal) ? named["global::".Length..] : named;
        return named.Length > 0 && named.All(c => c == '.' || CSharp.AssistContext.IsIdentifierChar(c)) ? named : null;
    }
}
