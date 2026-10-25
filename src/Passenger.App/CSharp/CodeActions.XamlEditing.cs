using System.Text.Json;
using Passenger.App.Editors;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Features;
using Passenger.Xaml.Syntax;

namespace Passenger.App.CSharp;

/// <summary>The XAML refactorings, Avalonia's and WPF's alike: Extract Resource and Extract Style with their dialogs, and
/// the one way a refactoring's or fix's changes to files are made. What changes more than one file, or makes one, is one
/// command Undo takes back whole.</summary>
internal sealed partial class CodeActions
{
    /// <summary>Extract Resource (Extract XAML Resource): the value at the caret to a keyed resource, through its
    /// dialog.</summary>
    public async Task ExtractResource()
    {
        const string name = "Extract Resource";
        ExtractResourceDialog? dialog = null;
        // Held until the dialog shows, which then waits for the person.
        using (BackgroundWork.Scope())
        {
            if (await XamlAtCaret(name) is not ({ } file, int caret))
            {
                return;
            }
            (ExtractableValue? value, string? why) = await BackgroundWork.Run(() => XamlRefactorings.ValueAt(file, caret));
            if (value is null)
            {
                context.Say(why ?? $"{name} is not available here");
                return;
            }
            Func<string, string?> texts = context.Editors.Texts().Text;
            IReadOnlyList<ResourcePlace> places = await BackgroundWork.Run(() => XamlRefactorings.Places(file, styles: false, texts));
            dialog = new ExtractResourceDialog(value, places, key => XamlRefactorings.KeyProblem(file, key), (key, place, everywhere) =>
            {
                string? refused = ApplyFileChanges(name, XamlRefactorings.ExtractResource(file, value, key, place, everywhere));
                if (refused is null)
                {
                    int replaced = everywhere ? value.Uses.Count : 1;
                    context.Say($"Extracted resource '{key}' to {place.Title}{(replaced > 1 ? $", used in {replaced} places" : string.Empty)}");
                }
                return Task.FromResult(refused);
            });
        }
        if (dialog is not null)
        {
            await ShowAsync(dialog);
        }
    }

    /// <summary>Extract Style (Extract XAML Style): the element's properties at the caret to a style, through its
    /// dialog.</summary>
    public async Task ExtractStyle()
    {
        const string name = "Extract Style";
        ExtractStyleDialog? dialog = null;
        using (BackgroundWork.Scope())
        {
            if (await XamlAtCaret(name) is not ({ } file, int caret))
            {
                return;
            }
            (StyleSource? source, string? why) = await BackgroundWork.Run(() => XamlRefactorings.StyleAt(file, caret));
            if (source is null)
            {
                context.Say(why ?? $"{name} is not available here");
                return;
            }
            Func<string, string?> texts = context.Editors.Texts().Text;
            IReadOnlyList<ResourcePlace> places = await BackgroundWork.Run(() => XamlRefactorings.Places(file, styles: source.ByClass, texts));
            dialog = new ExtractStyleDialog(source, places, styleName => XamlRefactorings.StyleNameProblem(file, source, styleName), (styleName, chosen, place) =>
            {
                string? refused = ApplyFileChanges(name, XamlRefactorings.ExtractStyle(file, source, styleName, chosen, place));
                if (refused is null)
                {
                    context.Say($"Extracted {Core.Plural.Of(chosen.Count, "property", "properties")} of {source.ElementName} to style '{styleName}' in {place.Title}");
                }
                return Task.FromResult(refused);
            });
        }
        if (dialog is not null)
        {
            await ShowAsync(dialog);
        }
    }

    /// <summary>The shown XAML file read as it stands and the caret; null, said in the status bar for
    /// <paramref name="what"/>, for a file that is not XAML of a project Passenger reads.</summary>
    private async Task<(XamlFile? File, int Caret)> XamlAtCaret(string what)
    {
        if (context.XamlCurrent is not { } xaml || context.Editors.Active is not { } document)
        {
            context.Say($"{what} works in a XAML file of an Avalonia or WPF project");
            return (null, 0);
        }
        int caret = context.Caret;
        if (await xaml.FileAsync() is not { } file)
        {
            context.Say($"{what} is not available: the project's XAML is not read");
            return (null, 0);
        }
        return context.Editors.Active == document ? (file, caret) : (null, 0);
    }

    /// <summary>A refactoring's or fix's changes to files made as one command (XAML's, an Aspire parameter's value set; Undo takes back
    /// every file, a file made included): each file's edits made from its text as it is now, open or on disk. Null once made; else why not.</summary>
    internal string? ApplyFileChanges(string name, IReadOnlyList<XamlFileChange> changes)
    {
        List<object> documentChanges = [];
        foreach (XamlFileChange change in changes)
        {
            string uri = DocumentUris.FromPath(change.Path);
            if (change.Made is { } made)
            {
                if (File.Exists(change.Path) || context.Editors.Find(change.Path) is not null)
                {
                    return $"{Path.GetFileName(change.Path)} is already there";
                }
                documentChanges.Add(new { kind = "create", uri });
                documentChanges.Add(new { textDocument = new { uri }, edits = new[] { new LspTextEdit(new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)), made) } });
                continue;
            }
            string text = context.ReadOpenOrDisk(change.Path);
            IReadOnlyList<TextEdit> edits = change.Edits?.Invoke(text) ?? [];
            if (edits.Count == 0)
            {
                return $"{Path.GetFileName(change.Path)} is no longer as {name} found it";
            }
            documentChanges.Add(new { textDocument = new { uri }, edits = ServerPlaces.EditsOf(TextSnapshot.From(text), edits) });
        }
        JsonElement edit = JsonSerializer.SerializeToElement(new { documentChanges }, Json.Options);
        List<string> dropped = [];
        try
        {
            TypeRename.Apply(context.Editors, edit, [], name, null, dropped);
        }
        catch (WorkspaceEditFailed failed)
        {
            return failed.Message;
        }
        if (changes.Any(c => c.Made is not null) || changes.Any(c => context.Editors.Documents.All(d => d.Path != c.Path)))
        {
            context.ChangedFiles();
        }
        return dropped.Count > 0 ? $"{name} was not applied to {string.Join(", ", dropped)}: it was edited meanwhile" : null;
    }

    /// <summary>The refactorings offered at the caret of a XAML file (Refactor This, and after the fixes in Show
    /// Context Actions): Extract Resource on a value, Extract Style on an element.</summary>
    internal static IReadOnlyList<Choice> XamlRefactoringsAt(XamlFile file, int caret)
    {
        List<Choice> offered = [];
        if (XamlRefactorings.ValueAt(file, caret).Value is { } value)
        {
            offered.Add(new Choice("Extract Resource…", value.Property, XamlRefactoring.Resource));
        }
        if (XamlRefactorings.StyleAt(file, caret).Source is { } source)
        {
            offered.Add(new Choice("Extract Style…", source.ElementName, XamlRefactoring.Style));
        }
        return offered;
    }

    /// <summary>A XAML refactoring chosen from a list, run as its action would.</summary>
    internal Task RunXamlRefactoring(XamlRefactoring chosen) => chosen switch
    {
        XamlRefactoring.Resource => ExtractResource(),
        XamlRefactoring.Style => ExtractStyle(),
        _ => popups.Carried("Rename", () =>
        {
            Rename();
            return Task.CompletedTask;
        }, closeModal: false),
    };

    internal enum XamlRefactoring
    {
        Resource,
        Style,
        Rename,
    }
}
