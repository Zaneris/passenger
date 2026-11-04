using Passenger.App.Explorer;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>A Godot project's page (<see cref="GodotPageViewer"/>) over its project.godot's editor: Godot Project, from the
/// project's row in the Explorer or Find Action, opens it. What it shows is read through Workspace's readers (the scenes'
/// <see cref="GodotDefinitions"/>, <see cref="GodotExports"/>, <see cref="GodotPlugins"/>) and the Godot found; a row goes to
/// its line or opens its file, and a plugin's box, the main scene, the input map, the autoloads and the layers' names are
/// changed in project.godot, asked first, each one command of Undo.</summary>
public partial class MainWindow
{
    private void AddGodotProject()
    {
        Editors.GodotPages = new GodotProjectPages
        {
            Read = GodotFactsAsync,
            GoTo = (file, line) => NavigateTo(file, new TextPosition(line, 0)),
            Open = file => OpenFile(file),
            Run = action => Request(action),
            TitleOf = action => actions![action]!.Title,
            Export = (folder, preset) => ShowGodotExport(folder, preset),
            SetPlugin = SetGodotPluginAsync,
            Edit = EditGodotProjectAsync,
        };
        actions!.Add(new IdeAction(AppActions.GodotProject, "Godot Project", "Godot", c => ShowGodotProject(GodotProjectOf(c)!))
        {
            WhyNot = c => Folder is null ? Refusals.NoFolder : GodotProjectOf(c) is null ? Refusals.NoGodotProject : null,
            Words = ["project.godot", "Project Settings", "Input Map", "Autoload", "Export Presets", "Plugins"],
        });
    }

    /// <summary>The Godot project an action is asked about: the Explorer row's (a project building a game, its project.godot
    /// or its folder), else the file shown's, else the folder's one; null for none.</summary>
    private string? GodotProjectOf(ActionContext context)
    {
        if (RowOf(context) is { } row && (GodotProjectOfRow(row) is { } folder || context.ExplorerRow is not null))
        {
            return GodotProjectOfRow(row);
        }
        return context.File?.Path is { } shown && session.GodotOf(shown) is { } game ? game : GodotFolder();
    }

    /// <summary>The Godot project of an Explorer row: a project's that builds a game, a project.godot's, the folder holding
    /// one, or the one a file lies in; null for none.</summary>
    private string? GodotProjectOfRow(ExplorerNode row) => row.Path is not { } path ? null : row.Kind switch
    {
        NodeKind.Project => Projects.FirstOrDefault(p => PathNames.Same(p.Path, path))?.GodotFolder,
        NodeKind.File when Path.GetFileName(path).Equals(GodotProject.FileName, StringComparison.OrdinalIgnoreCase) => Path.GetDirectoryName(path),
        NodeKind.Folder when File.Exists(Path.Combine(path, GodotProject.FileName)) => path,
        _ => null,
    };

    /// <summary>A Godot project's page shown: its project.godot opened with the page over its text.</summary>
    public string? ShowGodotProject(string folder)
    {
        string file = Path.Combine(folder, GodotProject.FileName);
        if (OpenFile(file) is { } refused)
        {
            return refused;
        }
        if (Editors.Find(file) is { } document && Editors.GodotPages?.For(document) is { } page)
        {
            page.Show(true);
            if (Editors.ActiveGroup.GodotPage is { } shown)
            {
                shown.Body.Focus();
            }
        }
        return null;
    }

    /// <summary>What a Godot project's page shows, the scenes' readers asked on the window's thread and the rest read off it.</summary>
    private async Task<GodotPageFacts> GodotFactsAsync(string folder)
    {
        UiThread.Check();
        GodotScenes scenes = await session.GodotScenes.ScenesAsync(folder);
        string? setting = settings.GodotExecutable;
        bool scripts = !Projects.Any(p => p.GodotFolder is { } game && PathNames.Same(game, folder));
        IReadOnlyDictionary<string, string>? folders = GodotFolders;
        IReadOnlyList<KeyValuePair<string, object>> values = GodotEditorSettings.ForPassenger(ScriptEditor);
        return await BackgroundWork.Run(async () =>
        {
            GodotDefinitions defined = scenes.Defined;
            GodotProject project = GodotProject.Parse(folder, defined.TextOf(GodotDefinitions.ProjectRes) ?? string.Empty);
            int Line(GodotPlace place) => defined.LineOf(place.Res, place.Start);
            // A file project.godot names by its uid, opened, and an autoload's shown, by its res:// path where the uid is known.
            string? Named(string? res) => res is not null && res.StartsWith(GodotUids.Scheme, StringComparison.Ordinal) ? scenes.ResOfUid(res) ?? res : res;
            string? FileOf(string? res)
            {
                string? named = Named(res);
                return named is not null && named.StartsWith("res://", StringComparison.Ordinal)
                    && Path.Combine(folder, named["res://".Length..].Replace('/', Path.DirectorySeparatorChar)) is var path && File.Exists(path) ? path : null;
            }

            (string, int, string?)? main = project.MainScene is { Length: > 0 } scene && defined.SettingOf("application", "run/main_scene") is ({ } place, _)
                ? (scene, Line(place), FileOf(scene)) : null;
            List<GodotPageAutoload> autoloads = [.. defined.Autoloads.Select(a => new GodotPageAutoload(a.Name, Line(a.Places[0]), Named(defined.AutoloadFile(a.Name)),
                FileOf(defined.AutoloadFile(a.Name)), defined.SettingOf("autoload", a.Name)?.Text?.StartsWith('*') == true))];
            List<GodotPageAction> inputs = [.. defined.Actions.Select(a => new GodotPageAction(a.Name, Line(a.Places[0]), defined.EventsOf(a.Name)))];

            GodotFound? found = GodotLaunch.Found(setting);
            string? answer = found is null ? null : await GodotExecutables.AnswerAsync(found);
            string godot = found is null ? Capital(GodotLaunch.NotFound)
                : (scripts ? found.WhyNotScripts : found.WhyNot) is { } why ? Capital(why)
                : $"Godot {(answer is null ? found.Version : GodotExecutables.ReadVersion(answer)?.Version) ?? "of a version unsaid"}{(found.Sharp is null ? string.Empty : ", a .NET build")}: {PathNames.Shown(found.Executable, null)}";
            bool runs = found is not null && (scripts ? found.WhyNotScripts : found.WhyNot) is null;
            string? version = answer is null ? null : GodotExports.TemplatesVersion(answer);
            string? templates = found is not null && version is not null
                ? GodotExports.TemplatesFolder(found.Executable, version, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), name => GodotUserFolders.Variable(folders, name), File.Exists)
                : null;
            bool looked = runs && templates is not null;
            List<GodotPagePreset> presets = [.. GodotExports.ReadIn(folder).Select(p => new GodotPagePreset(p,
                looked ? GodotExports.Missing(p, false, templates, File.Exists, folder) : [], looked ? GodotExports.Missing(p, true, templates, File.Exists, folder) : [], looked))];
            IReadOnlyList<GodotPlugin> plugins = GodotPlugins.Read(folder, defined.StringsOf(GodotPlugins.Section, GodotPlugins.Key));

            GodotSetUp setUp = GodotSetUp.Of(found, scripts, folders);
            bool handedOff = setUp.Values is { } now && values.All(v => now.TryGetValue(v.Key, out object? had) && Equals(had, v.Value));
            string handOff = setUp.Problem is { } problem ? $"Passenger cannot be Godot's script editor: {problem}"
                : handedOff ? $"Godot {setUp.Version}'s editor opens this project's scripts in this Passenger, at their line"
                : $"Godot {setUp.Version}'s editor opens its scripts in another editor";
            List<GodotPageLayer> layers = [.. GodotDefinitions.LayerKinds.SelectMany(kind => defined.Layers(kind).Where(l => l.Places.Count > 0)
                .Select(l => new GodotPageLayer(GodotDefinitions.LayersShown(kind), int.Parse(l.Name, System.Globalization.CultureInfo.InvariantCulture), l.Detail, Line(l.Places[0]), kind)))];
            List<GodotPageScene> listed = [.. scenes.Files.Where(f => f.Value.Header?.Tag == "gd_scene").OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new GodotPageScene(f.Key, f.Value.Header?.Text("uid")))];
            return new GodotPageFacts(folder, project.Name, godot, runs, main, autoloads, inputs, presets, templates, plugins, handOff, handedOff, layers, listed);
        });

        static string Capital(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>A change of a Godot project's project.godot its page asks for, once the person agrees, through the window's
    /// own writes as one command of Undo; null once written, else why not.</summary>
    private async Task<string?> EditGodotProjectAsync(string folder, GodotPageEdit edit)
    {
        UiThread.Check();
        string file = Path.Combine(folder, GodotProject.FileName);
        if (edit.Refused is { } refusal)
        {
            return Told(refusal);
        }
        bool agreed = await ConfirmAsync(edit.Asked, edit.Question, edit.Yes, null);
        // The keys go back to the page the link was on, which the write's reading draws again.
        if (Editors.ActiveGroup.GodotPage is { } shown)
        {
            shown.Body.Focus();
        }
        if (!agreed)
        {
            return Told(edit.Left);
        }
        if (SaveBeforeWriting([file]) is { } unsaved)
        {
            return Told($"{GodotProject.FileName} could not be saved: {unsaved}");
        }
        if (FileText.Load(file) is not { } had)
        {
            return Told($"{PathNames.Shown(file, Folder)} could not be read");
        }
        if (edit.Change(had.Text) is not { } text)
        {
            return Told(edit.Unchanged);
        }
        if (await ChangeFileAsync(edit.Command, [file], () =>
        {
            _ = FileText.SaveOver(file, had, text);
            return null;
        }) is { } refused)
        {
            return Told($"{GodotProject.FileName} could not be written: {refused}");
        }
        Editors.GodotPages?.Refresh();
        status.Text = edit.Done;
        return null;

        string Told(string why)
        {
            status.Text = why;
            return why;
        }
    }

    /// <summary>A plugin enabled or disabled in its project's project.godot once the person agrees: the list of those enabled
    /// written again with it put in or taken out, as one command of Undo; null once written, else why not.</summary>
    private async Task<string?> SetGodotPluginAsync(string folder, GodotPlugin plugin, bool enable)
    {
        UiThread.Check();
        string file = Path.Combine(folder, GodotProject.FileName);
        string doing = enable ? "Enable" : "Disable";
        bool agreed = await ConfirmAsync($"{doing} Plugin?",
            $"{doing} {plugin.Name} ({plugin.Res}) in {PathNames.Shown(file, Folder)}? Passenger writes the plugins it enables there; Godot's editor takes them as it next starts.",
            doing, null);
        // The keys go back to the page the box was on, which the write's reading draws again without it.
        if (Editors.ActiveGroup.GodotPage is { } shown)
        {
            shown.Body.Focus();
        }
        if (!agreed)
        {
            return Told($"{plugin.Name} was left {(plugin.Enabled ? "enabled" : "disabled")}");
        }
        if (SaveBeforeWriting([file]) is { } unsaved)
        {
            return Told($"{GodotProject.FileName} could not be saved: {unsaved}");
        }
        if (FileText.Load(file) is not { } had)
        {
            return Told($"{PathNames.Shown(file, Folder)} could not be read");
        }
        GodotFile read = GodotFile.Parse(had.Text);
        List<string> enabled = read.Sections.FirstOrDefault(s => s.Tag == GodotPlugins.Section)?.Property(GodotPlugins.Key) is { } listed
            ? [.. GodotDefinitions.StringsIn(had.Text, listed.Value).Select(s => s.Text)] : [];
        enabled.RemoveAll(r => r == plugin.Res);
        if (enable)
        {
            enabled.Add(plugin.Res);
        }
        string text = GodotWrites.WithPlugins(had.Text, enabled);
        if (text != had.Text && await ChangeFileAsync($"{doing} Plugin {plugin.Name}", [file], () =>
        {
            _ = FileText.SaveOver(file, had, text);
            return null;
        }) is { } refused)
        {
            return Told($"{GodotProject.FileName} could not be written: {refused}");
        }
        Editors.GodotPages?.Refresh();
        status.Text = $"{plugin.Name} is {(enable ? "enabled" : "disabled")} in {GodotProject.FileName}";
        return null;

        string Told(string why)
        {
            status.Text = why;
            return why;
        }
    }
}
