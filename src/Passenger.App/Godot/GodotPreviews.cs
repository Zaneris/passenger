using Passenger.App.CSharp;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>What a res:// path or uid:// name in a Godot project's code previews on its card (<see cref="FilePreview"/>):
/// a picture, a sound, or a scene with its top nodes, the root's first and then its children's.</summary>
internal static class GodotPreviews
{
    private static readonly string[] Sounds = [".wav", ".ogg", ".mp3"];

    /// <summary>The most of a scene's root's children its preview names.</summary>
    private const int MostNodes = 12;

    /// <summary>The preview of what a path names, a file the project has; null for one it lacks, or that is none of these.</summary>
    public static FilePreview? Of(GodotScenes scenes, string named, Func<string, bool> exists)
    {
        string path = named.Split("::")[0];
        string? res = path.StartsWith(GodotUids.Scheme, StringComparison.Ordinal) ? scenes.ResOfUid(path) : path;
        if (res is null || scenes.FileOf(res) is not { } file || !exists(file))
        {
            return null;
        }
        if (PictureFiles.Is(file))
        {
            return new FilePreview(file, res, PreviewKind.Picture, $"{res}, a picture", []);
        }
        if (PathNames.HasExtension(file, Sounds))
        {
            return new FilePreview(file, res, PreviewKind.Sound, $"{res}, a sound", []);
        }
        if (!PathNames.HasExtension(file, [".tscn"]) || scenes.Tree(res) is not { } root)
        {
            return null;
        }
        List<string> nodes = [Shown(root), .. root.Children.Take(MostNodes).Select(Shown)];
        if (root.Children.Count > MostNodes)
        {
            nodes.Add($"and {Plural.Of(root.Children.Count - MostNodes, "more node")}");
        }
        string words = root.Children.Count == 0 ? $"{res}, a scene of {nodes[0]}"
            : $"{res}, a scene of {nodes[0]} holding {string.Join(", ", nodes.Skip(1))}";
        return new FilePreview(file, res, PreviewKind.Scene, words, nodes);
    }

    private static string Shown(GodotTreeNode node) => $"{node.Name} ({GodotChecks.Shown(node)})";
}
