using System.ComponentModel;
using Avalonia.Controls;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>Every diff and merge a window shows, made one way whoever opens it: each pane coloured by its file's language,
/// a pane of an open file's own text in that file's colours as they change; a diff's Highlighting Differences as Settings
/// has it, a change to it kept for every diff after; and the colours made let go once the view leaves its window.</summary>
/// <param name="chosen">Highlighting Differences as Settings has it now.</param>
/// <param name="keep">Keeps the way a person chose in a diff.</param>
public sealed class DiffPanes(Func<DiffHighlighting> chosen, Action<DiffHighlighting> keep)
{
    /// <summary>A file's diff: <paramref name="diff"/>'s other text on the left in <paramref name="leftPath"/>'s colours,
    /// <paramref name="mine"/> on the right in <paramref name="rightPath"/>'s, editable while it is <paramref name="open"/>'s
    /// own text.</summary>
    public DiffView ForFile(string name, FileDiff diff, TextDocument mine, OpenDocument? open, (string Left, string Right) titles,
        string leftPath, string rightPath)
    {
        DiffView view = new(name, diff, mine, editable: open is not null, titles, chosen());
        view.HighlightingChanged += (_, way) => keep(way);
        Colour(view, [(view.Left, leftPath, null), (view.Right, rightPath, open)]);
        return view;
    }

    /// <summary>A file's three-way merge, its three panes in the file's colours.</summary>
    public static MergeView ForMerge(MergeTexts texts, string path)
    {
        MergeView view = new(texts, Path.GetFileName(path));
        Colour(view, [(view.Left, path, null), (view.Result, path, null), (view.Right, path, null)]);
        return view;
    }

    private static void Colour(Control view, IEnumerable<(CodeEditor Pane, string Path, OpenDocument? Open)> panes)
    {
        List<Action> letGo = [];
        foreach ((CodeEditor pane, string path, OpenDocument? open) in panes)
        {
            if (open is not null && ReferenceEquals(pane.Document, open.Text))
            {
                pane.Highlighter = open.Highlighter;
                void OnOpenChanged(object? sender, PropertyChangedEventArgs e)
                {
                    if (e.PropertyName == nameof(OpenDocument.Highlighter))
                    {
                        pane.Highlighter = open.Highlighter;
                    }
                }
                open.PropertyChanged += OnOpenChanged;
                letGo.Add(() => open.PropertyChanged -= OnOpenChanged);
            }
            else if (Languages.Highlighters.For(Languages.Language.ForPath(path), pane.Document) is { } colours)
            {
                pane.Highlighter = colours;
                if (colours is IDisposable owned)
                {
                    letGo.Add(owned.Dispose);
                }
            }
        }
        void OnLeft(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
        {
            view.DetachedFromVisualTree -= OnLeft;
            letGo.ForEach(go => go());
        }
        view.DetachedFromVisualTree += OnLeft;
    }
}
