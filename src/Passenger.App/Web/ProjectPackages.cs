using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>A project's own npm packages for a file (its Automatic ESLint and Prettier configuration), as its package manager
/// lays them out: where a Yarn Plug'n'Play project's .pnp.cjs places it for the file (<see cref="YarnPnp"/>), else in the
/// node_modules of the file's folder or the nearest folder above it that has the package, as Node.js resolves it (pnpm's
/// link there to its own store among them); or listed by the nearest package.json above the file that names it, and not
/// installed. Only the person's folders are looked in (<see cref="FoldersUp"/>).</summary>
public static class ProjectPackages
{
    /// <summary>The fields of package.json that list what <c>npm install</c> installs.</summary>
    private static readonly string[] Lists = ["dependencies", "devDependencies", "optionalDependencies", "peerDependencies"];

    /// <summary>The package's folder as Node.js finds it from the file, in the person's folders (<see cref="FoldersUp"/>),
    /// a Plug'n'Play project's within its archive; null when none is installed there.</summary>
    public static string? Installed(string file, string name)
    {
        if (YarnPnp.Resolve(file, name) is { } placed)
        {
            return YarnPnp.Exists(Path.Combine(placed, "package.json")) ? placed : null;
        }
        foreach (string folder in Path.GetDirectoryName(file) is { } start ? FoldersUp.From(start) : [])
        {
            if (FoldersUp.Found(folder, Path.Combine("node_modules", name, "package.json")) is { } manifest && File.Exists(manifest))
            {
                return Path.GetDirectoryName(manifest);
            }
        }
        return null;
    }

    /// <summary>The package folder a Manual configuration names (from the folder open when it is relative), null when it
    /// names none or none is there.</summary>
    public static string? Named(string? package, string folder)
    {
        if (package is not { Length: > 0 })
        {
            return null;
        }
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package, folder));
        return File.Exists(Path.Combine(full, "package.json")) ? full : null;
    }

    /// <summary>The nearest package.json above the file that lists the package, in the person's folders, when the package is
    /// not installed for it: what <c>npm install</c> there would put in place. Null when it is installed, or nothing lists it.</summary>
    public static string? Missing(string file, string name)
    {
        if (Installed(file, name) is not null)
        {
            return null;
        }
        foreach (string folder in Path.GetDirectoryName(file) is { } start ? FoldersUp.From(start) : [])
        {
            if (FoldersUp.Found(folder, "package.json") is { } manifest && File.Exists(manifest) && Names(manifest, name))
            {
                return manifest;
            }
        }
        return null;
    }

    /// <summary>The version a package's own package.json says it is, null when it does not read.</summary>
    public static string? Version(string package)
    {
        try
        {
            if (YarnPnp.ReadFile(Path.Combine(package, "package.json")) is not { } text)
            {
                return null;
            }
            using JsonDocument read = JsonDocument.Parse(text);
            return JsonMembers.Text(read.RootElement, "version");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Names(string manifest, string name)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(File.ReadAllText(manifest), LenientJson.Document);
            return Lists.Any(list => JsonMembers.Value(JsonMembers.Object(read.RootElement, list), name) is not null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // One being edited may not read yet: it lists nothing until it does.
            return false;
        }
    }

    /// <summary>Whether a "Run for files" glob (<c>**/*.{js,ts}</c>) covers a file, by its path from the folder open (a
    /// file outside it by its name alone), as an .editorconfig's sections match. A package's own file (under a
    /// node_modules) is a library's, neither linted nor formatted.</summary>
    public static bool Covers(string glob, string folder, string file)
    {
        if (file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("node_modules"))
        {
            return false;
        }
        string relative = PathNames.IsUnder(file, folder) && PathNames.Relative(file, folder) is { } below ? below : Path.GetFileName(file);
        return glob.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(one => EditorConfig.Matches(one, "/" + relative));
    }
}

/// <summary>npm's own script, which a package's install and scripts run on the Node.js in use (<see cref="ScriptRuns"/>),
/// so a Windows npm.cmd needs no shell.</summary>
public static class Npm
{
    /// <summary>npm's own script for a Node.js, null when there is none beside it or on the PATH.</summary>
    public static string? Cli(string node)
    {
        string folder = Path.GetDirectoryName(PathNames.Canonical(node)) ?? string.Empty;
        string onPath = Programs.Command("npm") is { } npm ? Path.GetDirectoryName(npm) ?? string.Empty : string.Empty;
        string[] candidates =
        [
            // The Windows installer's layout, and nvm-windows'.
            Path.Combine(folder, "node_modules", "npm", "bin", "npm-cli.js"),
            // macOS's and Linux's: bin/node beside lib/node_modules/npm (the official packages, Homebrew, nvm).
            Path.Combine(folder, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"),
            Path.Combine(Path.GetDirectoryName(node) ?? string.Empty, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"),
            Path.Combine(onPath, "node_modules", "npm", "bin", "npm-cli.js"),
            Path.Combine(onPath, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"),
        ];
        return candidates.Where(c => c.Length > 0 && File.Exists(c)).Select(Path.GetFullPath).FirstOrDefault();
    }
}
