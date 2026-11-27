using Microsoft.CodeAnalysis.Text;
using Passenger.App.Docking;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App;

public partial class MainWindow
{
    private SyntaxTreeView? syntaxTreeView;
    private OpenDocument? syntaxTreeDocument;

    private void AddSyntaxTree()
    {
        ToolWindow window = new(ToolWindowIds.SyntaxTree, "Syntax Tree", IconKeys.DatabaseRoutine,
            () => syntaxTreeView = new SyntaxTreeView(SelectSyntaxSource), ToolAnchor.RightTop)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowSyntaxTree,
        };
        Dock.Add(window);
        window.Built += (_, _) => FollowSyntaxTree();
        window.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ToolWindow.IsShown))
            {
                FollowSyntaxTree();
            }
        };
        Editors.ActiveChanged += (_, _) => FollowSyntaxTree();
        Editors.CaretMoved += (_, _) => FollowSyntaxTree();
        Closed += (_, _) => SetSyntaxTreeDocument(null);
    }

    private void FollowSyntaxTree()
    {
        if (syntaxTreeView is not { } view)
        {
            return;
        }
        if (!Dock.IsShown(ToolWindowIds.SyntaxTree))
        {
            SetSyntaxTreeDocument(null);
            return;
        }
        OpenDocument? active = Editors.Active;
        OpenDocument? source = active is { Syntax: not null, Diff: null, Tool: null }
            && ReferenceEquals(Editors.CodeEditor.Document, active.Text) ? active : null;
        SetSyntaxTreeDocument(source);
        if (source?.Syntax is not { } syntax)
        {
            view.Say("Open a C# file to see its syntax tree.");
        }
        else if (syntax.Newest is { } parsed && ReferenceEquals(parsed.Text, source.Text.Snapshot)
            && Equals(parsed.Tree.Options, syntax.Options))
        {
            view.Follow(parsed, Editors.CodeEditor.Selections.Primary);
        }
        else
        {
            view.WaitForParse();
        }
    }

    private void SetSyntaxTreeDocument(OpenDocument? source)
    {
        if (syntaxTreeDocument == source)
        {
            return;
        }
        if (syntaxTreeDocument is { } before)
        {
            before.Parsed -= SyntaxTreeParsed;
            before.Text.Changed -= SyntaxTreeEdited;
        }
        syntaxTreeDocument = source;
        syntaxTreeView?.Say(source is null ? "Open a C# file to see its syntax tree." : "Reading C# syntax…");
        if (source is not null)
        {
            source.Parsed += SyntaxTreeParsed;
            source.Text.Changed += SyntaxTreeEdited;
        }
    }

    private void SelectSyntaxSource(ParsedText parsed, TextSpan span)
    {
        if (syntaxTreeDocument is { } document && document == Editors.Active
            && ReferenceEquals(parsed.Text, document.Text.Snapshot)
            && ReferenceEquals(Editors.CodeEditor.Document, document.Text))
        {
            Editors.CodeEditor.Selections = SelectionSet.Single(new Selection(span.Start, span.End));
        }
    }

    private void SyntaxTreeParsed(object? sender, ParsedText parsed) => FollowSyntaxTree();

    private void SyntaxTreeEdited(object? sender, DocumentChangedEventArgs edit) => FollowSyntaxTree();
}
