using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Going from code to code: Go to Declaration and Find Usages as the file's language answers them, Go to
/// Implementation, Go to Type Declaration, Base and Derived Symbols through the server's type hierarchy, and the next or
/// previous error; straight to one place, else the places to choose from.</summary>
internal sealed class CodeNavigation(AssistContext context, PopupPlacement popups, QuickInfo quickInfo)
{
    private CodeEditor Editor => context.Editor;

    public async Task GoToDefinition(int offset)
    {
        using (BackgroundWork.Scope())
        {
            await context.Languages.DefinitionAsync(offset);
        }
    }

    public async Task GoToImplementation(int offset)
    {
        using (BackgroundWork.Scope())
        {
            if (context.Ask("Go to Implementation") is not { } lsp)
            {
                return;
            }
            try
            {
                Go(await CopiedAsync(await lsp.ImplementationAsync(offset)), offset, "Implementations", "No implementations found");
            }
            catch (LanguageServerFailed failed)
            {
                context.Failed("Go to Implementation", failed);
            }
        }
    }

    public Task GoToTypeDeclaration(int offset) => GoToRelated("Type Declaration", offset,
        async (lsp, at) => [.. (await lsp.TypeDefinitionAsync(at)).Select(l => new RelatedSymbols.Target(l))], "Cannot find a type declaration to go to");

    public Task GoToBaseSymbols(int offset) => GoToRelated("Base Symbols", offset, (lsp, at) => RelatedSymbols.BaseAsync(lsp, at, context.NameAt(at), context.Syntax, () => context.CSharp?.Session), "No base symbols found");

    public Task GoToDerivedSymbols(int offset) => GoToRelated("Derived Symbols", offset, (lsp, at) => RelatedSymbols.DerivedAsync(lsp, at, context.NameAt(at), () => context.CSharp?.Session), "No derived symbols found");

    /// <summary>Goes to what the server finds from the symbol at an offset (its type's declaration, its base or derived
    /// symbols): straight there when it is one place, else the places to choose from; the status bar says when none.</summary>
    private async Task GoToRelated(string what, int offset, Func<LspDocument, int, Task<IReadOnlyList<RelatedSymbols.Target>>> find, string none)
    {
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            if (context.Ask(what) is { } lsp)
            {
                TextDocument asked = Editor.Document;
                string name = context.NameAt(offset);
                IReadOnlyList<RelatedSymbols.Target> found = await find(lsp, offset);
                IReadOnlyList<LspLocation> places = await CopiedAsync([.. found.Select(t => t.Location)]);
                // The editor shows another file now: the places were for the one it showed.
                if (Editor.Document == asked)
                {
                    Go(places, offset, name.Length > 0 ? $"{what} of {name}" : what, none, [.. found.Select(t => t.Name)]);
                }
            }
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed(what, failed);
        }
    }

    /// <summary>Find Usages, the usages in the Find window, or Show Usages, in a popup at the offset.</summary>
    public async Task FindUsages(int offset, bool inPopup = false)
    {
        using (BackgroundWork.Scope())
        {
            await context.Languages.UsagesAsync(offset, inPopup);
        }
    }

    /// <summary>Find Usages through the language server: the usages of the name at an offset, with a C# symbol's in XAML,
    /// for the Find window or the popup, which names the files come under the projects that could use it that the server
    /// has not taken in yet.</summary>
    public async Task ServerUsagesAsync(int offset, bool inPopup)
    {
        int asked = offset;
        if (context.Editors.Active is not { } shown)
        {
            return;
        }
        IReadOnlyList<(string Path, string Project)> untaken;
        using (ChangeTracker waited = new(shown.Text))
        {
            untaken = await context.UntakenAsync(shown, () => waited.MapOffset(offset));
            offset = waited.MapOffset(offset);
        }
        if (context.Ask("Find Usages") is not { } lsp)
        {
            return;
        }
        try
        {
            IReadOnlyList<LspLocation> served = await lsp.ReferencesAsync(offset);
            // A Godot member's uses by its other names hold those of the generated one the offset may be on, which the server found too.
            IReadOnlyList<LspLocation> usages = [.. served, .. await context.Languages.Xaml.CodeUsesAsync(shown, lsp, offset),
                .. DocumentUris.Besides(await context.Languages.CSharp.GodotUsesAsync(shown, lsp, offset), served)];
            if (usages.Count == 0)
            {
                context.Say("No usages found");
                return;
            }
            int start = context.WordStart(offset);
            int end = offset;
            while (end < Editor.Document.Snapshot.Length && AssistContext.IsIdentifierChar(Editor.Document.Snapshot[end]))
            {
                end++;
            }
            string name = Editor.Document.Snapshot.GetText(start, end - start);
            context.ShowUsages($"Usages of {(name.Length > 0 ? name : "symbol")}", usages, inPopup ? asked : null,
                untaken.Count > 0 ? Refusals.Untaken([.. untaken.Select(u => Path.GetFileName(u.Path))]) : null);
        }
        catch (LanguageServerFailed failed)
        {
            context.Failed("Find Usages", failed);
        }
    }

    /// <summary>Where a declaration's answer leads: its page opened, else its places (<see cref="Go(IReadOnlyList{LspLocation}, int, string, string, IReadOnlyList{string?}?)"/>).</summary>
    public void Go(Declared declared, int offset)
    {
        if (declared.Page is { } page)
        {
            context.Browse(page);
            return;
        }
        Go(declared.Places, offset, declared.Title, declared.None, declared.Names);
    }

    /// <summary>Straight to the one place, else the places to choose from, in a popup or kept in the Find window; the status
    /// bar says <paramref name="none"/> when there are none.</summary>
    public void Go(IReadOnlyList<LspLocation> locations, int offset, string title, string none, IReadOnlyList<string?>? names = null)
    {
        if (locations.Count == 0)
        {
            context.Say(none);
        }
        else if (locations.Count == 1)
        {
            Arrive(locations[0]);
        }
        else if (context.KeepsPlaces())
        {
            context.ShowUsages(title, locations, popupAt: null);
        }
        else
        {
            Pick(title, locations, offset, names);
        }
    }

    /// <summary>The places to choose from: a type by its name with its file and line beside it, any other place by its file
    /// and line with its line of code beside it.</summary>
    private void Pick(string title, IReadOnlyList<LspLocation> locations, int offset, IReadOnlyList<string?>? names = null)
    {
        string root = context.Root ?? string.Empty;
        Dictionary<string, string> paths = DocumentUris.ToPaths(locations.Select(l => l.Uri));
        Func<string, LspPosition, (int Line, string Code)> preview = LinePreviews();
        List<Choice> choices = locations.Select((l, i) =>
        {
            string path = paths[l.Uri];
            (int line, string code) = preview(path, l.Range.Start);
            string shown = $"{PathNames.Shown(path, root)}:{line + 1}";
            return names?[i] is { } name
                ? new Choice(name, shown, l)
                : new Choice(shown, code, l, DetailIsCode: true);
        }).ToList();
        popups.ShowModal(new ChoicePopup(title, choices), offset, title, choice =>
        {
            if (choice?.Value is LspLocation location)
            {
                Arrive(location);
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>Places the server named, each in a source generator's code taken to its copy (<see cref="GeneratedCode"/>).</summary>
    private async Task<IReadOnlyList<LspLocation>> CopiedAsync(IReadOnlyList<LspLocation> places) =>
        context.CSharp is { } csharp ? await csharp.Generated.CopiedAsync(places) : places;

    /// <summary>Opens a place, a source generator's code that could not be copied said to be so.</summary>
    public void Arrive(LspLocation location)
    {
        if (DocumentUris.GeneratedName(location.Uri) is { } generated)
        {
            context.Say($"{generated} is code a source generator made, which the C# language server did not give");
            return;
        }
        string path = DocumentUris.ToPath(location.Uri);
        context.Navigate((path, ServerPlaces.PositionOf(context.Editors, path, location.Range.Start)));
    }

    /// <summary>The line of each of a list of places, in the editor's lines, and its code, each closed file read once for
    /// the list, not once a place, since it is read on the UI thread; an open file's from its text.</summary>
    public Func<string, LspPosition, (int Line, string Code)> LinePreviews()
    {
        Func<string, TextSnapshot?> texts = ServerPlaces.Texts(context.Editors);
        return (path, at) =>
        {
            TextSnapshot? text = texts(path);
            TextPosition place = ServerPlaces.PositionOf(text, at);
            return (place.Line, text is not null && place.Line < text.LineCount ? text.GetLineText(place.Line).Trim() : string.Empty);
        };
    }

    /// <summary>The next (or previous) error or warning after the caret, wrapping.</summary>
    public void StepToMarker(bool forward)
    {
        List<Marker> problems = Editor.Markers.Where(m => m.Kind is MarkerKind.Error or MarkerKind.Warning).ToList();
        if (problems.Count == 0)
        {
            context.Say("No errors or warnings in this file");
            return;
        }
        int caret = Editor.Selections.Primary.Caret;
        Marker target = forward
            ? problems.FirstOrDefault(m => m.Range.Start > caret, problems[0])
            : problems.LastOrDefault(m => m.Range.Start < caret, problems[^1]);
        Editor.Selections = SelectionSet.Single(target.Range.Start);
        IdeErrors.Observe(quickInfo.Show(target.Range.Start, Editor.PointOf(target.Range.Start)));
    }
}
