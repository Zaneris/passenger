using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>A Razor file's classes checked against the stylesheets its pages link (<see cref="Web.PageStylesheets"/>).</summary>
public sealed partial class CSharpDocument
{
    /// <summary>The classes a Razor file writes that no stylesheet its pages link defines, for the text as it stands; none
    /// for a C# file, without the folder's stylesheets, or while one linked could not be read.</summary>
    internal async Task<IReadOnlyList<Diagnostic>> StylesheetFindingsAsync(LspDocument lsp, CancellationToken cancel)
    {
        if (!lsp.IsRazor || service.Stylesheets is not { } stylesheets)
        {
            return [];
        }
        TextSnapshot text = document.Text.Snapshot;
        Web.PageClasses classes = await stylesheets.ClassesAsync(document.Path, cancel);
        return await BackgroundWork.Run(() => Web.PageStylesheets.Unknown(text, razor: true, classes), cancel);
    }

    /// <summary>What the pages link changed: a Razor file's classes are checked again.</summary>
    internal void StylesheetsChanged()
    {
        if (Lsp is { IsRazor: true })
        {
            Pull(TimeSpan.Zero);
        }
    }
}
