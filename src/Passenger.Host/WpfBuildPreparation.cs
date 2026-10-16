using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using System.Xml.Linq;

namespace Passenger.Host;

/// <summary>Checks the untouched project graph before a global CustomBefore import is composed. The first supported
/// shape has the same original import in every framework and in ordinary/design-time builds, so even WPF's temporary
/// compilation project inherits exactly that import. Other graphs keep their original build and require a restart for
/// future-view XAML updates; no user project or import is rewritten.</summary>
internal static class WpfBuildPreparation
{
    private const string Hook = "CustomBeforeMicrosoftCommonTargets";

    public static WpfBuildPrepared Run(WpfBuildParams asked, CancellationToken cancel)
    {
        Dictionary<string, Stamp> inputs = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string? before = null;
        string? candidateBefore = null;
        bool first = true;
        bool wpf = false;
        string? unsupported = null;
        HashSet<string> discovered = new(inputs.Comparer);
        try
        {
            // The first pass discovers resolved SDK extension locations. Re-evaluation after those locations have been
            // stamped cannot retain an import graph from just before a previously missing wildcard file was created.
            foreach (string mode in new[] { "ordinary", "watch", "design-time", "ordinary", "watch", "design-time" })
            {
                Dictionary<string, string> properties = mode == "design-time" ? new(DesignTimeBuilds.Defaults, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
                foreach ((string name, string value) in mode == "design-time" ? asked.DesignTimeProperties ?? asked.Properties : asked.Properties)
                {
                    properties[name] = value;
                }
                if (mode == "watch")
                {
                    properties["DotNetWatchBuild"] = "true";
                }
                if (properties.ContainsKey(Hook))
                {
                    return Refused("a solution global already overrides CustomBeforeMicrosoftCommonTargets");
                }
                using ProjectCollection collection = new(properties, loggers: null, ToolsetDefinitionLocations.Default);
                EvaluationContext context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
                Queue<string> queue = new(asked.Projects);
                HashSet<string> seen = new(inputs.Comparer);
                while (queue.TryDequeue(out string? path))
                {
                    cancel.ThrowIfCancellationRequested();
                    path = Path.GetFullPath(path);
                    if (!seen.Add(path))
                    {
                        continue;
                    }
                    // Named missing candidates matter too: a new closer Directory.Build file can replace the import
                    // chain without changing any previously loaded XML. Compiler temporary files are not discovery inputs.
                    for (string? directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
                    {
                        foreach (string name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" })
                        {
                            string candidate = Path.Combine(directory, name);
                            if (name == "global.json")
                            {
                                inputs.TryAdd(candidate, Stamps.File(candidate));
                            }
                            else
                            {
                                Discover(candidate, path);
                            }
                        }
                    }
                    Discover(path, path);
                    Project outer = Load(path, null);
                    List<Project> projects = [outer];
                    if (outer.GetPropertyValue("TargetFramework").Length == 0)
                    {
                        projects.AddRange(outer.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Distinct(StringComparer.OrdinalIgnoreCase).Select(framework => Load(path, framework)));
                    }
                    foreach (Project project in projects)
                    {
                        wpf |= project.GetPropertyValue("UseWPF").Equals("true", StringComparison.OrdinalIgnoreCase);
                        string sdk = project.GetPropertyValue("MSBuildToolsPath");
                        foreach (Microsoft.Build.Construction.ProjectRootElement xml in project.Imports.Select(i => i.ImportedProject).Prepend(project.Xml).Distinct())
                        {
                            inputs.TryAdd(xml.FullPath, Stamps.File(xml.FullPath));
                            if (!ToolsetImport(xml.FullPath, sdk))
                            {
                                Discover(xml.FullPath, path);
                            }
                            else
                            {
                                DiscoverToolsetImports(project, xml);
                            }
                            if (project.ExpandString(xml.TreatAsLocalProperty).Split(';', StringSplitOptions.TrimEntries).Contains(Hook, StringComparer.OrdinalIgnoreCase))
                            {
                                Reject($"{Path.GetFileName(path)} treats {Hook} as local");
                            }
                            if (!ToolsetImport(xml.FullPath, sdk) && xml.Targets.SelectMany(t => t.Tasks).Any(t => t.Name == "MSBuild"))
                            {
                                Reject($"{Path.GetFileName(xml.FullPath)} starts a custom nested MSBuild graph");
                            }
                            if (!ToolsetImport(xml.FullPath, sdk) && xml.RawXml.Contains("$(" + Hook + ")", StringComparison.OrdinalIgnoreCase))
                            {
                                Reject($"{Path.GetFileName(xml.FullPath)} reads the CustomBefore import property that a composed global would replace");
                            }
                            if (!ToolsetImport(xml.FullPath, sdk) && xml.Properties.Count(p => p.Name.Equals(Hook, StringComparison.OrdinalIgnoreCase)) > (xml == project.Xml ? 1 : 0))
                            {
                                Reject($"{Path.GetFileName(xml.FullPath)} changes the CustomBefore import property during evaluation");
                            }
                        }
                        foreach (string name in new[] { "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath", "DirectoryPackagesPropsPath" })
                        {
                            if (project.GetPropertyValue(name) is { Length: > 0 } explicitImport)
                            {
                                Discover(FullPath(project, explicitImport), path);
                            }
                        }
                        string extensions = project.GetPropertyValue("MSBuildProjectExtensionsPath");
                        if (extensions.Length > 0)
                        {
                            foreach (string suffix in new[] { ".nuget.g.props", ".nuget.g.targets" })
                            {
                                string generated = Path.Combine(FullPath(project, extensions), Path.GetFileName(path) + suffix);
                                inputs.TryAdd(generated, Stamps.File(generated));
                            }
                        }
                        string value = project.GetPropertyValue(Hook);
                        if (project.GetProperty(Hook)?.Xml is { } definition && !ToolsetImport(definition.ContainingProject.FullPath, sdk))
                        {
                            string literal = definition.Value.Replace("$(MSBuildThisFileDirectory)", string.Empty, StringComparison.OrdinalIgnoreCase)
                                .Replace("$(MSBuildProjectDirectory)", string.Empty, StringComparison.OrdinalIgnoreCase);
                            if (definition.Condition.Length > 0 || definition.Parent is Microsoft.Build.Construction.ProjectPropertyGroupElement { Condition.Length: > 0 }
                                || literal.Contains("$(", StringComparison.Ordinal) || literal.Contains("@(", StringComparison.Ordinal) || literal.Contains("%(", StringComparison.Ordinal))
                            {
                                Reject($"{Path.GetFileName(path)} selects its CustomBefore import through a build-dependent property or condition");
                            }
                        }
                        string[] imports = [.. project.Imports.Where(i => i.ImportingElement.Project.Trim() == "$(" + Hook + ")")
                            .Select(i => i.ImportedProject.FullPath).Distinct(inputs.Comparer)];
                        if (imports.Length > 1)
                        {
                            Reject($"{Path.GetFileName(path)} imports CustomBefore more than once");
                        }
                        string? imported = imports.FirstOrDefault();
                        // The outer cross-targeting project never imports Microsoft.Common.CurrentVersion.targets;
                        // only its inner framework builds compile WPF and must preserve the shared import identity.
                        bool crossTargeting = project.GetPropertyValue("IsCrossTargetingBuild").Equals("true", StringComparison.OrdinalIgnoreCase);
                        string? candidate = null;
                        if (value.Length > 0)
                        {
                            // Existing resolved imports give MSBuild's full path. An absent supported import must also
                            // have an absolute path, so its later creation invalidates this preparation.
                            if (!Path.IsPathFullyQualified(value))
                            {
                                Reject($"{Path.GetFileName(path)} has a relative {Hook} import");
                            }
                            candidate = FullPath(project, value);
                            inputs.TryAdd(candidate, Stamps.File(candidate));
                            if (!File.Exists(candidate))
                            {
                                string? declared = project.GetProperty(Hook)?.Xml?.ContainingProject.FullPath;
                                if (declared is null || !ToolsetImport(declared, sdk))
                                {
                                    Reject($"{Path.GetFileName(path)} names a custom CustomBefore import that does not exist yet");
                                }
                            }
                            if (!crossTargeting && File.Exists(candidate) && imported is null)
                            {
                                Reject($"{Path.GetFileName(path)} has an unrecognized conditional {Hook} import");
                            }
                            else if (!crossTargeting && imported is not null && !inputs.Comparer.Equals(candidate, imported))
                            {
                                Reject($"{Path.GetFileName(path)} changes the CustomBefore import path after importing it");
                            }
                        }
                        if (imported is not null && project.Imports.Where(i => string.Equals(i.ImportedProject.FullPath, imported, StringComparison.OrdinalIgnoreCase))
                            .Select(i => i.ImportedProject).Any(xml => xml.Properties.Any(p => p.Name.Equals(Hook, StringComparison.OrdinalIgnoreCase))))
                        {
                            Reject($"{Path.GetFileName(imported)} assigns the CustomBefore import property during its own evaluation");
                        }
                        if (!crossTargeting && first)
                        {
                            before = imported;
                            candidateBefore = candidate;
                            first = false;
                        }
                        else if (!crossTargeting && (!string.Equals(before, imported, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                            || !string.Equals(candidateBefore, candidate, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                        {
                            Reject("the project graph or its frameworks use different CustomBefore imports");
                        }
                        foreach (ProjectItem reference in project.GetItems("ProjectReference"))
                        {
                            if (new[] { "AdditionalProperties", "GlobalPropertiesToRemove", "SetConfiguration", "SetPlatform", "SetTargetFramework" }
                                .Any(name => reference.GetMetadataValue(name).Length > 0))
                            {
                                Reject($"{Path.GetFileName(path)} changes a referenced project's global properties");
                            }
                            queue.Enqueue(reference.GetMetadataValue("FullPath"));
                        }
                    }
                    collection.UnloadAllProjects();
                }

                Project Load(string path, string? framework)
                {
                    Dictionary<string, string> global = new(properties, StringComparer.OrdinalIgnoreCase);
                    if (framework is not null)
                    {
                        global["TargetFramework"] = framework;
                    }
                    return WindowsTargeting.Load(given => Project.FromFile(path, new ProjectOptions
                    {
                        GlobalProperties = given,
                        ProjectCollection = collection,
                        EvaluationContext = context,
                        LoadSettings = Evaluation.Settings,
                    }), global, collection);
                }
            }
            return unsupported is { } reason ? Refused(reason) : new WpfBuildPrepared(before, null, [.. inputs.Values], wpf);
        }
        catch (Exception error) when (error is InvalidProjectFileException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Xml.XmlException)
        {
            // Unlike a fully inspected unsupported graph, an interrupted/failed evaluation has no complete set of
            // invalidation inputs. Retry it on the next build instead of retaining a transient refusal indefinitely.
            return new WpfBuildPrepared(null, $"WPF future-view Hot Reload needs a restart: {error.Message}", [], wpf);
        }

        WpfBuildPrepared Refused(string reason) => new(null, $"WPF future-view Hot Reload needs a restart: {reason}", [.. inputs.Values]);
        void Reject(string reason) => unsupported ??= reason;

        void Discover(string file, string project)
        {
            inputs.TryAdd(file, Stamps.File(file));
            if (!discovered.Add(file + "\n" + project) || !File.Exists(file))
            {
                return;
            }
            XDocument xml = XDocument.Load(file);
            foreach (XElement definition in xml.Descendants().Where(e => e.Name.LocalName.Equals(Hook, StringComparison.OrdinalIgnoreCase)))
            {
                string literal = definition.Value.Replace("$(MSBuildThisFileDirectory)", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Replace("$(MSBuildProjectDirectory)", string.Empty, StringComparison.OrdinalIgnoreCase);
                if (((string?)definition.Attribute("Condition")) is { Length: > 0 } || ((string?)definition.Parent?.Attribute("Condition")) is { Length: > 0 }
                    || literal.Contains("$(", StringComparison.Ordinal) || literal.Contains("@(", StringComparison.Ordinal) || literal.Contains("%(", StringComparison.Ordinal))
                {
                    Reject($"{Path.GetFileName(file)} selects its CustomBefore import through a build-dependent property or condition");
                }
            }
            // User evaluation can also redirect an SDK import indirectly (for example MSBuildUserExtensionsPath in
            // a file-dependent PropertyGroup). The first supported shape never caches those undiscovered dependencies.
            if (xml.Descendants().SelectMany(e => e.Attributes()).Any(a => a.Name.LocalName == "Condition"
                    && (a.Value.Contains("Exists", StringComparison.OrdinalIgnoreCase) || a.Value.Contains("$([", StringComparison.Ordinal)))
                || xml.Descendants().Any(e => e.Parent?.Name.LocalName == "PropertyGroup" && e.Value.Contains("$([", StringComparison.Ordinal)))
            {
                Reject($"{Path.GetFileName(file)} has custom file-dependent conditions or property functions outside the supported import discovery shape");
            }
            foreach (XElement import in xml.Descendants().Where(e => e.Name.LocalName == "Import"))
            {
                string value = ((string?)import.Attribute("Project") ?? string.Empty)
                    .Replace("$(MSBuildThisFileDirectory)", Path.GetDirectoryName(file) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    .Replace("$(MSBuildProjectDirectory)", Path.GetDirectoryName(project), StringComparison.OrdinalIgnoreCase);
                if (value.Length == 0 || value.IndexOfAny(['*', '?', ';']) >= 0 || value.Contains("$(", StringComparison.Ordinal)
                    || value.Contains("@(", StringComparison.Ordinal) || value.Contains("%(", StringComparison.Ordinal) || import.Attribute("Sdk") is not null)
                {
                    Reject($"{Path.GetFileName(file)} selects a custom import whose discovery cannot be preserved by the shared build hook");
                    continue;
                }
                string candidate = Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(file)!);
                Discover(candidate, project);
            }
        }

        void DiscoverToolsetImports(Project project, Microsoft.Build.Construction.ProjectRootElement xml)
        {
            foreach (Microsoft.Build.Construction.ProjectImportElement import in xml.Imports)
            {
                if (import.Sdk.Length > 0)
                {
                    continue;
                }
                string expanded = project.ExpandString(import.Project.Replace("$(MSBuildThisFileDirectory)", xml.DirectoryPath + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
                foreach (string value in expanded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string candidate = Path.GetFullPath(ProjectCollection.Unescape(value).Replace('\\', Path.DirectorySeparatorChar), xml.DirectoryPath);
                    if (candidate.IndexOfAny(['*', '?']) < 0)
                    {
                        inputs.TryAdd(candidate, Stamps.File(candidate));
                        continue;
                    }
                    string directory = Path.GetDirectoryName(candidate)!;
                    if (directory.IndexOfAny(['*', '?']) >= 0)
                    {
                        Reject($"{Path.GetFileName(xml.FullPath)} searches an unsupported nested import wildcard");
                        continue;
                    }
                    string pattern = Path.GetFileName(candidate);
                    // Match only the import wildcard, so WPF's unrelated *_wpftmp.csproj churn in obj is irrelevant.
                    inputs.TryAdd(directory + "\n" + pattern, Stamps.Folder(directory, pattern));
                }
            }
        }
    }

    private static string FullPath(Project project, string value) => project.CreateProjectInstance().AddItem("_PassengerImport", value, null).GetMetadataValue("FullPath");

    private static bool ToolsetImport(string path, string sdk)
    {
        string? dotnet = Path.GetDirectoryName(Path.GetDirectoryName(sdk));
        return Within(path, sdk) || dotnet is not null && (Within(path, Path.Combine(dotnet, "sdk-manifests")) || Within(path, Path.Combine(dotnet, "packs")));
    }

    private static bool Within(string path, string directory) => directory.Length > 0 && path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
