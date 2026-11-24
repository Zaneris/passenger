using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.CSharp;
using Passenger.Core;

namespace Passenger.App.ToolWindows;

/// <summary>A row of the Clones window: a group of clones, open or not, or one clone under it.</summary>
public sealed class CloneRow(CloneSet set, ClonePlace? clone) : Observable, ITreeRow
{
    private bool expanded;

    public CloneSet Set { get; } = set;

    /// <summary>The clone it lists; null for its group's row.</summary>
    public ClonePlace? Clone { get; } = clone;

    public int Depth => Clone is null ? 0 : 1;

    public bool Expands => Clone is null;

    public bool IsExpanded
    {
        get => expanded;
        internal set => Set(ref expanded, value);
    }

    /// <summary>The group's clones' rows, made once.</summary>
    internal List<CloneRow> Below { get; } = [];
}

/// <summary>The Clones window: the groups of code clones a search found, the most code repeated first, each saying how
/// alike its clones are (exact, or renamed and what share of their tokens is written alike), how many there are and how
/// many lines each spans, with each clone's file, lines and member under it. A clone is gone to by Enter, F4 or a
/// double-click, its statements chosen; a group's clones are compared side by side, and Extract Method makes them share one
/// method where they can. Its side bar finds them again; its summary says what the search looked through and came to.</summary>
public sealed class ClonesView : DockPanel
{
    private readonly TreeRows<CloneRow> rows = [];
    private readonly TreeList list;
    private readonly EmptyState placeholder;
    private readonly TextBlock summary = MutedText.Caption(string.Empty);
    private List<CloneRow> top = [];
    private string? folder;

    public ClonesView()
    {
        summary.TextWrapping = TextWrapping.Wrap;
        summary.Margin = new Thickness(Gaps.Wide, Gaps.Small, Gaps.Wide, Gaps.Small);
        DockPanel.SetDock(summary, Dock.Top);
        Children.Add(ToolBars.Side(
            ToolButtons.Of(AppActions.FindClones, IconKeys.Rerun, "Find Code Clones", Request),
            ToolButtons.Of(AppActions.CompareClones, IconKeys.Diff, "Compare Clones Side by Side", Request),
            ToolButtons.Of(AppActions.ExtractClones, IconKeys.Wand, "Extract Method from Clones…", Request)));
        Children.Add(summary);
        list = new TreeList { Classes = { StyleClasses.Tree }, ItemsSource = rows };
        ReusedRows.Use(list, Row);
        TreeKeys.Attach<CloneRow>(list, Toggle, row =>
        {
            if (row.Clone is { } clone)
            {
                GoToRequested?.Invoke(this, clone);
            }
            return row.Clone is not null;
        });
        ListMenus.Attach(list, () => list.SelectedItem is not CloneRow chosen ? [] : chosen.Clone is { } clone
            ?
            [
                ListMenus.Source(() => GoToRequested?.Invoke(this, clone)),
                ListMenus.Keyed("Compare with First Clone", AppActions.CompareClones, () => Request(AppActions.CompareClones),
                    ReferenceEquals(clone, chosen.Set.Clones[0]) ? Refusals.FirstClone : null),
                ListMenus.Keyed("Extract Method from Clones…", AppActions.ExtractClones, () => Request(AppActions.ExtractClones), WhyNotExtracted?.Invoke(chosen.Set)),
                ListMenus.Item("Copy Path", () => IdeErrors.Observe(ListMenus.CopyAsync(this, clone.Path))),
            ]
            :
            [
                ListMenus.Keyed("Compare Clones Side by Side", AppActions.CompareClones, () => Request(AppActions.CompareClones)),
                ListMenus.Keyed("Extract Method from Clones…", AppActions.ExtractClones, () => Request(AppActions.ExtractClones), WhyNotExtracted?.Invoke(chosen.Set)),
            ]);
        list.SelectionChanged += (_, _) => ChosenChanged?.Invoke(this, EventArgs.Empty);
        placeholder = new EmptyState();
        Children.Add(new Panel { Children = { list, placeholder } });
        Show(null, null);
    }

    /// <summary>A side bar's button or a menu's item pressed: the app action it runs.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>A clone to go to, its statements chosen.</summary>
    public event EventHandler<ClonePlace>? GoToRequested;

    /// <summary>The row chosen changed.</summary>
    public event EventHandler? ChosenChanged;

    /// <summary>Why a group's clones can't share one method, where that is known; null where they can or it is not known yet.</summary>
    public Func<CloneSet, string?>? WhyNotExtracted { get; set; }

    private void Request(string action) => ActionRequested?.Invoke(this, action);

    /// <summary>The rows listed, as the groups opened leave them.</summary>
    public IReadOnlyList<CloneRow> Rows => rows;

    /// <summary>What the search found, as shown; null before any.</summary>
    public FoundClones? Found { get; private set; }

    /// <summary>What the window says over the rows: what the search looked through and came to.</summary>
    public string Summary => summary.Text ?? string.Empty;

    /// <summary>What the window says in place of rows; null while it lists some.</summary>
    public string? Said => placeholder.IsVisible ? placeholder.Text : null;

    /// <summary>The group of the row chosen, and its clone where the row is one.</summary>
    public (CloneSet? Set, ClonePlace? Clone) Chosen => list.SelectedItem is CloneRow chosen ? (chosen.Set, chosen.Clone) : (null, null);

    /// <summary>Chooses a group's row, or a clone's under it, opening the group.</summary>
    public void Choose(CloneSet set, ClonePlace? clone = null)
    {
        CloneRow? group = top.FirstOrDefault(r => ReferenceEquals(r.Set, set));
        if (group is null)
        {
            return;
        }
        if (clone is not null && !group.IsExpanded)
        {
            Toggle(group);
        }
        list.SelectedItem = clone is null ? group : group.Below.FirstOrDefault(r => ReferenceEquals(r.Clone, clone));
    }

    /// <summary>Shows what a search found, the first group open, its files shown as the open folder names them; none says
    /// how to search.</summary>
    public void Show(FoundClones? found, string? openFolder)
    {
        UiThread.Check();
        Found = found;
        folder = openFolder;
        summary.Text = found?.Said ?? string.Empty;
        summary.IsVisible = summary.Text.Length > 0;
        top = [];
        foreach (CloneSet set in found?.Sets ?? [])
        {
            CloneRow row = new(set, null);
            row.Below.AddRange(set.Clones.Select(c => new CloneRow(set, c)));
            top.Add(row);
        }
        if (top.Count > 0)
        {
            top[0].IsExpanded = true;
        }
        placeholder.IsVisible = top.Count == 0;
        list.IsVisible = top.Count > 0;
        if (found is null)
        {
            placeholder.SayWithKeys(keys => $"{ActionTips.Named("Find Code Clones", keys(AppActions.FindClones))} lists here the code repeated across the solution");
        }
        else
        {
            placeholder.Text = found.Problem ?? (found.Sets.Count == 0 ? "No code is repeated there" : null);
        }
        Relist();
        list.SelectedItem = rows.FirstOrDefault();
    }

    /// <summary>Opens or closes a group's row, as a click on its chevron does.</summary>
    public void Toggle(CloneRow row)
    {
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            rows.Close(row);
        }
        else if (row.Expands)
        {
            row.IsExpanded = true;
            rows.Open(row, row.Below);
        }
    }

    /// <summary>Every group opened.</summary>
    public void ExpandAll()
    {
        top.ForEach(r => r.IsExpanded = true);
        Relist();
    }

    /// <summary>Every group closed.</summary>
    public void CollapseAll()
    {
        top.ForEach(r => r.IsExpanded = false);
        Relist();
    }

    private void Relist()
    {
        CloneRow? chosen = list.SelectedItem as CloneRow;
        rows.Clear();
        rows.InsertRange(0, TreeRows<CloneRow>.Shown(top, r => r.Below));
        list.SelectedItem = chosen is not null && rows.Contains(chosen) ? chosen : null;
    }

    /// <summary>A row for whichever row the list hands its container: a group's chevron, how alike its clones are and how
    /// many of how many lines; a clone's file and lines and the member it is in.</summary>
    private Control Row()
    {
        RowExpander chevron = RowExpander.Of<CloneRow>(Toggle);
        DockPanel.SetDock(chevron, Dock.Left);
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock detail = MutedText.Of(string.Empty);
        detail.Margin = new Thickness(Gaps.Normal, 0, 0, 0);
        DockPanel.SetDock(name, Dock.Left);
        DockPanel row = new() { Children = { chevron, name, detail } };
        return row.Showing<CloneRow>(shown =>
        {
            chevron.Expands = shown.Expands;
            chevron.IsOpen = shown.IsExpanded;
            row.Margin = new Thickness(shown.Depth * Gaps.Indent, 0, 0, 0);
            name.Classes.Set(StyleClasses.Strong, shown.Clone is null);
            if (shown.Clone is { } clone)
            {
                name.Text = clone.Shown(folder);
                detail.Text = clone.Member;
                ToolTip.SetTip(row, $"{clone.Path}:{clone.Start.Line + 1}–{clone.End.Line + 1}\n{clone.Member}");
            }
            else
            {
                CloneSet set = shown.Set;
                name.Text = set.Kind;
                detail.Text = $"{Plural.Of(set.Clones.Count, "clone")} of {Plural.Of(set.Lines, "line")}";
                ToolTip.SetTip(row, $"{set.Kind}: {Plural.Of(set.Clones.Count, "clone")}, each {Plural.Of(set.Statements, "statement")} and {Plural.Of(set.Tokens, "token")}");
            }
        });
    }
}
