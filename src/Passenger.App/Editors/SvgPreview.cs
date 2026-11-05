using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>An SVG file's picture beside its editor, a group's own, drawn from the text as the editor has it and drawn again
/// as typing pauses, read off the window's thread, the images it names read from the file's folder; zoomed as an image's
/// preview is (<see cref="ZoomedPicture"/>), sharp at every zoom, its keys its own while it has them. Text that draws
/// nothing says why in the picture's place, the last picture drawn kept above it; a banner over the picture names what it
/// left out and why.</summary>
public sealed class SvgPreview : DockPanel, IOwnKeys
{
    /// <summary>How long typing pauses before the text is drawn again.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    private readonly ZoomedPicture picture = new();
    private readonly TextBlock size = MutedText.Of(string.Empty);
    private readonly TextBlock problem = new() { Classes = { StyleClasses.EmptyText }, IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Banner leftOut = new();
    private readonly Debounce typing;
    private OpenDocument? document;

    /// <summary>Drawings begun, which numbers each: only the newest one's picture is shown.</summary>
    private int drawings;

    /// <summary>The text of the picture shown, or of the problem said, so the same text is never drawn twice.</summary>
    private TextSnapshot? drawn;

    internal SvgPreview()
    {
        Focusable = true;
        LastChildFill = true;
        Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Editor_Background));
        size.FontSize = 12;
        size.Margin = new Thickness(8, 0);
        size.HorizontalAlignment = HorizontalAlignment.Right;
        problem.Margin = new Thickness(16, 8);
        AutomationProperties.SetName(picture, "SVG preview");
        SetDock(picture.Tools, Dock.Left);
        Border bar = ToolWindows.ToolBars.OverEditor(new DockPanel { Children = { picture.Tools, size } });
        SetDock(bar, Dock.Top);
        SetDock(problem, Dock.Top);
        Children.Add(bar);
        Children.Add(leftOut);
        Children.Add(problem);
        Children.Add(picture);
        typing = new Debounce(Pause, DrawAsync);
        KeyDown += (_, e) =>
        {
            if (picture.CommandFor(e) is { } zoom)
            {
                zoom();
                e.Handled = true;
            }
        };
        AddHandler(PointerPressedEvent, (_, _) => Focus(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The file it draws, null for none.</summary>
    public OpenDocument? Document => document;

    /// <summary>The picture and its zoom.</summary>
    public ZoomedPicture Picture => picture;

    /// <summary>Why the text as it stands draws nothing, null while it draws.</summary>
    public string? Problem => problem.IsVisible ? problem.Text : null;

    /// <summary>What the picture shown leaves out of what its text names, and why; null where it leaves out nothing.</summary>
    public string? LeftOut => leftOut.Said;

    /// <summary>The picture's zoom keys are its own while it has the keys, ahead of the IDE's keymap (whose ⌘- is Back).</summary>
    public bool TakesKey(KeyEventArgs e) => picture.CommandFor(e) is not null;

    /// <summary>Draws a file's text, or nothing; its edits drawn again as typing pauses.</summary>
    internal void Show(OpenDocument? next)
    {
        if (next == document)
        {
            return;
        }
        typing.Stop();
        if (document is not null)
        {
            document.Text.Changed -= OnChanged;
        }
        document = next;
        drawn = null;
        drawings++;
        picture.Picture = null;
        problem.IsVisible = false;
        leftOut.Say(null);
        size.Text = string.Empty;
        if (next is null)
        {
            return;
        }
        next.Text.Changed += OnChanged;
        picture.FitDown();
        IdeErrors.Observe(DrawAsync());
    }

    private void OnChanged(object? sender, DocumentChangedEventArgs e) => typing.Restart();

    /// <summary>The text as it stands drawn off the window's thread, shown unless a later drawing or another file came first.</summary>
    private async Task DrawAsync()
    {
        if (document is not { } shown || ReferenceEquals(shown.Text.Snapshot, drawn))
        {
            return;
        }
        TextSnapshot text = shown.Text.Snapshot;
        string file = shown.Path;
        int mine = ++drawings;
        (SvgPicture? read, string? why) = await BackgroundWork.Run(() => SvgPicture.Read(text.ToString(), file));
        if (mine != drawings || document != shown)
        {
            return;
        }
        drawn = text;
        problem.Text = why;
        problem.IsVisible = why is not null;
        if (read is not null)
        {
            picture.Picture = read;
            leftOut.Say(read.LeftOut);
            size.Text = string.Create(CultureInfo.CurrentCulture, $"{read.Size.Width:0.##} × {read.Size.Height:0.##}");
        }
    }
}
