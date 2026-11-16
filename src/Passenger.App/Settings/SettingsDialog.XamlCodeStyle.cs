namespace Passenger.App.Settings;

/// <summary>The Editor, Code Style, XAML page: the XML formatter's options, which Reformat Code and Code Cleanup lay
/// out Avalonia's and WPF's XAML by, then the attribute order and empty-element style. A file's .editorconfig says over
/// each, by the keys each row names.</summary>
public sealed partial class SettingsDialog
{
    private void AddXamlCodeStyle()
    {
        Add(Declared.AttributeStyle);
        Add(Declared.AttributeIndent);
        Add(Declared.SpacesAroundEquals);
        Add(Declared.SpaceAfterLastAttribute);
        Add(Declared.SpaceBeforeSelfClosing);
        Add(Declared.KeepLineBreaks);
        Add(Declared.MaxBlankLines);
        Add(Declared.MultilineElementsOnNewLine);
        Add(Declared.SinglelineElementsOnNewLine);
        Add(Declared.BreaksInsideElementsWithChildren);
        Add(Declared.BreaksInsideMultilineElements);
        Add(Declared.ChildIndent);
        Add(Declared.TextIndent);
        Add(Declared.BlankLineAfterProcessingInstructions);
        Add(Declared.WrapTags);
        Add(Declared.WrapAroundElements);
        Add(Declared.RightMargin);
        Add(Declared.ReorderAttributes);
        Add(Declared.AttributeOrder);
        Add(Declared.CollapseEmptyElements);
    }
}
