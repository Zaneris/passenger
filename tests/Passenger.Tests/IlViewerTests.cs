using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Run;
using Passenger.Editor;

namespace Passenger.Tests;

public sealed class IlViewerTests
{
    [AvaloniaFact]
    public void The_right_side_viewer_shows_the_last_build_in_both_modes_and_follows_the_file()
    {
        string folder = TestFolders.Create("il-viewer");
        string project = TestFolders.Write(folder, "Sample.csproj", TestProjects.Library());
        string first = TestFolders.Write(folder, "Counter.cs", """
            using System.Threading.Tasks;

            public static partial class Counter
            {
                public static async Task<int> CountAsync()
                {
                    await Task.Yield();
                    return 1;
                }
            }
            """);
        string second = TestFolders.Write(folder, "Counter.Other.cs", """
            public static partial class Counter
            {
                public static int OtherCount() => 2;
            }
            """);
        MainWindow window = Ui.OpenFolder(project);
        try
        {
            Ui.WaitFor(() => window.Projects is [{ IsEvaluated: true }], "the C# project evaluation");
            Assert.Null(window.OpenFile(first));
            Ui.WaitFor(() => window.EditorArea.Active?.ProjectContext is not null, "the active file's C# project context", Restore.Limit);
            ToggleButton stripe = window.GetVisualDescendants().OfType<ToggleButton>()
                .Single(button => button.Tag as string == ToolWindowIds.IlViewer);
            Ui.Click(stripe);
            ToolWindow tool = window.DockHost[ToolWindowIds.IlViewer];
            Assert.Equal(ToolAnchor.RightTop, tool.Anchor);
            Ui.WaitFor(() => tool.Content.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                text.Text == "Build the project to see the compiled code for Counter.cs."), "the prompt to build first");

            BuildOutcome built = Ui.Await(window.BuildAsync(), "the C# project build", Restore.Limit);
            Assert.True(built.Succeeded, built.Message + string.Join(Environment.NewLine, built.Output));
            CodeEditor output = tool.Content.GetVisualDescendants().OfType<CodeEditor>().Single();
            Ui.WaitFor(() => Text().Contains("IL_0000", StringComparison.Ordinal), "IL from the build");
            Assert.Contains("CountAsync", Text(), StringComparison.Ordinal);
            Assert.DoesNotContain("OtherCount", Text(), StringComparison.Ordinal);

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "await Task.Yield()");
            Ui.WaitFor(() => Highlight().Contains("::Yield()", StringComparison.Ordinal), "the IL instructions for the await statement");
            Assert.DoesNotContain("ldc.i4.1", Highlight(), StringComparison.Ordinal);
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "return 1");
            Ui.WaitFor(() => Highlight().Contains("ldc.i4.1", StringComparison.Ordinal), "the IL instructions for the return statement");
            Assert.DoesNotContain("::Yield()", Highlight(), StringComparison.Ordinal);

            int opcode = Text().IndexOf("ldc.i4.1", output.Highlights[0].Start, StringComparison.Ordinal);
            Assert.True(output.Highlights[0].Contains(opcode));
            Assert.Contains(output.Highlighter!.GetSpans(output.Document.Snapshot, opcode - 9, 17), span =>
                span is { Kind: TokenKind.Keyword } && span.Start == opcode && span.Length == "ldc.i4.1".Length);
            Assert.Contains(output.Highlighter.GetSpans(output.Document.Snapshot, opcode - 9, 17), span =>
                span is { Kind: TokenKind.Constant } && span.Start < opcode);
            Ui.Hover(output, output.PointOf(opcode + 1) + new Point(0, output.LineHeight / 2));
            Ui.WaitFor(() => ToolTip.GetIsOpen(output), "the opcode tooltip");
            StackPanel tip = Assert.IsType<StackPanel>(ToolTip.GetTip(output));
            Assert.Contains(tip.Children.OfType<TextBlock>(), text => text.Text == "ldc.i4.1");
            Assert.Contains(tip.Children.OfType<TextBlock>(), text => text.Text?.Contains("pop 0 · push 1", StringComparison.Ordinal) == true);
            Ui.Hover(output, output.PointOf(opcode - 5) + new Point(0, output.LineHeight / 2));
            Ui.WaitFor(() => !ToolTip.GetIsOpen(output), "the tooltip dismissing over the IL offset");

            Ui.Click(tool.Content.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "IlViewerCSharp"));
            Ui.WaitFor(() => Text().Contains("void MoveNext()", StringComparison.Ordinal), "the decompiled async state machine");
            Assert.Contains("IAsyncStateMachine", Text(), StringComparison.Ordinal);

            Assert.Null(window.OpenFile(second));
            Ui.WaitFor(() => Text().Contains("OtherCount", StringComparison.Ordinal), "the second file's compiled output");
            Assert.DoesNotContain("CountAsync", Text(), StringComparison.Ordinal);

            string Text() => output.Document.Snapshot.ToString();
            string Highlight() => output.Highlights.Count == 1 ? Text().Substring(output.Highlights[0].Start, output.Highlights[0].Length) : string.Empty;
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }
}
