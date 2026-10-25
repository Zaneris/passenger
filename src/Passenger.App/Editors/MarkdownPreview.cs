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
/// <see cref="Markdown"/>. It follows the text as typing pauses, read off the window's thread, and keeps the control of
/// every block whose lines did not change, so a key in a long README costs the window the blocks it changed; and it
/// follows the editor's scroll, the block at the editor's top line at its own top. Its web links open through the
/// window's browser, a link to a file beside it opens that file, a <c>#heading</c> scrolls to the heading, and it draws
/// the images of files inside the open folder alone, the rest as their words.</summary>
public sealed class MarkdownPreview : ScrollViewer
{
    /// <summary>How long typing pauses before the text is read again.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    private readonly CodeEditor editor;
    private readonly EditorArea area;
    private readonly StackPanel page;
    private readonly MarkdownPage links;
    private readonly Debounce typing;
    private OpenDocument? document;
    private List<Entry> entries = [];
    private int generation;

    /// <summary>Each picture's decode by its file, kept while the preview is shown and let go as it goes.</summary>
    private Dictionary<string, Task<AssetPreview?>> pictures = new(PathNames.Comparer);

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    internal MarkdownPreview(CodeEditor editor, EditorArea area)
    {
        this.editor = editor;
        this.area = area;
        Classes.Add("markdownPreview");
        Focusable = true;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        page = new StackPanel { Classes = { "mdPage" } };
        Content = page;
        links = new MarkdownPage(Follow) { LocalLinks = true, ImageFile = ImageFile, Load = LoadAsync };
        typing = new Debounce(Pause, RenderAsync);
        editor.ScrollChanged += (_, _) => FollowEditor();
        // A block that grew (a picture read) moves those under it: they follow once laid out, the scroll's extent with them.
        page.SizeChanged += (_, _) => FollowLaidOut();
    }

    /// <summary>A top-level block drawn: the lines it was read from, its control and, for a heading, its anchor.</summary>
    private sealed record Entry(Markdown.Placed Placed, Control Control, string? Anchor);

    /// <summary>The file it previews, null for none.</summary>
    public OpenDocument? Document => document;

    /// <summary>The top-level blocks' controls, in order.</summary>
    public IReadOnlyList<Control> Blocks => [.. entries.Select(e => e.Control)];

    /// <summary>The words drawn, a block of words a line.</summary>
    public string Text => string.Join('\n', page.GetLogicalDescendants().OfType<MarkdownText>().Select(b => b.Shown));

    /// <summary>The control of the block whose lines hold a line of the file (from zero), null for a line of none.</summary>
    public Control? BlockAt(int line) => entries.FirstOrDefault(e => line >= e.Placed.Line && line < e.Placed.End)?.Control;

    /// <summary>The first line (from zero) of the block a control draws.</summary>
    public int LineOf(Control block) => entries.First(e => e.Control == block).Placed.Line;

    /// <summary>The heading of an anchor (GitHub's: its words in lower case, hyphens for spaces), null for none.</summary>
    public Control? Heading(string anchor) => entries.FirstOrDefault(e => e.Anchor == anchor)?.Control;

    /// <summary>Where the preview's scroll stands with a block at its top.</summary>
    public double TopOf(Control block) => block.TranslatePoint(default, page) is { } at ? Offset.Y + page.Bounds.Top + at.Y : 0;

    /// <summary>Shows a file's text rendered, or nothing; a file shown again is read again only as its text changes.</summary>
    internal void Show(OpenDocument? next)
    {
        if (next == document)
        {
            return;
        }
        Clear();
        document = next;
        if (onScreen)
        {
            Hear(next);
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
        if (document is not null && entries.Count == 0)
        {
            IdeErrors.Observe(RenderAsync());
        }
    }

    /// <summary>Off screen it holds nothing it drew: its controls, highlighters and pictures go, the file it shows kept to
    /// read again as it comes back.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        onScreen = false;
        Hear(null);
        Clear();
    }

    private void OnTextChanged(object? sender, DocumentChangedEventArgs e) => typing.Restart();

    /// <summary>A file moved or renamed is read again, since its links and pictures are found from where it is.</summary>
    private void OnDocumentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenDocument.Path))
        {
            Clear();
            IdeErrors.Observe(RenderAsync());
        }
    }

    private void Clear()
    {
        typing.Stop();
        generation++;
        page.Children.Clear();
        entries = [];
        Dictionary<string, Task<AssetPreview?>> drawn = pictures;
        pictures = new(PathNames.Comparer);
        foreach (Task<AssetPreview?> picture in drawn.Values.Where(p => p.IsCompletedSuccessfully))
        {
            picture.Result?.Dispose();
        }
    }

    /// <summary>Reads the text off the window's thread, then puts in the controls of the blocks that changed.</summary>
    private async Task RenderAsync()
    {
        if (document is not { } shown || !onScreen)
        {
            return;
        }
        int asked = ++generation;
        TextSnapshot text = shown.Text.Snapshot;
        string[] kept = [.. entries.Select(e => e.Placed.Source)];
        Reading read = await BackgroundWork.Run(() => Read(text.ToString(), kept));
        if (asked != generation || document != shown)
        {
            return;
        }
        Apply(read);
    }

    /// <summary>A text's blocks, the block drawn that each may keep (by its lines as written, in order; -1 for none), and
    /// each heading's anchor.</summary>
    private sealed record Reading(IReadOnlyList<Markdown.Placed> Placed, int[] Kept, string?[] Anchors);

    private static Reading Read(string text, string[] drawn)
    {
        IReadOnlyList<Markdown.Placed> placed = Markdown.ParsePlaced(text);
        Dictionary<string, Queue<int>> byLines = new(StringComparer.Ordinal);
        for (int i = 0; i < drawn.Length; i++)
        {
            if (!byLines.TryGetValue(drawn[i], out Queue<int>? same))
            {
                byLines[drawn[i]] = same = new Queue<int>();
            }
            same.Enqueue(i);
        }
        int[] kept = new int[placed.Count];
        string?[] anchors = new string?[placed.Count];
        Dictionary<string, int> taken = new(StringComparer.Ordinal);
        for (int i = 0; i < placed.Count; i++)
        {
            kept[i] = byLines.TryGetValue(placed[i].Source, out Queue<int>? same) && same.TryDequeue(out int at) ? at : -1;
            if (placed[i].Block is Markdown.Heading heading)
            {
                // A second heading of the same words is GitHub's "-1", a third "-2".
                string slug = Markdown.Slug(Markdown.Plain(Markdown.Inlines(heading.Text)));
                anchors[i] = taken.TryGetValue(slug, out int count) ? $"{slug}-{count}" : slug;
                taken[slug] = count + 1;
            }
        }
        return new Reading(placed, kept, anchors);
    }

    /// <summary>The page's controls as the text's blocks are: a kept block's own, a changed block's made now, and only the
    /// stretch between what stands as it was before and after changed in the page.</summary>
    private void Apply(Reading read)
    {
        List<Entry> next = new(read.Placed.Count);
        for (int i = 0; i < read.Placed.Count; i++)
        {
            Control control = read.Kept[i] >= 0 ? entries[read.Kept[i]].Control : Markdown.Control(read.Placed[i].Block, links);
            next.Add(new Entry(read.Placed[i], control, read.Anchors[i]));
        }
        Controls children = page.Children;
        int before = 0;
        while (before < children.Count && before < next.Count && children[before] == next[before].Control)
        {
            before++;
        }
        int after = 0;
        while (after < children.Count - before && after < next.Count - before && children[^(after + 1)] == next[^(after + 1)].Control)
        {
            after++;
        }
        children.RemoveRange(before, children.Count - before - after);
        children.InsertRange(before, next.Skip(before).Take(next.Count - before - after).Select(e => e.Control));
        entries = next;
        FollowLaidOut();
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
    /// down it as the line is.</summary>
    private void FollowEditor()
    {
        if (document is null || editor.Document != document.Text || entries.Count == 0 || !editor.IsEffectivelyVisible)
        {
            return;
        }
        int top = editor.LinesShown.First;
        if (top == 0)
        {
            Offset = default;
            return;
        }
        int low = 0;
        int high = entries.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (entries[middle].Placed.Line <= top)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        if (found < 0)
        {
            Offset = default;
            return;
        }
        Entry entry = entries[found];
        double y;
        if (top < entry.Placed.End)
        {
            y = TopOf(entry.Control) + (entry.Control.Bounds.Height * (top - entry.Placed.Line) / Math.Max(1, entry.Placed.End - entry.Placed.Line));
        }
        else
        {
            // A blank line after a block: the next block comes up to the top.
            y = found + 1 < entries.Count ? TopOf(entries[found + 1].Control) : TopOf(entry.Control) + entry.Control.Bounds.Height;
        }
        Offset = new Vector(0, Math.Max(0, Math.Min(y, Extent.Height - Viewport.Height)));
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
        if ((Heading(anchor) ?? Heading(Markdown.Slug(anchor))) is not { } heading)
        {
            return false;
        }
        Offset = new Vector(0, Math.Max(0, Math.Min(TopOf(heading), Extent.Height - Viewport.Height)));
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
        string written = target.Trim().Trim('<', '>');
        if (area.Root() is not { } root || written.Length == 0 || written.StartsWith("//", StringComparison.Ordinal)
            || (!written.StartsWith('/') && Uri.TryCreate(written, UriKind.Absolute, out _)))
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
