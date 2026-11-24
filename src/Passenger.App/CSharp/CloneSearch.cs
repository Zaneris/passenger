using System.Diagnostics;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>What a search for code clones found: each group, the most code repeated first, in the editor's places; how many
/// files it read, how long it took, and what it looked through, or why it could not look (a selection holding no statement).</summary>
public sealed record FoundClones(IReadOnlyList<CloneSet> Sets, int Files, TimeSpan Took, string Searched, string? Problem = null)
{
    /// <summary>What the search came to, as the status bar and an agent are told it.</summary>
    public string Said => Problem ?? (Sets.Count == 0
        ? $"No code clones in {Searched} ({Plural.Of(Files, "file")} read in {Durations.Said(Took)})"
        : $"{Plural.Of(Sets.Count, "group")} of code clones in {Searched} ({Plural.Of(Files, "file")} read in {Durations.Said(Took)})");
}

/// <summary>Runs of statements alike: the same as written (<paramref name="Exact"/>), or the same but for names and literals,
/// with the share of their tokens written alike in all of them, and each run's place.</summary>
public sealed record CloneSet(bool Exact, int Alike, int Statements, int Tokens, IReadOnlyList<ClonePlace> Clones)
{
    /// <summary>How alike the runs are, in words ("Exact", "Renamed, 92% alike").</summary>
    public string Kind => Exact ? "Exact" : $"Renamed, {Alike}% alike";

    /// <summary>How many lines the first run spans.</summary>
    public int Lines => Clones[0].End.Line - Clones[0].Start.Line + 1;
}

/// <summary>A clone: its file, its first statement's start and its last's end in the editor's lines, the member it is in,
/// and the run as the language server named it, which Extract Method asks for again.</summary>
public sealed record ClonePlace(string Path, TextPosition Start, TextPosition End, string Member, CloneRun Run)
{
    /// <summary>Its file and lines, the file as the open folder shows it ("Shop/Calc.cs:12–20").</summary>
    public string Shown(string? folder) => $"{PathNames.Shown(Path, folder)}:{Start.Line + 1}–{End.Line + 1}";
}

/// <summary>A search for code clones through Passenger's extension in the C# language server (<see cref="Handlers.CodeClones"/>),
/// its places taken into the editor's lines off the window's thread.</summary>
internal static class CloneSearch
{
    /// <summary>The fewest tokens a run of statements holds to be listed as a clone: a few lines of code.</summary>
    public const int LeastTokens = 50;

    /// <summary>The clones among the files under <paramref name="within"/> (every C# file where empty), or of a selection's
    /// statements across them; <paramref name="open"/> gives an open file's text, which the server read as the editor has it.</summary>
    public static async Task<FoundClones> FindAsync(RoslynSession roslyn, IReadOnlyList<string> within, CloneRun? selection, string searched, int least,
        IReadOnlyDictionary<string, TextSnapshot> open, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        ClonesFound found = await roslyn.ExtensionAsync<ClonesFound>(Handlers.CodeClones, new ClonesAsked(within, least, selection), cancel).ConfigureAwait(false);
        TimeSpan took = Stopwatch.GetElapsedTime(began);
        if (found.Problem is { } problem)
        {
            return new FoundClones([], found.Documents, took, searched, problem);
        }
        Dictionary<string, (string Path, ServerLines? Lines)> files = new(StringComparer.Ordinal);
        List<CloneSet> sets = [];
        foreach (CloneGroup group in found.Groups)
        {
            List<ClonePlace> places = [];
            foreach (CloneRun run in group.Clones)
            {
                cancel.ThrowIfCancellationRequested();
                if (!files.TryGetValue(run.Path, out (string Path, ServerLines? Lines) file))
                {
                    string path = PathNames.Canonical(run.Path);
                    files[run.Path] = file = (path, open.TryGetValue(path, out TextSnapshot? text) ? ServerPlaces.Differing(text) : ServerPlaces.DifferingOnDisk(path));
                }
                places.Add(new ClonePlace(file.Path, ServerPlaces.PositionOf(file.Lines, new LspPosition(run.Start.Line, run.Start.Character)),
                    ServerPlaces.PositionOf(file.Lines, new LspPosition(run.End.Line, run.End.Character)), run.Member ?? string.Empty, run));
            }
            sets.Add(new CloneSet(group.Exact, group.Alike, group.Statements, group.Tokens, places));
        }
        return new FoundClones(sets, found.Documents, took, searched);
    }

    /// <summary>A selection as the server names its place, for Find Clones of Selection.</summary>
    public static CloneRun Selection(string path, TextSnapshot text, TextPosition start, TextPosition end)
    {
        LspPosition from = ServerPlaces.ServerPositionOf(text, start);
        LspPosition to = ServerPlaces.ServerPositionOf(text, end);
        return new CloneRun(path, new Place(from.Line, from.Character), new Place(to.Line, to.Character));
    }

    /// <summary>A clone's whole lines from its file's text, as the side-by-side view shows them.</summary>
    public static string LinesOf(TextSnapshot text, ClonePlace clone)
    {
        int last = Math.Min(clone.End.Line, text.LineCount - 1);
        int start = text.GetLineStart(Math.Min(clone.Start.Line, last));
        return text.GetText(start, text.GetLine(last).End - start) + "\n";
    }
}
