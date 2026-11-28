using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Templates;

/// <summary>The kinds of item a folder takes, one rule for New Item and Add's New: the item templates of its project's
/// language and those of none, dotnet new's beside Passenger's own; those its project's kind puts first (<see
/// cref="FirstOf"/>) and Passenger's own are the common ones Add's New offers, New Item listing them first and the rest
/// after. What makes one from dotnet new's templates is here too, so both make it alike (<see cref="RunAsync"/>).</summary>
public static class ItemKinds
{
    /// <summary>An option New Item and Add's New give a template themselves rather than show among its options.</summary>
    public const string NamespaceOption = "namespace";

    /// <summary>The short names of the item templates a project's kind puts first, in their order: a Blazor project's
    /// Razor component and page (one whose code runs in the browser, or holding a component), another web project's
    /// controllers, a MAUI project's XAML page and view, an Avalonia project's window and user control, a test project's
    /// MSTest or NUnit test class; none for any other, or outside a project.</summary>
    public static IReadOnlyList<string> FirstOf(ProjectModel? project) => project switch
    {
        null => [],
        { IsWeb: true } web when web.RunsInBrowser || web.Evaluated?.Items.Any(i => i.Path.EndsWith(RazorFiles.Component, StringComparison.OrdinalIgnoreCase)) == true =>
            ["razorcomponent", "page"],
        { IsWeb: true } => ["apicontroller", "mvccontroller"],
        _ when Takes(project, "Microsoft.Maui.Controls") || string.Equals(project.Property(MSBuildNames.UseMaui), "true", StringComparison.OrdinalIgnoreCase) =>
            ["maui-page-xaml", "maui-view-xaml"],
        _ when Takes(project, "Avalonia") => ["avalonia.window", "avalonia.usercontrol"],
        _ when project.Packages.Any(p => p.Name.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)) => ["mstest-class"],
        _ when Takes(project, "NUnit") => ["nunit-test"],
        _ => [],
    };

    /// <summary>Whether a template makes something in a folder of this language (C#, F#, VB; null outside a project): one
    /// of the language's, or of none.</summary>
    public static bool Fits(ProjectTemplate template, string? language) =>
        template.Languages.Count == 0 || language is not null && template.Languages.Contains(language);

    /// <summary>Every template New Item lists for a folder, of a read of dotnet new's (Passenger's own alone given null):
    /// its project kind's own first, then Passenger's own, then the rest by name.</summary>
    public static IReadOnlyList<ProjectTemplate> Listed(ItemTarget target, TemplateCatalog? read)
    {
        List<ProjectTemplate> theirs = [.. (read?.Items ?? []).Where(t => Fits(t, target.Language))];
        return [.. FirstAmong(target, theirs), .. OwnTemplates.All.Where(t => Fits(t, target.Language)),
            .. theirs.Where(t => FirstPlace(target, t) == int.MaxValue).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The common kinds Add's New offers a folder, in groups: Passenger's own C# types where the folder's code is
    /// C#, its text file, then its project kind's own of a read of dotnet new's (none until one is read).</summary>
    public static IReadOnlyList<IReadOnlyList<ProjectTemplate>> Common(ItemTarget target, TemplateCatalog? read)
    {
        ProjectTemplate[] own = [.. OwnTemplates.All.Where(t => Fits(t, target.Language))];
        IReadOnlyList<ProjectTemplate>[] groups =
        [
            [.. own.Where(t => t.Own!.DeclaresType)],
            [.. own.Where(t => !t.Own!.DeclaresType)],
            FirstAmong(target, [.. (read?.Items ?? []).Where(t => Fits(t, target.Language))]),
        ];
        return [.. groups.Where(g => g.Count > 0)];
    }

    /// <summary>Whether dotnet new is given the folder's namespace for a template: it takes one.</summary>
    public static bool TakesNamespace(TemplateVariant variant) =>
        variant.Options.Any(o => o.Name.Equals(NamespaceOption, StringComparison.OrdinalIgnoreCase));

    /// <summary>dotnet new's arguments for an item: the template, the name (where it takes one), where it goes (none in the
    /// line a dialog shows), the namespace (where given), the language, and the options given.</summary>
    public static List<string> Arguments(ProjectTemplate template, TemplateVariant variant, string name, string? output, string? space, IEnumerable<string> options)
    {
        List<string> arguments = [template.ShortName];
        if (template.TakesName)
        {
            arguments.AddRange(["--name", name]);
        }
        if (output is not null)
        {
            arguments.AddRange(["--output", output, "--no-update-check"]);
        }
        if (space is not null)
        {
            arguments.AddRange(["--namespace", space]);
        }
        if (variant.Language is { } language)
        {
            arguments.AddRange(["--language", language]);
        }
        arguments.AddRange(options);
        return arguments;
    }

    /// <summary>dotnet new run into a folder with these arguments (<see cref="Arguments"/>, its output given): what it made
    /// by the name and template given, else why not, anything it left taken away, and so too once stopped.</summary>
    public static async Task<(MadeItem? Made, string? WhyNot)> RunAsync(ProjectTemplate template, string folder, string name, IReadOnlyList<string> arguments,
        CancellationToken cancel)
    {
        HashSet<string> before = await BackgroundWork.Run(() => Entries(folder));
        try
        {
            Task<ChildResult> run = ChildProcesses.RunAsync(ProjectTemplates.NewCommand(arguments, folder), limit: TimeSpan.FromMinutes(5), cancel: cancel);
            BackgroundWork.Track(run);
            ChildResult made = await run;
            (List<string> files, List<string> folders) = await BackgroundWork.Run(() => MadeSince(before, Entries(folder)));
            if (!made.Succeeded || files.Count + folders.Count == 0)
            {
                await BackgroundWork.Run(() => TakeAway(files, folders));
                return (null, made.Succeeded ? $"dotnet new {template.ShortName} made nothing in {folder}" : $"{name} could not be created: {made.Said}");
            }
            return (new MadeItem(name, template.Name, files, folders, null), null);
        }
        catch (OperationCanceledException)
        {
            (List<string> files, List<string> folders) = await BackgroundWork.Run(() => MadeSince(before, Entries(folder)));
            await BackgroundWork.Run(() => TakeAway(files, folders));
            throw;
        }
    }

    /// <summary>The templates among these the project's kind puts first, in its order.</summary>
    private static IReadOnlyList<ProjectTemplate> FirstAmong(ItemTarget target, IReadOnlyList<ProjectTemplate> theirs) =>
        [.. theirs.Where(t => FirstPlace(target, t) < int.MaxValue).OrderBy(t => FirstPlace(target, t))];

    private static int FirstPlace(ItemTarget target, ProjectTemplate template) =>
        target.First.ToList().FindIndex(f => template.ShortNames.Contains(f, StringComparer.OrdinalIgnoreCase)) is var at and >= 0 ? at : int.MaxValue;

    private static bool Takes(ProjectModel project, string package) =>
        project.Packages.Any(p => p.Name.Equals(package, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every file and folder under a folder, walking only into those a project's wildcards enter (bin, obj,
    /// node_modules and hidden folders listed, not entered).</summary>
    private static HashSet<string> Entries(string folder)
    {
        HashSet<string> entries = new(PathNames.Comparer);
        foreach (string each in PathNames.FoldersUnder(folder, ProjectFolders.Enters))
        {
            try
            {
                entries.UnionWith(Directory.EnumerateFileSystemEntries(each));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Gone or shut: nothing in it is dotnet new's.
            }
        }
        return entries;
    }

    /// <summary>What came since the first listing: the files outside a folder that came, and the topmost folders that came.</summary>
    private static (List<string> Files, List<string> Folders) MadeSince(HashSet<string> before, HashSet<string> after)
    {
        List<string> came = [.. after.Where(e => !before.Contains(e))];
        List<string> folders = [.. came.Where(Directory.Exists).Where(f => !came.Any(o => PathNames.IsUnder(f, o))).Select(PathNames.Canonical)];
        List<string> files = [.. came.Where(File.Exists).Where(f => !came.Any(o => PathNames.IsUnder(f, o))).Select(PathNames.Canonical)];
        return (files, folders);
    }

    /// <summary>Takes away what dotnet new made before it failed or was stopped.</summary>
    private static void TakeAway(List<string> files, List<string> folders)
    {
        foreach (string path in files.Concat(folders))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                IdeErrors.Write($"A new item that could not be made left {path}: {error.Message}");
            }
        }
    }
}
