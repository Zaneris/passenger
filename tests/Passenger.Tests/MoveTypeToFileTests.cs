using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A type moved out of a file of two into a file of its own, by the light bulb's Move type to Circle.cs and by
/// Move's Move to Another File alike, on a real project taking its files by the SDK's globs: the new file's row
/// comes into the Explorer, the language server takes it into the project, so the type's uses in its old file and in
/// another show no error and Go to Declaration lands in the new file, and one Undo puts the type back where it was, the
/// file and its row gone and the server holding it no more.</summary>
public sealed class MoveTypeToFileTests : IClassFixture<RestoredFolders>, IDisposable
{
    private const string Shapes = "namespace Shop;\n\npublic class Square\n{\n    public Circle Inside() => new Circle();\n}\n\npublic class Circle\n{\n    public double Radius;\n}\n";

    private const string Garden = "namespace Shop;\n\npublic static class Garden\n{\n    public static double Pond() => new Circle().Radius;\n}\n";

    private readonly string folder;

    public MoveTypeToFileTests(RestoredFolders folders) => folder = PathNames.Canonical(folders.Take("move-type-", made =>
    {
        string project = PathNames.Canonical(made);
        TestFolders.Write(project, "Shop.csproj", TestProjects.Library());
        TestFolders.Write(project, "Shapes.cs", Shapes);
        TestFolders.Write(project, "Garden.cs", Garden);
        return project;
    }));

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_type_the_light_bulb_moves_to_its_own_file_is_in_the_explorer_and_the_project_and_undo_puts_it_back() =>
        Moves("Move type to Circle.cs", window => TypeRenameTests.ContextAction(window, "class Circ", "Move type to Circle.cs"));

    [AvaloniaFact]
    public void A_type_move_takes_to_another_file_is_in_the_explorer_and_the_project_and_undo_puts_it_back() =>
        Moves("Move to Another File", window =>
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "class Circ");
            window.EditorArea.CodeEditor.Focus();
            Ui.Run(window, AppActions.Move);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Move to Another File…"), () => $"Move to offer Move to Another File: [{string.Join("; ", window.Assist.Offered)}]");
            Assert.Equal("Move to Another File…", window.Assist.Offered[0]);
            Ui.Press(window, "ENTER");
            MoveTypeDialog dialog = RefactoringDialogsTests.Shown<MoveTypeDialog>(window, "Move to Another File");
            Assert.Equal("Circle.cs", dialog.TargetBox.Text);
            RefactoringDialogsTests.Next(dialog);
        });

    /// <summary>Circle moved out of Shapes.cs by <paramref name="move"/>, with Garden.cs, which uses it, open beside: the
    /// files as the move leaves them, then each thing a person sees of the new file, then Undo's.</summary>
    private void Moves(string command, Action<MainWindow> move)
    {
        string shapes = Path.Combine(folder, "Shapes.cs");
        string garden = Path.Combine(folder, "Garden.cs");
        string circle = Path.Combine(folder, "Circle.cs");
        MainWindow window = Ui.OpenFolder(folder, autosaves: false);
        List<(string Title, string Text)> asked = RefactoringDialogsTests.Answer(window);
        window.OpenFile(garden);
        window.OpenFile(shapes);
        Ui.Settle();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        OpenDocument shapesOpen = window.EditorArea.Active!;
        OpenDocument gardenOpen = window.EditorArea.Documents.Single(d => d.Path == garden);

        move(window);
        Ui.WaitFor(() => File.Exists(circle), () => $"{command} to make Circle.cs: {window.Status}");
        Ui.Settle();
        Assert.Equal("namespace Shop;\n\npublic class Circle\n{\n    public double Radius;\n}\n", File.ReadAllText(circle).ReplaceLineEndings("\n"));
        Assert.Equal("namespace Shop;\n\npublic class Square\n{\n    public Circle Inside() => new Circle();\n}\n", shapesOpen.Text.Snapshot.ToString());
        Assert.True(window.ExplorerView.Tree.Rows.Any(r => r.Path == circle),
            $"{command} made Circle.cs, but the Explorer shows no row for it: [{string.Join(", ", window.ExplorerView.Tree.Rows.Select(r => r.Name))}]");

        ServerHolds(window, circle, File.ReadAllText(circle), $"{command} made Circle.cs, which the language server never took into the project, so Circle reads as missing wherever it is used");
        NoErrors(window, gardenOpen, "Circle, used from another file, once moved to its own");
        NoErrors(window, shapesOpen, "Circle, used from the file it left, once moved to its own");
        window.OpenFile(garden);
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "new Circ");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => window.EditorArea.Active?.Path == circle, () => $"Go to Declaration of Circle to land in Circle.cs: {window.EditorArea.Active?.Name} is shown; {window.Status}");
        Assert.Equal(2, window.EditorArea.CodeEditor.CaretPosition.Line);

        window.OpenFile(shapes);
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([("Undo", $"Undo {command}? It changes Shapes.cs and Circle.cs.")], asked);
        Assert.False(File.Exists(circle), "Undo left Circle.cs");
        Assert.Equal(Shapes, shapesOpen.Text.Snapshot.ToString());
        Assert.False(window.ExplorerView.Tree.Rows.Any(r => r.Path == circle), "Undo deleted Circle.cs, but the Explorer still shows its row");
        ServerHolds(window, circle, null, "Undo deleted Circle.cs, but the language server still holds it, so Circle reads as declared twice");
        NoErrors(window, gardenOpen, "Circle, used from another file, once Undo put it back");
        window.OpenFile(garden);
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "new Circ");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => window.EditorArea.Active?.Path == shapes, () => $"Go to Declaration of Circle to land in Shapes.cs once Undo put it back: {window.EditorArea.Active?.Name} is shown; {window.Status}");
        Assert.Equal(7, window.EditorArea.CodeEditor.CaretPosition.Line);
        window.Close();
    }

    /// <summary>Waits for the language server to hold a file as <paramref name="text"/>, or to hold no file of that path (null):
    /// a file no tab shows it holds only in a project.</summary>
    private static void ServerHolds(MainWindow window, string path, string? text, string otherwise)
    {
        string? expected = text is null ? null : Passenger.Roslyn.Extension.TextHash.Of(text);
        Task<IReadOnlyList<Passenger.Roslyn.Extension.TextHeld>>? asking = null;
        Ui.WaitFor(() =>
        {
            if (asking is { IsCompletedSuccessfully: true } done && done.Result is [{ } held] && held.Hash == expected)
            {
                return true;
            }
            if (asking is null || asking.IsCompleted)
            {
                asking = window.CSharpService!.Session!.HeldTextsAsync([path]);
            }
            return false;
        }, otherwise, TimeSpan.FromSeconds(30));
    }

    /// <summary>Waits for an open file's diagnostics to hold no error.</summary>
    private static void NoErrors(MainWindow window, OpenDocument document, string what) =>
        Ui.WaitFor(() => window.CSharpService!.For(document)!.Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error),
            () => $"{document.Name} to show no error for {what}: {string.Join("; ", window.CSharpService!.For(document)!.Diagnostics.Select(d => d.Message))}");
}
