using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The status bar's path, as Rider's new UI has it: the active file's folders and name, then the type and
/// member the caret is in, from the language server's outline (asked again as editing pauses); a click on a member goes
/// to it, on the file or a folder shows it in the Explorer.</summary>
public partial class MainWindow
{
    private readonly StackPanel breadcrumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 18, 0) };
    private IReadOnlyList<DocumentSymbol> outline = [];
    private string? outlined;
    private DispatcherTimer? outlineAgain;
    private int outlining;

    /// <summary>What the path shows, so a caret that moves within the same member leaves its buttons as they are.</summary>
    private List<Crumb> shownCrumbs = [];

    /// <summary>The path shown, part by part.</summary>
    public IReadOnlyList<string> Breadcrumbs => [.. breadcrumbs.Children.OfType<Button>().Select(b => b.Content as string ?? string.Empty)];

    private void AddBreadcrumbs()
    {
        BreadcrumbHost.Content = breadcrumbs;
        StatusContent.SizeChanged += (_, _) => FitBreadcrumbs();
        StatusWidgets.SizeChanged += (_, _) => FitBreadcrumbs();
        StatusText.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.TextProperty)
            {
                FitBreadcrumbs();
            }
        };
        StatusIcon.PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty)
            {
                FitBreadcrumbs();
            }
        };
        BreadcrumbHost.PropertyChanged += (_, change) =>
        {
            if (change.Property == ScrollViewer.ViewportProperty)
            {
                foreach (Button crumb in breadcrumbs.Children.OfType<Button>())
                {
                    crumb.MaxWidth = Math.Max(0, BreadcrumbHost.Viewport.Width);
                }
                RevealCrumb();
            }
        };
        outlineAgain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        outlineAgain.Tick += (_, _) =>
        {
            outlineAgain.Stop();
            _ = OutlineAsync();
        };
        Editors.ActiveChanged += (_, _) =>
        {
            outline = [];
            outlined = null;
            ShowBreadcrumbs();
            outlineAgain.Stop();
            outlineAgain.Start();
        };
        Editors.CaretMoved += (_, _) => ShowBreadcrumbs();
        Editors.Opened += (_, document) => document.Text.Changed += (_, _) =>
        {
            if (Editors.Active == document)
            {
                outlineAgain.Stop();
                outlineAgain.Start();
            }
        };
    }

    /// <summary>Asks for the outline again as the language server's state changes (the files connected, the projects
    /// loaded) while the path has none for the file shown: asked before the server had the file, it found none, and the
    /// path would name no member until the next edit.</summary>
    private void OutlineWhenUnseen()
    {
        if (Editors.Active is { } document && outlined != document.Path)
        {
            _ = OutlineAsync();
        }
    }

    private async Task OutlineAsync()
    {
        int asked = ++outlining;
        if (Editors.Active is not { } document || session.CSharp?.For(document)?.Lsp is not { } lsp)
        {
            return;
        }
        try
        {
            IReadOnlyList<DocumentSymbol> found = await lsp.OutlineAsync();
            if (asked == outlining && Editors.Active == document)
            {
                outline = found;
                outlined = document.Path;
                ShowBreadcrumbs();
            }
        }
        catch (LanguageServerFailed)
        {
            // The path keeps the file's own parts; the next edit asks again.
        }
    }

    private void ShowBreadcrumbs()
    {
        List<Crumb> wanted = [];
        if (Editors.Active is { } document)
        {
            string relative = Folder is { } root && PathNames.IsUnder(document.Path, root)
                ? Path.GetRelativePath(root, document.Path)
                : Path.GetFileName(document.Path);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            // The last two folders: a deep path would push the rest of the status bar off.
            for (int i = Math.Max(0, parts.Length - 3); i < parts.Length - 1; i++)
            {
                wanted.Add(new Crumb(parts[i], Path.Combine([Folder ?? string.Empty, .. parts[..(i + 1)]]), null, Muted: true));
            }
            wanted.Add(new Crumb(parts[^1], document.Path, null, Muted: false));
            if (outlined == document.Path)
            {
                ServerLines lines = ServerLines.Of(document.Text.Snapshot);
                int line = lines.PositionOf(Editors.CodeEditor.Selections.Primary.Caret).Line;
                IReadOnlyList<DocumentSymbol> level = outline;
                while (level.FirstOrDefault(s => s.Range.Start.Line <= line && line <= s.Range.End.Line) is { } symbol)
                {
                    // The namespace is the file's own context; the path names what is in it.
                    if (symbol.Kind != 3)
                    {
                        wanted.Add(new Crumb(symbol.Name.Split(" : ")[0], document.Path, lines.Text.GetPosition(lines.OffsetOf(symbol.SelectionRange.Start)), Muted: false));
                    }
                    level = symbol.Children ?? [];
                }
            }
        }
        if (wanted.SequenceEqual(shownCrumbs))
        {
            return;
        }
        shownCrumbs = wanted;
        BreadcrumbHost.IsVisible = wanted.Count > 0;
        breadcrumbs.Children.Clear();
        for (int i = 0; i < wanted.Count; i++)
        {
            Crumb part = wanted[i];
            if (i > 0)
            {
                TextBlock between = new() { Text = "›", Classes = { "status" }, Margin = new Avalonia.Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
                breadcrumbs.Children.Add(between);
            }
            Button crumb = new()
            {
                Content = part.Text,
                ContentTemplate = new FuncDataTemplate<string>((text, _) => new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis }),
                Classes = { "crumb" },
                Opacity = part.Muted ? 0.75 : 1,
                MaxWidth = BreadcrumbHost.Viewport.Width > 0 ? BreadcrumbHost.Viewport.Width : double.PositiveInfinity,
            };
            ToolTip.SetTip(crumb, part.Target is null ? part.Path : $"{part.Text}\n{part.Path}");
            crumb.GotFocus += (_, _) => RevealCrumb();
            crumb.Click += (_, _) =>
            {
                if (part.Target is { } target)
                {
                    NavigateTo(part.Path, target);
                }
                else
                {
                    Explorer.Reveal(part.Path);
                }
            };
            breadcrumbs.Children.Add(crumb);
        }
        FitBreadcrumbs();
        RevealCrumb();
    }

    private void FitBreadcrumbs()
    {
        double available = Math.Max(0, StatusContent.Bounds.Width - StatusWidgets.Bounds.Width - (StatusIcon.IsVisible ? 20 : 0));
        // An operation's result shares the remaining space with the path; its full words stay on hover.
        BreadcrumbHost.MaxWidth = Math.Max(0, available * (string.IsNullOrEmpty(StatusText.Text) ? 1 : 0.4) - BreadcrumbHost.Margin.Right);
    }

    private void RevealCrumb() => Dispatcher.UIThread.Post(() =>
    {
        Button[] parts = [.. breadcrumbs.Children.OfType<Button>()];
        (parts.FirstOrDefault(crumb => crumb.IsKeyboardFocusWithin) ?? parts.LastOrDefault())?.BringIntoView();
    }, DispatcherPriority.Loaded);

    /// <summary>A part of the path: a folder (muted) or the file, which a click shows in the Explorer, or what the caret is
    /// in, which it goes to.</summary>
    private readonly record struct Crumb(string Text, string Path, TextPosition? Target, bool Muted);
}
