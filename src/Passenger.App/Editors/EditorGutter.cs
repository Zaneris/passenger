using System.Runtime.CompilerServices;
using Passenger.App.Debugging;
using Passenger.App.Navigation;
using Passenger.App.Testing;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.Editors;

/// <summary>An editor's gutter columns, fixed and always these: Git blame and the bookmarks left of the line numbers, the
/// marks (the debugger's, the tests', run icons, colours) and the change bars right of them, each fed from the file the
/// editor shows.</summary>
public sealed class EditorGutter
{
    private static readonly ConditionalWeakTable<CodeEditor, EditorGutter> Gutters = [];

    public EditorGutter(CodeEditor editor)
    {
        Editor = editor;
        editor.SetGutterColumns([Blame, Bookmarks], [Marks, Changes]);
        Gutters.AddOrUpdate(editor, this);
    }

    public CodeEditor Editor { get; }

    public BlameColumn Blame { get; } = new();

    public BookmarkColumn Bookmarks { get; } = new();

    public MarkColumn Marks { get; } = new();

    public ChangeColumn Changes { get; } = new();

    /// <summary>The gutter an editor has, if it has these columns.</summary>
    public static EditorGutter? Of(CodeEditor editor) => Gutters.TryGetValue(editor, out EditorGutter? gutter) ? gutter : null;

    /// <summary>Shows a file's marks.</summary>
    public void Show(OpenDocument document)
    {
        Marks.SetLineMarks(document.LineMarks);
        Marks.SetTestMarks(document.TestMarks);
        Marks.SetRunMarks(document.GutterRunMarks);
        Marks.SetColourMarks(document.ColourMarks);
        Changes.SetMarks(document.ChangeMarks);
        Blame.SetLabels(document.GutterAnnotations);
        Bookmarks.SetLines(document.Bookmarks);
    }

    /// <summary>Follows a property of the file shown that changed, if it is one of the gutter's.</summary>
    public void Follow(OpenDocument document, string? property)
    {
        switch (property)
        {
            case nameof(OpenDocument.LineMarks):
                Marks.SetLineMarks(document.LineMarks);
                break;
            case nameof(OpenDocument.TestMarks):
                Marks.SetTestMarks(document.TestMarks);
                break;
            case nameof(OpenDocument.RunMarks) or nameof(OpenDocument.RouteMarks):
                Marks.SetRunMarks(document.GutterRunMarks);
                break;
            case nameof(OpenDocument.ColourMarks):
                Marks.SetColourMarks(document.ColourMarks);
                break;
            case nameof(OpenDocument.ChangeMarks):
                Changes.SetMarks(document.ChangeMarks);
                break;
            case nameof(OpenDocument.GutterAnnotations):
                Blame.SetLabels(document.GutterAnnotations);
                break;
            case nameof(OpenDocument.Bookmarks):
                Bookmarks.SetLines(document.Bookmarks);
                break;
        }
    }

    /// <summary>Shows no marks, as an editor showing no file does.</summary>
    public void Clear()
    {
        Marks.SetLineMarks([]);
        Marks.SetTestMarks([]);
        Marks.SetRunMarks([]);
        Marks.SetColourMarks([]);
        Changes.SetMarks([]);
        Blame.SetLabels([]);
        Bookmarks.SetLines([]);
    }
}

/// <summary>What an editor's gutter shows, read as the editor's own: its columns' marks, none for an editor without them.</summary>
public static class EditorGutters
{
    extension(CodeEditor editor)
    {
        /// <summary>Its gutter's columns, none for an editor without them (a diff's, a preview's).</summary>
        public EditorGutter? Gutter => EditorGutter.Of(editor);

        /// <summary>The debugger's marks its gutter shows: breakpoints and the execution point.</summary>
        public IReadOnlyList<LineMark> LineMarks => EditorGutter.Of(editor)?.Marks.LineMarks ?? [];

        /// <summary>The tests' run icons its gutter shows.</summary>
        public IReadOnlyList<TestMark> TestMarks => EditorGutter.Of(editor)?.Marks.TestMarks ?? [];

        /// <summary>The run icons its gutter shows: run configurations' and routes'.</summary>
        public IReadOnlyList<RunMark> RunMarks => EditorGutter.Of(editor)?.Marks.RunMarks ?? [];

        /// <summary>The colours' swatches its gutter shows.</summary>
        public IReadOnlyList<ColourMark> ColourMarks => EditorGutter.Of(editor)?.Marks.ColourMarks ?? [];

        /// <summary>The text per line its gutter's annotations column shows (Git blame's), empty for none.</summary>
        public IReadOnlyList<string?> GutterAnnotations => EditorGutter.Of(editor)?.Blame.Labels ?? [];
    }
}
