using System.Globalization;

namespace Passenger.Xaml.Features;

/// <summary>Rider's Attributes format (ReSharper's <c>XmlAttributesFormat</c>), in its order.</summary>
public enum XamlAttributeStyle
{
    OnSingleLine,
    FirstAttributeOnSingleLine,
    OnDifferentLines,
    DoNotTouch,
}

/// <summary>Rider's Attributes indenting (ReSharper's <c>XmlAttributeIndenting</c>), in its order.</summary>
public enum XamlAttributeIndent
{
    SingleIndent,
    DoubleIndent,
    AlignByFirstAttribute,
}

/// <summary>Rider's Indent inside element (ReSharper's <c>XmlIndentingStyle</c>): One indent from parent element, No indent
/// from parent element, No indent at all, or Do not change indenting.</summary>
public enum XamlContentIndent
{
    DoNotTouch,
    OneIndent,
    ZeroIndent,
    RemoveIndent,
}

/// <summary>How Reformat Code lays out XAML: Rider's XML formatter's options (Settings, Editor, Code Style, XAML), each at
/// Rider's default and named in .editorconfig by Rider's own key (<c>xml_attribute_style</c>, <c>xml_attribute_indent</c>,
/// …, <c>max_line_length</c>), with XAML Styler's attribute ordering and empty-element style, off by default
/// (<c>xaml_enable_attribute_reordering</c>, <c>xaml_attribute_ordering_rule_groups</c>,
/// <c>xaml_remove_ending_tag_of_empty_element</c>). The indent itself is the file's (indent_style, indent_size).</summary>
public sealed record XamlFormatSettings
{
    /// <summary>XAML Styler's default attribute ordering rule groups, a group to each <c>;</c>, its rules split by commas.</summary>
    public const string DefaultAttributeOrder = "x:Class; xmlns, xmlns:x; xmlns:*; x:Key, Key, x:Name, Name, x:Uid, Uid, Title; "
        + "Grid.Row, Grid.RowSpan, Grid.Column, Grid.ColumnSpan, Canvas.Left, Canvas.Top, Canvas.Right, Canvas.Bottom; "
        + "Width, Height, MinWidth, MinHeight, MaxWidth, MaxHeight; "
        + "Margin, Padding, HorizontalAlignment, VerticalAlignment, HorizontalContentAlignment, VerticalContentAlignment, Panel.ZIndex; "
        + "*:*, *; PageSource, PageIndex, Offset, Color, TargetName, Property, Value, StartPoint, EndPoint; "
        + "mc:Ignorable, d:IsDataSource, d:LayoutOverrides, d:IsStaticText; Storyboard.*, From, To, Duration";

    public const int MostBlankLines = 100;
    public const int WidestMargin = 1000;

    /// <summary>Attributes format: Do not touch, as in Rider.</summary>
    public XamlAttributeStyle AttributeStyle { get; init; } = XamlAttributeStyle.DoNotTouch;

    /// <summary>Attributes indenting: Align by first attribute.</summary>
    public XamlAttributeIndent AttributeIndent { get; init; } = XamlAttributeIndent.AlignByFirstAttribute;

    /// <summary>Spaces around '=' in attribute.</summary>
    public bool SpacesAroundEquals { get; init; }

    /// <summary>Space after last attribute (before a start tag's <c>&gt;</c>).</summary>
    public bool SpaceAfterLastAttribute { get; init; }

    /// <summary>Space before '/&gt;'.</summary>
    public bool SpaceBeforeSelfClosing { get; init; } = true;

    /// <summary>Keep existing line breaks between tags.</summary>
    public bool KeepLineBreaks { get; init; } = true;

    /// <summary>Maximum blank lines between tags.</summary>
    public int MaxBlankLines { get; init; } = 2;

    /// <summary>Place multiline elements on a new line.</summary>
    public bool MultilineElementsOnNewLine { get; init; } = true;

    /// <summary>Place single-line elements on a new line.</summary>
    public bool SinglelineElementsOnNewLine { get; init; }

    /// <summary>Line breaks after start-tag and before end-tag in elements that contain other elements and no text.</summary>
    public bool BreaksInsideElementsWithChildren { get; init; } = true;

    /// <summary>Line breaks after start-tag and before end-tag in multiline elements.</summary>
    public bool BreaksInsideMultilineElements { get; init; } = true;

    /// <summary>Indent inside element that does not contain text.</summary>
    public XamlContentIndent ChildIndent { get; init; } = XamlContentIndent.OneIndent;

    /// <summary>Indent inside element that contains text.</summary>
    public XamlContentIndent TextIndent { get; init; } = XamlContentIndent.OneIndent;

    /// <summary>Blank line after processing instructions (<c>&lt;?xml …?&gt;</c>).</summary>
    public bool BlankLineAfterProcessingInstructions { get; init; } = true;

    /// <summary>Wrap tags and processing instructions: a start tag past the right margin breaks before an attribute.</summary>
    public bool WrapTags { get; init; } = true;

    /// <summary>Wrap before and after elements: an element that would run past the right margin starts a line of its own.</summary>
    public bool WrapAroundElements { get; init; } = true;

    /// <summary>Right margin (columns): 120, Rider's.</summary>
    public int RightMargin { get; init; } = 120;

    /// <summary>XAML Styler's Enable attribute reordering: attributes put in the order of <see cref="AttributeOrder"/>.</summary>
    public bool ReorderAttributes { get; init; }

    /// <summary>XAML Styler's attribute ordering rule groups: a group to each <c>;</c>, its rules (a name, <c>Prefix.*</c>,
    /// <c>*:*</c> for any prefixed name, <c>*</c> for any) split by commas. An attribute goes in the group of the rule that
    /// names it most nearly, and keeps its order among those its rule matches.</summary>
    public string AttributeOrder { get; init; } = DefaultAttributeOrder;

    /// <summary>XAML Styler's Remove ending tag of empty element: <c>&lt;Grid&gt;&lt;/Grid&gt;</c> is <c>&lt;Grid /&gt;</c>.</summary>
    public bool CollapseEmptyElements { get; init; }

    /// <summary>An empty attribute order back to the default.</summary>
    public XamlFormatSettings Clamped() =>
        this with { AttributeOrder = string.IsNullOrWhiteSpace(AttributeOrder) ? DefaultAttributeOrder : AttributeOrder };

    /// <summary>The .editorconfig keys read, as Rider names them: ReSharper's XML formatter's (which XAML's is) with their
    /// <c>resharper_</c> forms, and XAML Styler's options under <c>xaml_</c>.</summary>
    public static IReadOnlyList<string> EditorConfigKeys { get; } =
    [
        "xml_attribute_style", "xml_attribute_indent", "xml_spaces_around_eq_in_attribute", "xml_space_after_last_attribute",
        "xml_space_before_self_closing", "xml_keep_user_linebreaks", "xml_max_blank_lines_between_tags",
        "xml_linebreak_before_multiline_elements", "xml_linebreak_before_singleline_elements",
        "xml_linebreaks_inside_tags_for_elements_with_child_elements", "xml_linebreaks_inside_tags_for_multiline_elements",
        "xml_indent_child_elements", "xml_indent_text", "xml_blank_line_after_pi", "xml_wrap_tags_and_pi", "xml_wrap_around_elements", "max_line_length",
        "xaml_enable_attribute_reordering", "xaml_attribute_ordering_rule_groups", "xaml_remove_ending_tag_of_empty_element",
    ];

    /// <summary>These settings with what a file's .editorconfig says over them, by Rider's keys (or their
    /// <c>resharper_</c> forms); a value that does not read as its option's is passed over, as editorconfig's are.</summary>
    public XamlFormatSettings Over(Func<string, string?> editorConfig)
    {
        string? Xml(string key) => editorConfig("xml_" + key) ?? editorConfig("resharper_xml_" + key);
        XamlFormatSettings read = this;
        if (Choice<XamlAttributeStyle>(Xml("attribute_style")) is { } style)
        {
            read = read with { AttributeStyle = style };
        }
        if (Choice<XamlAttributeIndent>(Xml("attribute_indent")) is { } indent)
        {
            read = read with { AttributeIndent = indent };
        }
        if (Choice<XamlContentIndent>(Xml("indent_child_elements")) is { } children)
        {
            read = read with { ChildIndent = children };
        }
        if (Choice<XamlContentIndent>(Xml("indent_text")) is { } text)
        {
            read = read with { TextIndent = text };
        }
        read = read with
        {
            SpacesAroundEquals = Flag(Xml("spaces_around_eq_in_attribute")) ?? read.SpacesAroundEquals,
            SpaceAfterLastAttribute = Flag(Xml("space_after_last_attribute")) ?? read.SpaceAfterLastAttribute,
            SpaceBeforeSelfClosing = Flag(Xml("space_before_self_closing")) ?? read.SpaceBeforeSelfClosing,
            KeepLineBreaks = Flag(Xml("keep_user_linebreaks")) ?? read.KeepLineBreaks,
            MaxBlankLines = Number(Xml("max_blank_lines_between_tags"), MostBlankLines) ?? read.MaxBlankLines,
            MultilineElementsOnNewLine = Flag(Xml("linebreak_before_multiline_elements")) ?? read.MultilineElementsOnNewLine,
            SinglelineElementsOnNewLine = Flag(Xml("linebreak_before_singleline_elements")) ?? read.SinglelineElementsOnNewLine,
            BreaksInsideElementsWithChildren = Flag(Xml("linebreaks_inside_tags_for_elements_with_child_elements")) ?? read.BreaksInsideElementsWithChildren,
            BreaksInsideMultilineElements = Flag(Xml("linebreaks_inside_tags_for_multiline_elements")) ?? read.BreaksInsideMultilineElements,
            BlankLineAfterProcessingInstructions = Flag(Xml("blank_line_after_pi")) ?? read.BlankLineAfterProcessingInstructions,
            WrapTags = Flag(Xml("wrap_tags_and_pi")) ?? read.WrapTags,
            WrapAroundElements = Flag(Xml("wrap_around_elements")) ?? read.WrapAroundElements,
            ReorderAttributes = Flag(editorConfig("xaml_enable_attribute_reordering")) ?? read.ReorderAttributes,
            CollapseEmptyElements = Flag(editorConfig("xaml_remove_ending_tag_of_empty_element")) ?? read.CollapseEmptyElements,
        };
        string? margin = Xml("max_line_length") ?? editorConfig("max_line_length");
        if (margin?.Trim().Equals("off", StringComparison.OrdinalIgnoreCase) == true)
        {
            read = read with { RightMargin = 0 };
        }
        else if (Number(margin, WidestMargin) is { } columns)
        {
            read = read with { RightMargin = columns };
        }
        if (editorConfig("xaml_attribute_ordering_rule_groups") is { Length: > 0 } order && !string.IsNullOrWhiteSpace(order))
        {
            read = read with { AttributeOrder = order.Trim() };
        }
        return read;
    }

    /// <summary>An enum's member by its name in any case, with or without underscores (Rider writes <c>on_single_line</c>
    /// for one option and <c>OneIndent</c> for another).</summary>
    private static T? Choice<T>(string? value) where T : struct, Enum
    {
        if (Word(value) is not { } word)
        {
            return null;
        }
        string bare = word.Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (T member in Enum.GetValues<T>())
        {
            if (string.Equals(member.ToString(), bare, StringComparison.OrdinalIgnoreCase))
            {
                return member;
            }
        }
        return null;
    }

    private static bool? Flag(string? value) => Word(value)?.ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    private static int? Number(string? value, int most) =>
        int.TryParse(Word(value), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number <= most ? number : null;

    /// <summary>A value's first word: what follows it (an inline comment, which older editorconfigs allowed) is not part of it.</summary>
    private static string? Word(string? value)
    {
        string? trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        int space = trimmed.AsSpan().IndexOfAny(' ', '\t');
        return space < 0 ? trimmed : trimmed[..space];
    }
}
