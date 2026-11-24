using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>The repositories a folder holds beside the one it is in: each working tree whose top is a folder under it, at
/// most <see cref="Depth"/> deep (never inside a folder of dependencies or build output, nor one whose name begins with a
/// dot), less the one it is in and the submodules of any, which their own repository commits.</summary>
public static class GitRepositories
{
    /// <summary>How many folders deep under the open one a repository's top is looked for.</summary>
    public const int Depth = 3;

    /// <summary>The tops of the other repositories under <paramref name="folder"/>, in the order a walk nearest first finds
    /// them, each opened as git finds its working tree; <paramref name="own"/> the top of the one the folder is in, if any.</summary>
    public static async Task<IReadOnlyList<Git>> UnderAsync(string folder, string? own)
    {
        string start = PathNames.Canonical(folder);
        IReadOnlyList<string> tops = await BackgroundWork.Run(() => (IReadOnlyList<string>)[.. PathNames.FoldersUnder(start, Enters)
            .Where(f => !PathNames.Same(f, start) && Path.Exists(Path.Join(f, ".git")))]);
        List<Git> found = [];
        foreach (string top in tops)
        {
            if (await Git.OpenAsync(top) is { Tree: { } tree } && PathNames.Same(tree.Root, PathNames.Canonical(top))
                && (own is null || !PathNames.Same(tree.Root, own)) && !await tree.IsSubmoduleAsync())
            {
                found.Add(tree);
            }
        }
        return found;

        bool Enters(string sub) => PathNames.Same(sub, start) || PathNames.IsUnder(sub, start) && Steps(sub) <= Depth && !Skipped(Path.GetFileName(sub));

        int Steps(string sub) => (PathNames.Relative(sub, start) ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>A folder no repository's top is looked for in: a dot folder (git's own, an editor's), dependencies and build
    /// output.</summary>
    private static bool Skipped(string name) => name.StartsWith('.') || name is "node_modules" or "bin" or "obj" or "packages";
}

public sealed partial class Git
{
    /// <summary>Whether this working tree is a submodule of another repository, which commits it as one of its files.</summary>
    public async Task<bool> IsSubmoduleAsync()
    {
        GitResult super = await RunAsync(Root, ["rev-parse", "--show-superproject-working-tree"]);
        return super.Succeeded && super.Output.Trim().Length > 0;
    }
}
