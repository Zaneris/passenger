using Passenger.Core;
using Passenger.Workspace.GodotShader;

namespace Passenger.Workspace;

/// <summary>Godot's <c>res://</c> paths of a project's files: a file's path made from the project's folder, the file a path
/// names there, and the path a file writes resolved as Godot resolves it. Pure.</summary>
public static class GodotResPaths
{
    public const string Scheme = "res://";

    /// <summary>The res:// path of a file under a Godot project's folder; null outside it, or for the folder itself.</summary>
    public static string? Of(string project, string file) =>
        PathNames.IsUnder(file, project) ? Scheme + Path.GetRelativePath(project, file).Replace('\\', '/') : null;

    /// <summary>The file a res:// path names under a Godot project's folder, a sub-resource's <c>::</c> and what follows
    /// dropped; null for a path not res://, or one that climbs out of the folder.</summary>
    public static string? FileOf(string project, string path)
    {
        if (!path.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return null;
        }
        int colon = path.IndexOf(':', Scheme.Length);
        string full = Globalized(project, colon < 0 ? path : path[..colon]);
        return PathNames.IsUnder(full, project) ? full : null;
    }

    /// <summary>Whether a resolved res:// path names a file outside its project, a <c>..</c> kept at its start.</summary>
    public static bool ClimbsOut(string path) => path == Scheme + ".." || path.StartsWith(Scheme + "../", StringComparison.Ordinal);

    /// <summary>The file of a script a res:// path names, whose lines are its file's: as <see cref="FileOf"/>, but null for a
    /// script built into a scene or resource (<c>res://main.tscn::GDScript_x</c>), whose lines are none of its file's.</summary>
    public static string? ScriptFileOf(string project, string path) => path.Contains("::", StringComparison.Ordinal) ? null : FileOf(project, path);

    /// <summary>The file a path given to Godot names from a project's folder: a res:// path's or a relative one's from the
    /// folder, wherever it climbs to (an export's output may lie outside); a full one as it is.</summary>
    public static string Globalized(string project, string path) =>
        Path.GetFullPath(Path.Combine(project, (path.StartsWith(Scheme, StringComparison.Ordinal) ? path[Scheme.Length..] : path).Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The path a path written in a file (an <c>extends</c>, a <c>preload</c>, an <c>#include</c>) names, as Godot
    /// resolves it: a relative one from the folder of the file (a scene's own resource's, its scene's), then simplified as
    /// Godot's simplify_path does, a <c>..</c> past res:// kept; a uid:// name as it is, and a relative one as it is where
    /// there is no file to take it from.</summary>
    public static string Resolved(string written, string? from)
    {
        if (written.StartsWith(GodotUids.Scheme, StringComparison.Ordinal))
        {
            return written;
        }
        if (!GodotPathStrings.IsAbsolutePath(written))
        {
            if (from is null)
            {
                return written;
            }
            written = GodotPathStrings.PathJoin(GodotPathStrings.GetBaseDir(from), written);
        }
        return GodotPathStrings.SimplifyPath(written);
    }
}
