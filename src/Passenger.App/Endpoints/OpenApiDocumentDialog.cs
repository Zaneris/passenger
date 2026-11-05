using Avalonia.Controls;
using Avalonia.Threading;

namespace Passenger.App.Endpoints;

/// <summary>Name OpenAPI Document: where a web project's app serves the document its code names nowhere, a path or a URL on
/// this machine (<see cref="OpenApiAddresses.Named"/>), asked of the person; OK is pressable once what is typed is one. OK
/// closes it with <see cref="Document"/>; Cancel, Escape or a close, with none.</summary>
public sealed class OpenApiDocumentDialog : IdeWindow
{
    private readonly TextBlock why = new() { Classes = { StyleClasses.Muted }, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public OpenApiDocumentDialog(string project, string? named)
    {
        Address = new TextBox { Name = "OpenApiDocument", Classes = { StyleClasses.Search }, Text = named ?? string.Empty, PlaceholderText = OpenApiSources.Defaults[0] };
        Ok = DialogFooter.Default();
        Ok.Click += (_, _) => Accept();
        Button cancel = DialogFooter.Cancel(() => Close(false));
        Address.TextChanged += (_, _) => Check();
        DialogFooter.Question(this, $"Name {project}'s OpenAPI Document",
            [
                DialogFooter.Paragraph($"Where {project}'s app serves its OpenAPI document, which its code names nowhere: a path (/docs/v1.json), or its URL on this machine. "
                    + "Its answers in the Endpoints window are held to that document's schemas, and Generate Requests from OpenAPI reads it."),
                Address,
                why,
            ],
            DialogFooter.Of(Ok, cancel));
        Check();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => Address.Focus(), DispatcherPriority.Loaded);
    }

    public TextBox Address { get; }

    public Button Ok { get; }

    /// <summary>The document named, once OK took it: null until then, and when it was declined.</summary>
    public string? Document { get; private set; }

    private void Check()
    {
        (string? document, string? said) = OpenApiAddresses.Named(Address.Text);
        Ok.IsEnabled = document is not null;
        why.Text = (Address.Text ?? string.Empty).Trim().Length == 0 ? string.Empty : said ?? string.Empty;
    }

    private void Accept()
    {
        if (OpenApiAddresses.Named(Address.Text).Document is { } document)
        {
            Document = document;
            Close(true);
        }
    }
}
