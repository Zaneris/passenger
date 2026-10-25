using System.Runtime.CompilerServices;
using Passenger.App.Debugging;
using Passenger.App.Navigation;
using Passenger.App.Testing;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.Editors;

/// <summary>An editor's gutter columns, fixed and always these: Git blame and the bookmarks left of the line numbers, the
/// marks (the debugger's, the tests', run icons, colours) and the change bars right of them, each reading the file the
/// editor shows.</summary>
public sealed class EditorGutter
{
    private static readonly ConditionalWeakTable<CodeEditor, EditorGutter> Gutters = [];

    public EditorGutter(CodeEditor editor)
    {
        Editor = editor;
        Bookmarks = new BookmarkColumn(editor);
        Changes = new ChangeColumn(editor);
        editor.SetGutterColumns([Blame, Bookmarks], [Marks, Changes]);
        Gutters.AddOrUpdate(editor, this);
    }

    public CodeEditor Editor { get; }

    public BlameColumn Blame { get; } = new();

    public BookmarkColumn Bookmarks { get; }

    public MarkColumn Marks { get; } = new();

    public ChangeColumn Changes { get; }

    /// <summary>The gutter an editor has, if it has these columns.</summary>
    public static EditorGutter? Of(CodeEditor editor) => Gutters.TryGetValue(editor, out EditorGutter? gutter) ? gutter : null;

    /// <summary>Shows a file's marks, which the columns read from the file as it changes them, as every editor of it does;
    /// the bookmarks and the change bars are the editor's decorations.</summary>
    public void Show(OpenDocument document)
    {
        Marks.Show(document);
        Blame.Show(document);
    }

    /// <summary>Shows no marks, as an editor showing no file does.</summary>
    public void Clear()
    {
        Marks.Show(null);
        Blame.Show(null);
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
