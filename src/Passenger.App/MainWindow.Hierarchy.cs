using Passenger.App.CSharp;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Call Hierarchy, Type Hierarchy and Inspect This: the symbol at the caret in the Hierarchy window, its callers or
/// callees, its supertypes or subtypes, or where its value comes from or goes, from the language server.</summary>
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
        }, Docking.ToolAnchor.RightTop)
        {
            Serves = ToolWindowServes.EveryFolder,
            ExpandAll = () => IdeErrors.Observe(Hierarchy.ExpandAllAsync()),
            CollapseAll = () => Hierarchy.CollapseAll(),
        });
        // What the server finds at the caret, or why it found nothing, the status bar says as it answers.
        actions!.Add(new IdeAction(AppActions.CallHierarchy, ActionTitles.CallHierarchy, ActionGroups.Ide, context => IdeErrors.Observe(ShowCallHierarchyAsync()))
        {
            WhyNot = Refusals.NeedsFile,
            Menus = [new MenuPlace("Navigate", 2, 6)],
        });
        actions.Add(new IdeAction(AppActions.TypeHierarchy, ActionTitles.TypeHierarchy, ActionGroups.Ide, context => IdeErrors.Observe(ShowTypeHierarchyAsync()))
        {
            WhyNot = Refusals.NeedsFile,
            Menus = [new MenuPlace("Navigate", 2, 7)],
        });
        actions.Add(new IdeAction(AppActions.InspectThis, ActionTitles.InspectThis, ActionGroups.Ide, context => IdeErrors.Observe(InspectThisAsync()))
        {
            WhyNot = Refusals.NeedsFile,
            Menus = [new MenuPlace("Navigate", 2, 10)],
            Words = ["Value Origin", "Value Destination", "Data Flow", "Track Value Source", "Incoming Calls", "Outgoing Calls"],
        });
    }

    /// <summary>Inspect This: what the symbol at the caret offers to inspect, at the caret (where its value comes from and goes,
    /// its callers and callees, its supertypes and subtypes), the one chosen shown in the Hierarchy window; null once offered,
    /// else why not.</summary>
    public async Task<Words?> InspectThisAsync()
    {
        // Held, so what comes next in the window finds the choices offered.
        using IDisposable held = BackgroundWork.Scope();
        (Roslyn.Extension.Inspected? found, Words? why) = await InspectedAsync();
        if (found is not { Name: { } name })
        {
            return why;
        }
        List<Choice> choices = [];
        if (found.Value is { } value)
        {
            choices.Add(new Choice("Value Origins", $"where {name}'s value comes from", (Func<Task>)(() => ShowValueFlowAsync(name, value, destinations: false))));
            choices.Add(new Choice("Value Destinations", $"where {name}'s value goes", (Func<Task>)(() => ShowValueFlowAsync(name, value, destinations: true))));
        }
        if (found.Calls)
        {
            choices.Add(new Choice("Callers", $"what calls {name}", (Func<Task>)(() => ShowCallHierarchyAsync())));
            choices.Add(new Choice("Callees", $"what {name} calls", (Func<Task>)(() => ShowCallHierarchyAsync(callees: true))));
        }
        if (found.Types)
        {
            choices.Add(new Choice("Supertypes", $"what {name} derives from", (Func<Task>)(() => ShowTypeHierarchyAsync())));
            choices.Add(new Choice("Subtypes", $"what derives from {name}", (Func<Task>)(() => ShowTypeHierarchyAsync(subtypes: true))));
        }
        if (choices.Count == 0)
        {
            return Said(Refusals.NothingToInspect(name));
        }
        assist.OfferAtCaret("Inspect This", choices, choice => choice?.Value is Func<Task> show ? show() : Task.CompletedTask);
        return null;
    }

    /// <summary>What the extension finds to inspect at the caret, or why nothing, said in the status bar.</summary>
    private async Task<(Roslyn.Extension.Inspected? Found, Words? Why)> InspectedAsync()
    {
        const string what = "Inspect This";
        if (Editors.Active is not { } active || session.CSharp is not { } csharp || (csharp.For(active) is null && !GeneratedCode.IsCopy(active.Path)))
        {
            return (null, Said(Refusals.NothingToInspect(null)));
        }
        CSharpDocument? features = csharp.For(active);
        if (!csharp.IsReady || features is { Lsp: null })
        {
            return (null, Said(csharp.Unready(what, active)));
        }
        try
        {
            (LspDocument? lsp, string? refused) = features is null ? await csharp.Generated.AskableAsync(active) : (features.Lsp, null);
            if (lsp is null || Editors.Active != active)
            {
                return (null, Said(refused ?? Refusals.NothingToInspect(null)));
            }
            Roslyn.Extension.Inspected found = await lsp.ExtensionAsync<Roslyn.Extension.Inspected>(Roslyn.Extension.Handlers.InspectAt,
                new Roslyn.Extension.Caret(lsp.PlaceOf(Editors.CodeEditor.Selections.Primary.Caret)));
            return found.Name is null ? (null, Said(Refusals.NothingToInspect(null))) : (found, null);
        }
        catch (LanguageServerFailed failed)
        {
            return (null, Said($"{what} failed: {failed.Reason}"));
        }
    }

    /// <summary>A value's flow in the Hierarchy window: its holder at the top, and under it where its value comes from, the
    /// server's own value tracking, or where it goes, each place opened on to where its own value comes from or goes.</summary>
    public async Task<Words?> ShowValueFlowAsync(string name, Roslyn.Extension.ValueFlowItem value, bool destinations)
    {
        // Held, so what comes next in the window finds the first level listed.
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            HierarchyItem root = ItemOf(value);
            HierarchyNode top = (await NodesAsync([(root, root.SelectionRange)]))[0];
            ShowToolWindow(ToolWindowIds.Hierarchy);
            await Hierarchy.ShowAsync($"Value of {name}", top, new HierarchyDirection("Origins", of => ValueLevelAsync(of, origins: true)),
                new HierarchyDirection("Destinations", of => ValueLevelAsync(of, origins: false)), first: !destinations);
            return null;
        }
        catch (LanguageServerFailed failed)
        {
            return Said($"Inspect This failed: {failed.Reason}");
        }
    }

    /// <summary>One level of a value's flow, where the item's value comes from or goes.</summary>
    private async Task<IReadOnlyList<HierarchyNode>> ValueLevelAsync(HierarchyItem of, bool origins)
    {
        if (of.Data is not { } data || System.Text.Json.JsonSerializer.Deserialize<Roslyn.Extension.ValueFlowItem>(data) is not { } asked)
        {
            return [];
        }
        Roslyn.Extension.ValueFlowFound found = await Running().ExtensionAsync<Roslyn.Extension.ValueFlowFound>(Roslyn.Extension.Handlers.ValueFlow,
            new Roslyn.Extension.ValueFlowAsked(asked, origins));
        return await NodesAsync([.. found.Items.Select(ItemOf).Select(i => (i, i.SelectionRange))]);
    }

    /// <summary>A place of a value's flow as a hierarchy's item, the place itself its data to ask on from.</summary>
    private static HierarchyItem ItemOf(Roslyn.Extension.ValueFlowItem place)
    {
        LspRange range = new(new LspPosition(place.From.Line, place.From.Character), new LspPosition(place.To.Line, place.To.Character));
        return new HierarchyItem
        {
            Name = place.Name,
            Kind = (SymbolKind)place.Kind,
            Detail = place.Detail,
            Uri = DocumentUris.FromPath(place.Path),
            Range = range,
            SelectionRange = range,
            Data = System.Text.Json.JsonSerializer.SerializeToElement(place),
        };
    }

    /// <summary>Call Hierarchy: who calls the symbol at the caret, and whom it calls (first with <paramref name="callees"/>);
    /// null when shown, else why not.</summary>
    public async Task<Words?> ShowCallHierarchyAsync(bool callees = false)
    {
        (HierarchyNode? top, Words? why) = await Prepare("Call Hierarchy", "No method, property or event at the caret", (lsp, offset) => lsp.PrepareCallHierarchyAsync(offset));
        if (top is null)
        {
            return why;
        }
        ShowToolWindow(ToolWindowIds.Hierarchy);
        await Hierarchy.ShowAsync($"Calls of {top.Item.Name}", top, new HierarchyDirection("Callers", async of =>
            await NodesAsync([.. (await Running().IncomingCallsAsync(of)).Select(call => (call.From, call.FromRanges.FirstOrDefault() ?? call.From.SelectionRange))])),
            new HierarchyDirection("Callees", async of => await NodesAsync([.. (await Running().OutgoingCallsAsync(of)).Select(call => (call.To, call.To.SelectionRange))])), first: !callees);
        return null;
    }

    /// <summary>Type Hierarchy: the type at the caret's base types and interfaces, and the types that derive from it (first with
    /// <paramref name="subtypes"/>).</summary>
    public async Task<Words?> ShowTypeHierarchyAsync(bool subtypes = false)
    {
        (HierarchyNode? top, Words? why) = await Prepare("Type Hierarchy", "No type at the caret", (lsp, offset) => lsp.PrepareTypeHierarchyAsync(offset));
        if (top is null)
        {
            return why;
        }
        ShowToolWindow(ToolWindowIds.Hierarchy);
        await Hierarchy.ShowAsync($"Hierarchy of {top.Item.Name}", top,
            new HierarchyDirection("Supertypes", async of => await NodesAsync([.. (await Running().Server.SupertypesAsync(of)).Select(t => (t, t.SelectionRange))])),
            new HierarchyDirection("Subtypes", async of => await NodesAsync([.. (await Running().Server.SubtypesAsync(of)).Select(t => (t, t.SelectionRange))])), first: !subtypes);
        return null;
    }

    /// <summary>The symbol at the caret as a hierarchy's root, in a file of the server's or a generator's code copied, or
    /// why there is none, said in the status bar: nothing there, the server not ready, or its failure.</summary>
    private async Task<(HierarchyNode? Top, Words? Why)> Prepare(string what, string none, Func<LspDocument, int, Task<HierarchyItem?>> prepare)
    {
        if (Editors.Active is not { } active || session.CSharp is not { } csharp || (csharp.For(active) is null && !GeneratedCode.IsCopy(active.Path)))
        {
            return (null, Said(none));
        }
        CSharpDocument? features = csharp.For(active);
        if (!csharp.IsReady || features is { Lsp: null })
        {
            return (null, Said(csharp.Unready(what, active)));
        }
        try
        {
            (LspDocument? lsp, string? refused) = features is null ? await csharp.Generated.AskableAsync(active) : (features.Lsp, null);
            if (lsp is null || Editors.Active != active)
            {
                return (null, Said(refused ?? none));
            }
            return await prepare(lsp, Editors.CodeEditor.Selections.Primary.Caret) is { } item ? ((await NodesAsync([(item, item.SelectionRange)]))[0], null) : (null, Said(none));
        }
        catch (LanguageServerFailed failed)
        {
            return (null, Said($"{what} failed: {failed.Reason}"));
        }
    }

    /// <summary>The server running now, asked for as each level of a hierarchy opens: the window may have been shown by
    /// one since started again.</summary>
    private RoslynSession Running() =>
        session.CSharp?.Session ?? throw new LanguageServerFailed(Refusals.CSharpNotRunning.Shown);

    private Words Said(Words why)
    {
        status.Text = why.Shown;
        return why;
    }

    /// <summary>A level's rows, each landing at its place (a call, or the item's name) in the editor's lines of the file
    /// its code was written in (<see cref="RoslynSession.WrittenAsync"/>), each file read once for it.</summary>
    private async Task<IReadOnlyList<HierarchyNode>> NodesAsync(IReadOnlyList<(HierarchyItem Item, LspRange At)> found)
    {
        IReadOnlyList<LspLocation> places = await Running().WrittenAsync([.. found.Select(f => new LspLocation(f.Item.Uri, f.At))]);
        if (session.CSharp is { } csharp)
        {
            places = await csharp.Generated.CopiedAsync(places);
        }
        Func<string, TextSnapshot?> texts = ServerPlaces.Texts(Editors);
        return [.. found.Select((f, i) =>
        {
            string path = DocumentUris.ToPath(places[i].Uri);
            return new HierarchyNode(f.Item, path, ServerPlaces.PositionOf(texts(path), places[i].Range.Start));
        })];
    }
}
