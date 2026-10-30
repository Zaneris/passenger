using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>The folder a project's load or restore error blames that this user cannot write, where MSBuild writes what the
/// project loads with: only where the error's words say a write to a path it quotes was refused (access denied, a
/// read-only file system), and only the nearest folder there is of that path when it is the path's own (the path, or the
/// folder it is in) or the project's (under its folder or the open folder). A part of a path not found is a refusal under
/// the project's folders alone, where a folder MSBuild would have made could not be. The file system's answers are the
/// caller's, so what is matched here is the words.</summary>
public static partial class UnwritableFolders
{
    /// <summary>The first folder the error blames that cannot be written, as the error spells it; null where it blames none
    /// or each can be.</summary>
    public static string? Of(string error, string project, string? folder, Func<string, bool> exists, Func<string, bool> writable)
    {
        string?[] own = [Path.GetDirectoryName(project), folder];
        foreach (Match refused in Refused().Matches(error))
        {
            string path = refused.Groups["path"].Value;
            string? at = path;
            while (at is not null && !exists(at))
            {
                at = Path.GetDirectoryName(at);
            }
            if (at is null || writable(at))
            {
                continue;
            }
            bool named = PathNames.Same(at, path) || (Path.GetDirectoryName(path) is { } holding && PathNames.Same(at, holding));
            bool owned = own.Any(o => o is not null && (PathNames.Same(at, o) || PathNames.IsUnder(at, o)));
            if (owned || (named && !refused.Groups["missing"].Success))
            {
                return at;
            }
        }
        return null;
    }

    /// <summary>The runtime's words for a write the system refused, or for a part of a path not there, each with the path.</summary>
    [GeneratedRegex(@"(?:Access to the path|(?<missing>Could not find a part of the path)) '(?<path>/[^'\n]+)'|(?:Read-only file system|Permission denied|Operation not permitted) : '(?<path>/[^'\n]+)'")]
    private static partial Regex Refused();
}
