using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The status bar's path: the active file's folders and name, then the type and member the caret is in, from
/// the language server's outline (asked again as editing pauses); a click on a member goes to it, on the file or a
/// folder shows it in the Explorer.</summary>
public partial class MainWindow
{
    private readonly StackPanel breadcrumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 18, 0) };
    private IReadOnlyList<DocumentSymbol> outline = [];
    private string? outlined;
    private Debounce? outlineAgain;
    private int outlining;

    /// <summary>What the path shows, so a caret that moves within the same member leaves its buttons as they are.</summary>
    private List<Crumb> shownCrumbs = [];

    /// <summary>The path shown, part by part.</summary>
    public IReadOnlyList<string> Breadcrumbs => [.. breadcrumbs.Children.OfType<Button>().Select(b => b.Content as string ?? string.Empty)];

    private void AddBreadcrumbs()
    {
        BreadcrumbHost.Children.Add(breadcrumbs);
        outlineAgain = new Debounce(TimeSpan.FromMilliseconds(400), OutlineAsync);
        Editors.ActiveChanged += (_, _) =>
        {
            outline = [];
            outlined = null;
            ShowBreadcrumbs();
            OutlineLater();
        };
        Editors.CaretMoved += (_, _) => ShowBreadcrumbs();
        Editors.Opened += (_, document) => document.Text.Changed += (_, _) =>
        {
            if (Editors.Active == document)
            {
                OutlineLater();
            }
        };
    }

    /// <summary>Asks for the outline once editing pauses, for a file the C# server has or will have (a C# or Razor file
    /// shown before its server, or its service, is up): a file no server serves gives none, so no pause is waited out
    /// for it.</summary>
    private void OutlineLater()
    {
        if (Editors.Active is { } document && (session.CSharp?.For(document)?.Lsp is not null
            || StartsLanguageServer && document.Language.Service == Languages.LanguageService.CSharp))
        {
            outlineAgain?.Restart();
        }
    }

    /// <summary>Asks for the outline again as the language server's state changes (the files connected, the projects
    /// loaded) while the path has none for the file shown: asked before the server had the file, it found none, and the
    /// path would name no member until the next edit.</summary>
    private void OutlineWhenUnseen()
    {
        if (Editors.Active is { } document && outlined != document.Path)
        {
            IdeErrors.Observe(OutlineAsync());
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
                    if (symbol.Kind != SymbolKind.Namespace)
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
        breadcrumbs.Children.Clear();
        for (int i = 0; i < wanted.Count; i++)
        {
            Crumb part = wanted[i];
            if (i > 0)
            {
                TextBlock between = new() { Text = "›", Classes = { "status" }, Margin = new Avalonia.Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
                breadcrumbs.Children.Add(between);
            }
            Button crumb = new() { Content = part.Text, Classes = { "crumb" }, Opacity = part.Muted ? 0.75 : 1 };
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
    }

    /// <summary>A part of the path: a folder (muted) or the file, which a click shows in the Explorer, or what the caret is
    /// in, which it goes to.</summary>
    private readonly record struct Crumb(string Text, string Path, TextPosition? Target, bool Muted);
}
