using Passenger.App.Editors;
using Passenger.App.Workspace;

namespace Passenger.App.Vcs;

/// <summary>A diff in a tab of the editors, titled as its window would be: its document holds no text
/// (<see cref="OpenDocument.Diff"/>), so it is never its file's tab, and each group showing it makes its own view. It holds
/// open the file its right side edits, and closes with that file's last tab or once the file closes for good. Asked again,
/// it takes what the diff is now.</summary>
public sealed class DiffTab : IDiffHost
{
    private readonly EditorArea editors;
    private readonly Action<DiffView, IDiffHost> wire;
    private IDisposable? hold;

    /// <param name="wire">What the window hangs on each view made (Jump to Source).</param>
    public DiffTab(EditorArea editors, DiffContent shown, Action<DiffView, IDiffHost> wire)
    {
        this.editors = editors;
        this.wire = wire;
        Shown = shown;
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
        return view;
    }

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
