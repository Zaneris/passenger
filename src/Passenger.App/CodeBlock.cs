using Avalonia.Controls;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>Code shown to read and copy (an agent's setup, the arguments an agent asks to use a tool with): the editor's
/// font on its ground, in Shell.axaml's Border.code frame, selectable and wrapped rather than cut.</summary>
public static class CodeBlock
{
    public static Border Of(string text) => new()
    {
        Classes = { StyleClasses.Code },
        Child = new SelectableTextBlock { Text = text, FontFamily = CodeEditor.CodeFont, FontSize = 12, TextWrapping = TextWrapping.Wrap },
    };
}
