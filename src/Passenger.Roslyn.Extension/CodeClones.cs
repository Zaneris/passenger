using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.GeneratedCodeRecognition;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>Code clones across the solution's C# documents, generated code left out: runs of whole statements of one block
/// that are the same, or the same but for names and literals, read from each document's syntax with its trivia passed over.
/// Each statement is a fingerprint of its tokens, every name one token and every literal one of its kind; runs alike are
/// found in buckets by their fingerprints, never compared in pairs, grown statement by statement while they stay alike, a
/// run inside another group's runs left out. What a document holds is kept while its text stands.</summary>
public sealed class CodeClones : IExtensionWorkspaceMessageHandler<ClonesAsked, ClonesFound>
{
    /// <summary>The most groups an answer lists, the largest first.</summary>
    private const int MostGroups = 1000;

    /// <summary>Each document's statement lists; null for generated code, which is passed over.</summary>
    private static readonly DocumentReadsKept<Statement[][]?> Kept = new();

    public async Task<ClonesFound> ExecuteAsync(ClonesAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        List<Read> documents = await ReadAsync(context.Solution, message.Within, cancellationToken).ConfigureAwait(false);
        if (message.Selection is { } selection)
        {
            return await AlikeAsync(context.Solution, documents, selection, cancellationToken).ConfigureAwait(false);
        }
        List<Found> found = Find(documents, Math.Max(1, message.Tokens), cancellationToken);
        return new ClonesFound(await DescribedAsync(documents, found, cancellationToken).ConfigureAwait(false), documents.Count);
    }

    /// <summary>A statement's fingerprints: two of its tokens with names and literals each as their kind, which runs are
    /// bucketed and grown by, and one of its tokens as written; how many tokens it has, and where it stands.</summary>
    internal readonly record struct Statement(ulong Renamed, ulong Check, ulong Exact, int Tokens, int Start, int End)
    {
        public (ulong, ulong) Key => (Renamed, Check);
    }

    /// <summary>A document read: its statement lists, each a block's statements in order.</summary>
    private sealed record Read(Document Document, Statement[][] Lists);

    /// <summary>A run of statements in a list of a document read, from its first statement for so many.</summary>
    private readonly record struct Run(int Document, int List, int First, int Count);

    /// <summary>Runs found alike, as many statements each.</summary>
    private sealed record Found(List<Run> Runs, int Count, int Tokens);

    /// <summary>The C# documents under the paths given, every one where none is, each file once however many projects hold it.</summary>
    private static async Task<List<Read>> ReadAsync(Solution solution, IReadOnlyList<string> within, CancellationToken cancel)
    {
        List<Read> read = [];
        HashSet<string> seen = new(PathNames.Comparer);
        foreach (Project project in solution.Projects)
        {
            if (project.Language != LanguageNames.CSharp)
            {
                continue;
            }
            bool whole = within.Count == 0 || within.Any(p => SamePath(p, project.FilePath));
            List<Document> documents = [.. project.Documents.Where(d => d.FilePath is { } path && (whole || within.Any(p => PathNames.Same(path, p) || PathNames.IsUnder(path, p)))
                && seen.Add(path))];
            IReadOnlyList<Statement[][]?> lists = await Kept.OfAsync(project, documents, ListsAsync, cancel).ConfigureAwait(false);
            for (int i = 0; i < documents.Count; i++)
            {
                if (lists[i] is { } own)
                {
                    read.Add(new Read(documents[i], own));
                }
            }
        }
        return read;
    }

    /// <summary>A document's statement lists with each statement's fingerprints; none for generated code.</summary>
    private static async Task<DocumentReadsKept<Statement[][]?>.Read> ListsAsync(Document document, CancellationToken cancel)
    {
        if (document.Project.Services.GetService<IGeneratedCodeRecognitionService>() is { } recognition
            && await recognition.IsGeneratedCodeAsync(document, cancel).ConfigureAwait(false)
            || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
        {
            return new(null, Bound: false);
        }
        return new(Lists(root, cancel), Bound: false);
    }

    /// <summary>Every block's statements (a switch section's and the top-level statements too), fingerprinted from one
    /// pass over the document's tokens: a statement's are a stretch of them.</summary>
    internal static Statement[][] Lists(SyntaxNode root, CancellationToken cancel)
    {
        List<SyntaxToken> tokens = [.. root.DescendantTokens().Where(t => t.Span.Length > 0)];
        int[] starts = [.. tokens.Select(t => t.SpanStart)];
        TokenHashes hashes = new(tokens);
        List<Statement[]> lists = [];
        foreach (SyntaxNode node in root.DescendantNodesAndSelf())
        {
            cancel.ThrowIfCancellationRequested();
            IEnumerable<StatementSyntax>? statements = node switch
            {
                BlockSyntax block => block.Statements,
                SwitchSectionSyntax section => section.Statements,
                CompilationUnitSyntax unit => unit.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement),
                _ => null,
            };
            List<Statement> list = [];
            foreach (StatementSyntax statement in statements ?? [])
            {
                SyntaxToken first = statement.GetFirstToken();
                SyntaxToken last = statement.GetLastToken();
                if (first.Span.Length == 0)
                {
                    continue;
                }
                int from = Sorted.FirstAtOrAfter<int>(starts, first.SpanStart);
                int to = Sorted.FirstAtOrAfter<int>(starts, last.SpanStart) + 1;
                list.Add(new Statement(hashes.Renamed(from, to), hashes.Check(from, to), hashes.Exact(from, to), to - from, statement.SpanStart, statement.Span.End));
            }
            if (list.Count > 0)
            {
                lists.Add([.. list]);
            }
        }
        return [.. lists];
    }

    /// <summary>Every group of runs alike of at least so many tokens, the largest first: each run's fewest statements
    /// reaching the tokens, bucketed by their fingerprints, a bucket whose runs all follow statements alike left to the
    /// run before; each bucket grown statement by statement while its runs stay alike, parting where they part.</summary>
    private static List<Found> Find(List<Read> documents, int least, CancellationToken cancel)
    {
        Dictionary<(ulong, ulong), List<Run>> buckets = [];
        for (int d = 0; d < documents.Count; d++)
        {
            Statement[][] lists = documents[d].Lists;
            for (int l = 0; l < lists.Length; l++)
            {
                cancel.ThrowIfCancellationRequested();
                Statement[] list = lists[l];
                SequenceHashes sequence = new(list);
                int end = 0;
                int tokens = 0;
                for (int first = 0; first < list.Length; first++)
                {
                    while (end < list.Length && tokens < least)
                    {
                        tokens += list[end++].Tokens;
                    }
                    if (tokens < least)
                    {
                        break;
                    }
                    (ulong, ulong) key = sequence.Of(first, end);
                    if (!buckets.TryGetValue(key, out List<Run>? runs))
                    {
                        buckets[key] = runs = [];
                    }
                    runs.Add(new Run(d, l, first, end - first));
                    tokens -= list[first].Tokens;
                }
            }
        }
        List<Found> found = [];
        foreach (List<Run> bucket in buckets.Values)
        {
            cancel.ThrowIfCancellationRequested();
            if (bucket.Count < 2 || FollowAlike(documents, bucket))
            {
                continue;
            }
            Grow(documents, bucket, bucket[0].Count, found);
        }
        return Outermost(documents, found);
    }

    /// <summary>Whether every run of a bucket follows a statement alike, so a run starting a statement sooner holds them.</summary>
    private static bool FollowAlike(List<Read> documents, List<Run> bucket)
    {
        (ulong, ulong)? before = null;
        foreach (Run run in bucket)
        {
            if (run.First == 0)
            {
                return false;
            }
            (ulong, ulong) key = documents[run.Document].Lists[run.List][run.First - 1].Key;
            if (before is { } other && other != key)
            {
                return false;
            }
            before = key;
        }
        return true;
    }

    /// <summary>Runs alike for so many statements grown while their next statements stay alike; where they part, the runs as
    /// they are form a group, and those still alike past it grow on as their own.</summary>
    private static void Grow(List<Read> documents, List<Run> start, int count, List<Found> found)
    {
        Stack<(List<Run> Runs, int Count)> growing = new([(start, count)]);
        while (growing.Count > 0)
        {
            (List<Run> runs, int length) = growing.Pop();
            while (true)
            {
                IGrouping<(ulong, ulong)?, Run>[] next = [.. runs.GroupBy(r => Next(documents, r, length))];
                if (next.Length == 1 && next[0].Key is not null)
                {
                    length++;
                    continue;
                }
                if (Apart([.. runs.Select(r => r with { Count = length })]) is { Count: >= 2 } apart)
                {
                    found.Add(new Found(apart, length, Tokens(documents, apart[0])));
                }
                foreach (IGrouping<(ulong, ulong)?, Run> part in next)
                {
                    if (part.Key is not null && part.Count() >= 2)
                    {
                        growing.Push(([.. part], length + 1));
                    }
                }
                break;
            }
        }
    }

    /// <summary>The key of the statement after a run of so many, or null at its list's end.</summary>
    private static (ulong, ulong)? Next(List<Read> documents, Run run, int count)
    {
        Statement[] list = documents[run.Document].Lists[run.List];
        return run.First + count < list.Length ? list[run.First + count].Key : null;
    }

    /// <summary>Runs none of which overlaps one before it: repeated statements make runs alike that overlap.</summary>
    private static List<Run> Apart(List<Run> runs)
    {
        List<Run> apart = [];
        foreach (Run run in runs.OrderBy(r => r.Document).ThenBy(r => r.List).ThenBy(r => r.First))
        {
            if (apart.Count == 0 || apart[^1] is var last && (last.Document != run.Document || last.List != run.List || last.First + last.Count <= run.First))
            {
                apart.Add(run);
            }
        }
        return apart;
    }

    private static int Tokens(List<Read> documents, Run run)
    {
        Statement[] list = documents[run.Document].Lists[run.List];
        int tokens = 0;
        for (int i = run.First; i < run.First + run.Count; i++)
        {
            tokens += list[i].Tokens;
        }
        return tokens;
    }

    /// <summary>The groups whose runs are not all inside a larger group's, the most code repeated first.</summary>
    private static List<Found> Outermost(List<Read> documents, List<Found> found)
    {
        Dictionary<int, List<(int Start, int End)>> kept = [];
        List<Found> outermost = [];
        foreach (Found group in found.OrderByDescending(g => (long)g.Tokens * (g.Runs.Count - 1)).ThenByDescending(g => g.Tokens)
            .ThenBy(g => g.Runs[0].Document).ThenBy(g => g.Runs[0].List).ThenBy(g => g.Runs[0].First))
        {
            List<(int Document, int Start, int End)> spans = [.. group.Runs.Select(r => (r.Document, Span(documents, r).Start, Span(documents, r).End))];
            if (spans.All(s => kept.TryGetValue(s.Document, out List<(int Start, int End)>? spansKept) && spansKept.Any(k => k.Start <= s.Start && s.End <= k.End)))
            {
                continue;
            }
            outermost.Add(group);
            foreach ((int document, int start, int end) in spans)
            {
                if (!kept.TryGetValue(document, out List<(int Start, int End)>? spansKept))
                {
                    kept[document] = spansKept = [];
                }
                spansKept.Add((start, end));
            }
            if (outermost.Count == MostGroups)
            {
                break;
            }
        }
        return outermost;
    }

    /// <summary>A run's span in its document, from its first statement's start to its last's end.</summary>
    private static (int Start, int End) Span(List<Read> documents, Run run)
    {
        Statement[] list = documents[run.Document].Lists[run.List];
        return (list[run.First].Start, list[run.First + run.Count - 1].End);
    }

    /// <summary>Each group as the answer gives it: whether its runs are the same as written, the share of tokens written
    /// alike in all of them, and each run's place and member.</summary>
    private static async Task<List<CloneGroup>> DescribedAsync(List<Read> documents, List<Found> found, CancellationToken cancel)
    {
        List<CloneGroup> groups = [];
        foreach (Found group in found)
        {
            cancel.ThrowIfCancellationRequested();
            bool exact = group.Runs.Select(r => ExactOf(documents, r)).Distinct().Count() == 1;
            List<SyntaxToken[]> tokens = [];
            List<CloneRun> runs = [];
            foreach (Run run in group.Runs)
            {
                Document document = documents[run.Document].Document;
                SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
                SyntaxNode root = (await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false))!;
                (int start, int end) = Span(documents, run);
                tokens.Add([.. root.DescendantTokens(TextSpan.FromBounds(start, end)).Where(t => t.Span.Length > 0 && t.SpanStart >= start && t.Span.End <= end)]);
                runs.Add(new CloneRun(document.FilePath!, PlaceAt(text, start), PlaceAt(text, end), MemberOf(root.FindToken(start))));
            }
            groups.Add(new CloneGroup(exact, exact ? 100 : Alike(tokens), group.Count, group.Tokens, runs));
        }
        return groups;
    }

    /// <summary>A run's fingerprint as written: its statements' exact fingerprints in order.</summary>
    private static ulong ExactOf(List<Read> documents, Run run)
    {
        Statement[] list = documents[run.Document].Lists[run.List];
        ulong hash = 0;
        for (int i = run.First; i < run.First + run.Count; i++)
        {
            hash = unchecked((hash * 1_099_511_628_211UL) ^ list[i].Exact);
        }
        return hash;
    }

    /// <summary>The percent of token places every run writes alike, of runs whose tokens line up one for one.</summary>
    private static int Alike(List<SyntaxToken[]> runs)
    {
        int count = runs.Min(r => r.Length);
        if (count == 0)
        {
            return 0;
        }
        int same = 0;
        for (int i = 0; i < count; i++)
        {
            string text = runs[0][i].Text;
            if (runs.All(r => r[i].Text == text))
            {
                same++;
            }
        }
        return (int)Math.Floor(100.0 * same / count);
    }

    /// <summary>The member a place is in, by its type's name and its own ("Order.Add", "Order.this[]", "Order..ctor" as
    /// "Order.Order"); the type alone for a member with no name, "top-level statements" outside any type.</summary>
    internal static string MemberOf(SyntaxToken token)
    {
        string? member = null;
        List<string> types = [];
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax type:
                    types.Insert(0, type.Identifier.ValueText);
                    break;
                case MemberDeclarationSyntax declaration when member is null && node is not BaseNamespaceDeclarationSyntax:
                    member = declaration switch
                    {
                        MethodDeclarationSyntax method => method.Identifier.ValueText,
                        ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
                        DestructorDeclarationSyntax destructor => "~" + destructor.Identifier.ValueText,
                        PropertyDeclarationSyntax property => property.Identifier.ValueText,
                        IndexerDeclarationSyntax => "this[]",
                        EventDeclarationSyntax @event => @event.Identifier.ValueText,
                        OperatorDeclarationSyntax @operator => "operator " + @operator.OperatorToken.Text,
                        ConversionOperatorDeclarationSyntax conversion => "operator " + conversion.Type,
                        _ => null,
                    };
                    break;
            }
        }
        return types.Count == 0 ? "top-level statements" : member is null ? string.Join('.', types) : $"{string.Join('.', types)}.{member}";
    }

    /// <summary>The runs alike to the statements a selection covers, the selection's run first; any size, as it was chosen.</summary>
    private static async Task<ClonesFound> AlikeAsync(Solution solution, List<Read> documents, CloneRun selection, CancellationToken cancel)
    {
        if (DocumentIdsOf(solution, selection.Path).Select(solution.GetDocument).OfType<Document>().FirstOrDefault() is not { } document
            || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
        {
            return new ClonesFound([], documents.Count, "The selection is in no C# file of the solution");
        }
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        TextSpan span = TextSpan.FromBounds(WrittenPlaces.Offset(text, selection.Start), WrittenPlaces.Offset(text, selection.End));
        if (StatementRun(root, span, chosen: true) is not { Count: > 0 } statements)
        {
            return new ClonesFound([], documents.Count, "The selection is in no statement: Find Clones of Selection compares statements");
        }
        Statement[] asked = Lists(root, cancel).Select(list => Within(list, statements[0].SpanStart, statements[^1].Span.End)).FirstOrDefault(s => s.Length > 0) ?? [];
        if (asked.Length == 0)
        {
            return new ClonesFound([], documents.Count, "The selection is in no statement: Find Clones of Selection compares statements");
        }
        (ulong, ulong) key = new SequenceHashes(asked).Of(0, asked.Length);
        int own = documents.FindIndex(d => d.Document.FilePath is { } path && PathNames.Same(path, document.FilePath ?? string.Empty));
        List<Run> runs = [];
        for (int d = 0; d < documents.Count; d++)
        {
            Statement[][] lists = documents[d].Lists;
            for (int l = 0; l < lists.Length; l++)
            {
                cancel.ThrowIfCancellationRequested();
                SequenceHashes sequence = new(lists[l]);
                for (int first = 0; first + asked.Length <= lists[l].Length; first++)
                {
                    bool overlapsSelection = d == own && lists[l][first].Start < asked[^1].End && asked[0].Start < lists[l][first + asked.Length - 1].End;
                    if (!overlapsSelection && sequence.Of(first, first + asked.Length) == key)
                    {
                        runs.Add(new Run(d, l, first, asked.Length));
                    }
                }
            }
        }
        if (runs.Count == 0)
        {
            return new ClonesFound([], documents.Count);
        }
        // The selection's own run leads its group, read from its document whether or not the search's paths take it in.
        Read selected = new(document, [asked]);
        documents.Add(selected);
        List<Run> group = [new Run(documents.Count - 1, 0, 0, asked.Length), .. Apart(runs)];
        int tokens = asked.Sum(s => s.Tokens);
        List<CloneGroup> described = await DescribedAsync(documents, [new Found(group, asked.Length, tokens)], cancel).ConfigureAwait(false);
        documents.RemoveAt(documents.Count - 1);
        return new ClonesFound(described, documents.Count);
    }

    /// <summary>The statements of one list from a start to an end, where it holds a run so.</summary>
    private static Statement[] Within(Statement[] list, int start, int end)
    {
        int first = Array.FindIndex(list, s => s.Start == start);
        int last = Array.FindIndex(list, s => s.End == end);
        return first >= 0 && last >= first ? list[first..(last + 1)] : [];
    }

    /// <summary>The run of whole statements of one block a span covers, trivia aside: those of the innermost block holding
    /// statements wholly inside it. Where it covers none, and <paramref name="chosen"/> (a selection, a caret), the innermost
    /// statement holding it.</summary>
    internal static List<StatementSyntax> StatementRun(SyntaxNode root, TextSpan span, bool chosen)
    {
        SyntaxToken token = root.FindToken(span.Start);
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            IEnumerable<StatementSyntax>? statements = node switch
            {
                BlockSyntax block => block.Statements,
                SwitchSectionSyntax section => section.Statements,
                CompilationUnitSyntax unit => unit.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement),
                _ => null,
            };
            if (statements is null)
            {
                continue;
            }
            List<StatementSyntax> inside = [.. statements.Where(s => span.Contains(s.Span))];
            if (inside.Count > 0)
            {
                return inside;
            }
        }
        if (!chosen)
        {
            return [];
        }
        return token.Parent?.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault(s => s is not BlockSyntax && s.Span.Contains(span)) is { } holding ? [holding] : [];
    }

    /// <summary>A token's code as a clone reads it: a name one code whatever it is, a literal one per kind, anything else its
    /// kind; and as written, its own text's.</summary>
    internal static (ulong Renamed, ulong Exact) CodesOf(SyntaxToken token)
    {
        SyntaxKind kind = token.Kind();
        ulong renamed = kind switch
        {
            SyntaxKind.IdentifierToken => 0x9E3779B97F4A7C15UL,
            SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword => 0xC2B2AE3D27D4EB4FUL,
            _ => Mix((ulong)kind),
        };
        bool written = kind is SyntaxKind.IdentifierToken or SyntaxKind.NumericLiteralToken or SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken
            or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.Utf8StringLiteralToken or SyntaxKind.SingleLineRawStringLiteralToken
            or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8SingleLineRawStringLiteralToken or SyntaxKind.Utf8MultiLineRawStringLiteralToken
            or SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword;
        return (renamed, written ? Fnv(kind == SyntaxKind.IdentifierToken ? token.ValueText : token.Text) : renamed);
    }

    /// <summary>Whether a token is one a renamed clone may write otherwise: a name or a literal.</summary>
    internal static bool Renamable(SyntaxToken token) => CodesOf(token) is var codes && codes.Renamed != codes.Exact;

    private static ulong Mix(ulong value)
    {
        value ^= value >> 33;
        value = unchecked(value * 0xFF51AFD7ED558CCDUL);
        value ^= value >> 33;
        value = unchecked(value * 0xC4CEB9FE1A85EC53UL);
        return value ^ (value >> 33);
    }

    private static ulong Fnv(string text)
    {
        ulong hash = 14_695_981_039_346_656_037UL;
        foreach (char c in text)
        {
            hash = unchecked((hash ^ c) * 1_099_511_628_211UL);
        }
        return Mix(hash);
    }

    /// <summary>Polynomial fingerprints of a document's tokens, any stretch of them read at once from their prefixes.</summary>
    private sealed class TokenHashes
    {
        private const ulong RenamedBase = 0x100000001B3UL;
        private const ulong CheckBase = 0x5DEECE66DUL;
        private readonly ulong[] renamed;
        private readonly ulong[] check;
        private readonly ulong[] exact;
        private readonly ulong[] renamedPower;
        private readonly ulong[] checkPower;

        public TokenHashes(List<SyntaxToken> tokens)
        {
            int count = tokens.Count;
            renamed = new ulong[count + 1];
            check = new ulong[count + 1];
            exact = new ulong[count + 1];
            renamedPower = new ulong[count + 1];
            checkPower = new ulong[count + 1];
            renamedPower[0] = 1;
            checkPower[0] = 1;
            for (int i = 0; i < count; i++)
            {
                (ulong code, ulong written) = CodesOf(tokens[i]);
                unchecked
                {
                    renamed[i + 1] = (renamed[i] * RenamedBase) + code;
                    check[i + 1] = (check[i] * CheckBase) + (code ^ 0x2545F4914F6CDD1DUL);
                    exact[i + 1] = (exact[i] * RenamedBase) + written;
                    renamedPower[i + 1] = renamedPower[i] * RenamedBase;
                    checkPower[i + 1] = checkPower[i] * CheckBase;
                }
            }
        }

        public ulong Renamed(int from, int to) => unchecked(renamed[to] - (renamed[from] * renamedPower[to - from]));

        public ulong Check(int from, int to) => unchecked(check[to] - (check[from] * checkPower[to - from]));

        public ulong Exact(int from, int to) => unchecked(exact[to] - (exact[from] * renamedPower[to - from]));
    }

    /// <summary>Polynomial fingerprints of a list's statements in order, any run of them read at once.</summary>
    private readonly struct SequenceHashes
    {
        private const ulong RenamedBase = 0x9E3779B185EBCA87UL;
        private const ulong CheckBase = 0xC2B2AE3D27D4EB4FUL;
        private readonly ulong[] renamed;
        private readonly ulong[] check;
        private readonly ulong[] renamedPower;
        private readonly ulong[] checkPower;

        public SequenceHashes(Statement[] list)
        {
            renamed = new ulong[list.Length + 1];
            check = new ulong[list.Length + 1];
            renamedPower = new ulong[list.Length + 1];
            checkPower = new ulong[list.Length + 1];
            renamedPower[0] = 1;
            checkPower[0] = 1;
            for (int i = 0; i < list.Length; i++)
            {
                unchecked
                {
                    renamed[i + 1] = (renamed[i] * RenamedBase) + list[i].Renamed;
                    check[i + 1] = (check[i] * CheckBase) + list[i].Check;
                    renamedPower[i + 1] = renamedPower[i] * RenamedBase;
                    checkPower[i + 1] = checkPower[i] * CheckBase;
                }
            }
        }

        public (ulong, ulong) Of(int from, int to) => unchecked((renamed[to] - (renamed[from] * renamedPower[to - from]), check[to] - (check[from] * checkPower[to - from])));
    }
}
