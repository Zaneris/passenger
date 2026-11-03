using Avalonia.Controls;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Passenger as Godot's script editor, in one step a person asks for: the Godot found (or chosen in Settings) is
/// told, in its version's editor settings file, to open its C# scripts and its other scripts in this Passenger at their
/// line, which a launch then hands to the window on the project (<see cref="LaunchHandOff"/>). It asks first, showing
/// what it writes and where, then says what it changed; where the file cannot be read or written, it shows the values to
/// set in Godot's Editor Settings instead. A Godot project's first opening in a person's window offers it in the banner
/// over the editor, once: taken or dismissed, never again for that folder (<see cref="Workspace.WorkspaceState.GodotEditorOffered"/>).</summary>
public partial class MainWindow
{
    /// <summary>Whether the open folder's offer of Passenger as Godot's script editor was taken or dismissed.</summary>
    private bool godotEditorOffered;

    /// <summary>The folder whether to offer it was last looked at for, so a folder is looked at once a window.</summary>
    private string? godotOfferLooked;

    private void AddGodotEditor()
    {
        actions!.Add(new IdeAction(AppActions.GodotScriptEditor, "Make Passenger Godot's Script Editor…", "Godot", _ => IdeErrors.Observe(MakeGodotScriptEditorAsync()))
        {
            WhyNot = _ => Folder is null ? Refusals.NoFolder : GodotFolder() is null ? Refusals.NoGodotProject : null,
            Menus = [new MenuPlace("Tools", 0, 6)],
            Words = ["Godot", "External Editor", "Exec Path"],
            Works = AppActions.GodotScriptEditor,
        });
        GodotBanner.Link(actions[AppActions.GodotScriptEditor]!.Title, () =>
        {
            GodotEditorOffered();
            Request(AppActions.GodotScriptEditor);
        });
        AddGodotAddon();
        AddGodotOpenInEditor();
        GodotBanner.Link(actions[AppActions.GodotAddon]!.Title, () =>
        {
            GodotEditorOffered();
            Request(AppActions.GodotAddon);
        });
        GodotBanner.Link("Dismiss", GodotEditorOffered);
    }

    /// <summary>What the banner over the editor says of Godot's script editor, null while it says nothing.</summary>
    public string? GodotSaid => GodotBanner.Said;

    /// <summary>The offer taken or dismissed: gone, and kept with the folder so it never comes again.</summary>
    private void GodotEditorOffered()
    {
        godotEditorOffered = true;
        GodotBanner.Say(null);
        KeepWorkspace();
    }

    /// <summary>Offers Passenger as Godot's script editor in the banner where the folder is a Godot project a person's window
    /// keeps, not yet offered, and the Godot found can take it and does not open its scripts here already; looked at off
    /// the window's thread, once a folder.</summary>
    private void OfferGodotEditor()
    {
        UiThread.Check();
        if (!keepsWorkspace || godotEditorOffered || Folder is not { } folder || godotOfferLooked == folder || GodotFolder() is null)
        {
            return;
        }
        godotOfferLooked = folder;
        IdeErrors.Observe(OfferGodotEditorAsync(folder));
    }

    private async Task OfferGodotEditorAsync(string folder)
    {
        string? setting = settings.GodotExecutable;
        bool scripts = !Projects.Any(p => p.GodotFolder is not null);
        IReadOnlyDictionary<string, string>? folders = GodotFolders;
        IReadOnlyList<KeyValuePair<string, object>> values = GodotEditorSettings.ForPassenger(ScriptEditor);
        GodotSetUp found = await BackgroundWork.Run(() => GodotSetUp.Of(GodotLaunch.Found(setting), scripts, folders));
        if (Folder != folder || godotEditorOffered || found.Problem is not null
            || found.Values is { } now && values.All(v => now.TryGetValue(v.Key, out object? had) && Equals(had, v.Value)))
        {
            return;
        }
        GodotBanner.Say($"Godot {found.Version}'s editor can open this project's scripts in Passenger, at their line");
    }

    /// <summary>The program Godot's editor is told to open its scripts in: this Passenger.</summary>
    private static string ScriptEditor => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Passenger.exe" : "Passenger");

    /// <summary>The open folder's Godot project: a project of it on Godot.NET.Sdk beside a project.godot, else the folder
    /// itself holding one.</summary>
    private string? GodotFolder() =>
        Projects.Select(p => p.GodotFolder).OfType<string>().FirstOrDefault()
        ?? (Folder is { } folder && File.Exists(Path.Combine(folder, GodotProject.FileName)) ? folder : null);

    /// <summary>Finds the Godot, reads its settings, asks, and writes Passenger's values; says what it came to.</summary>
    private async Task MakeGodotScriptEditorAsync()
    {
        UiThread.Check();
        if (this.work.Begin(AppActions.GodotScriptEditor, "Finding Godot and its editor settings", stoppable: false) is not { } work)
        {
            return;
        }
        using (work)
        {
            IReadOnlyList<KeyValuePair<string, object>> values = GodotEditorSettings.ForPassenger(ScriptEditor);
            string? setting = settings.GodotExecutable;
            // A Godot project of GDScript alone takes any Godot 4, .NET or not.
            bool scripts = !Projects.Any(p => p.GodotFolder is not null);
            IReadOnlyDictionary<string, string>? folders = GodotFolders;
            GodotSetUp found = await BackgroundWork.Run(() => GodotSetUp.Of(GodotLaunch.Found(setting), scripts, folders));
            if (found.Problem is { } problem)
            {
                Told(work, false, $"Passenger cannot be Godot's script editor: {problem}");
                return;
            }
            if (found.Values is { } now && values.All(v => now.TryGetValue(v.Key, out object? had) && Equals(had, v.Value)))
            {
                Told(work, true, $"Godot {found.Version}'s editor already opens its scripts in this Passenger, as {PathNames.Shown(found.Written!, null)} says");
                return;
            }
            string listed = string.Join(Environment.NewLine, values.Select(v => $"{v.Key} = {GodotEditorSettings.Written(v.Value)}"));
            if (found.Unreadable is { } unreadable)
            {
                await SetByHandAsync(work, $"Godot's editor settings cannot be read ({unreadable})", values);
                return;
            }
            work.Say("Asking to write Godot's editor settings");
            CheckBox addon = new()
            {
                Content = $"Also install Passenger's addon in this project ({GodotAddon.FolderName} under addons, enabled in {GodotProject.FileName}): "
                    + "Godot's Play then builds here, and this Passenger debugs what it plays",
                IsChecked = false,
            };
            StackPanel detail = new() { Spacing = 8, Children = { Copyable(listed), addon } };
            if (!await ConfirmAsync("Make Passenger Godot's Script Editor?",
                $"Godot {found.Version}'s editor will open its C# scripts and its other scripts in Passenger, at their line, in the window on their project. "
                + $"Passenger writes these into {PathNames.Shown(found.Written!, null)}, keeping the rest; Godot's editor takes them as it next starts.",
                "Write Settings", detail))
            {
                Told(work, false, "Godot's editor settings were left as they were");
                return;
            }
            string? failed = await BackgroundWork.Run(() => found.Write(values));
            if (failed is not null)
            {
                await SetByHandAsync(work, $"{PathNames.Shown(found.Written!, null)} could not be written ({failed})", values);
                return;
            }
            string[] changed = [.. values.Where(v => found.Values?.TryGetValue(v.Key, out object? had) != true || !Equals(had, v.Value)).Select(v => v.Key)];
            string said = $"Godot {found.Version}'s editor now opens its scripts in Passenger: {Plural.Listed(changed)} set in {PathNames.Shown(found.Written!, null)}; "
                + "restart Godot's editor if it is open, which takes them as it starts";
            if (addon.IsChecked == true && await InstallGodotAddonAsync(work, ask: false) is { } notInstalled)
            {
                Told(work, false, $"{said}; {notInstalled}");
                return;
            }
            Told(work, true, addon.IsChecked == true ? $"{said}; Passenger's addon is installed in this project" : said);
        }
    }

    /// <summary>Where the file cannot be had: the values shown to set in Godot's own Editor Settings, copied on the person's word.</summary>
    private async Task SetByHandAsync(Working work, string why, IReadOnlyList<KeyValuePair<string, object>> values)
    {
        string paste = string.Join(Environment.NewLine, values.Select(v => $"{v.Key}: {v.Value switch
        {
            string text => text,
            GodotEditorSettings.Custom => "Custom",
            object other => GodotEditorSettings.Written(other),
        }}"));
        work.Say("Showing the values to set in Godot");
        bool copy = await ConfirmAsync("Set Godot's Script Editor by Hand",
            $"{why}. In Godot's editor, open Editor, Editor Settings, turn on Advanced Settings, and set these:", "Copy Values", Copyable(paste));
        if (copy && Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, paste);
        }
        Told(work, false, $"Passenger cannot write Godot's editor settings: {why}; set them in Godot's Editor Settings{(copy ? ", the values copied" : string.Empty)}");
    }

    private void Told(Working work, bool succeeded, string said)
    {
        status.Text = said;
        work.Tell(succeeded, said);
    }

    /// <summary>Lines a person can select and copy, under a question.</summary>
    private static TextBox Copyable(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        FontFamily = Passenger.Editor.CodeEditor.CodeFont,
        MaxHeight = 220,
    };

    /// <summary>A Godot's editor settings as found off the window's thread, in the user folders the window's Godots keep:
    /// its version, the file it reads and the one it writes, the script editors' values the file read gives; or why there
    /// is none to write.</summary>
    private sealed record GodotSetUp(string? Version, string? Read, string? Written, IReadOnlyDictionary<string, object>? Values, string? Unreadable, string? Problem)
    {
        public static GodotSetUp Of(GodotFound? found, bool scripts, IReadOnlyDictionary<string, string>? folders)
        {
            if (found is null)
            {
                return new(null, null, null, null, null, GodotLaunch.NotFound);
            }
            if ((scripts ? found.WhyNotScripts : found.WhyNot) is { } why)
            {
                return new(null, null, null, null, null, why);
            }
            string[] numbers = (found.Version ?? string.Empty).Split('.');
            if (numbers.Length < 2 || !int.TryParse(numbers[0], System.Globalization.CultureInfo.InvariantCulture, out int major)
                || !int.TryParse(numbers[1], System.Globalization.CultureInfo.InvariantCulture, out int minor))
            {
                return new(null, null, null, null, null, $"the version of the Godot at {found.Executable} is unknown, which names its editor settings file");
            }
            if (GodotEditorSettings.Folder(found.Executable, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), name => Godot.GodotUserFolders.Variable(folders, name),
                File.Exists) is not { } folder)
            {
                return new(found.Version, null, null, null, null, "no folder for Godot's editor settings is known: HOME (APPDATA on Windows) is not set");
            }
            (string? read, string written) = GodotEditorSettings.Files(folder, major, minor, File.Exists);
            try
            {
                IReadOnlyDictionary<string, object> values = read is null ? new Dictionary<string, object>()
                    : GodotEditorSettings.Read(File.ReadAllText(read), GodotEditorSettings.ForPassenger(string.Empty).Select(v => v.Key));
                return new(found.Version, read, written, values, null, null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new(found.Version, read, written, null, error.Message, null);
            }
        }

        /// <summary>Writes the values into the file Godot reads, as the one it writes; why not, or null once written.</summary>
        public string? Write(IReadOnlyList<KeyValuePair<string, object>> values)
        {
            try
            {
                string text = Read is null ? string.Empty : File.ReadAllText(Read);
                Directory.CreateDirectory(Path.GetDirectoryName(Written!)!);
                AtomicFile.Write(Written!, System.Text.Encoding.UTF8.GetBytes(GodotEditorSettings.With(text, values)));
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return error.Message;
            }
        }
    }
}
