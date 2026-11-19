using System.Collections;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>A tree's keys pressed as a person presses them, the same in every tree window: on a row with rows under it,
/// Left closes it if it is open, Right opens it, Down goes to the first row under it, Left goes back up to it, Left again
/// closes it, and Enter, on a row that stands for nothing to open, opens it again. A screen reader meets the rows, through
/// their peers, as a tree's items open or closed as they are, the first row under one opened a level deeper and first of
/// the rows beside it.</summary>
internal static class TreeKeysCheck
{
    /// <summary>Presses the keys on <paramref name="row"/> of the list showing <paramref name="rows"/>.</summary>
    public static void Press<T>(TopLevel window, IEnumerable rows, T row, string tree, bool enterOpens = true)
        where T : class, ITreeRow =>
        Press(window, rows, row, r => r, tree, enterOpens);

    /// <summary>Presses the keys on <paramref name="row"/> of the list showing <paramref name="rows"/>, whose items' rows
    /// <paramref name="rowOf"/> reads; <paramref name="enterOpens"/> false where Enter opens what the row stands for.</summary>
    public static void Press<T>(TopLevel window, IEnumerable rows, T row, Func<T, ITreeRow> rowOf, string tree, bool enterOpens = true)
        where T : class
    {
        ListBox list = window.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, rows));
        Assert.True(rowOf(row).Expands, $"The row chosen in {tree} has no rows under it to open");
        Choose(list, row);
        if (rowOf(row).IsExpanded)
        {
            Ui.Press(window, "Left");
            Assert.False(Current(list, row, rowOf).IsExpanded, $"Left on an open row of {tree} did not close it");
            row = Chosen(list, row);
        }
        Said(list, row, rowOf, tree);
        Ui.Press(window, "Right");
        row = Chosen(list, row);
        Assert.True(rowOf(row).IsExpanded, $"Right on a closed row of {tree} did not open it");
        int at = list.Items.IndexOf(row);
        T first = at + 1 < list.ItemCount && list.Items[at + 1] is T shown && rowOf(shown).Depth > rowOf(row).Depth
            ? shown
            : throw new Xunit.Sdk.XunitException($"Right opened a row of {tree} with no rows under it shown");
        Under(list, row, first, rowOf, tree);
        Ui.Press(window, "Right");
        Assert.Same(row, list.SelectedItem);
        Ui.Press(window, "Down");
        Assert.True(list.SelectedIndex == at + 1, $"Down on an open row of {tree} did not go to the first row under it");
        if (list.SelectedItem is T child && rowOf(child).IsExpanded)
        {
            Ui.Press(window, "Left");
        }
        Ui.Press(window, "Left");
        Assert.True(ReferenceEquals(list.SelectedItem, row), $"Left on a closed row of {tree} did not go up to the row it sits under");
        Assert.True(list.ContainerFromItem(row)?.IsFocused == true, $"Left up to a row of {tree} left the keys on the row it came from");
        Ui.Press(window, "Left");
        row = Chosen(list, row);
        Assert.False(rowOf(row).IsExpanded, $"Left on an open row of {tree} did not close it");
        Assert.False(at + 1 < list.ItemCount && list.Items[at + 1] is T next && rowOf(next).Depth > rowOf(row).Depth,
            $"Left closed a row of {tree} whose rows still show under it");
        Said(list, row, rowOf, tree);
        if (enterOpens)
        {
            Ui.Press(window, "Enter");
            row = Chosen(list, row);
            Assert.True(rowOf(row).IsExpanded, $"Enter on a row of {tree} that stands for nothing to open did not open it");
        }
    }

    /// <summary>The peer a screen reader asks of a row, held to saying it is open, closed, or neither as the row is.</summary>
    private static TreeList.TreeRowPeer Said<T>(ListBox list, T row, Func<T, ITreeRow> rowOf, string tree)
        where T : class
    {
        AutomationControlType whole = ControlAutomationPeer.CreatePeerForElement(list).GetAutomationControlType();
        Assert.True(list is TreeList && whole == AutomationControlType.Tree, $"A screen reader meets {tree} as a {whole}, not a tree");
        list.ScrollIntoView(row);
        list.UpdateLayout();
        Control container = list.ContainerFromItem(row) ?? throw new Xunit.Sdk.XunitException($"{tree} shows no row for {row}");
        TreeList.TreeRowPeer peer = Assert.IsType<TreeList.TreeRowPeer>(ControlAutomationPeer.CreatePeerForElement(container));
        ExpandCollapseState state = rowOf(row) switch
        {
            { Expands: true, IsExpanded: true } => ExpandCollapseState.Expanded,
            { Expands: true } => ExpandCollapseState.Collapsed,
            _ => ExpandCollapseState.LeafNode,
        };
        Assert.True(peer.GetAutomationControlType() == AutomationControlType.TreeItem && peer.ExpandCollapseState == state
            && peer.GetProvider<IExpandCollapseProvider>()?.ExpandCollapseState == (state != ExpandCollapseState.LeafNode || TreeList.TreeRowPeer.LeavesOffered ? state : null),
            $"A screen reader meets {tree}'s row \"{peer.GetName()}\" as a {peer.GetAutomationControlType()} that is {peer.ExpandCollapseState}"
            + $" (asked: {peer.GetProvider<IExpandCollapseProvider>()?.ExpandCollapseState.ToString() ?? "nothing"}), not a tree item that is {state}, which a reader on macOS must always be able to ask");
        return peer;
    }

    /// <summary>Holds the first row under one just opened to being said a level deeper, first of the rows beside it, as
    /// many as the rows shown under the one opened at its depth, and to giving the system that place.</summary>
    private static void Under<T>(ListBox list, T opened, T first, Func<T, ITreeRow> rowOf, string tree)
        where T : class
    {
        int depth = rowOf(opened).Depth;
        int beside = 0;
        for (int at = list.Items.IndexOf(opened) + 1; at < list.ItemCount && list.Items[at] is T each && rowOf(each).Depth > depth; at++)
        {
            beside += rowOf(each).Depth == rowOf(first).Depth ? 1 : 0;
        }
        TreeList.TreeRowPeer above = Said(list, opened, rowOf, tree);
        TreeList.TreeRowPeer under = Said(list, first, rowOf, tree);
        Control container = list.ContainerFromItem(first)!;
        Assert.True((under.Level, under.PositionInSet, under.SizeOfSet) == (above.Level + 1, 1, beside)
            && (AutomationProperties.GetPositionInSet(container), AutomationProperties.GetSizeOfSet(container)) == (1, beside),
            $"The first row under one opened in {tree} is said at level {under.Level}, {under.PositionInSet} of {under.SizeOfSet}, where it stands at level "
            + $"{above.Level + 1}, 1 of {beside}");
    }

    /// <summary>Chooses a row and gives it the keys, as a click on it does.</summary>
    private static void Choose<T>(ListBox list, T row)
        where T : class
    {
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        list.UpdateLayout();
        list.ContainerFromItem(row)?.Focus(NavigationMethod.Pointer);
        Ui.Settle();
        Assert.True(list.ContainerFromItem(row)?.IsFocused == true, "The row chosen did not take the keys");
    }

    /// <summary>The row chosen now: a tree that draws a row again in its place (its chevron turned) chooses the new one.</summary>
    private static T Chosen<T>(ListBox list, T was)
        where T : class =>
        list.SelectedItem as T ?? was;

    private static ITreeRow Current<T>(ListBox list, T was, Func<T, ITreeRow> rowOf)
        where T : class =>
        rowOf(Chosen(list, was));
}
