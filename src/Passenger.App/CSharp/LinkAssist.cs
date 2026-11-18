using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.CSharp;

/// <summary>An address written in the shown file's comment or string, as its editor finds it (<see cref="CodeEditor.LinkOn"/>):
/// Go to Declaration follows it, and the hover card says where it leads and how it is followed. First on the ladder, so a
/// link in any language is a link.</summary>
internal sealed class LinkAssist(AssistContext context) : LanguageAssist
{
    public override Task<bool> DefinitionAsync(int offset)
    {
        if (LinkIn(context.Editors.Active, offset) is not { } link)
        {
            return Passed;
        }
        context.Follow(link.Target);
        return Answered;
    }

    public override Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (LinkIn(document, offset)?.Target is not LinkTarget target)
        {
            return Passed;
        }
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
        return Answered;
    }

    /// <summary>The link at an offset of a file the editor shows; null for another file, or where there is none.</summary>
    private TextLink? LinkIn(OpenDocument? document, int offset) =>
        document is not null && ReferenceEquals(context.Editor.Document, document.Text) ? context.Editor.LinkOn(offset) : null;
}
