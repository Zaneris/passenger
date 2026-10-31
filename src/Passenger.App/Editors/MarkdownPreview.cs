using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>A Markdown file's preview beside its editor, a group's own (each split and editor window has one), drawn by
/// <see cref="Markdown"/> as a page of the blocks in sight (<see cref="MarkdownBlocks"/>), each file's page kept while its
/// tab is open. It follows the text as typing pauses, read off the window's thread, keeping the control of every block
/// whose lines did not change, and follows the editor's scroll, the block at the editor's top line at its own top. Its
/// web links open through the window's browser, a link to a file beside it opens that file, a <c>#heading</c> scrolls to
/// the heading, and it draws the images of files inside the open folder alone, the rest as their words.</summary>
public sealed class MarkdownPreview : ScrollViewer
{
    /// <summary>How long typing pauses before the text is read again.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    private readonly CodeEditor editor;
    private readonly EditorArea area;
    private readonly MarkdownPage links;
    private readonly Debounce typing;
    private readonly Dictionary<OpenDocument, MarkdownBlocks> pages = [];
    private OpenDocument? document;
    private MarkdownBlocks? page;
    private int generation;

    /// <summary>Each picture's decode by its file, kept while the preview is shown and let go as it goes.</summary>
    private Dictionary<string, Task<AssetPreview?>> pictures = new(PathNames.Comparer);

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    internal MarkdownPreview(CodeEditor editor, EditorArea area)
    {
        this.editor = editor;
        this.area = area;
        Classes.Add(StyleClasses.MarkdownPreview);
        Focusable = true;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        links = new MarkdownPage(Follow) { LocalLinks = true, ImageFile = ImageFile, Load = LoadAsync };
        typing = new Debounce(Pause, RenderAsync);
        editor.ScrollChanged += (_, _) => FollowEditor();
    }

    /// <summary>The file it previews, null for none.</summary>
    public OpenDocument? Document => document;

    /// <summary>The controls of the top-level blocks drawn now, in order.</summary>
    public IReadOnlyList<Control> Blocks => page is null ? [] : [.. page.Shown.Select(e => e.Control!)];

    /// <summary>The words drawn now, a block of words a line.</summary>
    public string Text => page is null ? "" : string.Join('\n', page.GetLogicalDescendants().OfType<MarkdownText>().Select(b => b.Shown));

    /// <summary>The control of the block drawn whose lines hold a line of the file (from zero), null for a line of none
    /// or of a block not drawn now.</summary>
    public Control? BlockAt(int line) => page?.Shown.FirstOrDefault(e => line >= e.Placed.Line && line < e.Placed.End)?.Control;

    /// <summary>The first line (from zero) of the block a control draws.</summary>
    public int LineOf(Control block) => page!.Shown.First(e => e.Control == block).Placed.Line;

    /// <summary>The heading drawn of an anchor (GitHub's: its words in lower case, hyphens for spaces), null for none.</summary>
    public Control? Heading(string anchor) => page?.Shown.FirstOrDefault(e => e.Anchor == anchor)?.Control;

    /// <summary>Where the preview's scroll stands with a block at its top.</summary>
    public double TopOf(Control block) => page is not null && block.TranslatePoint(default, page) is { } at ? Offset.Y + page.Bounds.Top + at.Y : 0;

    /// <summary>Shows a file's text rendered, or nothing: its page as it was left, read again only as its text changed.</summary>
    internal void Show(OpenDocument? next)
    {
        if (next == document)
        {
            return;
        }
        typing.Stop();
        generation++;
        document = next;
        if (next is null)
        {
            page = null;
        }
        else if (pages.TryGetValue(next, out MarkdownBlocks? kept))
        {
            page = kept;
            kept.Arrive();
        }
        else
        {
            page = NewPage();
            pages[next] = page;
        }
        // Before the page is handed over, since its pictures ask for theirs again as they attach.
        LetPicturesGo();
        Content = page;
        if (page is not null && page.From is null)
        {
            Offset = default;
        }
        if (onScreen)
        {
            Hear(next);
            ReadAgain();
        }
    }

    /// <summary>Lets go of a file's page, its tab closed: shown, it goes as the next file is.</summary>
    internal void Forget(OpenDocument closed) => pages.Remove(closed);

    /// <summary>Lets go of every page but the one shown, previews turned off.</summary>
    internal void Forget()
    {
        foreach (OpenDocument kept in pages.Keys.Where(d => d != document).ToList())
        {
            pages.Remove(kept);
        }
    }

    private MarkdownBlocks NewPage()
    {
        MarkdownBlocks blocks = new(block => Markdown.Control(block, links), FollowEditor) { Classes = { StyleClasses.MdPage } };
        // A block that grew (a picture read, a narrower page) moves those under it: they follow once laid out.
        blocks.Moved += (_, _) => FollowLaidOut();
        return blocks;
    }

    /// <summary>Reads the text shown again where its page was drawn from another version of it.</summary>
    private void ReadAgain()
    {
        if (document is { } shown && page is { } drawn && drawn.From != (shown.Text, shown.Text.Version))
        {
            IdeErrors.Observe(RenderAsync());
        }
    }

    private bool onScreen;

    /// <summary>The file whose changes it hears: the one it shows, while it is on screen alone, so a split closed with a
    /// preview in it is held by nothing of its file's.</summary>
    private OpenDocument? followed;

    private void Hear(OpenDocument? next)
    {
        if (followed == next)
        {
            return;
        }
        if (followed is not null)
        {
            followed.Text.Changed -= OnTextChanged;
            followed.PropertyChanged -= OnDocumentChanged;
        }
        followed = next;
        if (next is not null)
        {
            next.Text.Changed += OnTextChanged;
            next.PropertyChanged += OnDocumentChanged;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        onScreen = true;
        Hear(document);
        ReadAgain();
    }

    /// <summary>Off screen it hears nothing of its file and holds no picture; its pages keep the blocks they drew, to show
    /// as it comes back.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        onScreen = false;
        Hear(null);
        typing.Stop();
        generation++;
        LetPicturesGo();
    }

    private void OnTextChanged(object? sender, DocumentChangedEventArgs e) => typing.Restart();

    /// <summary>A file moved or renamed is drawn again, since its links and pictures are found from where it is.</summary>
    private void OnDocumentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenDocument.Path) && document is { } moved)
        {
            pages.Remove(moved);
            document = null;
            Show(moved);
        }
    }

    private void LetPicturesGo()
    {
        Dictionary<string, Task<AssetPreview?>> drawn = pictures;
        pictures = new(PathNames.Comparer);
        foreach (Task<AssetPreview?> picture in drawn.Values)
        {
            if (picture.IsCompletedSuccessfully)
            {
                picture.Result?.Dispose();
            }
        }
    }

    /// <summary>Reads the text off the window's thread, then has the page take its blocks, those that did not change kept.</summary>
    private async Task RenderAsync()
    {
        if (document is not { } shown || page is not { } into || !onScreen)
        {
            return;
        }
        int asked = ++generation;
        TextSnapshot text = shown.Text.Snapshot;
        int version = shown.Text.Version;
        IReadOnlyList<MarkdownBlocks.Entry> drawn = into.Entries;
        MarkdownBlocks.Entry[] read = await BackgroundWork.Run(() => Read(text.ToString(), drawn));
        if (asked != generation || document != shown || page != into)
        {
            return;
        }
        into.Take(read, shown.Text, version);
        FollowLaidOut();
    }

    /// <summary>A text's blocks, each taking the block drawn it is (by its lines as written, in order), and each heading's
    /// anchor.</summary>
    private static MarkdownBlocks.Entry[] Read(string text, IReadOnlyList<MarkdownBlocks.Entry> drawn)
    {
        IReadOnlyList<Markdown.Placed> placed = Markdown.ParsePlaced(text);
        Dictionary<string, Queue<MarkdownBlocks.Entry>> byLines = new(StringComparer.Ordinal);
        foreach (MarkdownBlocks.Entry entry in drawn)
        {
            if (!byLines.TryGetValue(entry.Placed.Source, out Queue<MarkdownBlocks.Entry>? same))
            {
                byLines[entry.Placed.Source] = same = new Queue<MarkdownBlocks.Entry>();
            }
            same.Enqueue(entry);
        }
        MarkdownBlocks.Entry[] read = new MarkdownBlocks.Entry[placed.Count];
        Dictionary<string, int> taken = new(StringComparer.Ordinal);
        for (int i = 0; i < placed.Count; i++)
        {
            MarkdownBlocks.Entry? kept = byLines.TryGetValue(placed[i].Source, out Queue<MarkdownBlocks.Entry>? same) && same.TryDequeue(out MarkdownBlocks.Entry? at) ? at : null;
            string? anchor = null;
            if (placed[i].Block is Markdown.Heading heading)
            {
                // A second heading of the same words is GitHub's "-1", a third "-2".
                string slug = Markdown.Slug(Markdown.Plain(Markdown.Inlines(heading.Text)));
                anchor = taken.TryGetValue(slug, out int count) ? $"{slug}-{count}" : slug;
                taken[slug] = count + 1;
            }
            read[i] = new MarkdownBlocks.Entry(i, placed[i], anchor, kept);
        }
        return read;
    }

    private bool following;

    /// <summary>Follows the editor once the page is laid out: an offset set before then would be held to the old extent.</summary>
    private void FollowLaidOut()
    {
        if (following)
        {
            return;
        }
        following = true;
        Dispatcher.UIThread.Post(() =>
        {
            following = false;
            FollowEditor();
        }, DispatcherPriority.Background);
    }

    /// <summary>The preview scrolled so the block at the editor's top line stands at its top, a long block's part as far
    /// down it as the line is; whether it moved.</summary>
    private bool FollowEditor()
    {
        if (document is null || page is not { } shown || editor.Document != document.Text || shown.Entries.Count == 0 || !editor.IsEffectivelyVisible)
        {
            return false;
        }
        int top = editor.LinesShown.First;
        int found = top == 0 ? -1 : shown.IndexAtLine(top);
        if (found < 0)
        {
            return ScrollWithin(0);
        }
        Markdown.Placed placed = shown.Entries[found].Placed;
        (double at, double height) = shown.Drawn(found);
        // The page's top in the preview's scroll: its margin.
        double y = Offset.Y + shown.Bounds.Top;
        if (top < placed.End)
        {
            y += at + (height * (top - placed.Line) / Math.Max(1, placed.End - placed.Line));
        }
        else
        {
            // A blank line after a block: the next block comes up to the top.
            y += found + 1 < shown.Entries.Count ? shown.Drawn(found + 1).Top : at + height;
        }
        return ScrollWithin(y);
    }

    /// <summary>Scrolls the preview to a place, as far as the page reaches; whether it moved.</summary>
    private bool ScrollWithin(double y)
    {
        Vector to = new(0, Math.Max(0, Math.Min(y, Extent.Height - Viewport.Height)));
        if (to == Offset)
        {
            return false;
        }
        Vector before = Offset;
        page?.Jump();
        Offset = to;
        return Offset != before;
    }

    /// <summary>A link clicked: a web address in the person's browser, a heading of this file scrolled to, another file
    /// opened in the editor (one written from the folder's root when it begins with a slash).</summary>
    private void Follow(Uri target)
    {
        if (target.IsAbsoluteUri)
        {
            area.Browse(target);
            return;
        }
        string written = target.OriginalString;
        int hash = written.IndexOf('#', StringComparison.Ordinal);
        string path = hash >= 0 ? written[..hash] : written;
        string anchor = hash >= 0 ? Uri.UnescapeDataString(written[(hash + 1)..]) : "";
        if (path.Length == 0)
        {
            ScrollTo(anchor);
            return;
        }
        if (FileOf(path) is { } file)
        {
            area.OpenLinked(file);
        }
    }

    /// <summary>Scrolls to a heading by its anchor, as written or as GitHub makes one of the words written.</summary>
    public bool ScrollTo(string anchor)
    {
        if (page is not { } shown)
        {
            return false;
        }
        string made = Markdown.Slug(anchor);
        int index = -1;
        for (int i = 0; i < shown.Entries.Count && index < 0; i++)
        {
            index = shown.Entries[i].Anchor == anchor ? i : -1;
        }
        for (int i = 0; i < shown.Entries.Count && index < 0; i++)
        {
            index = shown.Entries[i].Anchor == made ? i : -1;
        }
        if (index < 0)
        {
            return false;
        }
        ScrollWithin(Offset.Y + shown.Bounds.Top + shown.Drawn(index).Top);
        return true;
    }

    /// <summary>The file a link or an image names from the previewed file's folder, or from the open folder's root for one
    /// that begins with a slash; null for a link that names none.</summary>
    private string? FileOf(string written)
    {
        if (document is null)
        {
            return null;
        }
        string relative = Uri.UnescapeDataString(written.Split('?', 2)[0]);
        string rest = relative.TrimStart('/');
        // Still rooted (a drive, a share's \\host): none of the folder's, and resolving it would reach that host.
        if (Path.IsPathRooted(rest) || rest.StartsWith('\\'))
        {
            return null;
        }
        string? from = relative.StartsWith('/') ? area.Root() : Path.GetDirectoryName(document.Path);
        if (from is null)
        {
            return null;
        }
        try
        {
            return PathNames.Canonical(Path.Combine(from, rest));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The file of an image the preview draws: one of an image type, there, inside the open folder once its links
    /// are resolved; never a web address, which nothing here fetches.</summary>
    private string? ImageFile(string target)
    {
        if (area.Root() is not { } root || MarkdownPage.Local(target) is not { } written)
        {
            return null;
        }
        return FileOf(written) is { } file && AssetPreview.KindOf(file) == AssetKind.Image && PathNames.IsUnder(file, PathNames.Canonical(root)) && File.Exists(file)
            ? file
            : null;
    }

    /// <summary>A picture's decode off the window's thread, once while the preview is shown, its size and pixels held
    /// to an image preview's limits; null when it could not be read.</summary>
    private async Task<Bitmap?> LoadAsync(string file)
    {
        if (!pictures.TryGetValue(file, out Task<AssetPreview?>? loading))
        {
            loading = BackgroundWork.Run(() =>
            {
                try
                {
                    return AssetPreview.Load(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return (AssetPreview?)null;
                }
            });
            pictures[file] = loading;
        }
        Dictionary<string, Task<AssetPreview?>> asked = pictures;
        AssetPreview? picture = await loading;
        if (asked != pictures)
        {
            // Cleared while it was read: nothing shows it now.
            picture?.Dispose();
            return null;
        }
        return picture?.Bitmap;
    }
}
