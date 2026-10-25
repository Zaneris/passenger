using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Passenger.App.Updates;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Editors;

/// <summary>A group's view of release notes the app carries (What's New): Passenger's mark and the notes' title over
/// their Markdown rendered (<see cref="Markdown"/>), on the editor's ground and scrolled as one page. Its links open in
/// the person's browser through the window.</summary>
public sealed class ReleaseNotesView : ScrollViewer
{
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    public ReleaseNotesView(OpenDocument document, Action<Uri> browse)
    {
        Classes.Add("notes");
        Focusable = true;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        string version = ReleaseNotes.VersionOf(document.Path) ?? Product.Version;
        Image mark = new() { Classes = { "notesMark" }, Source = new Bitmap(AssetLoader.Open(new Uri("avares://Passenger/Assets/passenger.png"))) };
        RenderOptions.SetBitmapInterpolationMode(mark, BitmapInterpolationMode.HighQuality);
        Title = $"What's New in Passenger {version}";
        Grid hero = new() { Classes = { "notesHero" }, ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        hero.Children.Add(mark);
        hero.Children.Add(new StackPanel
        {
            [Grid.ColumnProperty] = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Classes = { "notesTitle" }, Text = Title, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Classes = { "notesSubtitle" },
                    Text = "Help, What's New shows these notes again.",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        });
        page = new StackPanel { Classes = { "notesPage" }, Children = { hero } };
        Markdown.Render(Markdown.Parse(document.Text.Snapshot.ToString()), page, browse);
        Content = page;
    }

    private readonly StackPanel page;

    /// <summary>Moves through the page by an editor's move, whichever keys the keymap gives it: a line start or the
    /// text's to its top, an end to its bottom, a page or a line up or down. True once taken.</summary>
    public bool Move(string action)
    {
        switch (action)
        {
            case EditorActions.LineStart or EditorActions.TextStart:
                ScrollToHome();
                return true;
            case EditorActions.LineEnd or EditorActions.TextEnd:
                ScrollToEnd();
                return true;
            case EditorActions.PageUp:
                PageUp();
                return true;
            case EditorActions.PageDown:
                PageDown();
                return true;
            case EditorActions.Up or EditorActions.ScrollUp:
                LineUp();
                return true;
            case EditorActions.Down or EditorActions.ScrollDown:
                LineDown();
                return true;
            default:
                return false;
        }
    }

    /// <summary>The page's heading: what the notes are of.</summary>
    public string Title { get; }

    /// <summary>The notes' blocks of words, as drawn, in order.</summary>
    public IEnumerable<MarkdownText> Blocks => page.GetLogicalDescendants().OfType<MarkdownText>();

    /// <summary>What the page says under its heading, a block a line.</summary>
    public string Text => string.Join('\n', Blocks.Select(b => b.Shown));
}
