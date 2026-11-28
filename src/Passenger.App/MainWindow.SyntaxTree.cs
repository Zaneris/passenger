using Microsoft.CodeAnalysis.Text;
using Passenger.App.Docking;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App;

public partial class MainWindow
{
    private void AddSyntaxTree()
    {
        SyntaxTreeView? view = null;
        FollowedDocument followed = FollowedDocument.OfSyntax(() => Refresh(Source()));
        ToolWindow window = new(ToolWindowIds.SyntaxTree, ToolWindowTitles.SyntaxTree, IconKeys.SyntaxTree,
            () => view = new SyntaxTreeView(SelectSource), ToolAnchor.RightTop)
        {
            Serves = ToolWindowServes.EveryFolder,
            Action = AppActions.ShowSyntaxTree,
        };
        Dock.Add(window);
        window.Built += (_, _) => Follow();
        window.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ToolWindow.IsShown))
            {
                Follow();
            }
        };
        Editors.ActiveChanged += (_, _) => Follow();
        Editors.CaretMoved += (_, _) => Follow();
        Closed += (_, _) => ChangeSource(null);

        OpenDocument? Source() => Dock.IsShown(ToolWindowIds.SyntaxTree)
            && Editors.Active is { Syntax: not null, Diff: null, Tool: null } active
            && ReferenceEquals(Editors.CodeEditor.Document, active.Text) ? active : null;

        void ChangeSource(OpenDocument? next)
        {
            if (followed.Show(next))
            {
                view?.Say(next is null ? SyntaxTreeView.NoSource : "Reading C# syntax…");
            }
        }

        void Follow()
        {
            if (view is null)
            {
                return;
            }
            OpenDocument? document = Source();
            ChangeSource(document);
            Refresh(document);
        }

        void Refresh(OpenDocument? document)
        {
            if (view is null || document?.Syntax is not { } syntax)
            {
                return;
            }
            if (syntax.Newest is { } parsed && ReferenceEquals(parsed.Text, document.Text.Snapshot)
                && Equals(parsed.Tree.Options, syntax.Options))
            {
                view.Follow(parsed, Editors.CodeEditor.Selections.Primary.Range);
            }
            else
            {
                view.ClearChoice();
            }
        }

        void SelectSource(ParsedText parsed, TextSpan span)
        {
            if (Source() is { } document && document == followed.Document && ReferenceEquals(parsed.Text, document.Text.Snapshot))
            {
                Editors.CodeEditor.Select(new TextRange(span.Start, span.Length));
            }
        }
    }
}
