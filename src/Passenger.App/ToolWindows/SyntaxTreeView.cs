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

internal sealed class SyntaxTreeView : DockPanel
{
    private readonly TreeList list = new() { Classes = { StyleClasses.Tree } };
    private readonly TreeRows<Row> rows = [];
    private readonly EmptyState message = new("Open a C# file to see its syntax tree.");
    private readonly Border details = new() { IsVisible = false, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(Gaps.Normal) };
    private readonly TextBlock syntaxKind = Value();
    private readonly TextBlock roslynType = Value();
    private readonly TextBlock sourceSpan = Value();
    private readonly TextBlock fullSpan = Value();
    private readonly TextBlock sourceText = Value();
    private readonly TextBlock diagnostic = new() { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2 };
    private readonly Action<ParsedText, TextSpan> selectSource;
    private readonly Debounce reveal;
    private ParsedText? shown;
    private Row? root;
    private TextSpan? wanted;
    private CancellationTokenSource? finding;

    internal SyntaxTreeView(Action<ParsedText, TextSpan> selectSource)
    {
        this.selectSource = selectSource;
        reveal = new Debounce(TypingPauses.SyntaxTree, RevealAsync);
        list.ItemsSource = rows;
        list.IsVisible = false;
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
        MakeDetails();
        SetDock(details, Dock.Bottom);
        Children.Add(details);
        Children.Add(new Panel { Children = { list, message } });
    }

    internal void Follow(ParsedText parsed, Selection selection)
    {
        if (parsed.DeepAt is not null)
        {
            Say(CSharpParsing.TooDeep);
            return;
        }
        if (shown?.Tree != parsed.Tree)
        {
            StopRevealing();
            list.SelectedItem = null;
            shown = parsed;
            root = new Row(parsed.Root, 0);
            rows.Show([root]);
            message.IsVisible = false;
            list.IsVisible = true;
        }
        TextSpan span = TextSpan.FromBounds(selection.Start, selection.End);
        if (wanted != span)
        {
            finding?.Cancel();
            wanted = span;
            reveal.Restart();
        }
    }

    internal void Say(string text)
    {
        StopRevealing();
        list.SelectedItem = null;
        shown = null;
        root = null;
        if (rows.Count > 0)
        {
            rows.Clear();
        }
        list.IsVisible = false;
        message.Text = text;
        message.IsVisible = true;
    }

    internal void WaitForParse()
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
        if (root is not { } start || wanted is not { } span)
        {
            return;
        }
        using CancellationTokenSource request = new();
        finding = request;
        try
        {
            List<Row> path = await BackgroundWork.Run(() => PathTo(start, span, request.Token), request.Token);
            if (request.IsCancellationRequested)
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
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // The selection, file or window changed while the branch was being read.
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
            details.IsVisible = false;
            return;
        }
        syntaxKind.Text = row.Kind.ToString();
        roslynType.Text = row.RoslynType;
        sourceSpan.Text = row.Span.ToString();
        fullSpan.Text = row.FullSpan.ToString();
        sourceText.Text = Preview(parsed.Text, row.Span);
        diagnostic.Text = row.IsMissing ? "Missing from source" : row.Diagnostic;
        diagnostic.IsVisible = diagnostic.Text is not null;
        details.IsVisible = true;
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

    private void MakeDetails()
    {
        Grid fields = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = Gaps.Normal, RowSpacing = Gaps.Hair };
        (string Label, TextBlock Value)[] entries =
        [
            ("Kind", syntaxKind), ("Roslyn type", roslynType), ("Span", sourceSpan), ("Full span", fullSpan), ("Source", sourceText),
        ];
        for (int index = 0; index < entries.Length; index++)
        {
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            (string label, TextBlock value) = entries[index];
            TextBlock heading = MutedText.Of(label);
            Grid.SetRow(heading, index);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            fields.Children.Add(heading);
            fields.Children.Add(value);
        }
        details.Bind(Border.BorderBrushProperty, details.GetResourceObservable(TokenKeys.Divider));
        details.Child = new StackPanel { Spacing = Gaps.Small, Children = { fields, diagnostic } };
    }

    private static TextBlock Value() => new() { Classes = { StyleClasses.Code }, TextTrimming = TextTrimming.CharacterEllipsis };

    private static string Preview(TextSnapshot text, TextSpan span)
    {
        const int limit = 80;
        string code = text.GetText(span.Start, Math.Min(span.Length, limit));
        return code.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + (span.Length > limit ? "…" : "");
    }

    private static List<Row> PathTo(Row root, TextSpan selection, CancellationToken cancel)
    {
        List<Row> path = [root];
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            Row? match = path[^1].Children.FirstOrDefault(row => Covers(row, selection));
            if (match is null)
            {
                return path;
            }
            path.Add(match);
        }
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

        public IReadOnlyList<Row> Children => LazyInitializer.EnsureInitialized(ref children, () => ReadChildren().ToArray());

        private IEnumerable<Row> ReadChildren()
        {
            if (IsTrivia)
            {
                if (trivia.GetStructure() is { } structure)
                {
                    yield return new Row(structure, Depth + 1);
                }
                yield break;
            }
            if (syntax.IsNode)
            {
                foreach (SyntaxNodeOrToken child in syntax.ChildNodesAndTokens())
                {
                    yield return new Row(child, Depth + 1);
                }
                yield break;
            }
            SyntaxToken token = syntax.AsToken();
            foreach (SyntaxTrivia detail in token.LeadingTrivia)
            {
                yield return new Row(default, Depth + 1, detail);
            }
            foreach (SyntaxTrivia detail in token.TrailingTrivia)
            {
                yield return new Row(default, Depth + 1, detail);
            }
        }
    }
}
