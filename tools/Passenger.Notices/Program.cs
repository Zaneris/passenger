using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Passenger.Core;
using Passenger.Tools;

namespace Passenger.Notices;

/// <summary>Writes THIRD-PARTY-NOTICES.txt, which the app carries beside it, from what the build gives it in a file of
/// <c>name=value</c> lines: the packages the app copies, the debugger's and Roslyn's server's (their .deps.json files),
/// the runtime a self-contained app carries, THIRD_PARTY_NOTICES.md and the licence texts; or, given <c>for=tools</c>,
/// the container tools' own, from their .deps.json. A package whose licence it cannot write in full fails the build,
/// naming it.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Passenger.Notices : error PN0001: takes one argument, the file of what to read");
            return 1;
        }
        try
        {
            Inputs inputs = Inputs.Read(args[0]);
            string text = NoticesFile.Write(inputs);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(inputs.Out))!);
            File.WriteAllText(inputs.Out, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return 0;
        }
        catch (NoticeException e)
        {
            // MSBuild's own form, so the build fails with each line as an error.
            foreach (string problem in e.Problems)
            {
                Console.Error.WriteLine($"Passenger.Notices : error PN0002: {problem}");
            }
            return 1;
        }
    }
}

/// <summary>Why the notices cannot be written: each package or file that stands in the way.</summary>
public sealed class NoticeException(IReadOnlyList<string> problems) : Exception(string.Join(Environment.NewLine, problems))
{
    public IReadOnlyList<string> Problems => problems;
}

/// <summary>What the build hands over, one <c>name=value</c> a line.</summary>
public sealed class Inputs
{
    public string Out { get; private set; } = "";

    /// <summary>Whose notices these are: the app's, or the container tools' (<c>for=tools</c>).</summary>
    public bool ForTools { get; private set; }

    /// <summary>The container tools' .deps.json: every package they carry, and the runtime pack they are published with.</summary>
    public List<string> ToolsDeps { get; } = [];

    /// <summary>The sections of THIRD_PARTY_NOTICES.md's copied source carried, by their titles; none given, all of them.</summary>
    public List<string> Sections { get; } = [];

    /// <summary>Passenger's own LICENSE.</summary>
    public string License { get; private set; } = "";

    /// <summary>THIRD_PARTY_NOTICES.md: Roslyn's server's table, and the copied source and assets carried whole.</summary>
    public string Notices { get; private set; } = "";

    /// <summary>The folder of licence texts by SPDX identifier.</summary>
    public string Licenses { get; private set; } = "";

    /// <summary>The NuGet package folder the restore used.</summary>
    public string PackageRoot { get; private set; } = "";

    /// <summary>The app's packages, as Id/Version: those it copies, and the runtime pack a self-contained app carries.</summary>
    public List<string> AppPackages { get; } = [];

    /// <summary>The debugger's .deps.json files.</summary>
    public List<string> DebuggerDeps { get; } = [];

    /// <summary>The project host's .deps.json files.</summary>
    public List<string> HostDeps { get; } = [];

    /// <summary>Roslyn's server package's folder, and the folder its server is in.</summary>
    public string? Server { get; private set; }

    public string? ServerFolder { get; private set; }

    /// <summary>The packages the server's .deps.json names whose assemblies Passenger leaves out of roslyn/.</summary>
    public HashSet<string> ServerLeftOut { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The node_modules folders of the npm packages carried under web/ (the web language servers).</summary>
    public List<string> NodeModules { get; } = [];

    /// <summary>Further licences carried whole, each as title|path.</summary>
    public List<(string Title, string Path)> Texts { get; } = [];

    public static Inputs Read(string path)
    {
        Inputs inputs = new();
        InputsFile file = new(path, problem => new NoticeException([problem]));
        foreach ((string name, string value) in file.Lines())
        {
            switch (name)
            {
                case "out":
                    inputs.Out = value;
                    break;
                case "for":
                    inputs.ForTools = value switch
                    {
                        "app" => false,
                        "tools" => true,
                        _ => throw file.Failure($"for={value} names neither the app nor the tools"),
                    };
                    break;
                case "tools":
                    inputs.ToolsDeps.Add(value);
                    break;
                case "section":
                    inputs.Sections.Add(value);
                    break;
                case "license":
                    inputs.License = value;
                    break;
                case "notices":
                    inputs.Notices = value;
                    break;
                case "licenses":
                    inputs.Licenses = value;
                    break;
                case "packages":
                    inputs.PackageRoot = value;
                    break;
                case "package":
                    inputs.AppPackages.Add(value);
                    break;
                case "debugger":
                    inputs.DebuggerDeps.Add(value);
                    break;
                case "host":
                    inputs.HostDeps.Add(value);
                    break;
                case "server":
                    inputs.Server = value;
                    break;
                case "serverFolder":
                    inputs.ServerFolder = value;
                    break;
                case "serverLeftOut":
                    inputs.ServerLeftOut.Add(value);
                    break;
                case "node":
                    inputs.NodeModules.Add(value);
                    break;
                case "text":
                    inputs.Texts.Add(file.Pair(name, value, "a licence's title and the file of its text, as title|path"));
                    break;
                default:
                    throw file.Failure($"'{name}' is not something the notices read");
            }
        }
        if (inputs.Out.Length == 0 || inputs.License.Length == 0 || inputs.Notices.Length == 0 || inputs.Licenses.Length == 0 || inputs.PackageRoot.Length == 0)
        {
            throw new NoticeException([$"{path} needs out, license, notices, licenses and packages"]);
        }
        return inputs;
    }
}

/// <summary>Where the app carries a package: what a reader of the notices finds it in.</summary>
public static class Place
{
    public const string App = "the app";
    public const string Debugger = "debugger/";
    public const string Host = "host/";
    public const string Server = "roslyn/";
    public const string Web = "web/";
    public const string Tools = "the container tools";
}

/// <summary>One package as the notices name it: where it is carried, the copyright its package states, and its licence
/// (its name, and its full text or, for terms that are not open source, what they are).</summary>
public sealed class Entry(string id, string version)
{
    public string Id => id;

    public string Version => version;

    /// <summary>The app first, then debugger/, host/ and roslyn/.</summary>
    public SortedSet<string> Places { get; } = new(Comparer<string>.Create((a, b) => Order(a).CompareTo(Order(b))));

    private static int Order(string place) => Array.IndexOf([Place.App, Place.Debugger, Place.Host, Place.Server, Place.Web, Place.Tools], place);

    public string Copyright { get; set; } = "";

    public string Licence { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>The package's own notice files, each as its name and text.</summary>
    public List<(string File, string Text)> Notices { get; } = [];
}

/// <summary>The notices file: Passenger's own licence named, every package grouped by the licence text it is under, the
/// notices the packages carry of their own, and THIRD_PARTY_NOTICES.md's copied source and assets.</summary>
public static partial class NoticesFile
{
    /// <summary>Between two packages' licences; a test reads the packages by it.</summary>
    public const string Rule = "--------------------------------------------------------------------------------";

    /// <summary>The line in THIRD_PARTY_NOTICES.md after which it is carried as it is.</summary>
    public const string CarriedFrom = "<!-- The app's notices (THIRD-PARTY-NOTICES.txt) carry everything below this line as it is. -->";

    public static string Write(Inputs inputs)
    {
        List<string> problems = [];
        Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
        Licences licences = new(inputs.Licenses);

        foreach (string package in inputs.AppPackages)
        {
            string[] parts = package.Split('/');
            Restored(parts[0], parts[1], null, Place.App);
        }
        foreach (string deps in inputs.DebuggerDeps)
        {
            foreach ((string id, string version, string? path) in Dependencies(deps))
            {
                Restored(id, version, path, Place.Debugger);
            }
        }
        foreach (string deps in inputs.HostDeps)
        {
            foreach ((string id, string version, string? path) in Dependencies(deps))
            {
                Restored(id, version, path, Place.Host);
            }
        }
        foreach (string deps in inputs.ToolsDeps)
        {
            foreach ((string id, string version, string? path) in Dependencies(deps))
            {
                Restored(id, version, path, Place.Tools);
            }
        }
        if (inputs.Server is { } server)
        {
            Package(Path.GetFullPath(server), Place.Server);
            Dictionary<string, (string Licence, string Copyright)> table = ServerTable(inputs.Notices, problems);
            foreach (string deps in Directory.EnumerateFiles(inputs.ServerFolder ?? server, "*.deps.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                foreach ((string id, string version, string? _) in Dependencies(deps).Where(d => !inputs.ServerLeftOut.Contains(d.Id)))
                {
                    if (!table.TryGetValue(id, out (string Licence, string Copyright) row))
                    {
                        problems.Add($"Roslyn's server carries {id} {version}, which THIRD_PARTY_NOTICES.md's \"Inside roslyn/\" table does not name: add it with the licence and copyright its package states");
                        continue;
                    }
                    Entry entry = Add(id, version, Place.Server);
                    if (entry.Licence.Length == 0)
                    {
                        entry.Copyright = row.Copyright;
                        (entry.Licence, entry.Text) = licences.Stated(row.Licence);
                    }
                }
            }
        }
        foreach (string modules in inputs.NodeModules)
        {
            foreach (string folder in NpmPackages(modules))
            {
                Npm(folder);
            }
        }
        if (problems.Count > 0)
        {
            throw new NoticeException(problems);
        }

        StringBuilder text = new();
        string copyright = File.ReadLines(inputs.License).FirstOrDefault(l => l.StartsWith("Copyright", StringComparison.Ordinal))?.Trim() ?? "";
        text.Append(inputs.ForTools ? Invariant($"""
            Third-party notices for Passenger's container tools
            ===================================================

            Passenger is {copyright}, under the MIT License in LICENSE.txt beside this file. Its container tools
            (passenger-tools, which Passenger runs inside a Linux container to debug and profile the .NET programs there)
            carry the software named below, each part under its own licence:

            1. Packages: each with the copyright its package states and the full text of its licence: the .NET runtime
               the tools run on, the debugger's and the profiler's packages, and dbgshim for the tools' system (musl's
               own for a musl system).
            2. The notices the packages carry of their own.
            3. Source Passenger's debugger copied from other projects.

            This file is written from the packages themselves each time the tools are built.


            1. Packages
            ===========

            """) : Invariant($"""
            Third-party notices for Passenger
            =================================

            Passenger is {copyright}, under the MIT License in LICENSE.txt beside this file. It carries the software
            named below, each part under its own licence:

            1. Packages: each with where Passenger carries it (the app; its debugger, under debugger/; its project host,
               under host/; Roslyn's language server, under roslyn/; the npm packages of the JavaScript, TypeScript,
               HTML, CSS and JSON language servers, under web/), the copyright its package states, and the full text
               of its licence.
            2. The notices the packages carry of their own.
            3. Source and assets Passenger copied from other projects, and the licences of its typefaces.

            This file is written from the packages themselves each time Passenger is built.


            1. Packages
            ===========

            """));
        foreach (IGrouping<string, Entry> group in entries.Values
            .GroupBy(e => e.Licence + "\n" + e.Text)
            .OrderBy(g => g.First().Licence, StringComparer.Ordinal)
            .ThenBy(g => g.Select(e => e.Id).Min(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            text.Append('\n').Append(Rule).Append('\n');
            foreach (Entry entry in group.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Version, StringComparer.Ordinal))
            {
                text.Append(Invariant($"{entry.Id} {entry.Version} ({string.Join(", ", entry.Places)}): {entry.Copyright}\n"));
            }
            Entry first = group.First();
            text.Append(Invariant($"\nLicence: {first.Licence}\n\n{first.Text}\n"));
        }

        text.Append("\n\n2. The notices the packages carry\n==================================\n");
        foreach (IGrouping<string, (Entry Entry, string File, string Text)> notice in entries.Values
            .SelectMany(e => e.Notices.Select(n => (Entry: e, n.File, n.Text)))
            .GroupBy(n => n.Text)
            .OrderBy(g => g.Select(n => n.Entry.Id).Min(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> from = notice.OrderBy(n => n.Entry.Id, StringComparer.OrdinalIgnoreCase).Select(n => $"{n.Entry.Id} {n.Entry.Version} ({n.File})");
            text.Append('\n').Append(Rule).Append('\n');
            text.Append(Invariant($"From {string.Join(", ", from)}:\n\n{notice.Key}\n"));
        }

        text.Append(inputs.ForTools ? "\n\n3. Source copied into the debugger\n==================================\n\n"
            : "\n\n3. Source and assets copied into Passenger\n==========================================\n\n");
        text.Append(Carried(inputs.Notices, inputs.Sections)).Append('\n');
        foreach ((string title, string path) in inputs.Texts)
        {
            text.Append('\n').Append(Rule).Append('\n');
            text.Append(Invariant($"{title}:\n\n{Read(path)}\n"));
        }
        return text.ToString();

        // A package the restore put in the package folder, by the path its .deps.json gives or by its id and version.
        void Restored(string id, string version, string? path, string place)
        {
            string folder = Path.Combine(inputs.PackageRoot, path ?? Path.Combine(id.ToLowerInvariant(), version.ToLowerInvariant()));
            if (!Directory.Exists(folder))
            {
                problems.Add($"{id} {version} is not in the package folder ({folder}); restore first");
                return;
            }
            Package(folder, place);
        }

        // A package's entry from its own folder: its nuspec's copyright and licence, and the notice files it carries.
        void Package(string folder, string place)
        {
            string[] nuspecs = Directory.GetFiles(folder, "*.nuspec");
            if (nuspecs.Length != 1)
            {
                problems.Add($"{folder} holds {nuspecs.Length} nuspec files, not one");
                return;
            }
            XElement metadata = XDocument.Load(nuspecs[0]).Root!.Elements().First(e => e.Name.LocalName == "metadata");
            string? Field(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            string id = Field("id")!;
            // A nuspec's version can carry build metadata ("2.25.29+RR"), which its folder's name leaves out.
            string version = Field("version")!.Split('+')[0];
            Entry entry = Add(id, version, place);
            if (entry.Licence.Length > 0)
            {
                return;
            }
            string? stated = Field("copyright");
            string authors = Field("authors") ?? "";
            entry.Copyright = string.IsNullOrWhiteSpace(stated) ? $"no copyright line; its authors: {authors}"
                : stated.Any(char.IsLetter) ? OneLine(stated) : $"{OneLine(stated)}, {authors}";
            XElement? license = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
            if (license?.Attribute("type")?.Value == "expression")
            {
                (string Licence, string Text)? written = licences.Expression(license.Value.Trim(), out string? missing);
                if (written is null)
                {
                    problems.Add($"{id} {version} is under {license.Value.Trim()}, and {missing} has no text in {inputs.Licenses}: add SPDX's text/{missing}.txt there (its README says from where)");
                    return;
                }
                (entry.Licence, entry.Text) = written.Value;
            }
            else if (license?.Attribute("type")?.Value == "file" && File.Exists(Path.Combine(folder, license.Value.Trim())))
            {
                entry.Licence = $"its package's {license.Value.Trim()}";
                entry.Text = Read(Path.Combine(folder, license.Value.Trim()));
            }
            else if (license is null && Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().Where(f => LicenceName().IsMatch(f))
                .Order(StringComparer.Ordinal).FirstOrDefault() is { } carried)
            {
                // An older package names only its licence's URL, and carries the licence itself.
                entry.Licence = $"its package's {carried} (its nuspec names only {Field("licenseUrl") ?? "no licence"})";
                entry.Text = Read(Path.Combine(folder, carried));
            }
            else
            {
                problems.Add($"{id} {version} states no licence expression or file (its licence URL: {Field("licenseUrl") ?? "none"}): give the notices its licence's text");
                return;
            }
            foreach (string file in Directory.EnumerateFiles(folder).Concat(Directory.Exists(Path.Combine(folder, "notices")) ? Directory.EnumerateFiles(Path.Combine(folder, "notices")) : [])
                .Where(f => NoticeName().IsMatch(Path.GetFileName(f))).Order(StringComparer.Ordinal))
            {
                string text = Read(file);
                entry.Notices.Add((Path.GetRelativePath(folder, file).Replace('\\', '/'), file.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) ? Rtf.ToText(text) : text));
            }
        }

        // An npm package's entry from its folder: its SPDX licence, the licence file it carries or SPDX's, its notices.
        void Npm(string folder)
        {
            JsonElement package;
            try
            {
                using JsonDocument read = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "package.json")));
                package = read.RootElement.Clone();
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
                problems.Add($"{folder} has no package.json that reads: {error.Message}");
                return;
            }
            string? name = JsonMembers.Text(package, "name");
            string? version = JsonMembers.Text(package, "version");
            if (name is null || version is null)
            {
                problems.Add($"{folder}'s package.json names no name and version");
                return;
            }
            Entry entry = Add(name, version, Place.Web);
            if (entry.Licence.Length > 0)
            {
                return;
            }
            string? expression = JsonMembers.Text(package, "license");
            if (expression is null)
            {
                problems.Add($"{name} {version} (under web/) states no SPDX licence in its package.json: give the notices its licence's text");
                return;
            }
            string? file = Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().Where(f => LicenceName().IsMatch(f)).Order(StringComparer.Ordinal).FirstOrDefault();
            if (file is not null)
            {
                entry.Licence = $"{expression} (its package's {file})";
                entry.Text = Read(Path.Combine(folder, file));
            }
            else if (licences.Expression(expression, out string? missing) is { } written)
            {
                (entry.Licence, entry.Text) = written;
            }
            else
            {
                problems.Add($"{name} {version} (under web/) is under {expression}, carries no licence file, and {missing} has no text in {inputs.Licenses}: add SPDX's text/{missing}.txt there (its README says from where)");
                return;
            }
            // npm takes an author as a string, or as an object with a name.
            string? author = JsonMembers.Text(package, "author") ?? JsonMembers.Text(JsonMembers.Object(package, "author"), "name");
            entry.Copyright = file is not null && entry.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => CopyrightLine().IsMatch(l)) is { } line
                ? OneLine(line.TrimStart('*', ' '))
                : $"no copyright line; its author: {author ?? "none named"}";
            foreach (string notice in Directory.EnumerateFiles(folder).Where(f => NoticeName().IsMatch(Path.GetFileName(f))).Order(StringComparer.Ordinal))
            {
                entry.Notices.Add((Path.GetFileName(notice), Read(notice)));
            }
        }

        Entry Add(string id, string version, string place)
        {
            string key = $"{id}/{version}";
            if (!entries.TryGetValue(key, out Entry? entry))
            {
                entry = new Entry(id, version);
                entries[key] = entry;
            }
            entry.Places.Add(place);
            return entry;
        }
    }

    /// <summary>The packages a .deps.json file names, with the folder of each under the package folder where it gives one,
    /// and the runtime pack a self-contained program carries ("runtimepack.Microsoft.NETCore.App.Runtime.osx-arm64").</summary>
    private static IEnumerable<(string Id, string Version, string? Path)> Dependencies(string deps)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(deps));
        foreach (JsonProperty library in JsonMembers.Properties(document.RootElement, "libraries"))
        {
            string[] name = library.Name.Split('/');
            switch (JsonMembers.Text(library.Value, "type"))
            {
                case "package":
                    yield return (name[0], name[1], JsonMembers.Text(library.Value, "path"));
                    break;
                case "runtimepack":
                    yield return (name[0]["runtimepack.".Length..], name[1], null);
                    break;
            }
        }
    }

    /// <summary>Every npm package folder under a node_modules folder, nested ones (a package's own node_modules) and
    /// scoped ones (@vscode/l10n) included.</summary>
    private static IEnumerable<string> NpmPackages(string modules)
    {
        if (!Directory.Exists(modules))
        {
            yield break;
        }
        foreach (string entry in Directory.EnumerateDirectories(modules).Order(StringComparer.Ordinal))
        {
            IEnumerable<string> packages = Path.GetFileName(entry).StartsWith('@') ? Directory.EnumerateDirectories(entry).Order(StringComparer.Ordinal) : [entry];
            foreach (string package in packages.Where(p => File.Exists(Path.Combine(p, "package.json"))))
            {
                yield return package;
                foreach (string nested in NpmPackages(Path.Combine(package, "node_modules")))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>THIRD_PARTY_NOTICES.md's "Inside roslyn/" table: each package it names, with the licence and copyright its
    /// row gives. The server's package carries these, but not their nuspecs.</summary>
    private static Dictionary<string, (string Licence, string Copyright)> ServerTable(string notices, List<string> problems)
    {
        Dictionary<string, (string, string)> table = new(StringComparer.OrdinalIgnoreCase);
        bool inside = false;
        foreach (string line in File.ReadLines(notices))
        {
            if (line.StartsWith('#'))
            {
                inside = line.StartsWith("### Inside `roslyn/`", StringComparison.Ordinal);
                continue;
            }
            if (!inside || !line.StartsWith("| `", StringComparison.Ordinal))
            {
                continue;
            }
            string[] cells = [.. line.Split('|').Skip(1).SkipLast(1).Select(c => c.Trim())];
            if (cells.Length != 3)
            {
                problems.Add($"THIRD_PARTY_NOTICES.md: a row of the \"Inside roslyn/\" table has {cells.Length} cells, not three: {line}");
                continue;
            }
            foreach (Match id in Backticked().Matches(cells[0]))
            {
                table[id.Groups[1].Value] = (Plain(cells[1]), Plain(cells[2]));
            }
        }
        if (table.Count == 0)
        {
            problems.Add("THIRD_PARTY_NOTICES.md has no \"### Inside `roslyn/`\" table of the packages Roslyn's server carries");
        }
        return table;
    }

    /// <summary>THIRD_PARTY_NOTICES.md from <see cref="CarriedFrom"/> on: the copied source and assets, with their licences;
    /// or only the sections (each from its "## " title to the next) <paramref name="sections"/> names, when it names any.</summary>
    private static string Carried(string notices, IReadOnlyList<string> sections)
    {
        string text = Read(notices);
        int at = text.IndexOf(CarriedFrom, StringComparison.Ordinal);
        if (at < 0)
        {
            throw new NoticeException([$"THIRD_PARTY_NOTICES.md has no line '{CarriedFrom}' before the copied source and assets it lists"]);
        }
        string carried = text[(at + CarriedFrom.Length)..].Trim('\n');
        if (sections.Count == 0)
        {
            return carried;
        }
        Dictionary<string, string> titled = new(StringComparer.Ordinal);
        foreach (string section in ("\n" + carried).Split("\n## ").Skip(1))
        {
            int titleEnd = section.IndexOf('\n', StringComparison.Ordinal);
            titled[(titleEnd < 0 ? section : section[..titleEnd]).Trim()] = ("## " + section).Trim('\n');
        }
        string[] missing = [.. sections.Where(s => !titled.ContainsKey(s))];
        if (missing.Length > 0)
        {
            throw new NoticeException([.. missing.Select(m => $"THIRD_PARTY_NOTICES.md has no section '## {m}' after the line '{CarriedFrom}'")]);
        }
        return string.Join("\n\n", sections.Select(s => titled[s]));
    }

    /// <summary>A text file's text, with the line endings it is written with here and without the control characters some
    /// carry (a NUL after an RTF document's end).</summary>
    public static string Read(string path) => Controls().Replace(File.ReadAllText(path).ReplaceLineEndings("\n"), "").Trim('\n', '﻿');

    private static string OneLine(string text) => Whitespace().Replace(text, " ").Trim();

    /// <summary>A Markdown cell's words: no backticks.</summary>
    private static string Plain(string cell) => OneLine(cell.Replace("`", "", StringComparison.Ordinal));

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    [GeneratedRegex(@"^(third[-_ ]?party[-_ ]?notices?(text)?|notices?)(\.(txt|md|rtf))?$", RegexOptions.IgnoreCase)]
    private static partial Regex NoticeName();

    /// <summary>An npm package's licence file: LICENSE, LICENSE.md, License.txt, LICENSE-MIT.txt, LICENCE, COPYING.</summary>
    [GeneratedRegex(@"^(licen[cs]e|copying)([-.][\w.-]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LicenceName();

    /// <summary>A licence file's copyright line, not a template's placeholder (Apache's "Copyright [yyyy] ...").</summary>
    [GeneratedRegex(@"^(\*\s*)?Copyright\b(?!.*(\[yyyy\]|\{yyyy\}|<year>))", RegexOptions.None)]
    private static partial Regex CopyrightLine();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex Backticked();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\x00-\x08\x0B-\x1F\x7F]")]
    private static partial Regex Controls();
}

/// <summary>The licence texts by SPDX identifier, in the folder beside this tool.</summary>
public sealed partial class Licences(string folder)
{
    /// <summary>An SPDX expression's licences ("MIT", "MIT OR Apache-2.0") and their texts one after another; null with the
    /// identifier that has no text.</summary>
    public (string Licence, string Text)? Expression(string expression, out string? missing)
    {
        missing = null;
        List<string> texts = [];
        foreach (Match id in Identifier().Matches(expression))
        {
            if (id.Value is "AND" or "OR" or "WITH")
            {
                continue;
            }
            string file = Path.Combine(folder, id.Value + ".txt");
            if (!File.Exists(file))
            {
                missing = id.Value;
                return null;
            }
            // The template's placeholder copyright line goes, with the blank line after it.
            texts.Add(NoticesFile.Read(file).Replace("Copyright (c) <year> <copyright holders>\n\n", "", StringComparison.Ordinal));
        }
        return (expression, string.Join("\n\n" + new string('-', 40) + "\n\n", texts));
    }

    /// <summary>A licence as THIRD_PARTY_NOTICES.md's table states it: the licence and its text when the table starts with
    /// an identifier there is one for ("MIT (their releases there)"), else the table's words (terms that are not open
    /// source), which are not copied.</summary>
    public (string Licence, string Text) Stated(string stated)
    {
        Match id = Identifier().Match(stated);
        if (id.Success && id.Index == 0 && Expression(id.Value, out _) is { } text)
        {
            return text;
        }
        return (stated, "These terms are not an open-source licence and are not copied here. The package that carries this one, Roslyn's "
            + "language server (roslyn-language-server), states the MIT License for itself as a whole.");
    }

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9.+\-]*")]
    private static partial Regex Identifier();
}

/// <summary>The plain text of an RTF document (a package's ThirdPartyNotices.rtf): its characters and paragraphs, without
/// its formatting, its font and colour tables, or a field's instruction.</summary>
public static class Rtf
{
    private static readonly string[] Skipped = ["fonttbl", "colortbl", "stylesheet", "info", "pict", "fldinst", "generator"];

    public static string ToText(string rtf)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding windows = Encoding.GetEncoding(1252);
        StringBuilder text = new();
        Stack<bool> outer = new();
        bool skipping = false;
        int i = 0;
        while (i < rtf.Length)
        {
            char c = rtf[i];
            if (c == '{')
            {
                outer.Push(skipping);
                i++;
            }
            else if (c == '}')
            {
                skipping = outer.Count > 0 && outer.Pop();
                i++;
            }
            else if (c is '\r' or '\n')
            {
                i++;
            }
            else if (c != '\\')
            {
                if (!skipping)
                {
                    text.Append(c);
                }
                i++;
            }
            else if (i + 1 < rtf.Length && rtf[i + 1] is '\\' or '{' or '}')
            {
                if (!skipping)
                {
                    text.Append(rtf[i + 1]);
                }
                i += 2;
            }
            else if (i + 1 < rtf.Length && rtf[i + 1] == '*')
            {
                skipping = true;
                i += 2;
            }
            else if (i + 3 < rtf.Length && rtf[i + 1] == '\'')
            {
                if (!skipping)
                {
                    text.Append(windows.GetString([Convert.ToByte(rtf.Substring(i + 2, 2), 16)]));
                }
                i += 4;
            }
            else if (i + 1 < rtf.Length && char.IsAsciiLetter(rtf[i + 1]))
            {
                int start = ++i;
                while (i < rtf.Length && char.IsAsciiLetter(rtf[i]))
                {
                    i++;
                }
                string word = rtf[start..i];
                int numberStart = i;
                if (i < rtf.Length && rtf[i] == '-')
                {
                    i++;
                }
                while (i < rtf.Length && char.IsAsciiDigit(rtf[i]))
                {
                    i++;
                }
                string number = rtf[numberStart..i];
                if (i < rtf.Length && rtf[i] == ' ')
                {
                    i++;
                }
                if (Skipped.Contains(word))
                {
                    skipping = true;
                }
                else if (!skipping && word is "par" or "line")
                {
                    text.Append('\n');
                }
                else if (!skipping && word == "tab")
                {
                    text.Append('\t');
                }
                else if (word == "u" && int.TryParse(number, System.Globalization.CultureInfo.InvariantCulture, out int unicode))
                {
                    if (!skipping)
                    {
                        text.Append((char)(unicode < 0 ? unicode + 65536 : unicode));
                    }
                    // The character's stand-in for readers without Unicode.
                    i = Math.Min(rtf.Length, i + 1);
                }
            }
            else
            {
                // A control symbol (\~, \-, \_): a space for the unbreakable one, nothing for the rest.
                if (!skipping && i + 1 < rtf.Length && rtf[i + 1] == '~')
                {
                    text.Append(' ');
                }
                i += 2;
            }
        }
        return string.Join('\n', text.ToString().Split('\n').Select(l => l.TrimEnd())).Trim('\n');
    }
}
