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

    /// <summary>What the path shows, so a caret that moves within the same member leaves its buttons as they are, and the
    /// parts wanted, worked out into a list kept for it.</summary>
    private readonly List<Crumb> shownCrumbs = [];
    private readonly List<Crumb> wantedCrumbs = [];

    /// <summary>The file's parts of the path (its last folders and its name), worked out once for the file and folder.</summary>
    private (string Path, string? Folder, Crumb[] Parts) fileCrumbs = (string.Empty, null, []);

    /// <summary>The path's buttons and the marks between them, made as the longest path shown needs them and given each
    /// path's words in place; those past the path shown are hidden.</summary>
    private readonly List<(TextBlock Between, Button Crumb)> crumbButtons = [];

    /// <summary>The path shown, part by part.</summary>
    public IReadOnlyList<string> Breadcrumbs => [.. breadcrumbs.Children.OfType<Button>().Where(b => b.IsVisible).Select(b => b.Content as string ?? string.Empty)];

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

    /// <summary>Asks for the outline again as a file is opened in the language server or its state changes (the projects
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
        List<Crumb> wanted = wantedCrumbs;
        wanted.Clear();
        if (Editors.Active is { } document)
        {
            wanted.AddRange(FileCrumbs(document.Path));
            if (outlined == document.Path)
            {
                ServerLines lines = ServerLines.Of(document.Text.Snapshot);
                int line = lines.PositionOf(Editors.CodeEditor.Selections.Primary.Caret).Line;
                IReadOnlyList<DocumentSymbol> level = outline;
                while (OutlineHolding(level, line) is { } symbol)
                {
                    // The namespace is the file's own context; the path names what is in it.
                    if (symbol.Kind != SymbolKind.Namespace)
                    {
                        wanted.Add(new Crumb(symbol.Name, document.Path, lines.Text.GetPosition(lines.OffsetOf(symbol.SelectionRange.Start)), Muted: false));
                    }
                    level = symbol.Children ?? [];
                }
            }
        }
        if (wanted.SequenceEqual(shownCrumbs))
        {
            return;
        }
        shownCrumbs.Clear();
        shownCrumbs.AddRange(wanted);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i == crumbButtons.Count)
            {
                AddCrumbButton();
            }
            (TextBlock between, Button crumb) = crumbButtons[i];
            Crumb part = wanted[i];
            between.IsVisible = i > 0;
            crumb.IsVisible = true;
            crumb.Content = part.Target is null ? part.Text : NameOf(part.Text);
            crumb.Opacity = part.Muted ? 0.75 : 1;
        }
        for (int i = wanted.Count; i < crumbButtons.Count; i++)
        {
            crumbButtons[i].Between.IsVisible = false;
            crumbButtons[i].Crumb.IsVisible = false;
        }
    }

    /// <summary>The file's parts of the path: its last two folders under the open folder (a deep path would push the rest of
    /// the status bar off) and its name, worked out once for each file.</summary>
    private Crumb[] FileCrumbs(string path)
    {
        if (fileCrumbs.Path == path && fileCrumbs.Folder == Folder)
        {
            return fileCrumbs.Parts;
        }
        string relative = Folder is { } root && PathNames.IsUnder(path, root)
            ? Path.GetRelativePath(root, path)
            : Path.GetFileName(path);
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        List<Crumb> made = [];
        for (int i = Math.Max(0, parts.Length - 3); i < parts.Length - 1; i++)
        {
            made.Add(new Crumb(parts[i], Path.Combine([Folder ?? string.Empty, .. parts[..(i + 1)]]), null, Muted: true));
        }
        made.Add(new Crumb(parts[^1], path, null, Muted: false));
        fileCrumbs = (path, Folder, [.. made]);
        return fileCrumbs.Parts;
    }

    /// <summary>The symbol of a level of the outline whose lines hold a line, the first; null for none.</summary>
    private static DocumentSymbol? OutlineHolding(IReadOnlyList<DocumentSymbol> level, int line)
    {
        for (int i = 0; i < level.Count; i++)
        {
            if (level[i].Range.Start.Line <= line && line <= level[i].Range.End.Line)
            {
                return level[i];
            }
        }
        return null;
    }

    /// <summary>A member's name as the path shows it: without the base types its outline name may give after " : ".</summary>
    private static string NameOf(string name) => name.IndexOf(" : ", StringComparison.Ordinal) is var colon and >= 0 ? name[..colon] : name;

    /// <summary>A button of the path and the mark before it, which a click on reads the part shown there when clicked.</summary>
    private void AddCrumbButton()
    {
        int index = crumbButtons.Count;
        TextBlock between = new() { Text = "›", Classes = { "status" }, Margin = new Avalonia.Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
        Button crumb = new() { Classes = { "crumb" } };
        crumb.Click += (_, _) =>
        {
            if (index >= shownCrumbs.Count)
            {
                return;
            }
            Crumb part = shownCrumbs[index];
            if (part.Target is { } target)
            {
                NavigateTo(part.Path, target);
            }
            else
            {
                Explorer.Reveal(part.Path);
            }
        };
        breadcrumbs.Children.Add(between);
        breadcrumbs.Children.Add(crumb);
        crumbButtons.Add((between, crumb));
    }

    /// <summary>A part of the path: a folder (muted) or the file, which a click shows in the Explorer, or what the caret is
    /// in, which it goes to, named as its outline names it.</summary>
    private readonly record struct Crumb(string Text, string Path, TextPosition? Target, bool Muted);
}
