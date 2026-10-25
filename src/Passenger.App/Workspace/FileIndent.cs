using Passenger.Languages;
using Passenger.Text;

namespace Passenger.App.Workspace;

/// <summary>Where a file's indentation comes from, the first of these that says: an .editorconfig, the file's own
/// lines, the language's default.</summary>
public enum IndentSource
{
    EditorConfig,
    Detected,
    Default,
}

/// <summary>How a file indents and why, as the status bar's indent widget names it.</summary>
public sealed record FileIndent(IndentOptions Options, IndentSource Source, string? EditorConfig = null)
{
    /// <summary>A file's indentation: its .editorconfig's, else what its own lines showed, else its language's.</summary>
    public static FileIndent Choose(EditorConfig conventions, IndentOptions? detected, IndentOptions language) =>
        conventions.Indent(language) is { } configured ? new FileIndent(configured, IndentSource.EditorConfig, conventions.IndentFrom)
        : detected is { } own ? new FileIndent(own, IndentSource.Detected)
        : new FileIndent(language, IndentSource.Default);

    /// <summary>How a file not open indents, read from disk as an open one's is.</summary>
    public static IndentOptions Of(string path, string text)
    {
        Language language = Language.ForPath(path);
        return Choose(Text.EditorConfig.For(path), language.Detect(text), language.Indent).Options;
    }

    /// <summary>The widget's words for the indentation: "Tab", "4 spaces".</summary>
    public string Name => Options.UseTabs ? "Tab" : Core.Plural.Of(Options.Size, "space");

    /// <summary>The widget's text: the indentation and where it comes from.</summary>
    public string Shown => Source switch
    {
        IndentSource.EditorConfig => $"{Name}, from .editorconfig",
        IndentSource.Detected => $"{Name}, detected",
        _ => $"{Name}, default",
    };

    /// <summary>The widget's tip, naming the .editorconfig that gives the indentation.</summary>
    public string Tip(string language) => Source switch
    {
        IndentSource.EditorConfig => $"Indent: {Name}, from {EditorConfig}",
        IndentSource.Detected => $"Indent: {Name}, detected from the file's own lines",
        _ => $"Indent: {Name}, {language}'s default",
    };
}
