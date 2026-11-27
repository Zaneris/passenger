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
    private TextSpan? editorSpan;
    private CancellationTokenSource? activeReveal;

    /// <summary>Whether the editor's selection is choosing the row, which leaves the editor's selection as it is.</summary>
    private bool revealing;

    internal SyntaxTreeView(Action<ParsedText, TextSpan> selectSource)
    {
        this.selectSource = selectSource;
        reveal = new Debounce(TypingPauses.SyntaxTree, RevealAsync);
        list.ItemsSource = rows;
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ReusedRows.Use(list, MakeRow);
        TreeKeys.Attach<Row>(list, Toggle, acts: new ListActs { Source = SelectChosenSource });
        list.SelectionChanged += (_, _) =>
        {
            ShowChoice();
            // A row the keys move to selects its code; one the editor's selection chose leaves the editor as it is.
            if (!revealing && list.IsKeyboardFocusWithin)
            {
                SelectChosenSource();
            }
        };
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
        if (editorSpan != span)
        {
            CancelReveal();
            editorSpan = span;
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
        CancelReveal();
        editorSpan = null;
        list.SelectedItem = null;
    }

    private void CancelReveal()
    {
        reveal.Stop();
        activeReveal?.Cancel();
    }

    private async Task RevealAsync()
    {
        if (rows.Count == 0 || editorSpan is not { } span)
        {
            return;
        }
        Row root = rows[0];
        using CancellationTokenSource request = new();
        activeReveal = request;
        try
        {
            List<Row>? path = await BackgroundWork.Run(() => PathTo(root, span, request.Token));
            if (request.IsCancellationRequested || path is null)
            {
                return;
            }
            revealing = true;
            try
            {
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
                revealing = false;
            }
        }
        finally
        {
            if (activeReveal == request)
            {
                activeReveal = null;
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
        List<(string Name, Control Value)> properties = [];
        Add("Kind", row.Kind.ToString(), code: true);
        Add("Description", row.Description);
        Add("Roslyn type", row.RoslynType, code: true);
        Add("Span", row.Span.ToString(), code: true);
        Add("Full span", row.FullSpan.ToString(), code: true);
        Add("Source", Preview(parsed.Text, row.Span), code: true);
        Add("Syntax", row.IsMissing ? "Missing from source" : row.Diagnostic);
        details.Show(() => details.Section("Properties", properties));

        void Add(string name, string? value, bool code = false)
        {
            if (value is not null)
            {
                properties.Add((name, DetailsTables.Words(value, code)));
            }
        }
    }

    private void SelectChosenSource()
    {
        if (list.SelectedItem is Row row)
        {
            SelectSource(row);
        }
    }

    private void SelectSource(Row row)
    {
        if (shown is not { } parsed)
        {
            return;
        }
        CancelReveal();
        // The tree's choice can share its span with a child. Keep the chosen row when the editor echoes that span.
        editorSpan = row.Span;
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
            string span = row.Span.ToString();
            ToolTip.SetTip(line, row.Description is { } description ? $"{description}\n{span}" : span);
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
        public string? Description => Kind switch
        {
            SyntaxKind.CompilationUnit => "The root of a C# source file.",
            SyntaxKind.NamespaceDeclaration or SyntaxKind.FileScopedNamespaceDeclaration => "Declares a namespace for the enclosed code.",
            SyntaxKind.ClassDeclaration => "Declares a class type.",
            SyntaxKind.MethodDeclaration => "Declares a method, its parameters, and any body.",
            SyntaxKind.LocalDeclarationStatement => "Declares one or more local variables or constants.",
            SyntaxKind.Block => "A group of statements enclosed in braces.",
            SyntaxKind.IfStatement => "Runs a statement when a condition is true, with an optional else branch.",
            SyntaxKind.ReturnStatement => "Exits a function, optionally returning a value.",
            SyntaxKind.IdentifierName => "Refers to a name written as an identifier.",
            SyntaxKind.InvocationExpression => "Invokes a callable expression with an argument list.",
            SyntaxKind.SimpleMemberAccessExpression => "Accesses a named member with a dot.",
            SyntaxKind.ObjectCreationExpression or SyntaxKind.ImplicitObjectCreationExpression => "Creates a new instance with the new keyword.",
            SyntaxKind.ParenthesizedExpression => "An expression enclosed in parentheses.",
            SyntaxKind.ConditionalExpression => "Chooses a value using the conditional ? : operator.",
            SyntaxKind.SimpleAssignmentExpression => "Assigns the right operand to the left operand.",
            SyntaxKind.AddExpression => "A binary + expression.",
            SyntaxKind.SubtractExpression => "A binary - expression.",
            SyntaxKind.MultiplyExpression => "A binary * expression.",
            SyntaxKind.DivideExpression => "A binary / expression.",
            SyntaxKind.EqualsExpression => "An equality comparison using ==.",
            SyntaxKind.NotEqualsExpression => "An inequality comparison using !=.",
            SyntaxKind.LogicalAndExpression => "A conditional AND expression using &&.",
            SyntaxKind.LogicalOrExpression => "A conditional OR expression using ||.",
            SyntaxKind.CoalesceExpression => "A null-coalescing expression using ??.",
            SyntaxKind.SimpleLambdaExpression or SyntaxKind.ParenthesizedLambdaExpression => "An anonymous function written with =>.",
            _ => null,
        };
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
