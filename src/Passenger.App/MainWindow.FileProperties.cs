using Avalonia.Controls;
using Avalonia.Interactivity;
using Passenger.App.Workspace;
using Passenger.Text;
using Passenger.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The line separator and encoding: the status bar's two widgets, and File, File Encoding and Line Separators.
/// A line separator chosen converts every break and writes the file. An encoding chosen is taken at once when nothing
/// changes; otherwise a question, Reload (the file's bytes read again in it) or Convert (the text written in it),
/// warning before either loses something. Each is one undoable step, its undo and redo reaching the file as it
/// did.</summary>
public partial class MainWindow
{
    private void AddFileProperties()
    {
        actions!.Add(new IdeAction(A.FileEncoding, "File Encoding", "IDE", _ => ShowEncodingMenu())
        {
            WhyNot = Unchangeable,
            Menus = [new MenuPlace("File", 2, 0)],
        });
        for (int i = 0; i < LineSeparators.All.Count; i++)
        {
            LineSeparator separator = LineSeparators.All[i];
            // The file's own is checked (MenuChoice), and choosing it again changes nothing.
            actions.Add(new IdeAction(LineSeparatorAction(separator), separator.Title(), "Line Separators", context =>
            {
                if (context.File!.Separator != separator)
                {
                    ConvertLineSeparators(context.File, separator);
                }
            })
            {
                WhyNot = Unchangeable,
                IsChecked = context => context.File?.Separator == separator,
                Menus = [new MenuPlace("File/Line Separators", 0, i)],
            });
        }
        Editors.Opened += (_, document) =>
        {
            document.WriteFailed += (_, problem) => status.Text = problem;
            // The widgets' tips say what the .editorconfig asks, read again on a change to it or a save.
            document.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OpenDocument.Conventions) && Editors.Active == document)
                {
                    status.ShowStorage(document);
                }
            };
        };
    }

    private static string LineSeparatorAction(LineSeparator separator) => separator switch
    {
        LineSeparator.CRLF => A.LineSeparatorsCrlf,
        LineSeparator.CR => A.LineSeparatorsCr,
        _ => A.LineSeparatorsLf,
    };

    /// <summary>Why the file's line separator and encoding cannot change: none is open, or it is read-only.</summary>
    private static string? Unchangeable(ActionContext context) =>
        context.File is not { } file ? Refusals.NoFile : file.IsReadOnly ? $"{file.Name} is read-only" : null;


    /// <summary>The line separator popup: the Line Separators menu, the file's own checked.</summary>
    private void OnLineSeparatorClicked(object? sender, RoutedEventArgs e)
    {
        ActionContext context = actions!.Context();
        List<Control> items = [.. LineSeparators.All.Select(s => FlyoutEntry(new MenuEntry(LineSeparatorAction(s)), context))];
        Open(new ContextMenu { ItemsSource = items, Placement = PlacementMode.TopEdgeAlignedRight }, LineSeparatorButton);
    }

    private void OnEncodingClicked(object? sender, RoutedEventArgs e) => Request(A.FileEncoding);

    /// <summary>The encoding popup over the status bar's widget: the encodings it lists first, then More, the file's
    /// own checked. Choosing one asks, when it changes anything, whether to reload the file in it or convert the file
    /// to it.</summary>
    private void ShowEncodingMenu()
    {
        if (Editors.Active is not { } document)
        {
            return;
        }
        string current = document.EncodingName;
        // Doubled, Shift_JIS's underscore is shown rather than taken for an access key.
        MenuItem Item(FileEncoding encoding) => MenuChoice.Of(encoding.Name.Replace("_", "__", StringComparison.Ordinal), encoding.Name == current,
            () => IdeErrors.Observe(ChooseEncodingAsync(document, encoding)));
        MenuItem more = new() { Header = "More" };
        foreach (FileEncoding encoding in FileEncoding.More)
        {
            more.Items.Add(Item(encoding));
        }
        List<Control> items = [.. FileEncoding.First.Select(Item), new Separator(), more];
        Open(new ContextMenu { ItemsSource = items, Placement = PlacementMode.TopEdgeAlignedRight }, EncodingButton);
    }

    /// <summary>An encoding chosen: taken at once when the bytes read as the same text and the text writes as the same
    /// bytes, or when only a UTF-8 BOM comes or goes (Add BOM, Remove BOM); otherwise Reload or Convert, as the person
    /// answers, each warned against first when it would lose something.</summary>
    private async Task ChooseEncodingAsync(OpenDocument document, FileEncoding to)
    {
        if (document.IsReadOnly)
        {
            status.Text = $"{document.Name} is read-only";
            return;
        }
        string from = document.EncodingName;
        EncodingChange change = document.Weigh(to);
        try
        {
            if (EncodingChange.OnlyBom(document.Encoding, to))
            {
                document.ConvertTo(to);
                status.Text = $"{document.Name} was converted to {to.Name}";
                return;
            }
            if (change is { Reload: Safety.Same, Convert: Safety.Same })
            {
                document.TakeEncoding(to);
                status.Text = $"{document.Name}'s encoding is {to.Name} now: its text and its bytes are the same in it";
                return;
            }
            string? answer = await Choose($"{document.Name}: Reload or Convert to {to.Name}",
                $"The encoding you've chosen ('{to.Name}') may change the contents of '{document.Name}'. Do you want to\n"
                + $"1. Reload the file from disk in the new encoding '{to.Name}' and overwrite editor contents or\n"
                + "2. Convert the text and overwrite file in the new encoding?",
                ["Reload", "Convert"], null);
            if (answer == "Reload")
            {
                if (change.OtherBom)
                {
                    status.Text = $"File '{document.Name}' can't be reloaded in the '{to.Name}' encoding: it starts with the byte-order mark of another";
                    return;
                }
                if (change.Reload == Safety.Unsafe && !await Confirm($"Incompatible Encoding: {to.Name}",
                    $"File '{document.Name}' most likely isn't stored in the '{to.Name}' encoding.\n\nCurrent encoding: '{from}'", "Reload Anyway"))
                {
                    return;
                }
                document.ReloadIn(to);
                status.Text = $"{document.Name} was reloaded in {to.Name}";
            }
            else if (answer == "Convert")
            {
                if (change.Convert == Safety.Unsafe && !await Confirm($"Incompatible Encoding: {to.Name}",
                    $"Please do not convert to '{to.Name}'.\n\nEncoding '{to.Name}' does not support some characters from the text, '{change.Unheld}' the first of them: converting writes each as '?'.",
                    "Convert Anyway"))
                {
                    return;
                }
                document.ConvertTo(to);
                status.Text = $"{document.Name} was converted to {to.Name}";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Nothing changed: the file could not be read again, or written.
            status.Text = $"{document.Name} is still {from}: {error.Message}";
        }
    }

    /// <summary>Converts the line separators: the file saved first, then every break made the one chosen and the file
    /// written so.</summary>
    private void ConvertLineSeparators(OpenDocument document, LineSeparator to)
    {
        LineSeparator from = document.Separator;
        if (document.IsModified && !Editors.Save(document))
        {
            return;
        }
        try
        {
            document.ConvertLineSeparators(to);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            status.Text = $"{document.Name} could not be saved: {error.Message}";
            return;
        }
        status.Text = $"Changed Line Separators from {from} to {to}";
    }

    /// <summary>Asks which of several ways to go on (Reload or Convert), the answer the button's words or null for Cancel;
    /// <paramref name="chosen"/> is the one Enter takes, null for Cancel.</summary>
    private Task<string?> Choose(string title, string text, IReadOnlyList<string> choices, string? chosen) =>
        new ChoiceDialog(title, text, choices, chosen).ShowDialog<string?>(this);
}
