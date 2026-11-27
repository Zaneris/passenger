using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App.ToolWindows;

internal sealed class SyntaxTreeView : Panel
{
    internal const string NoSource = "Open a C# file to see its syntax tree.";

    private readonly TreeList list = new() { Classes = { StyleClasses.Tree } };
    private readonly TreeRows<Row> rows = [];
    private readonly EmptyState message = new(NoSource);
    private readonly DetailsTables details = new();
    private readonly PaneSplit split;
    private readonly Action<ParsedText, TextSpan> selectSource;
    private readonly Debounce reveal;
    private ParsedText? shown;
    private TextSpan? wanted;
    private CancellationTokenSource? finding;

    internal SyntaxTreeView(Action<ParsedText, TextSpan> selectSource)
    {
        this.selectSource = selectSource;
        reveal = new Debounce(TypingPauses.SyntaxTree, RevealAsync);
        list.ItemsSource = rows;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ReusedRows.Use(list, MakeRow);
        TreeKeys.Attach<Row>(list, Toggle);
        list.SelectionChanged += (_, _) => ShowChoice();
        list.AddHandler(InputElement.KeyUpEvent, (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown
                && list.SelectedItem is Row row)
            {
                SelectSource(row);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        split = new PaneSplit(list, details, "4*,1,*") { IsVisible = false };
        split.SetPresentation(stack: true, onlySecond: false);
        split.RowDefinitions[0].MinHeight = ListRow.Height * 2;
        split.RowDefinitions[2].MinHeight = ListRow.Height * 2;
        Children.Add(split);
        Children.Add(message);
    }

    internal void Follow(ParsedText parsed, TextRange selection)
    {
        if (parsed.DeepAt is not null)
        {
            Say(CSharpParsing.TooDeep);
            return;
        }
        if (shown?.Tree != parsed.Tree)
        {
            ClearChoice();
            shown = parsed;
            rows.Show([new Row(parsed.Root, 0)]);
            message.IsVisible = false;
            split.IsVisible = true;
        }
        TextSpan span = new(selection.Start, selection.Length);
        if (wanted != span)
        {
            finding?.Cancel();
            wanted = span;
            reveal.Restart();
        }
    }

    internal void Say(string text)
    {
        ClearChoice();
        shown = null;
        if (rows.Count > 0)
        {
            rows.Clear();
        }
        split.IsVisible = false;
        message.Text = text;
        message.IsVisible = true;
    }

    internal void ClearChoice()
    {
        StopRevealing();
        list.SelectedItem = null;
    }

    private void StopRevealing()
    {
        reveal.Stop();
        finding?.Cancel();
        wanted = null;
    }

    private async Task RevealAsync()
    {
        if (rows.Count == 0 || wanted is not { } span)
        {
            return;
        }
        Row root = rows[0];
        using CancellationTokenSource request = new();
        finding = request;
        try
        {
            List<Row>? path = await BackgroundWork.Run(() => PathTo(root, span, request.Token));
            if (request.IsCancellationRequested || path is null)
            {
                return;
            }
            foreach (Row parent in path.Take(path.Count - 1))
            {
                if (!parent.IsExpanded)
                {
                    Toggle(parent);
                }
            }
            list.SelectedItem = path[^1];
        }
        finally
        {
            if (finding == request)
            {
                finding = null;
            }
        }
    }

    private void ShowChoice()
    {
        if (list.SelectedItem is not Row row || shown is not { } parsed)
        {
            details.Say("Select a syntax item to see its details.");
            return;
        }
        List<(string Name, Control Value)> properties =
        [
            ("Kind", DetailsTables.Words(row.Kind.ToString(), code: true)),
            ("Roslyn type", DetailsTables.Words(row.RoslynType, code: true)),
            ("Span", DetailsTables.Words(row.Span.ToString(), code: true)),
            ("Full span", DetailsTables.Words(row.FullSpan.ToString(), code: true)),
            ("Source", DetailsTables.Words(Preview(parsed.Text, row.Span), code: true)),
        ];
        if ((row.IsMissing ? "Missing from source" : row.Diagnostic) is { } diagnostic)
        {
            properties.Add(("Syntax", DetailsTables.Words(diagnostic)));
        }
        details.Show(() => details.Section("Properties", properties));
    }

    private void SelectSource(Row row)
    {
        if (shown is not { } parsed)
        {
            return;
        }
        StopRevealing();
        // The tree's choice can share its span with a child. Keep the chosen row when the editor echoes that span.
        wanted = row.Span;
        selectSource(parsed, row.Span);
    }

    private static string Preview(TextSnapshot text, TextSpan span)
    {
        const int limit = 80;
        string code = text.GetText(span.Start, Math.Min(span.Length, limit));
        return code.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + (span.Length > limit ? "…" : "");
    }

    private static List<Row>? PathTo(Row root, TextSpan selection, CancellationToken cancel)
    {
        List<Row> path = [root];
        while (!cancel.IsCancellationRequested)
        {
            Row? match = path[^1].Children.FirstOrDefault(row => Covers(row, selection));
            if (match is null)
            {
                return path;
            }
            path.Add(match);
        }
        return null;
    }

    private static bool Covers(Row row, TextSpan selection) => selection.IsEmpty
        ? row.FullSpan.Contains(selection.Start) || (row.Kind == SyntaxKind.EndOfFileToken && row.FullSpan.End == selection.Start)
        : row.FullSpan.Contains(selection);

    private void Toggle(Row row)
    {
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded)
        {
            rows.Open(row, TreeRows<Row>.Shown(row.Children, child => child.Children));
        }
        else
        {
            rows.Close(row);
        }
    }

    private Control MakeRow()
    {
        RowGlyphs glyphs = new();
        TextBlock kind = new() { VerticalAlignment = VerticalAlignment.Center };
        DockPanel line = new() { Height = ListRow.Height, Background = Brushes.Transparent, Children = { glyphs, kind } };
        DockPanel.SetDock(glyphs, Dock.Left);
        glyphs.ChevronPressed += (_, e) =>
        {
            if (line.DataContext is Row row)
            {
                Toggle(row);
                e.Handled = true;
            }
        };
        line.PointerPressed += (_, e) =>
        {
            if (line.DataContext is Row row && e.GetCurrentPoint(line).Properties.IsLeftButtonPressed)
            {
                SelectSource(row);
            }
        };
        return line.Showing<Row>(row =>
        {
            line.Margin = new Thickness(row.Depth * RowExpander.Column, 0, Gaps.Normal, 0);
            glyphs.HasChevron = row.Expands;
            glyphs.Turned = row.IsExpanded;
            glyphs.IconKey = row.Icon;
            kind.Text = row.Kind.ToString();
            ToolTip.SetTip(line, row.Span.ToString());
        });
    }

    private sealed class Row(SyntaxNodeOrToken syntax, int depth, SyntaxTrivia trivia = default) : Observable, ITreeRow
    {
        private IReadOnlyList<Row>? children;
        private bool expanded;
        private bool IsTrivia => trivia.RawKind != 0;

        public int Depth { get; } = depth;
        public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
        public bool Expands => IsTrivia ? trivia.HasStructure : syntax.IsNode
            ? syntax.ChildNodesAndTokens().Count > 0 : syntax.HasLeadingTrivia || syntax.HasTrailingTrivia;
        public SyntaxKind Kind => IsTrivia ? trivia.Kind() : syntax.Kind();
        public TextSpan Span => IsTrivia ? trivia.Span : syntax.Span;
        public TextSpan FullSpan => IsTrivia ? trivia.FullSpan : syntax.FullSpan;
        public string Icon => IsTrivia ? IconKeys.Edit : syntax.IsNode ? IconKeys.Structure : IconKeys.Reference;
        public string RoslynType => IsTrivia ? nameof(SyntaxTrivia) : syntax.AsNode()?.GetType().Name ?? nameof(SyntaxToken);
        public bool IsMissing => !IsTrivia && syntax.IsMissing;
        public string? Diagnostic => (IsTrivia ? trivia.GetDiagnostics() : syntax.GetDiagnostics()).FirstOrDefault() is { } error
            ? $"{error.Id} {error.GetMessage()}" : null;

        public IReadOnlyList<Row> Children => LazyInitializer.EnsureInitialized(ref children, ReadChildren);

        private IReadOnlyList<Row> ReadChildren()
        {
            if (IsTrivia)
            {
                return trivia.GetStructure() is { } structure ? [new Row(structure, Depth + 1)] : [];
            }
            if (syntax.IsNode)
            {
                return [.. syntax.ChildNodesAndTokens().Select(child => new Row(child, Depth + 1))];
            }
            SyntaxToken token = syntax.AsToken();
            return [.. token.LeadingTrivia.Concat(token.TrailingTrivia).Select(detail => new Row(default, Depth + 1, detail))];
        }
    }
}
