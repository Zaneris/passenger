using Avalonia.Controls;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>C# script templates for Godot's Attach Script dialog in the project's style (<see cref="GodotScriptTemplates"/>):
/// written into its script_templates folder when the person asks, for the base classes they check, one command of Undo;
/// where Passenger's templates there no longer fit the project's style (its .editorconfig, root namespace or C# version
/// changed), offered once to write again, never written unasked; and left out of the game's build again where Godot wrote
/// its project anew.</summary>
public partial class MainWindow
{
    /// <summary>The style the folder's templates were last written in or offered for (<see cref="WorkspaceState.GodotTemplatesStyle"/>).</summary>
    private string? godotTemplatesStyle;

    private void AddGodotTemplates()
    {
        actions!.Add(new IdeAction(AppActions.GodotScriptTemplates, "Write C# Script Templates for Godot…", "Godot", _ => IdeErrors.Observe(WriteGodotTemplatesAsync()))
        {
            WhyNot = _ => Folder is null ? Refusals.NoFolder : GodotCSharpProject() is null ? Refusals.NoGodotProject : null,
            Menus = [new MenuPlace("Tools", 0, 9)],
            Words = ["Godot", "script", "template", "attach", "C#", "namespace", "style"],
            Works = AppActions.GodotScriptTemplates,
        });
    }

    /// <summary>The folder's Godot game's C# project, whose style the templates take.</summary>
    private ProjectModel? GodotCSharpProject() => Projects.FirstOrDefault(p => p.GodotFolder is not null);

    /// <summary>The Godot project's folder and the style a C# file of its project is written in, read off the window's thread.</summary>
    private async Task<(string Folder, GodotScriptStyle Style)?> GodotTemplateStyleAsync()
    {
        if (GodotCSharpProject() is not { GodotFolder: { } folder } project)
        {
            return null;
        }
        string script = Path.Combine(project.Folder, "Script.cs");
        string root = project.Property(Passenger.Host.CarriedProperties.RootNamespace) is { Length: > 0 } named ? named : Path.GetFileNameWithoutExtension(project.Path);
        bool fileScoped = Editors.ParseOptions(script).LanguageVersion >= LanguageVersion.CSharp10;
        EditorConfig config = await BackgroundWork.Run(() => EditorConfig.For(script));
        return (folder, GodotScriptTemplates.StyleOf(name => config[name], root, fileScoped));
    }

    private async Task WriteGodotTemplatesAsync()
    {
        UiThread.Check();
        if (work.Begin(AppActions.GodotScriptTemplates, "Reading the project's style", stoppable: false) is not { } working)
        {
            return;
        }
        using (working)
        {
            (bool done, string said) = await WriteGodotTemplatesAsync(working);
            Told(working, done, said);
        }
    }

    private async Task<(bool Done, string Said)> WriteGodotTemplatesAsync(Working working)
    {
        if (await GodotTemplateStyleAsync() is not ({ } folder, { } style) || GodotCSharpProject() is not { } project)
        {
            return (false, Refusals.NoGodotProject);
        }
        List<CheckBox> boxes = [.. GodotScriptTemplates.Bases.Select(b => new CheckBox { Content = b, IsChecked = true })];
        StackPanel checks = new() { Spacing = 4 };
        checks.Children.AddRange(boxes);
        string shape = style.Namespace is not { } space ? "in no namespace" : style.FileScoped ? $"in the file-scoped namespace {space}" : $"in the namespace {space}";
        working.Say("Asking which base classes to write templates for");
        if (!await ConfirmAsync("Write C# Script Templates for Godot?",
            $"Godot's Attach Script dialog will offer \"{GodotScriptTemplates.Name}\" for each base class checked: a partial class {shape}, indented and braced as the "
            + $"project's .editorconfig says, written into {GodotScriptTemplates.Folder} in the project, which {Path.GetFileName(project.Path)} then leaves out of its build. "
            + "Pick it once there, and Godot keeps it for that base class.",
            "Write Templates", checks))
        {
            return (false, "The C# script templates were left as they were");
        }
        List<string> bases = [.. boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content!)];
        if (bases.Count == 0)
        {
            return (false, "No base class was checked, so no C# script template was written");
        }
        List<(string Path, string Text)> files = [.. bases.Select(b => (GodotScriptTemplates.PathOf(folder, b), GodotScriptTemplates.Text(b, style)))];
        string? refused = await ChangeFileAsync("Write C# Script Templates for Godot", [.. files.Select(f => f.Path), project.Path], () =>
        {
            foreach ((string path, string text) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _ = File.Exists(path) && FileText.Load(path) is { } now ? FileText.SaveOver(path, now, text) : FileText.SaveNew(path, text);
            }
            // The templates are C# of no class Godot's names fill in, which the game's build would take for its own.
            return ProjectEdits.RemoveCompiled(project.Path, GodotScriptTemplates.Excluded);
        });
        if (refused is not null)
        {
            return (false, $"The C# script templates could not be written: {refused}");
        }
        godotTemplatesStyle = Stamp(style);
        KeepWorkspace();
        return (true, $"Godot's Attach Script dialog offers {GodotScriptTemplates.Name} for {Plural.Listed(bases)}, {shape}: pick it there once, and Godot keeps it");
    }

    /// <summary>Where Passenger's templates in the Godot project no longer fit the project's style, and that style was not
    /// offered before: the status bar offers to write them again, once.</summary>
    private async Task OfferGodotTemplatesAsync()
    {
        UiThread.Check();
        if (!keepsWorkspace || await GodotTemplateStyleAsync() is not ({ } folder, { } style) || Stamp(style) == godotTemplatesStyle)
        {
            return;
        }
        bool stale = await BackgroundWork.Run(() => GodotScriptTemplates.Bases.Any(b => GodotScriptTemplates.PathOf(folder, b) is var path && File.Exists(path)
            && FileText.Load(path)?.Text is { } text && GodotScriptTemplates.IsPassengers(text) && text.ReplaceLineEndings("\n") != GodotScriptTemplates.Text(b, style)));
        if (!stale || Stamp(style) == godotTemplatesStyle)
        {
            return;
        }
        godotTemplatesStyle = Stamp(style);
        KeepWorkspace();
        status.Say("Passenger's C# script templates for Godot no longer fit the project's style", null,
            new StatusPlace("write them again", () => Request(AppActions.GodotScriptTemplates)));
    }

    /// <summary>Whether the game's project is being given back its leaving out of the templates. A UI-thread field.</summary>
    private bool leavingTemplatesOut;

    /// <summary>Where Passenger's templates are in the Godot project and its C# project no longer leaves them out of its build
    /// (Godot writes the project anew as it makes a C# script with no solution beside it), the line is added back, one command
    /// of Undo, and the status bar says so.</summary>
    private async Task LeaveGodotTemplatesOutAsync()
    {
        UiThread.Check();
        if (leavingTemplatesOut || GodotCSharpProject() is not { GodotFolder: { } folder } project)
        {
            return;
        }
        leavingTemplatesOut = true;
        try
        {
            bool taken = await BackgroundWork.Run(() => GodotScriptTemplates.Bases.Any(b => GodotScriptTemplates.PathOf(folder, b) is var path && File.Exists(path)
                && FileText.Load(path)?.Text is { } text && GodotScriptTemplates.IsPassengers(text)) && !RemovesTemplates(project.Path));
            if (!taken)
            {
                return;
            }
            string name = Path.GetFileName(project.Path);
            string? refused = await ChangeFileAsync("Leave C# Script Templates Out of the Build", [project.Path],
                () => ProjectEdits.RemoveCompiled(project.Path, GodotScriptTemplates.Excluded));
            status.Say(refused is null
                ? $"{name} was written again without leaving Passenger's C# script templates for Godot out of its build, so they are left out again"
                : $"{name} builds Passenger's C# script templates for Godot as its own code, and they could not be left out again: {refused}", refused is null ? null : ProblemSeverity.Warning);
        }
        finally
        {
            leavingTemplatesOut = false;
        }

        // A project that cannot be read is no project to add to.
        static bool RemovesTemplates(string project)
        {
            try
            {
                return ProjectEdits.RemovesCompiled(project, GodotScriptTemplates.Excluded);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    private static string Stamp(GodotScriptStyle style) => style.ToString();
}
