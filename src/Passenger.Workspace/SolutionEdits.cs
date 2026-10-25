using System.Text.RegularExpressions;
using System.Xml.Linq;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Workspace;

/// <summary>The Solution view's edits to a solution's file, a .sln or a .slnx: solution folders added, renamed and
/// removed with all they hold, projects and files put in a folder, taken out again and followed where they moved. Each
/// changes only the lines or elements it is about, the rest kept in its encoding, line endings and indentation. A
/// folder is named by its path in the solution, <see cref="SolutionFolder.Path"/>. An edit returns null when done, else
/// why not; a file that cannot be read or written throws.</summary>
public static class SolutionEdits
{
    public static string? AddFolder(string solution, string? parent, string name) =>
        InvalidFolderName(name) ?? Edit(solution, file => file.AddFolder(parent, name));

    public static string? RenameFolder(string solution, string folder, string name) =>
        InvalidFolderName(name) ?? Edit(solution, file => file.RenameFolder(folder, name));

    /// <summary>Takes a folder out with every folder, project and file under it; their files stay on disk.</summary>
    public static string? RemoveFolder(string solution, string folder) => Edit(solution, file => file.RemoveFolder(folder));

    /// <summary>Puts a project (a canonical path) in a folder, or at the solution's top with none.</summary>
    public static string? AddProject(string solution, string? folder, string project) =>
        Edit(solution, file => file.AddProject(folder, project));

    public static string? RemoveProject(string solution, string project) => Edit(solution, file => file.RemoveProject(project));

    /// <summary>Puts a file (a canonical path) in a folder, as a Solution Item.</summary>
    public static string? AddFile(string solution, string folder, string path) => Edit(solution, file => file.AddFile(folder, path));

    public static string? RemoveFile(string solution, string folder, string path) => Edit(solution, file => file.RemoveFile(folder, path));

    /// <summary>The solution following files and folders that moved or were renamed: each project and file it names
    /// names where it went, and a .sln's project named after its file takes the file's new name. Whether it changed.</summary>
    public static bool Move(string solution, IReadOnlyList<(string From, string To)> moves)
    {
        SolutionText file = Open(solution);
        if (!file.Move(path => moves.FirstOrDefault(m => PathNames.Within(path, m.From)) is { From: not null } move ? PathNames.Moved(path, move.From, move.To) : path))
        {
            return false;
        }
        file.Write();
        return true;
    }

    /// <summary>Why a name cannot be a solution folder's (Visual Studio refuses the same characters), or null.</summary>
    public static string? InvalidFolderName(string name) =>
        name.Trim().Length == 0 ? "A name is needed"
        : name.Trim() is "." or ".." || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 ? $"{name} is not a solution folder name"
        : null;

    private static string? Edit(string solution, Func<SolutionText, string?> change)
    {
        SolutionText file = Open(solution);
        if (change(file) is { } problem)
        {
            return problem;
        }
        file.Write();
        return null;
    }

    private static SolutionText Open(string solution) =>
        Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase) ? new SlnxText(solution) : new SlnText(solution);

    /// <summary>A solution's file read for one change as a save reads a file: its encoding, byte order mark and line
    /// breaks, which go back as they came.</summary>
    private abstract class SolutionText
    {
        private readonly FileText file;

        protected SolutionText(string path)
        {
            FilePath = path;
            Root = Path.GetDirectoryName(path)!;
            file = FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file");
            Text = file.Text;
        }

        protected string FilePath { get; }

        /// <summary>The solution's folder, which the paths in it are relative to.</summary>
        protected string Root { get; }

        protected string Text { get; }

        public abstract string? AddFolder(string? parent, string name);
        public abstract string? RenameFolder(string folder, string name);
        public abstract string? RemoveFolder(string folder);
        public abstract string? AddProject(string? folder, string project);
        public abstract string? RemoveProject(string project);
        public abstract string? AddFile(string folder, string path);
        public abstract string? RemoveFile(string folder, string path);

        /// <summary>Each path it names as <paramref name="moved"/> gives where that is now; whether one changed.</summary>
        public abstract bool Move(Func<string, string> moved);

        protected abstract string Written();

        /// <summary>Writes the solution back as an editor's save does (<see cref="FileText.Save"/>), in the encoding
        /// and line breaks it came with.</summary>
        public void Write() => FileText.Save(FilePath, Written(), file.Encoding, file.HasBom, file.CrBreaks);

        /// <summary>A path the solution names, relative to it, as the file system names it.</summary>
        protected string Resolve(string relative) => PathNames.Canonical(Path.Combine(Root, relative.Replace('\\', '/')));

        protected string Relative(string path) => Path.GetRelativePath(Root, path);

        /// <summary>Where a path the solution names went, relative to it, with the separators the path it replaces was
        /// written with (the file's own for a bare name).</summary>
        protected string Followed(string written, string to, char separator)
        {
            char used = written.Contains('\\') ? '\\' : written.Contains('/') ? '/' : separator;
            return Relative(to).Replace('\\', used).Replace('/', used);
        }

        protected static string NameOf(string project) => Path.GetFileNameWithoutExtension(project);

        protected static string NoFolder(string folder) => $"The solution has no folder {folder.Trim('/')}";
    }

    /// <summary>A .sln, line by line, each line kept as it was read: a CRLF file's lines keep their '\r', and the lines
    /// added end the same way.</summary>
    private sealed class SlnText : SolutionText
    {
        private const string FolderType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";

        private static readonly Regex ProjectLine = new(@"^Project\(""(?<type>\{[^}]+\})""\)\s*=\s*""(?<name>[^""]*)"",\s*""(?<path>[^""]*)"",\s*""(?<id>\{[^}]+\})""");
        private static readonly Regex PairLine = new(@"^\s*(?<left>\{[^}]+\})\s*=\s*(?<right>\{[^}]+\})");
        private static readonly Regex ItemLine = new(@"^\s*[^=]*?\s*=\s*(?<path>.+?)\s*$");

        private readonly List<string> lines;
        private readonly string cr;

        public SlnText(string path) : base(path)
        {
            lines = [.. Text.Split('\n')];
            cr = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r" : string.Empty;
        }

        protected override string Written() => string.Join('\n', lines);

        /// <summary>One Project ... EndProject block: the lines it starts and ends on.</summary>
        private sealed record Entry(string Type, string Name, string RelativePath, string Id, int Start, int End)
        {
            public bool IsFolder => Type.Equals(FolderType, StringComparison.OrdinalIgnoreCase);
        }

        private List<Entry> Entries()
        {
            List<Entry> found = [];
            for (int i = 0; i < lines.Count; i++)
            {
                if (ProjectLine.Match(lines[i]) is { Success: true } m)
                {
                    int end = lines.FindIndex(i + 1, l => l.Trim() == "EndProject");
                    end = end < 0 ? i : end;
                    found.Add(new Entry(m.Groups["type"].Value, m.Groups["name"].Value, m.Groups["path"].Value, m.Groups["id"].Value, i, end));
                    i = end;
                }
            }
            return found;
        }

        /// <summary>A global section's first and last lines, or null when the file has none.</summary>
        private (int Start, int End)? Section(string name)
        {
            int start = lines.FindIndex(l => l.TrimStart().StartsWith($"GlobalSection({name})", StringComparison.Ordinal));
            int end = start < 0 ? -1 : lines.FindIndex(start + 1, l => l.Trim() == "EndGlobalSection");
            return end < 0 ? null : (start, end);
        }

        /// <summary>Each entry's parent folder, from NestedProjects.</summary>
        private Dictionary<string, string> Parents()
        {
            Dictionary<string, string> parents = new(StringComparer.OrdinalIgnoreCase);
            if (Section("NestedProjects") is { } nested)
            {
                for (int i = nested.Start + 1; i < nested.End; i++)
                {
                    if (PairLine.Match(lines[i]) is { Success: true } pair)
                    {
                        parents[pair.Groups["left"].Value] = pair.Groups["right"].Value;
                    }
                }
            }
            return parents;
        }

        /// <summary>The folder a path in the solution names, found as <see cref="SolutionModel"/> reads one: each name inside
        /// the folder NestedProjects puts it in.</summary>
        private Entry? Folder(string path)
        {
            List<Entry> entries = Entries();
            Dictionary<string, string> parents = Parents();
            Entry? at = null;
            foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                at = entries.FirstOrDefault(e => e.IsFolder && e.Name == part && string.Equals(parents.GetValueOrDefault(e.Id), at?.Id, StringComparison.OrdinalIgnoreCase));
                if (at is null)
                {
                    return null;
                }
            }
            return at;
        }

        private IEnumerable<Entry> Children(string? folder)
        {
            Dictionary<string, string> parents = Parents();
            return Entries().Where(e => string.Equals(parents.GetValueOrDefault(e.Id), folder, StringComparison.OrdinalIgnoreCase));
        }

        public override string? AddFolder(string? parent, string name)
        {
            Entry? into = parent is null ? null : Folder(parent);
            if (parent is not null && into is null)
            {
                return NoFolder(parent);
            }
            if (Children(into?.Id).Any(e => e.IsFolder && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return $"{name} is already there";
            }
            string id = NewId();
            InsertEntry(FolderType, name, name, id);
            if (into is not null)
            {
                Nest(id, into.Id);
            }
            return null;
        }

        public override string? RenameFolder(string folder, string name)
        {
            if (Folder(folder) is not { } renamed)
            {
                return NoFolder(folder);
            }
            Dictionary<string, string> parents = Parents();
            if (Children(parents.GetValueOrDefault(renamed.Id)).Any(e => e.IsFolder && e.Id != renamed.Id && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return $"{name} is already there";
            }
            Respell(renamed, name, name);
            return null;
        }

        /// <summary>An entry's Project line with a new name and path, the rest of it as it was.</summary>
        private void Respell(Entry entry, string name, string path)
        {
            string line = lines[entry.Start];
            Match m = ProjectLine.Match(line);
            Group oldName = m.Groups["name"];
            Group oldPath = m.Groups["path"];
            lines[entry.Start] = $"{line[..oldName.Index]}{name}{line[(oldName.Index + oldName.Length)..oldPath.Index]}{path}{line[(oldPath.Index + oldPath.Length)..]}";
        }

        public override bool Move(Func<string, string> moved)
        {
            bool changed = false;
            foreach (Entry entry in Entries())
            {
                if (entry.IsFolder)
                {
                    // Its files ("path = path"), each line replaced by one, so the entries found stay where they are.
                    (int Start, int End) items = Items(entry) ?? (0, 0);
                    for (int i = items.Start + 1; i < items.End; i++)
                    {
                        if (ItemLine.Match(lines[i]) is { Success: true } item && Resolve(item.Groups["path"].Value) is var path && moved(path) is var to && to != path)
                        {
                            string written = Followed(item.Groups["path"].Value, to, '\\');
                            lines[i] = $"{lines[i][..(lines[i].Length - lines[i].TrimStart().Length)]}{written} = {written}{cr}";
                            changed = true;
                        }
                    }
                    continue;
                }
                string was = Resolve(entry.RelativePath);
                if (moved(was) is var now && now == was)
                {
                    continue;
                }
                // Visual Studio names a project after its file, so one that was takes its file's new name.
                bool named = entry.Name.Equals(Path.GetFileNameWithoutExtension(entry.RelativePath.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase);
                Respell(entry, named ? NameOf(now) : entry.Name, Followed(entry.RelativePath, now, '\\'));
                changed = true;
            }
            return changed;
        }

        public override string? RemoveFolder(string folder)
        {
            if (Folder(folder) is not { } removed)
            {
                return NoFolder(folder);
            }
            Dictionary<string, string> parents = Parents();
            HashSet<string> gone = new(StringComparer.OrdinalIgnoreCase) { removed.Id };
            for (bool more = true; more;)
            {
                more = false;
                foreach ((string child, string parent) in parents)
                {
                    more |= gone.Contains(parent) && gone.Add(child);
                }
            }
            RemoveEntries(gone);
            return null;
        }

        public override string? AddProject(string? folder, string project)
        {
            Entry? into = folder is null ? null : Folder(folder);
            if (folder is not null && into is null)
            {
                return NoFolder(folder);
            }
            if (Entries().Any(e => !e.IsFolder && Resolve(e.RelativePath) == project))
            {
                return $"{NameOf(project)} is already in the solution";
            }
            string id = NewId();
            InsertEntry(TypeOf(project), NameOf(project), Relative(project).Replace('/', '\\'), id);
            // A project the configurations do not name is never built with the solution: each builds it as Any CPU.
            (int Start, int End) configurations = Section("SolutionConfigurationPlatforms")
                ?? AddSection("SolutionConfigurationPlatforms", "preSolution", null, ["Debug|Any CPU = Debug|Any CPU", "Release|Any CPU = Release|Any CPU"]);
            List<string> names = [.. lines.GetRange(configurations.Start + 1, configurations.End - configurations.Start - 1).Select(l => l.Split('=')[0].Trim()).Where(l => l.Length > 0)];
            (int Start, int End) platforms = Section("ProjectConfigurationPlatforms")
                ?? AddSection("ProjectConfigurationPlatforms", "postSolution", configurations.End + 1, []);
            lines.InsertRange(platforms.End, names.SelectMany(n => new[]
            {
                $"\t\t{id}.{n}.ActiveCfg = {n.Split('|')[0]}|Any CPU{cr}",
                $"\t\t{id}.{n}.Build.0 = {n.Split('|')[0]}|Any CPU{cr}",
            }));
            if (into is not null)
            {
                Nest(id, into.Id);
            }
            return null;
        }

        public override string? RemoveProject(string project)
        {
            if (Entries().FirstOrDefault(e => !e.IsFolder && Resolve(e.RelativePath) == project) is not { } removed)
            {
                return $"{NameOf(project)} is not in the solution";
            }
            RemoveEntries(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { removed.Id });
            return null;
        }

        public override string? AddFile(string folder, string path)
        {
            if (Folder(folder) is not { } into)
            {
                return NoFolder(folder);
            }
            string item = Relative(path).Replace('/', '\\');
            if (Items(into) is { } items)
            {
                if (Enumerable.Range(items.Start + 1, items.End - items.Start - 1).Any(i => ItemPath(i) == path))
                {
                    return $"{Path.GetFileName(path)} is already in {into.Name}";
                }
                lines.Insert(items.End, $"\t\t{item} = {item}{cr}");
            }
            else
            {
                lines.InsertRange(into.End, [$"\tProjectSection(SolutionItems) = preProject{cr}", $"\t\t{item} = {item}{cr}", $"\tEndProjectSection{cr}"]);
            }
            return null;
        }

        public override string? RemoveFile(string folder, string path)
        {
            if (Folder(folder) is not { } from)
            {
                return NoFolder(folder);
            }
            int line = Items(from) is { } listed ? Enumerable.Range(listed.Start + 1, listed.End - listed.Start - 1).FirstOrDefault(i => ItemPath(i) == path, -1) : -1;
            if (line < 0 || Items(from) is not { } items)
            {
                return $"{Path.GetFileName(path)} is not in {from.Name}";
            }
            lines.RemoveAt(line);
            // A folder with no files left has no section for them, as Visual Studio writes one.
            if (items.End - 1 == items.Start + 1)
            {
                lines.RemoveRange(items.Start, 2);
            }
            return null;
        }

        /// <summary>A folder's ProjectSection(SolutionItems): its first and last lines.</summary>
        private (int Start, int End)? Items(Entry folder)
        {
            int start = lines.FindIndex(folder.Start, folder.End - folder.Start, l => l.Contains("ProjectSection(SolutionItems)", StringComparison.Ordinal));
            int end = start < 0 ? -1 : lines.FindIndex(start, folder.End - start, l => l.Trim() == "EndProjectSection");
            return end < 0 ? null : (start, end);
        }

        private string? ItemPath(int line) => ItemLine.Match(lines[line]) is { Success: true } m ? Resolve(m.Groups["path"].Value) : null;

        /// <summary>A Project block after the last one, before Global.</summary>
        private void InsertEntry(string type, string name, string path, string id)
        {
            int at = Entries().LastOrDefault() is { } last ? last.End + 1 : GlobalStart();
            lines.InsertRange(at, [$"Project(\"{type}\") = \"{name}\", \"{path}\", \"{id}\"{cr}", $"EndProject{cr}"]);
        }

        private void Nest(string child, string parent)
        {
            (int Start, int End) nested = Section("NestedProjects")
                ?? AddSection("NestedProjects", "preSolution", Section("ExtensibilityGlobals")?.Start, []);
            lines.Insert(nested.End, $"\t\t{child} = {parent}{cr}");
        }

        /// <summary>A new global section, at a line or before EndGlobal, the Global block itself made when there is none.</summary>
        private (int Start, int End) AddSection(string name, string when, int? at, IReadOnlyList<string> content)
        {
            int end = lines.FindIndex(l => l.Trim() == "EndGlobal");
            if (end < 0)
            {
                end = GlobalStart();
                lines.InsertRange(end, [$"Global{cr}", $"EndGlobal{cr}"]);
                end++;
            }
            int start = at ?? end;
            lines.InsertRange(start, [$"\tGlobalSection({name}) = {when}{cr}", .. content.Select(c => $"\t\t{c}{cr}"), $"\tEndGlobalSection{cr}"]);
            return (start, start + content.Count + 1);
        }

        /// <summary>Where Global begins, or would: before the empty line after the file's last line break.</summary>
        private int GlobalStart()
        {
            int global = lines.FindIndex(l => l.Trim() == "Global");
            return global >= 0 ? global : lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count;
        }

        /// <summary>Takes out entries' blocks and every line that names them: their nesting, their configurations and the
        /// dependencies other projects have on them. A section they leave empty goes too.</summary>
        private void RemoveEntries(HashSet<string> ids)
        {
            SortedSet<int> gone = [];
            foreach (Entry entry in Entries().Where(e => ids.Contains(e.Id)))
            {
                for (int i = entry.Start; i <= entry.End; i++)
                {
                    gone.Add(i);
                }
            }
            List<(int Start, int End)> emptied = [];
            foreach (string name in new[] { "NestedProjects", "ProjectConfigurationPlatforms" })
            {
                if (Section(name) is not { } section)
                {
                    continue;
                }
                List<int> naming = [.. Enumerable.Range(section.Start + 1, section.End - section.Start - 1).Where(i => Names(lines[i]))];
                gone.UnionWith(naming);
                if (naming.Count > 0 && naming.Count == section.End - section.Start - 1)
                {
                    emptied.Add(section);
                }
            }
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains("ProjectSection(ProjectDependencies)", StringComparison.Ordinal))
                {
                    for (i++; i < lines.Count && lines[i].Trim() != "EndProjectSection"; i++)
                    {
                        if (PairLine.Match(lines[i]) is { Success: true } pair && ids.Contains(pair.Groups["left"].Value))
                        {
                            gone.Add(i);
                        }
                    }
                }
            }
            foreach ((int start, int end) in emptied)
            {
                gone.Add(start);
                gone.Add(end);
            }
            foreach (int line in gone.Reverse())
            {
                lines.RemoveAt(line);
            }

            bool Names(string line) =>
                PairLine.Match(line) is { Success: true } pair ? ids.Contains(pair.Groups["left"].Value) || ids.Contains(pair.Groups["right"].Value)
                : line.TrimStart() is { Length: > 38 } entry && entry[0] == '{' && entry[38] == '.' && ids.Contains(entry[..38]);
        }

        private static string NewId() => Guid.NewGuid().ToString("B").ToUpperInvariant();

        /// <summary>The project type Visual Studio and dotnet sln give a project file of its kind.</summary>
        private static string TypeOf(string project) => Path.GetExtension(project).ToLowerInvariant() switch
        {
            ".fsproj" => "{F2A71F9B-5D33-465A-A702-920D77279786}",
            ".vbproj" => "{F184B08F-C81C-45F6-A57F-5ABD9991F28F}",
            _ => "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}",
        };
    }

    /// <summary>A .slnx, changed through <see cref="XmlText"/>: an element added is indented as its neighbours are, one
    /// taken out goes with its line, and every other character stays as it was read.</summary>
    private sealed class SlnxText : SolutionText
    {
        private readonly XmlText xml;

        public SlnxText(string path) : base(path) => xml = new XmlText(Text);

        private XElement Solution => xml.Root;

        protected override string Written() => xml.Text;

        /// <summary>The Folder elements, each with its path in the solution as the reader makes it ("/src/tools/").</summary>
        private IEnumerable<(XElement Element, string Path)> Folders() =>
            Solution.Elements().Where(e => e.Name.LocalName == "Folder" && e.Attribute("Name") is not null)
                .Select(e => (e, PathOf(e.Attribute("Name")!.Value)));

        private static string PathOf(string name) => $"/{string.Join('/', name.Split('/', StringSplitOptions.RemoveEmptyEntries))}/";

        /// <summary>Whether a folder is in the solution: written, or implied by a folder written inside it.</summary>
        private bool Has(string folder) => Folders().Any(f => f.Path.StartsWith(folder, StringComparison.Ordinal));

        private static string ParentOf(string folder) => folder[..(folder.TrimEnd('/').LastIndexOf('/') + 1)];

        private bool Taken(string path, string? except = null) =>
            Folders().Any(f => f.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase) && !(except is not null && f.Path.StartsWith(except, StringComparison.Ordinal)));

        public override string? AddFolder(string? parent, string name)
        {
            if (parent is not null && !Has(parent))
            {
                return NoFolder(parent);
            }
            string path = $"{parent ?? "/"}{name}/";
            if (Taken(path))
            {
                return $"{name} is already there";
            }
            Element(path);
            return null;
        }

        public override string? RenameFolder(string folder, string name)
        {
            if (!Has(folder))
            {
                return NoFolder(folder);
            }
            string renamed = $"{ParentOf(folder)}{name}/";
            if (Taken(renamed, except: folder))
            {
                return $"{name} is already there";
            }
            // A .slnx names each folder by its whole path, so the folders inside it are renamed too.
            xml.Apply([.. Folders().Where(f => f.Path.StartsWith(folder, StringComparison.Ordinal))
                .Select(f => (xml.ValueOf(f.Element, "Name")!.Value, renamed + f.Path[folder.Length..]))
                .Select(v => (v.Value.Start, v.Value.Length, XmlText.Quoted(v.Item2, xml.Text[v.Value.Start - 1])))]);
            return null;
        }

        public override string? RemoveFolder(string folder)
        {
            if (!Folders().Any(f => f.Path.StartsWith(folder, StringComparison.Ordinal)))
            {
                return NoFolder(folder);
            }
            while (Folders().FirstOrDefault(f => f.Path.StartsWith(folder, StringComparison.Ordinal)).Element is { } gone)
            {
                xml.Remove(gone);
            }
            return null;
        }

        public override string? AddProject(string? folder, string project)
        {
            if (folder is not null && !Has(folder))
            {
                return NoFolder(folder);
            }
            if (Projects().Any(p => p.Path == project))
            {
                return $"{NameOf(project)} is already in the solution";
            }
            XElement into = folder is null ? Solution : Element(folder);
            string added = $"<Project Path=\"{XmlText.Quoted(Relative(project).Replace('\\', '/'), '"')}\" />";
            if (into.Elements().LastOrDefault(e => e.Name.LocalName == "Project") is { } last)
            {
                xml.After(last, added);
            }
            else
            {
                xml.Append(into, added);
            }
            return null;
        }

        public override bool Move(Func<string, string> moved)
        {
            List<(int Start, int Length, string With)> edits = [];
            // A project's or file's Path, and a build dependency's Project.
            foreach (XElement element in Solution.Descendants())
            {
                string? named = element.Name.LocalName switch
                {
                    "Project" or "File" => "Path",
                    "BuildDependency" => "Project",
                    _ => null,
                };
                if (named is null || element.Attribute(named) is not { } attribute || xml.ValueOf(element, named) is not { } span)
                {
                    continue;
                }
                string was = Resolve(attribute.Value);
                if (moved(was) is var now && now != was)
                {
                    edits.Add((span.Start, span.Length, XmlText.Quoted(Followed(attribute.Value, now, '/'), xml.Text[span.Start - 1])));
                }
            }
            if (edits.Count == 0)
            {
                return false;
            }
            xml.Apply(edits);
            return true;
        }

        public override string? RemoveProject(string project)
        {
            if (Projects().FirstOrDefault(p => p.Path == project).Element is not { } removed)
            {
                return $"{NameOf(project)} is not in the solution";
            }
            Remove(removed);
            return null;
        }

        public override string? AddFile(string folder, string path)
        {
            if (!Has(folder))
            {
                return NoFolder(folder);
            }
            XElement into = Element(folder);
            if (Files(into).Any(f => f.Path == path))
            {
                return $"{Path.GetFileName(path)} is already in {folder.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1]}";
            }
            // Files come before projects in a folder, as Visual Studio writes them.
            string added = $"<File Path=\"{XmlText.Quoted(Relative(path).Replace('\\', '/'), '"')}\" />";
            if (into.Elements().LastOrDefault(e => e.Name.LocalName == "File") is { } last)
            {
                xml.After(last, added);
            }
            else if (into.Elements().FirstOrDefault(e => e.Name.LocalName == "Project") is { } first)
            {
                xml.Before(first, added);
            }
            else
            {
                xml.Append(into, added);
            }
            return null;
        }

        public override string? RemoveFile(string folder, string path)
        {
            XElement? removed = Folders().Where(f => f.Path == folder).SelectMany(f => Files(f.Element)).FirstOrDefault(f => f.Path == path).Element;
            if (removed is null)
            {
                return $"{Path.GetFileName(path)} is not in {folder.Trim('/')}";
            }
            Remove(removed);
            return null;
        }

        private IEnumerable<(XElement Element, string Path)> Projects() =>
            Solution.Descendants().Where(e => e.Name.LocalName == "Project" && e.Attribute("Path") is not null).Select(e => (e, Resolve(e.Attribute("Path")!.Value)));

        private IEnumerable<(XElement Element, string Path)> Files(XElement folder) =>
            folder.Elements().Where(e => e.Name.LocalName == "File" && e.Attribute("Path") is not null).Select(e => (e, Resolve(e.Attribute("Path")!.Value)));

        /// <summary>A folder's own element, written now if the folder was only implied: among the folders in order of their
        /// paths, before the solution's own projects, as Visual Studio writes them. As the text reads once it is written.</summary>
        private XElement Element(string folder)
        {
            if (Folders().FirstOrDefault(f => f.Path == folder).Element is { } written)
            {
                return written;
            }
            string made = $"<Folder Name=\"{XmlText.Quoted(folder, '"')}\" />";
            if (Folders().FirstOrDefault(f => string.Compare(f.Path, folder, StringComparison.OrdinalIgnoreCase) > 0).Element is { } next)
            {
                xml.Before(next, made);
            }
            else if (Folders().LastOrDefault().Element is { } last)
            {
                xml.After(last, made);
            }
            else if (Solution.Elements().FirstOrDefault(e => e.Name.LocalName == "Project") is { } project)
            {
                xml.Before(project, made);
            }
            else
            {
                xml.Append(Solution, made);
            }
            return Folders().First(f => f.Path == folder).Element;
        }

        /// <summary>Takes an element out with its line; a folder left with nothing in it closes itself.</summary>
        private void Remove(XElement element)
        {
            string? holder = element.Parent is { } parent && parent != Solution && parent.Attribute("Name") is { } name ? PathOf(name.Value) : null;
            xml.Remove(element);
            if (holder is not null && Folders().FirstOrDefault(f => f.Path == holder).Element is { } folder
                && !folder.Elements().Any() && folder.Nodes().All(n => n is XText { Value: var text } && string.IsNullOrWhiteSpace(text)) && xml.InnerOf(folder) is not null)
            {
                xml.Replace(folder, xml.StartTag(folder) + " />");
            }
        }
    }
}
