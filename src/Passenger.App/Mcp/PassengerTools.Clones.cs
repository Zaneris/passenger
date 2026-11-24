using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.Mcp;

/// <summary>Code clones for an agent, as Find Code Clones and Find Clones of Selection find them in the window: one request
/// of Passenger's extension where an agent would read every file.</summary>
public sealed partial class PassengerTools
{
    /// <summary>The most clones a group lists over the solution: a statement repeated hundreds of times lists the rest only
    /// where an agent asks of one of them.</summary>
    private const int MostClonesListed = 20;

    /// <summary>A group of clones as an agent reads it: exact or renamed, the percent of tokens written alike, how many
    /// statements and lines each clone has, and each clone's place.</summary>
    public sealed record ClonesAnswer(string Kind, int Alike, int Statements, int Lines, IReadOnlyList<CloneAnswer> Clones)
    {
        /// <summary>How many more clones the group has, left out, and how to read them.</summary>
        public string? More { get; init; }
    }

    /// <summary>A clone's file, first and last lines, and the member it is in.</summary>
    public sealed record CloneAnswer(string Path, int Line, int EndLine, string Member);

    [McpServerTool(Name = McpTools.Clones, Title = "Duplicated code", ReadOnly = true), Description(
        "Code clones in the C# code, as Find Code Clones finds them: runs of whole statements the same (exact) or the same but " +
        "for names and literals (renamed; alike, the percent of tokens written alike), the most code repeated first, each " +
        "clone's lines and member. within, a folder, file or project file, looks only there; path with line and endLine instead " +
        "finds the statements on those lines anywhere in the solution. tokens is the fewest a clone holds (50).")]
    public Task<CallToolResult> Clones(string? within = null, string? path = null, int? line = null, int? endLine = null, int tokens = CloneSearch.LeastTokens,
        CancellationToken cancel = default) => OnUi(cancel, async () =>
    {
        string? inside = Within(within);
        CloneRun? selection = null;
        if (path is not null)
        {
            string full = Resolve(path);
            int first = line ?? throw new ArgumentException("line is the first line of the statements path names", nameof(line));
            TextSnapshot text = Editors.TextOf(full) ?? throw new ArgumentException($"{path} could not be read", nameof(path));
            int last = Math.Clamp(endLine ?? first, first, text.LineCount);
            if (first < 1 || first > text.LineCount)
            {
                throw new ArgumentException($"{path} has {text.LineCount} lines", nameof(line));
            }
            selection = CloneSearch.Selection(full, text, new TextPosition(first - 1, 0), new TextPosition(last - 1, text.GetLine(last - 1).Length));
        }
        RoslynSession roslyn = await Server(cancel);
        Dictionary<string, TextSnapshot> open = new(PathNames.Comparer);
        foreach (OpenDocument document in Editors.Documents.Where(d => d.HoldsText))
        {
            open.TryAdd(document.Path, document.Text.Snapshot);
        }
        FoundClones found = await BackgroundWork.Run(() => CloneSearch.FindAsync(roslyn, inside is null ? [] : [inside], selection, within ?? "the solution",
            Math.Max(1, tokens), open, cancel), cancel);
        if (found.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        List<ClonesAnswer> groups = [];
        foreach (CloneSet set in found.Sets)
        {
            List<CloneAnswer> clones = [.. set.Clones.Select(c => new CloneAnswer(Relative(c.Path), c.Start.Line + 1, c.End.Line + 1, c.Member))];
            // A selection's one group takes the room a list has; a group among many its first few.
            (IReadOnlyList<CloneAnswer> kept, string? more) = selection is not null
                ? AgentAnswers.Within(clones, Size, AgentAnswers.MostCharacters - WordsRoom, "clone", "within narrowed lists fewer")
                : clones.Count > MostClonesListed
                    ? ([.. clones.Take(MostClonesListed)], AgentAnswers.More(clones.Count - MostClonesListed, "clone", "path with the line and endLine of one lists them"))
                    : (clones, null);
            groups.Add(new ClonesAnswer(set.Exact ? "exact" : "renamed", set.Alike, set.Statements, set.Lines, kept) { More = more });
        }
        return Held(groups, "group", "tokens raised or within narrowed lists fewer", note: $"{Plural.Of(found.Files, "file")} read in {Durations.Said(found.Took)}");
    });
}
