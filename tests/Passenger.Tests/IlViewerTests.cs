using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Run;
using Passenger.Editor;

namespace Passenger.Tests;

public sealed class IlViewerTests
{
    [AvaloniaFact]
    public void Assembly_follows_the_paused_managed_frame_and_clears_when_it_runs_on()
    {
        string folder = TestFolders.Create("assembly-viewer");
        string project = TestFolders.Write(folder, "Sample.csproj", TestProjects.Console());
        string file = TestFolders.Write(folder, "Program.cs", """
            System.Console.WriteLine(Square(7));

            static int Square(int value)
            {
                int result = value * value;
                return result;
            }
            """);
        MainWindow window = Ui.OpenFolder(project);
        try
        {
            Ui.WaitFor(() => window.Projects is [{ IsEvaluated: true }], "the console project evaluation", Restore.Limit);
            Assert.Null(window.OpenFile(file));
            ToggleButton stripe = window.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Tag as string == ToolWindowIds.IlViewer);
            Ui.Click(stripe);
            ToolWindow tool = window.DockHost[ToolWindowIds.IlViewer];
            Assert.Equal("Compiled Code", tool.Title);
            Ui.Click(tool.Content.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == "IlViewerAsm"));
            Assert.True(Prompt());

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "int result");
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Launched(window, window.DebugAsync(), "the program's debugger", Restore.Limit);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused, () => Ui.Said(window), Restore.Limit);
            CodeEditor output = tool.Content.GetVisualDescendants().OfType<CodeEditor>().Single();
            Ui.WaitFor(() => output.Highlights.Count == 1, () => "the current native instruction, with the pane saying "
                + string.Join(" | ", tool.Content.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), Restore.Limit);
            string assembly = output.Document.Snapshot.ToString();
            Assert.Contains("ret", assembly, StringComparison.Ordinal);
            Assert.Contains("0x", assembly, StringComparison.Ordinal);
            Assert.Contains(tool.Content.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Square", StringComparison.Ordinal) == true);
            Text.TextRange current = output.Highlights[0];
            Assert.StartsWith("> ", assembly.Substring(current.Start, current.Length), StringComparison.Ordinal);
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "return result");
            Ui.WaitFor(() => output.Highlights.Count == 1 && output.Highlights[0] != current, "the assembly for the return statement", TimeSpan.FromSeconds(10));
            int ret = assembly.LastIndexOf("ret", StringComparison.Ordinal);
            output.GoTo(output.Document.Snapshot.GetPosition(ret));
            Ui.Settle();
            Ui.Hover(output, output.PointOf(ret + 1) + new Point(0, output.LineHeight / 2));
            Ui.WaitFor(() => ToolTip.GetIsOpen(output), "the ASM instruction tooltip", TimeSpan.FromSeconds(10));
            StackPanel tip = Assert.IsType<StackPanel>(ToolTip.GetTip(output));
            Assert.Contains(tip.Children.OfType<TextBlock>(), text => text.Text == "ret");
            Assert.Contains(tip.Children.OfType<TextBlock>(), text => text.Text?.Contains("return", StringComparison.OrdinalIgnoreCase) == true);
            Assert.True(Assert.Single(output.UsageHighlights).Range.Contains(ret));

            Ui.Click(stripe);
            Assert.False(window.DockHost.IsShown(ToolWindowIds.IlViewer));
            int stop = window.Debugger.Session!.Stops;
            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.Session is { State: DebugState.Paused } paused && paused.Stops > stop, "another stop with Compiled Code closed");
            Assert.Equal(assembly, output.Document.Snapshot.ToString());
            Ui.Click(stripe);
            Ui.WaitFor(() => output.Highlights.Count == 1 && output.Highlights[0] != current, "the new instruction after reopening Compiled Code");

            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(Prompt, "the assembly cleared after continuing", Restore.Limit);

            bool Prompt() => tool.Content.GetVisualDescendants().OfType<TextBlock>().Any(t =>
                t.IsVisible && t.Text == "Pause a .NET program to see the selected stack frame's assembly.");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void The_right_side_viewer_shows_the_last_build_in_both_modes_and_follows_the_file()
    {
        string folder = TestFolders.Create("il-viewer");
        string project = TestFolders.Write(folder, "Sample.csproj", TestProjects.Library());
        string first = TestFolders.Write(folder, "Counter.cs", """
            using System.Threading.Tasks;

            public static partial class Counter
            {
                [Marker]
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

            public sealed class MarkerAttribute : System.Attribute { }
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
            int attribute = Text().IndexOf("[Marker]", StringComparison.Ordinal) + 1;
            Assert.True(attribute > 0);
            Assert.NotNull(output.LinkOn(attribute));
            output.GoTo(output.Document.Snapshot.GetPosition(attribute));
            Ui.Settle();
            Ui.Click(output, output.PointOf(attribute + 1) + new Point(0, output.LineHeight / 2));
            Ui.WaitFor(() => window.EditorArea.Active?.Text.Snapshot.ToString().Contains("class MarkerAttribute", StringComparison.Ordinal) == true,
                "the clicked attribute's declaration", TimeSpan.FromSeconds(10));

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
