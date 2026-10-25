using Passenger.App.CSharp;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Call Hierarchy and Type Hierarchy: the symbol at the caret in the Hierarchy window, its callers or callees,
/// its supertypes or subtypes, from the language server.</summary>
public partial class MainWindow
{
    private HierarchyView? hierarchy;

    /// <summary>The Hierarchy tool window, built as it is first shown or asked for.</summary>
    public HierarchyView Hierarchy => hierarchy ?? (HierarchyView)Dock[ToolWindowIds.Hierarchy].Content;

    private void AddHierarchy()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Hierarchy, "Hierarchy", IconKeys.Hierarchy, () =>
        {
            hierarchy = new HierarchyView();
            hierarchy.GoToRequested += (_, node) => NavigateTo(node.Path, node.Where);
            hierarchy.Failed += (_, why) => status.Text = why;
            return hierarchy;
        }, Docking.ToolAnchor.RightTop));
        // What the server finds at the caret, or why it found nothing, the status bar says as it answers.
        actions!.Add(new IdeAction(AppActions.CallHierarchy, "Call Hierarchy", "IDE", context => IdeErrors.Observe(ShowCallHierarchyAsync()))
        {
            WhyNot = Refusals.NeedsFile,
            Menus = [new MenuPlace("Navigate", 2, 6)],
        });
        actions.Add(new IdeAction(AppActions.TypeHierarchy, "Type Hierarchy", "IDE", context => IdeErrors.Observe(ShowTypeHierarchyAsync()))
        {
            WhyNot = Refusals.NeedsFile,
            Menus = [new MenuPlace("Navigate", 2, 7)],
        });
    }

    /// <summary>Call Hierarchy: who calls the symbol at the caret, and whom it calls; null when shown, else why not.</summary>
    public async Task<string?> ShowCallHierarchyAsync()
    {
        (HierarchyItem? item, string? why) = await Prepare("Call Hierarchy", "No method, property or event at the caret", (lsp, offset) => lsp.PrepareCallHierarchyAsync(offset));
        if (item is null)
        {
            return why;
        }
        ShowToolWindow(ToolWindowIds.Hierarchy);
        await Hierarchy.ShowAsync($"Calls of {item.Name}", Node(item, ServerPlaces.Texts(Editors)), new HierarchyDirection("Callers", async of =>
            Nodes(await RunningServer().IncomingCallsAsync(of), (call, texts) => Node(call.From, texts, call.FromRanges.FirstOrDefault()))),
            new HierarchyDirection("Callees", async of => Nodes(await RunningServer().OutgoingCallsAsync(of), (call, texts) => Node(call.To, texts))));
        return null;
    }

    /// <summary>Type Hierarchy: the type at the caret's base types and interfaces, and the types that derive from it.</summary>
    public async Task<string?> ShowTypeHierarchyAsync()
    {
        (HierarchyItem? item, string? why) = await Prepare("Type Hierarchy", "No type at the caret", (lsp, offset) => lsp.PrepareTypeHierarchyAsync(offset));
        if (item is null)
        {
            return why;
        }
        ShowToolWindow(ToolWindowIds.Hierarchy);
        await Hierarchy.ShowAsync($"Hierarchy of {item.Name}", Node(item, ServerPlaces.Texts(Editors)),
            new HierarchyDirection("Supertypes", async of => Nodes(await RunningServer().SupertypesAsync(of), (t, texts) => Node(t, texts))),
            new HierarchyDirection("Subtypes", async of => Nodes(await RunningServer().SubtypesAsync(of), (t, texts) => Node(t, texts))));
        return null;
    }

    /// <summary>The symbol at the caret as a hierarchy's root, or why there is none, said in the status bar: nothing
    /// there, the server not ready, or its failure.</summary>
    private async Task<(HierarchyItem? Item, string? Why)> Prepare(string what, string none, Func<LspDocument, int, Task<HierarchyItem?>> prepare)
    {
        if (Editors.Active is not { } active || session.CSharp?.For(active) is not { } features)
        {
            return (null, Said(none));
        }
        if (features.Lsp is not { } lsp || !session.CSharp.IsReady)
        {
            return (null, Said(session.CSharp.Unready(what)));
        }
        try
        {
            return await prepare(lsp, Editors.CodeEditor.Selections.Primary.Caret) is { } item ? (item, null) : (null, Said(none));
        }
        catch (LanguageServerFailed failed)
        {
            return (null, Said($"{what} failed: {failed.Reason}"));
        }
    }

    /// <summary>The server running now, asked for as each level of a hierarchy opens: the window may have been shown by
    /// one since started again.</summary>
    private LanguageServer RunningServer() =>
        session.CSharp?.Session?.Server ?? throw new LanguageServerFailed("the C# language server is not running");

    private string Said(string why)
    {
        status.Text = why;
        return why;
    }

    /// <summary>A level's rows, each file read once for it.</summary>
    private IReadOnlyList<HierarchyNode> Nodes<T>(IEnumerable<T> found, Func<T, Func<string, TextSnapshot?>, HierarchyNode> node)
    {
        Func<string, TextSnapshot?> texts = ServerPlaces.Texts(Editors);
        return [.. found.Select(f => node(f, texts))];
    }

    /// <summary>An item as a row, landing at a place in it (a call) or at its name, in the editor's lines of its file.</summary>
    private static HierarchyNode Node(HierarchyItem item, Func<string, TextSnapshot?> texts, LspRange? at = null)
    {
        string path = DocumentUris.ToPath(item.Uri);
        return new HierarchyNode(item, path, ServerPlaces.PositionOf(texts(path), (at ?? item.SelectionRange).Start));
    }
}
