using Passenger.App.Editors;
using Passenger.App.Workspace;

namespace Passenger.App.Vcs;

/// <summary>A diff in a tab of the editors, titled as its window would be: its document holds no text
/// (<see cref="OpenDocument.Diff"/>), so it is never its file's tab, and each group showing it makes its own view. It holds
/// open the file its right side edits, and closes with that file's last tab or once the file closes for good. Asked again,
/// it takes what the diff is now. One the Commit window opened follows the file chosen there (<see cref="Follows"/>).</summary>
public sealed class DiffTab : IDiffHost
{
    private readonly EditorArea editors;
    private readonly Action<DiffView, IDiffHost> wire;
    private IDisposable? hold;

    /// <param name="wire">What the window hangs on each view made (Jump to Source).</param>
    /// <param name="follows">Whether it follows the file chosen in the Commit window, until it is kept.</param>
    public DiffTab(EditorArea editors, DiffContent shown, Action<DiffView, IDiffHost> wire, bool follows = false)
    {
        this.editors = editors;
        this.wire = wire;
        Shown = shown;
        Follows = follows;
        hold = shown.File is { } file ? editors.Hold(file) : null;
        Document = OpenDocument.OfDiff(this);
        editors.Closed += OnFileGone;
        editors.TabsGone += OnFileGone;
    }

    public DiffContent Shown { get; private set; }

    /// <summary>The tab's document, which every group showing the diff has a tab of.</summary>
    public OpenDocument Document { get; }

    public DiffView? View => editors.ActiveGroup.Active == Document ? editors.ActiveGroup.Diff
        : editors.Groups.FirstOrDefault(g => g.Active == Document)?.Diff;

    /// <summary>A view for a group that shows the tab.</summary>
    public DiffView Make()
    {
        DiffView view = Shown.Make();
        wire(view, this);
        view.Right.SelectionChanged += (_, _) => Caret = view.Right.Selections.Primary.Caret;
        view.Edited += (_, _) => Keep();
        return view;
    }

    /// <summary>Whether it follows the file chosen in the Commit window, taking that file's diff in place of its own as the
    /// choice moves, its title drawn aslant; kept (<see cref="Keep"/>) once its tab is pinned or the file edited in it,
    /// it shows its own file from then on, and the next diff the Commit window opens comes in a tab of its own.</summary>
    public bool Follows { get; private set; }

    /// <summary>Keeps it: it follows the Commit window's choice no more.</summary>
    public void Keep()
    {
        if (Follows)
        {
            Follows = false;
            Document.FollowsChanged();
        }
    }

    /// <summary>Where the file's side's caret was last, in the file's text: the tab's place, which Back comes back to.</summary>
    public int Caret { get; private set; }

    /// <summary>The diff asked for again: what it is now, which the groups showing it show afresh.</summary>
    public void Take(DiffContent shown)
    {
        IDisposable? held = shown.File is { } file ? editors.Hold(file) : null;
        hold?.Dispose();
        hold = held;
        Shown = shown;
        Document.DiffChanged();
    }

    public void Close() => editors.Close(Document);

    public void GiveWay()
    {
    }

    /// <summary>The tab closed: its file is let go, which closes once no tab has it.</summary>
    internal void LetGo()
    {
        editors.Closed -= OnFileGone;
        editors.TabsGone -= OnFileGone;
        hold?.Dispose();
        hold = null;
    }

    private void OnFileGone(object? sender, OpenDocument file)
    {
        if (file == Shown.File)
        {
            Close();
        }
    }
}
