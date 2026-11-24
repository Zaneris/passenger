using System.Text.Json;
using Passenger.Core;

namespace Passenger.App;

/// <summary>A file icon theme an extension contributes: its id, its label and its file's path inside the extension.</summary>
public sealed record VsCodeIconContribution(string Id, string Label, string Path);

/// <summary>What a VS Code file icon theme draws under one variant, each by its icon's id: a file, a folder and an open
/// folder it names nothing else for, and its icons by a file's name, extension and language and by a folder's name, closed
/// and open, each name and extension matched in any letter case.</summary>
public sealed record VsCodeIconAssociations(string? File, string? Folder, string? FolderExpanded, IReadOnlyDictionary<string, string> FileNames,
    IReadOnlyDictionary<string, string> FileExtensions, IReadOnlyDictionary<string, string> LanguageIds, IReadOnlyDictionary<string, string> FolderNames,
    IReadOnlyDictionary<string, string> FolderNamesExpanded);

/// <summary>The icon a VS Code file icon theme gives a file or folder: its id, null for none, and whether the theme names
/// the file or folder (by its name, an extension or its language) rather than drawing it as any other.</summary>
public readonly record struct VsCodeIconMatch(string? Icon, bool Named);

/// <summary>A VS Code file icon theme as read: its name, each icon's SVG by the icon's id (its path inside the extension),
/// and what it draws under Dark and under Light, the second its <c>light</c> section laid over the first.</summary>
public sealed record VsCodeIconTheme(string Name, IReadOnlyDictionary<string, string> Icons, VsCodeIconAssociations Dark, VsCodeIconAssociations Light)
{
    /// <summary>A file's icon by its name and the name of the folder it is in, as VS Code finds one: its whole name, then each
    /// extension it ends in, the longest first (<c>d.ts</c> before <c>ts</c>, a leading dot's too), each first under its
    /// folder's name (<c>workflows/yml</c>), then its language's id; the theme's file icon where it names none of them.</summary>
    public VsCodeIconMatch FileIcon(string name, string? folder, bool dark, Func<string?> language)
    {
        VsCodeIconAssociations given = dark ? Dark : Light;
        if (Find(given.FileNames, name, folder) is { } named)
        {
            return new VsCodeIconMatch(named, true);
        }
        for (int dot = name.IndexOf('.', StringComparison.Ordinal); dot >= 0 && dot < name.Length - 1; dot = name.IndexOf('.', dot + 1))
        {
            if (Find(given.FileExtensions, name[(dot + 1)..], folder) is { } extended)
            {
                return new VsCodeIconMatch(extended, true);
            }
        }
        return language() is { } id && given.LanguageIds.TryGetValue(id, out string? spoken) ? new VsCodeIconMatch(spoken, true) : new VsCodeIconMatch(given.File, false);
    }

    /// <summary>A folder's icon by its name, open or closed: an open one's by its name where the theme gives one, else its
    /// closed one's; the theme's folder icon where it names none, open or closed.</summary>
    public VsCodeIconMatch FolderIcon(string name, bool open, bool dark)
    {
        VsCodeIconAssociations given = dark ? Dark : Light;
        if ((open && given.FolderNamesExpanded.TryGetValue(name, out string? named)) || given.FolderNames.TryGetValue(name, out named))
        {
            return new VsCodeIconMatch(named, true);
        }
        return new VsCodeIconMatch(open ? given.FolderExpanded ?? given.Folder : given.Folder, false);
    }

    private static string? Find(IReadOnlyDictionary<string, string> associated, string key, string? folder) =>
        (folder is { Length: > 0 } && associated.TryGetValue($"{folder}/{key}", out string? under)) || associated.TryGetValue(key, out under) ? under : null;
}

/// <summary>A VS Code file icon theme's files taken from its extension, by their paths there (the manifest, the theme's
/// JSON and every icon's SVG it draws), its extension's version, and the theme as read.</summary>
public sealed record VsCodeIconTaken(IReadOnlyDictionary<string, byte[]> Files, string Version, VsCodeIconTheme Theme, IReadOnlyList<string> Problems);

/// <summary>Reads VS Code file icon themes (an extension's <c>iconThemes</c>) as data: a theme's JSON as VS Code reads it,
/// comments and trailing commas allowed, its icons' definitions (an SVG's path beside the theme's file inside the
/// extension; an icon of a font's character or a picture of another kind is passed over and named, as Passenger draws
/// SVGs alone), its file, folder and open folder icons, its icons by file name, extension and language and by folder name,
/// closed and open, and its <c>light</c> section. Nothing of the extension runs; the JSON is held to
/// <see cref="ThemeFiles.MostBytes"/>, a theme to <see cref="MostIcons"/> icons and <see cref="MostNamed"/> names of each
/// kind, an SVG to <see cref="ThemeFiles.MostIconBytes"/>. Pure.</summary>
public static class VsCodeIconThemes
{
    /// <summary>The most icons one theme may define.</summary>
    public const int MostIcons = 5000;

    /// <summary>The most file names, extensions, languages or folder names one theme may give icons for, of each kind.</summary>
    public const int MostNamed = 20_000;

    private const int LongestName = 260;

    private static readonly string[] Members = ["iconDefinitions", "file", "folder", "folderExpanded", "rootFolder", "rootFolderExpanded", "rootFolderNames",
        "rootFolderNamesExpanded", "fileExtensions", "fileNames", "folderNames", "folderNamesExpanded", "languageIds", "light", "highContrast", "fonts",
        "hidesExplorerArrows", "showLanguageModeIcons"];

    /// <summary>The file icon themes a manifest's <c>contributes.iconThemes</c> names, each with an id, a label and a JSON
    /// file inside the extension; another is passed over and named.</summary>
    public static IReadOnlyList<VsCodeIconContribution> Contributed(string file, JsonElement manifest, List<string> problems)
    {
        IReadOnlyList<JsonElement> contributed = JsonMembers.Items(JsonMembers.Value(manifest, "contributes"), "iconThemes");
        if (contributed.Count > VsCodeThemes.MostThemes)
        {
            throw new FormatException($"contributes {Numbers.Said(contributed.Count)} file icon themes, more than the {Numbers.Said(VsCodeThemes.MostThemes)} Passenger reads of one extension");
        }
        List<VsCodeIconContribution> themes = [];
        foreach (JsonElement theme in contributed)
        {
            string? id = JsonMembers.Text(theme, "id");
            string? label = JsonMembers.Text(theme, "label") ?? id;
            string? path = JsonMembers.Text(theme, "path");
            if (id is not { Length: > 0 and <= VsCodeThemes.LongestLabel } || id.Any(char.IsControl) || label is not { Length: > 0 and <= VsCodeThemes.LongestLabel }
                || label.Any(char.IsControl) || path is null || VsCodeThemes.Inside(path, ".") is not { } inside)
            {
                problems.Add($"{file}: a file icon theme it contributes ({ThemeJson.Shown(theme)}) names no id, label or path inside the extension Passenger reads, so it was passed over");
                continue;
            }
            themes.Add(new VsCodeIconContribution(id, label, inside));
        }
        return themes;
    }

    /// <summary>A file icon theme from its JSON at <paramref name="path"/> inside its extension, named
    /// <paramref name="name"/>; its icons' SVGs are not read here.</summary>
    public static ThemeRead<VsCodeIconTheme> ReadTheme(string path, string name, ReadOnlyMemory<byte> bytes) => VsCodeThemes.Parse(path, bytes, (root, problems) =>
    {
        foreach (JsonProperty member in root.EnumerateObject().Where(m => !Members.Contains(m.Name, StringComparer.Ordinal)))
        {
            problems.Add($"{path}: {member.Name} is no member of a file icon theme Passenger reads, so it was passed over");
        }
        Dictionary<string, string> icons = Icons(path, root, problems);
        VsCodeIconAssociations dark = Associations(path, root, null, icons, problems);
        VsCodeIconAssociations light = JsonMembers.Object(root, "light") is { } over ? Associations(path, over, dark, icons, problems) : dark;
        if (icons.Count == 0)
        {
            throw new FormatException("defines no icon Passenger draws: it is no file icon theme of SVGs");
        }
        return new VsCodeIconTheme(name, icons, dark, light);
    });

    /// <summary>A file icon theme of a <c>.vsix</c> by its label or id, with its manifest, its JSON and every SVG it draws
    /// (an SVG that is missing or no SVG passed over and named, so its files and folders keep Passenger's icons); why not
    /// where the <c>.vsix</c>, its manifest or the theme cannot be read, or it contributes no such theme.</summary>
    public static (VsCodeIconTaken? Taken, string? Why) FromVsix(byte[] vsix, string named)
    {
        if (VsCodeThemes.Vsix(vsix, out string? unzipped) is not { } files)
        {
            return (null, unzipped);
        }
        return FromFiles(files, named);
    }

    /// <summary>A file icon theme of an extension's files by its label or id, as <see cref="FromVsix"/> takes it.</summary>
    public static (VsCodeIconTaken? Taken, string? Why) FromFiles(VsCodeThemes.ExtensionFile files, string named)
    {
        (byte[]? manifest, string? unread) = files(VsCodeThemes.Manifest);
        if (manifest is null)
        {
            return (null, unread);
        }
        ThemeRead<VsCodeExtension> extension = VsCodeThemes.ReadManifest(VsCodeThemes.Manifest, manifest);
        if (extension.Read is not { } read)
        {
            return (null, extension.Refused);
        }
        if ((read.IconThemes.FirstOrDefault(t => t.Label == named) ?? read.IconThemes.FirstOrDefault(t => t.Id == named)) is not { } contributed)
        {
            return (null, $"{read.Id} {read.Version} contributes no file icon theme labelled {named}"
                + (read.IconThemes.Count == 0 ? string.Empty : $": it contributes {string.Join(", ", read.IconThemes.Select(t => t.Label))}"));
        }
        (byte[]? json, string? missing) = files(contributed.Path);
        if (json is null)
        {
            return (null, missing);
        }
        ThemeRead<VsCodeIconTheme> theme = ReadTheme(contributed.Path, contributed.Label, json);
        if (theme.Read is not { } whole)
        {
            return (null, theme.Refused);
        }
        Dictionary<string, byte[]> taken = new(StringComparer.Ordinal) { [VsCodeThemes.Manifest] = manifest, [contributed.Path] = json };
        List<string> problems = [.. extension.Problems, .. theme.Problems];
        (Dictionary<string, byte[]> svgs, IReadOnlyList<string> passed) = Svgs(whole, files);
        foreach ((string path, byte[] bytes) in svgs)
        {
            taken[path] = bytes;
        }
        problems.AddRange(passed);
        return (new VsCodeIconTaken(taken, read.Version, whole, problems), null);
    }

    /// <summary>Every SVG a theme draws, each read once through <paramref name="files"/> and checked as a theme's own icon is
    /// (<see cref="ThemeImages.IconWhyNot"/>); one that cannot be is passed over and named.</summary>
    public static (Dictionary<string, byte[]> Svgs, IReadOnlyList<string> Passed) Svgs(VsCodeIconTheme theme, VsCodeThemes.ExtensionFile files)
    {
        Dictionary<string, byte[]> svgs = new(StringComparer.Ordinal);
        List<string> passed = [];
        foreach (string path in theme.Icons.Values.Distinct(StringComparer.Ordinal))
        {
            (byte[]? bytes, string? why) = files(path);
            string? refused = bytes is null ? why : ThemeImages.IconWhyNot($"{theme.Name}'s icon {path}", bytes);
            if (refused is not null)
            {
                passed.Add($"{refused}, so the files it stands for keep Passenger's icon");
                continue;
            }
            svgs[path] = bytes!;
        }
        return (svgs, passed);
    }

    /// <summary>Each icon a theme defines as an SVG's path inside the extension, beside the theme's file; a font's character
    /// or another kind of picture is passed over, named once for all.</summary>
    private static Dictionary<string, string> Icons(string path, JsonElement root, List<string> problems)
    {
        IReadOnlyList<JsonProperty> defined = JsonMembers.Properties(root, "iconDefinitions");
        if (defined.Count > MostIcons)
        {
            throw new FormatException($"defines {Numbers.Said(defined.Count)} icons, more than the {Numbers.Said(MostIcons)} Passenger reads of one file icon theme");
        }
        Dictionary<string, string> icons = new(StringComparer.Ordinal);
        List<string> undrawn = [];
        string folder = VsCodeThemes.Folder(path);
        foreach (JsonProperty icon in defined)
        {
            if (JsonMembers.Text(icon.Value, "iconPath") is { } file && VsCodeThemes.Inside(file, folder, ".svg") is { } svg)
            {
                icons[icon.Name] = svg;
            }
            else
            {
                undrawn.Add(icon.Name);
            }
        }
        if (undrawn.Count > 0)
        {
            problems.Add($"{path}: {(undrawn.Count == 1 ? "its icon" : $"{Numbers.Said(undrawn.Count)} of its icons")} {string.Join(", ", undrawn.Take(5))}{(undrawn.Count > 5 ? ", …" : string.Empty)} "
                + $"{Plural.Agreeing(undrawn.Count, "is", "are")} no SVG inside the extension (a font's character or another kind of picture, which Passenger does not draw), "
                + "so the files and folders they stand for keep Passenger's icons");
        }
        return icons;
    }

    /// <summary>What a section of a theme draws, each name, extension, language and icon an icon it defines, laid over
    /// <paramref name="under"/> where it is the <c>light</c> section.</summary>
    private static VsCodeIconAssociations Associations(string path, JsonElement section, VsCodeIconAssociations? under, Dictionary<string, string> icons,
        List<string> problems)
    {
        string within = under is null ? string.Empty : "light's ";
        int unknown = 0;
        string? Single(string member)
        {
            if (JsonMembers.Text(section, member) is not { } id)
            {
                return null;
            }
            if (icons.ContainsKey(id))
            {
                return id;
            }
            unknown++;
            return null;
        }
        Dictionary<string, string> Named(string member, IReadOnlyDictionary<string, string>? beneath)
        {
            Dictionary<string, string> named = beneath is null ? new(StringComparer.OrdinalIgnoreCase) : new(beneath, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<JsonProperty> given = JsonMembers.Properties(section, member);
            if (given.Count > MostNamed)
            {
                throw new FormatException($"gives {within}{member} {Numbers.Said(given.Count)} icons, more than the {Numbers.Said(MostNamed)} Passenger reads");
            }
            foreach (JsonProperty entry in given)
            {
                if (entry.Name.Length is 0 or > LongestName || entry.Value.ValueKind != JsonValueKind.String || !icons.ContainsKey(entry.Value.GetString()!))
                {
                    unknown++;
                    continue;
                }
                named[entry.Name] = entry.Value.GetString()!;
            }
            return named;
        }
        VsCodeIconAssociations read = new(Single("file") ?? under?.File, Single("folder") ?? under?.Folder, Single("folderExpanded") ?? under?.FolderExpanded,
            Named("fileNames", under?.FileNames), Named("fileExtensions", under?.FileExtensions), Named("languageIds", under?.LanguageIds),
            Named("folderNames", under?.FolderNames), Named("folderNamesExpanded", under?.FolderNamesExpanded));
        if (unknown > 0)
        {
            problems.Add($"{path}: {within}{Plural.Of(unknown, "file or folder", "files or folders")} {Plural.Agreeing(unknown, "names", "name")} an icon it does not define as an SVG, "
                + "so each keeps Passenger's icon");
        }
        return read;
    }
}
