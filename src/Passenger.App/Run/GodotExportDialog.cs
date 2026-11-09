using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>The Godot found as an export takes it, read off the window's thread: its templates' version ("4.7.2.stable.mono")
/// and their folder, or why no export can run (no Godot, one that cannot run the project).</summary>
public sealed record GodotExportGodot(string? Version, string? Templates, string? Problem);

/// <summary>Publish of a Godot project: its export_presets.cfg's presets, the one chosen exported as a release or a debug
/// build to its path (the preset's own, or another typed), with the command line Godot runs; whether the Godot found has the
/// templates the export takes, with Install Templates… from a .tpz the person picks and the link to Godot's download of
/// them. Export closes with the export to run, greyed with why while the templates are missing.</summary>
public sealed class GodotExportDialog : IdeWindow
{
    /// <summary>The words of a release export, the first kind offered.</summary>
    public const string Release = "Release";

    /// <summary>The words of a debug export.</summary>
    public const string Debug = "Debug";

    /// <summary>The words of the button that installs the templates from an archive.</summary>
    public const string InstallWords = "Install Templates…";

    private readonly string folder;
    private readonly IReadOnlyList<GodotPreset> presets;
    private readonly Func<Task<GodotExportGodot>> read;
    private readonly Func<IReadOnlyList<GodotPreset>, Task<bool>> install;
    private readonly Action<Uri> browse;
    private readonly ComboBox presetBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox kindBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { Release, Debug }, SelectedIndex = 0 };
    private readonly TextBox pathBox = new() { Classes = { StyleClasses.Search } };
    private readonly TextBlock destination = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextTrimming = TextTrimming.PathSegmentEllipsis, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock platform = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock templates = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button installButton = new() { Content = InstallWords };
    private readonly Button download = new() { Classes = { StyleClasses.Link }, Content = "Godot's download of them", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock command = new() { Classes = { StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock problem = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button export = DialogFooter.Default("Export");
    private GodotExportGodot? godot;
    private bool installing;

    /// <param name="read">The Godot found, read again after an install.</param>
    /// <param name="install">The templates the presets take installed from an archive the person picks; true once installed.</param>
    public GodotExportDialog(string folder, string name, IReadOnlyList<GodotPreset> presets, string? chosen, Func<Task<GodotExportGodot>> read,
        Func<IReadOnlyList<GodotPreset>, Task<bool>> install, Action<Uri> browse)
    {
        FormRows.Scope(this);
        this.folder = folder;
        this.presets = presets;
        this.read = read;
        this.install = install;
        this.browse = browse;
        Title = $"Export {name}";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        problem.Bind(TextBlock.ForegroundProperty, problem.GetResourceObservable(TokenKeys.Error));
        presetBox.ItemsSource = presets.Select(p => p.Name).ToArray();
        presetBox.SelectedIndex = Math.Max(0, presets.ToList().FindIndex(p => p.Name == chosen));
        export.Click += (_, _) => Chosen();
        installButton.Click += (_, _) => IdeErrors.Observe(InstallAsync());
        download.Click += (_, _) => browse(DownloadPage);
        presetBox.SelectionChanged += (_, _) =>
        {
            pathBox.Text = Preset?.ExportPath ?? string.Empty;
            Paint();
        };
        kindBox.SelectionChanged += (_, _) => Paint();
        pathBox.TextChanged += (_, _) => Paint();
        pathBox.Text = Preset?.ExportPath ?? string.Empty;
        StackPanel fields = new()
        {
            Spacing = 10,
            Children =
            {
                FormRows.Of("Preset:", presetBox),
                FormRows.Of("Platform:", platform),
                FormRows.Of("Export:", kindBox),
                new StackPanel { Children = { FormRows.Of("Path:", pathBox), FormRows.Of(null, destination) } },
                FormRows.Of("Templates:", templates),
                FormRows.Of(null, new StackPanel { Orientation = Orientation.Horizontal, Children = { installButton, download } }),
            },
        };
        Content = DialogFooter.Headed($"Export {name} with Godot", command, fields, DialogFooter.OkCancel(export, () => Close(null), problem));
        Opened += (_, _) => IdeErrors.Observe(ReadAsync());
        Paint();
    }

    /// <summary>The preset chosen; null while the project has none.</summary>
    public GodotPreset? Preset => presetBox.SelectedIndex is int index and >= 0 && index < presets.Count ? presets[index] : null;

    /// <summary>Whether a debug export is chosen.</summary>
    public bool IsDebug => kindBox.SelectedIndex == 1;

    /// <summary>Why Export is greyed, null while it is not.</summary>
    public string? Problem => problem.Text is { Length: > 0 } said ? said : null;

    /// <summary>What it says of the templates.</summary>
    public string TemplatesSaid => templates.Text ?? string.Empty;

    /// <summary>The Godot found has been read, the templates said.</summary>
    public bool IsRead => godot is not null && !installing;

    /// <summary>Where Godot offers its export templates for download, its archive of releases.</summary>
    private Uri DownloadPage
    {
        get
        {
            string[] words = (godot?.Version ?? string.Empty).Split('.');
            words = words is [.., "mono"] ? words[..^1] : words;
            return new Uri(words.Length >= 3 ? $"https://godotengine.org/download/archive/{string.Join('.', words[..^1])}-{words[^1]}/" : "https://godotengine.org/download/archive/");
        }
    }

    private async Task ReadAsync()
    {
        godot = await read();
        Paint();
    }

    private async Task InstallAsync()
    {
        installing = true;
        Paint();
        try
        {
            if (await install(presets))
            {
                godot = await read();
            }
        }
        finally
        {
            installing = false;
            Paint();
        }
    }

    private void Chosen()
    {
        if (Preset is { } preset && Problem is null && !installing)
        {
            string path = pathBox.Text?.Trim() ?? string.Empty;
            Close(new GodotExportRequest(folder, preset.Name, IsDebug, path == preset.ExportPath ? null : path));
        }
    }

    private void Paint()
    {
        GodotPreset? preset = Preset;
        platform.Text = preset?.Platform ?? string.Empty;
        string path = pathBox.Text?.Trim() ?? string.Empty;
        string? output = path.Length == 0 ? null : GodotResPaths.Globalized(folder, path);
        destination.Text = output is null ? string.Empty : $"Exports to {PathNames.Shown(output, null)}";
        command.Text = preset is null ? string.Empty
            : $"godot {GodotExecutables.Headless} --path {Word(folder)} {(IsDebug ? "--export-debug" : "--export-release")} {Word(preset.Name)} {Word(output ?? path)}";
        IReadOnlyList<string> missing = preset is not null && godot is { Problem: null } found ? GodotExports.Missing(preset, IsDebug, found.Templates, File.Exists, folder) : [];
        templates.Text = installing ? "Installing…"
            : godot is null ? "Looking for the Godot found's templates…"
            : godot.Problem is { } none ? none
            : missing.Count == 0 ? $"Installed for Godot {godot.Version}{(godot.Templates is { } at ? $" in {PathNames.Shown(at, null)}" : string.Empty)}"
            : $"Godot {godot.Version} has no {Plural.Listed(missing)}{(godot.Templates is { } where ? $" in {PathNames.Shown(where, null)}" : string.Empty)}";
        installButton.IsEnabled = !installing && godot is { Problem: null, Templates: not null };
        download.IsVisible = godot is { Problem: null } && missing.Count > 0;
        problem.Text = presets.Count == 0 ? $"The project has no {GodotExports.FileName}: make an export preset in Godot's editor (Project, Export)"
            : path.Length == 0 ? "Give the file to export to"
            : godot is { Problem: { } cannot } ? cannot
            : missing.Count > 0 ? $"Install the templates {preset!.Name} takes first"
            : null;
        export.IsEnabled = Problem is null && godot is not null && !installing;

        // A word as a command line shows it, in quotes where it holds a space.
        static string Word(string word) => word.Contains(' ', StringComparison.Ordinal) ? $"\"{word}\"" : word;
    }
}
