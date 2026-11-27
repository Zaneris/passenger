using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

public sealed class SyntaxTreeTests : IDisposable
{
    private readonly string folder = TestFolders.Create("syntax-tree-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void The_right_syntax_tree_follows_the_active_CSharp_editors_selection_and_unsaved_edits()
    {
        const string code = "class Calculator\n{\n    int Combine(int left, int right)\n    {\n        // Combine the values.\n        return left + right;\n    }\n}\n";
        string source = TestFolders.Write(folder, "Calculator.cs", code);
        TestFolders.Write(folder, "Other.cs", "record Other;\n");
        TestFolders.Write(folder, "Notes.txt", "plain text");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false, autosaves: false);
        try
        {
            window.OpenFile("Calculator.cs");
            Ui.Settle();
            ToolWindow tool = Assert.Single(window.DockHost.Windows, w => w.Title == "Syntax Tree");
            Assert.Equal(ToolAnchor.RightTop, tool.Anchor);
            Assert.Single(window.DockHost.GetVisualDescendants().OfType<ToggleButton>(), b => Equals(b.Tag, tool.Id));
            CodeEditor editor = window.EditorArea.CodeEditor;
            int start = code.IndexOf("left + right", StringComparison.Ordinal);
            editor.Selections = SelectionSet.Single(start + 1);
            Assert.True(window.Perform(AppActions.ShowSyntaxTree));
            Ui.Settle();
            Assert.Equal(Selection.At(start + 1), editor.Selections.Primary);
            TreeList tree = Assert.Single(tool.Content.GetVisualDescendants().OfType<TreeList>());
            PaneSplit panes = Assert.Single(tool.Content.GetVisualDescendants().OfType<PaneSplit>());
            Assert.True(panes.IsStacked);
            editor.Focus();
            Ui.WaitFor(() => SelectedKind(tree, "IdentifierToken"), "the identifier token at the caret");
            Assert.Same(editor, window.FocusManager?.GetFocusedElement());

            ListBoxItem identifierName = Assert.IsAssignableFrom<ListBoxItem>(tree.ContainerFromIndex(tree.SelectedIndex - 1));
            Assert.Contains(identifierName.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "IdentifierName");
            Ui.Click(identifierName);
            Ui.WaitFor(() => editor.Selections.Primary.Range == new TextRange(start, "left".Length),
                "the source span chosen from the identifier name");
            Ui.Settle();
            Assert.True(SelectedKind(tree, "IdentifierName"));

            editor.Focus();
            editor.Selections = SelectionSet.Single(new Selection(start + "left + right".Length, start));
            Ui.WaitFor(() => SelectedKind(tree, "AddExpression"), "the addition covering the backward selection");
            Assert.Equal(start, editor.Selections.Primary.Caret);
            Assert.Contains(tool.Content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "BinaryExpressionSyntax");
            Assert.Contains(tool.Content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "left + right");
            Assert.True(tree.ContainerFromIndex(0)?.GetVisualDescendants().OfType<RowGlyphs>().Single().Turned);

            int operation = start + "left ".Length;
            ListBoxItem plus = tree.GetVisualDescendants().OfType<ListBoxItem>()
                .Single(item => item.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "PlusToken"));
            Ui.Click(plus);
            Ui.WaitFor(() => editor.Selections.Primary.Start == operation && editor.Selections.Primary.Length == 1,
                "the plus operator selected in the editor from the tree");
            Assert.True(SelectedKind(tree, "PlusToken"));

            double initialTreeHeight = tree.Bounds.Height;
            Point divider = Ui.Centre(panes.Divider.Splitter, window);
            Ui.Drag(window, divider, divider - new Vector(0, 60));
            Ui.WaitFor(() => tree.Bounds.Height < initialTreeHeight - 40, "the lower property pane made taller by dragging");
            double resizedTreeHeight = tree.Bounds.Height;

            int comment = code.IndexOf("Combine the values", StringComparison.Ordinal);
            editor.Selections = SelectionSet.Single(comment);
            Ui.WaitFor(() => SelectedKind(tree, "SingleLineCommentTrivia"), "the comment trivia at the caret");

            editor.Focus();
            editor.Selections = SelectionSet.Single(new Selection(operation, operation + 1));
            window.KeyTextInput("*");
            editor.Selections = SelectionSet.Single(new Selection(start, start + "left * right".Length));
            Ui.WaitFor(() => SelectedKind(tree, "MultiplyExpression"), "multiplication before saving");
            Assert.Equal(code, File.ReadAllText(source));

            window.KeyTextInput("0");
            window.OpenFile("Other.cs");
            CodeEditor other = window.EditorArea.CodeEditor;
            other.Selections = SelectionSet.Single(1);
            Ui.WaitFor(() => SelectedKind(tree, "RecordKeyword"), "the record keyword in the newly active file");
            Ui.Settle();
            Assert.True(SelectedKind(tree, "RecordKeyword"));
            Assert.Equal(resizedTreeHeight, tree.Bounds.Height, precision: 1);

            window.DockHost.Hide(ToolWindowIds.SyntaxTree);
            window.OpenFile("Calculator.cs");
            window.EditorArea.CodeEditor.Selections = SelectionSet.Single(1);
            Assert.Equal(0, tree.ItemCount);
            Assert.True(window.Perform(AppActions.ShowSyntaxTree));
            Ui.WaitFor(() => SelectedKind(tree, "ClassKeyword"), "the active file's syntax after reopening the pane");
            Assert.Equal(resizedTreeHeight, tree.Bounds.Height, precision: 1);

            window.OpenFile("Notes.txt");
            Ui.WaitFor(() => !tree.IsEffectivelyVisible && tree.ItemCount == 0, "the empty state for a non-C# file");
        }
        finally
        {
            window.Close();
        }
    }

    private static bool SelectedKind(TreeList tree, string kind) =>
        tree.SelectedIndex >= 0 && tree.ContainerFromIndex(tree.SelectedIndex)?.GetVisualDescendants()
            .OfType<TextBlock>().Any(text => text.Text == kind) == true;
}
