using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.CSharp;

/// <summary>An address written in the shown file's comment or string, as its editor finds it (<see cref="CodeEditor.LinkOn"/>):
/// where the file's language declares nothing there, Go to Declaration follows it and the hover card says where it leads
/// and how it is followed. First on the ladder, so a link in any language is a link, yet a language's own answer at it (an
/// Aspire service's address, which goes to the AppHost adding it) comes first.</summary>
internal sealed class LinkAssist(AssistContext context) : LanguageAssist
{
    public override async Task<bool> DefinitionAsync(int offset)
    {
        if (context.Editors.Active is not { } shown || LinkIn(shown, offset) is not { } link || await context.Languages.DeclaresAsync(shown, offset))
        {
            return false;
        }
        if (context.Editors.Active == shown)
        {
            context.Follow(link.Target);
        }
        return true;
    }

    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (LinkIn(document, offset)?.Target is not LinkTarget target || await context.Languages.DeclaresAsync(document, offset))
        {
            return false;
        }
        cancel.ThrowIfCancellationRequested();
        card.AddText(target switch
        {
            WebLink web => web.Address.AbsoluteUri,
            ProgramFileLink file => file.Path,
            _ => string.Empty,
        });
        if (context.FollowKeys() is { } keys)
        {
            card.AddText($"Go to Declaration ({keys}) opens it");
        }
        return true;
    }

    /// <summary>The link at an offset of a file the editor shows; null for another file, or where there is none.</summary>
    private TextLink? LinkIn(OpenDocument? document, int offset) =>
        document is not null && ReferenceEquals(context.Editor.Document, document.Text) ? context.Editor.LinkOn(offset) : null;
}
