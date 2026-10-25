using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Web;

/// <summary>A project's own npm packages for a file (its Automatic ESLint and Prettier configuration): installed in the
/// node_modules of the file's folder or the nearest folder above it that has the package, as Node.js resolves it; or
/// listed by the nearest package.json above the file that names it, and not installed.</summary>
public static class ProjectPackages
{
    /// <summary>The fields of package.json that list what <c>npm install</c> installs.</summary>
    private static readonly string[] Lists = ["dependencies", "devDependencies", "optionalDependencies", "peerDependencies"];

    /// <summary>The package's folder as Node.js finds it from the file, null when none is installed.</summary>
    public static string? Installed(string file, string name)
    {
        for (string? folder = Path.GetDirectoryName(file); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            string package = Path.Combine(folder, "node_modules", name);
            if (File.Exists(Path.Combine(package, "package.json")))
            {
                return package;
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

    /// <summary>The nearest package.json above the file that lists the package, when the package is not installed for
    /// it: what <c>npm install</c> there would put in place. Null when it is installed, or nothing lists it.</summary>
    public static string? Missing(string file, string name)
    {
        if (Installed(file, name) is not null)
        {
            return null;
        }
        for (string? folder = Path.GetDirectoryName(file); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            string manifest = Path.Combine(folder, "package.json");
            if (File.Exists(manifest) && Names(manifest, name))
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
            using JsonDocument read = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "package.json")));
            return read.RootElement.TryGetProperty("version", out JsonElement version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool Names(string manifest, string name)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return read.RootElement.ValueKind == JsonValueKind.Object && Lists.Any(list =>
                read.RootElement.TryGetProperty(list, out JsonElement listed) && listed.ValueKind == JsonValueKind.Object && listed.TryGetProperty(name, out _));
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
        string relative = PathNames.IsUnder(file, folder) ? Path.GetRelativePath(folder, file) : Path.GetFileName(file);
        return glob.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(one => EditorConfig.Matches(one, "/" + relative.Replace('\\', '/')));
    }
}

/// <summary>npm for "Run 'npm install'": the npm that comes with the Node.js in use (its npm-cli.js, run on that
/// Node.js, so a Windows npm.cmd needs no shell), else the one on the PATH, through ChildProcesses.</summary>
public static class Npm
{
    /// <summary>npm's own script for a Node.js, null when there is none beside it or on the PATH.</summary>
    public static string? Cli(string node)
    {
        string folder = Path.GetDirectoryName(PathNames.Canonical(node)) ?? string.Empty;
        string onPath = Programs.OnPath(OperatingSystem.IsWindows() ? "npm.cmd" : "npm") is { } npm ? Path.GetDirectoryName(npm) ?? string.Empty : string.Empty;
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

    /// <summary><c>npm install</c> in a folder on a Node.js, which its packages' scripts find first on the PATH.</summary>
    public static ProcessStartInfo Install(string node, string cli, string folder)
    {
        ProcessStartInfo start = ChildProcesses.Command(node, [cli, "install"], folder);
        string path = start.Environment.TryGetValue("PATH", out string? set) && set is not null ? set : string.Empty;
        start.Environment["PATH"] = $"{Path.GetDirectoryName(node)}{Path.PathSeparator}{path}";
        return start;
    }
}
