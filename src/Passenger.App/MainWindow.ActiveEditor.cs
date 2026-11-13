using Passenger.App.Settings;
using Passenger.App.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>View, Active Editor: Soft-Wrap and Show Whitespaces for the file the editor shows, and Increase, Decrease
/// and Reset Font Size, which Ctrl+wheel (Cmd+wheel on macOS) also does while Settings allows it. A file opens
/// soft-wrapped when Settings' masks match its name and with whitespace shown when Settings shows it; the font size is
/// Settings' own, so a zoom reaches every editor and is kept as Settings keeps it.</summary>
public partial class MainWindow
{
    private void AddActiveEditor()
    {
        Editors.Opened += (_, document) =>
        {
            document.SoftWrap = settings.WrapsFile(document.Path);
            document.ShowWhitespaces = settingsStore.Effective.ShowWhitespaces;
        };
        Editors.ForEachEditor(editor => editor.ZoomRequested += (_, points) => Zoom(settings.CodeSize() + points));
        actions!.Add(new IdeAction(A.SoftWrap, "Soft-Wrap", "Editor", context => context.File!.SoftWrap = !context.File.SoftWrap)
        {
            WhyNot = Refusals.NeedsFile,
            IsChecked = context => context.File?.SoftWrap == true,
            Menus = [new MenuPlace("View/Active Editor", 0, 0)],
        });
        actions.Add(new IdeAction(A.ShowWhitespaces, "Show Whitespaces", "Editor", context => context.File!.ShowWhitespaces = !context.File.ShowWhitespaces)
        {
            WhyNot = Refusals.NeedsFile,
            IsChecked = context => context.File?.ShowWhitespaces == true,
            Menus = [new MenuPlace("View/Active Editor", 1, 0)],
        });
        Add(A.IncreaseFontSize, "Increase Font Size", "Editor", _ => Zoom(settings.CodeSize() + 1),
            context => Refusals.NeedsFile(context) ?? (settings.CodeSize() >= AppSettings.MaxFontSize ? "The editor's font is as large as it goes" : null),
            ("View/Active Editor", 2, 0));
        Add(A.DecreaseFontSize, "Decrease Font Size", "Editor", _ => Zoom(settings.CodeSize() - 1),
            context => Refusals.NeedsFile(context) ?? (settings.CodeSize() <= AppSettings.MinFontSize ? "The editor's font is as small as it goes" : null),
            ("View/Active Editor", 2, 1));
        // Back to the size the theme gives, or Passenger's own: set by nobody.
        Add(A.ResetFontSize, "Reset Font Size", "Editor", _ => ApplySettings(settings with { EditorFontSize = null }), Refusals.NeedsFile, ("View/Active Editor", 2, 2));
    }

    /// <summary>The editors' font at a size, within what Settings allows, kept as Settings keeps it.</summary>
    private void Zoom(double size)
    {
        double next = Math.Clamp(Math.Round(size), AppSettings.MinFontSize, AppSettings.MaxFontSize);
        if (next != settings.CodeSize())
        {
            ApplySettings(settings with { EditorFontSize = next });
        }
    }

    /// <summary>The open files take a change to Settings' soft wrap or whitespace where it changes what Settings says of
    /// them, so a file turned on or off from View, Active Editor stays so otherwise.</summary>
    private void FollowViewSettings(AppSettings before, AppSettings next)
    {
        foreach (OpenDocument document in Editors.Documents)
        {
            bool wraps = next.WrapsFile(document.Path);
            if (wraps != before.WrapsFile(document.Path))
            {
                document.SoftWrap = wraps;
            }
            if (next.ShowWhitespaces != before.ShowWhitespaces)
            {
                document.ShowWhitespaces = next.ShowWhitespaces;
            }
        }
    }
}
