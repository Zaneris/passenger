using Passenger.App.CSharp;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App;

/// <summary>Find Usages in the Find window: the language server's usages told apart by what each does there (read from
/// the file's syntax), put under their projects and files, each search kept as a tab.</summary>
public partial class MainWindow
{
    private FindView? find;

    /// <summary>The Find tool window, built as it is first shown or asked for.</summary>
    public FindView Find => find ?? (FindView)Dock[ToolWindowIds.Find].Content;

    private void AddFind()
    {
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Find, "Find", IconKeys.Search, () =>
        {
            find = new FindView(path => Editors.TextOf(path)?.ToString());
            find.GoToRequested += (_, usage) => NavigateTo(usage.Path, new TextPosition(usage.Line, usage.Column));
            return find;
        }, Docking.ToolAnchor.BottomLeft) { Action = AppActions.ShowFind, ActionTitle = "Find Tool Window" });
        assist.UsagesFound += (_, found) => ShowUsages(found.Title, found.Usages);
    }

    /// <summary>Bumped by each search shown, so one that lands after a later one is dropped.</summary>
    private int usageSearches;

    private void ShowUsages(string title, IReadOnlyList<LspLocation> locations) => IdeErrors.Observe(ShowUsagesAsync(title, locations));

    /// <summary>The usages under what each does there, read from each file's syntax off the UI thread: an open file's
    /// shared parse as it stands, a file not open read from disk and parsed there.</summary>
    private async Task ShowUsagesAsync(string title, IReadOnlyList<LspLocation> locations)
    {
        int search = ++usageSearches;
        // Each file's path made canonical once, not once a usage: that asks the disk.
        Dictionary<string, string> paths = locations.Select(l => l.Uri).Distinct(StringComparer.Ordinal).ToDictionary(u => u, DocumentUris.ToPath, StringComparer.Ordinal);
        List<(string Path, TextSnapshot? Open, ParsedText? Syntax, string Project, List<LspLocation> Here)> files =
            [.. locations.GroupBy(l => paths[l.Uri], StringComparer.Ordinal).Select(file =>
            {
                OpenDocument? open = Editors.Find(file.Key);
                return (file.Key, open?.Text.Snapshot, open?.Syntax?.Current, ProjectNameOf(file.Key), file.ToList());
            })];
        List<Usage> usages = await BackgroundWork.Run(() =>
        {
            List<Usage> found = [];
            foreach ((string path, TextSnapshot? open, ParsedText? syntax, string project, List<LspLocation> here) in files)
            {
                if ((syntax?.Text ?? open ?? ServerPlaces.Read(path)) is not { } snapshot)
                {
                    // A file that cannot be read keeps the server's places, the best there is to say of them.
                    found.AddRange(here.Select(l => new Usage(path, l.Range.Start.Line, l.Range.Start.Character,
                        l.Range.End.Line == l.Range.Start.Line ? Math.Max(0, l.Range.End.Character - l.Range.Start.Character) : 0, string.Empty, UsageKind.Other, project)));
                    continue;
                }
                ServerLines lines = ServerLines.Of(snapshot);
                List<int> offsets = [.. here.Select(l => lines.OffsetOf(l.Range.Start))];
                IReadOnlyList<UsageKind> kinds = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? UsageKinds.Classify(syntax?.Root ?? Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(snapshot.ToString()).GetRoot(), offsets)
                    : [.. here.Select(_ => UsageKind.Other)];
                for (int i = 0; i < here.Count; i++)
                {
                    LspRange range = here[i].Range;
                    TextPosition start = snapshot.GetPosition(offsets[i]);
                    int length = range.End.Line == range.Start.Line ? range.End.Character - range.Start.Character : 0;
                    found.Add(new Usage(path, start.Line, start.Column, Math.Max(0, length), snapshot.GetLineText(start.Line), kinds[i], project));
                }
            }
            return found;
        });
        if (search != usageSearches)
        {
            return;
        }
        Find.Show(title, usages);
        ShowToolWindow(ToolWindowIds.Find);
        status.Text = $"{title}: {usages.Count} found";
    }
}
