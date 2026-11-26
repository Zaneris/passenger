using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Languages;
using Passenger.Text;

namespace Passenger.Tests;

public sealed class ProtobufTests : FileColoursFolder
{
    private const string Request = """
        syntax = "proto3";
        package example.v1;
        import "types.proto";

        // A request uses a type declared in another file.
        message Request {
          Shared value = 13;
        }
        """;

    [AvaloniaFact]
    public void A_proto_file_has_syntax_colours_without_a_language_server()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "request.proto", Request);
        Assert.Same(Language.Protobuf, window.EditorArea.Active!.Language);
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "message"));
        Assert.Equal(TokenKind.String, KindOf(editor, "\"types.proto\""));
        Assert.Equal(TokenKind.Comment, KindOf(editor, "// A request"));
        Assert.Equal(TokenKind.Class, KindOf(editor, "Shared"));
        Assert.Equal(TokenKind.Number, KindOf(editor, "13"));
        window.Close();
    }

    [AvaloniaFact]
    public void Buf_completes_imported_types_goes_to_their_definitions_and_marks_invalid_edits()
    {
        Assert.SkipWhen(Programs.OnPath(OperatingSystem.IsWindows() ? "buf.exe" : "buf") is null, "The Buf CLI is not on the PATH");
        string request = Path.Combine(folder, "request.proto");
        string types = Path.Combine(folder, "types.proto");
        File.WriteAllText(request, Request);
        File.WriteAllText(types, """
            syntax = "proto3";
            package example.v1;

            message Shared {
              string name = 1;
            }
            """);

        MainWindow window = Ui.OpenFolder(folder, languageServer: false, autosaves: false);
        Assert.Null(window.OpenFile(request));
        OpenDocument shown = window.EditorArea.Active!;
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => window.Session.Web?.For(shown)?.Lsp is not null, "Buf attaches to request.proto");
        editor.Focus();
        Ui.PlaceAfter(editor, "  Sh");
        Ui.Run(window, AppActions.Complete);
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("Shared"),
            () => $"completion includes the imported message. Shown: {string.Join(", ", window.Assist.CompletionLabels)}", TimeSpan.FromSeconds(15));
        Ui.Press(window, "ESCAPE");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => PathNames.Same(window.EditorArea.Active!.Path, types), "Go to Declaration opens the imported schema");
        Assert.Equal(new TextPosition(3, "message ".Length), window.EditorArea.CodeEditor.CaretPosition);

        Assert.Null(window.OpenFile(request));
        shown = window.EditorArea.Active!;
        editor = window.EditorArea.CodeEditor;
        int typeStart = Request.IndexOf("Shared", StringComparison.Ordinal);
        editor.ApplyEdits([new TextEdit(typeStart, "Shared".Length, "UnknownType")]);
        Ui.WaitFor(() => shown.Markers.Any(m => m.Kind == MarkerKind.Error && m.Message.Contains("UnknownType", StringComparison.Ordinal)),
            "Buf reports the unresolved type in the unsaved edit");

        editor.ApplyEdits([new TextEdit(typeStart, "UnknownType".Length, "Shared")]);
        Ui.WaitFor(() => shown.Markers.All(m => m.Kind != MarkerKind.Error), "Buf clears the error after the type is restored");
        window.Close();
    }
}
