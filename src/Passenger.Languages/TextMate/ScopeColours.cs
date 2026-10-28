using Passenger.Editor;

namespace Passenger.Languages.TextMate;

/// <summary>Which kind of token each grammar's scopes are; the colour scheme in force colours each kind. A selector
/// names a scope and every scope under it ("string" is "string.quoted.double.json" too), the longest that names a
/// token's innermost scope wins, and a scope no rule names takes its parent's kind. A selector never names a scope's
/// ancestors; language exceptions retain their full scope names from the original colour mapping.</summary>
internal static class ScopeColours
{
    public static readonly IReadOnlyList<(string Selector, TokenKind Kind)> Rules =
    [
        ("comment", TokenKind.Comment),
        ("string", TokenKind.String),
        ("constant.numeric", TokenKind.Number),
        ("constant.language", TokenKind.Keyword),
        ("constant.character.escape", TokenKind.StringEscape),
        ("keyword", TokenKind.Keyword),
        ("keyword.operator", TokenKind.Text),
        ("keyword.operator.new", TokenKind.Keyword),
        ("keyword.operator.expression", TokenKind.Keyword),
        ("storage", TokenKind.Keyword),
        ("variable.language", TokenKind.Keyword),
        ("entity.name.function", TokenKind.Method),
        ("support.function", TokenKind.Method),
        ("entity.name.type", TokenKind.Class),
        ("entity.name.class", TokenKind.Class),
        ("entity.other.inherited-class", TokenKind.Class),
        ("support.class", TokenKind.Class),
        ("support.type.primitive", TokenKind.Keyword),
        ("variable.other.property", TokenKind.Field),
        ("variable.other.object.property", TokenKind.Field),
        ("meta.object-literal.key", TokenKind.Field),
        ("invalid", TokenKind.Text),

        // JavaScript and TypeScript: a template's expressions are code; a regular expression is a string throughout.
        ("meta.template.expression", TokenKind.Text),
        ("keyword.operator.quantifier.regexp", TokenKind.String),
        ("keyword.operator.or.regexp", TokenKind.String),
        ("keyword.control.anchor.regexp", TokenKind.String),
        ("keyword.other.back-reference.regexp", TokenKind.String),
        ("constant.other.character-class", TokenKind.String),
        ("constant.character.character-class.regexp", TokenKind.String),

        // XML and HTML: the whole tag in the tag's colour (its brackets, its =), its attributes' names and values in theirs.
        ("meta.tag", TokenKind.Tag),
        ("entity.name.tag", TokenKind.Tag),
        ("punctuation.definition.tag", TokenKind.Tag),
        ("entity.other.attribute-name", TokenKind.AttributeName),
        ("string.quoted.double.xml", TokenKind.AttributeValue),
        ("string.quoted.single.xml", TokenKind.AttributeValue),
        ("string.quoted.double.html", TokenKind.AttributeValue),
        ("string.quoted.single.html", TokenKind.AttributeValue),
        ("string.unquoted.html", TokenKind.AttributeValue),
        ("constant.character.entity", TokenKind.Entity),
        ("string.unquoted.cdata", TokenKind.Text),

        // JSON: keys apart from values.
        ("support.type.property-name.json", TokenKind.JsonKey),

        // YAML: keys in their colour, every unquoted scalar (numbers, booleans, a block's lines) plain.
        ("entity.name.tag.yaml", TokenKind.YamlKey),
        ("string.unquoted.plain", TokenKind.Text),
        ("string.unquoted.block.yaml", TokenKind.Text),
        ("constant.language.boolean.yaml", TokenKind.Text),
        ("constant.language.null.yaml", TokenKind.Text),
        ("constant.language.value.yaml", TokenKind.Text),
        ("constant.language.merge.yaml", TokenKind.Text),
        ("constant.numeric.float.yaml", TokenKind.Text),
        ("constant.numeric.integer.yaml", TokenKind.Text),
        ("constant.other.timestamp.yaml", TokenKind.Text),
        ("keyword.control.flow.alias.yaml", TokenKind.Text),
        ("keyword.control.property.anchor.yaml", TokenKind.Text),
        ("keyword.control.flow.block-scalar.folded.yaml", TokenKind.Text),
        ("keyword.control.flow.block-scalar.literal.yaml", TokenKind.Text),
        ("entity.name.type.anchor.yaml", TokenKind.Text),

        // Markdown: headings bold in the constant's colour, markers in the keyword's, code in its own.
        ("markup.heading", TokenKind.Heading),
        ("punctuation.definition.heading.markdown", TokenKind.Keyword),
        ("markup.bold", TokenKind.Bold),
        ("punctuation.definition.bold.markdown", TokenKind.Keyword),
        ("markup.italic", TokenKind.Italic),
        ("punctuation.definition.italic.markdown", TokenKind.Keyword),
        ("markup.strikethrough", TokenKind.Text),
        ("markup.inline.raw", TokenKind.Code),
        ("markup.fenced_code", TokenKind.Code),
        ("markup.raw", TokenKind.Code),
        ("meta.separator.markdown", TokenKind.Code),
        ("punctuation.definition.table.markdown", TokenKind.Code),
        ("punctuation.separator.table.markdown", TokenKind.Code),
        ("markup.quote", TokenKind.Quote),
        ("punctuation.definition.quote.begin.markdown", TokenKind.Keyword),
        ("punctuation.definition.list.begin.markdown", TokenKind.Keyword),
        ("string.other.link.title.markdown", TokenKind.Link),
        ("string.other.link.description.markdown", TokenKind.Link),
        ("markup.underline.link", TokenKind.LinkDestination),
        ("constant.other.reference.link.markdown", TokenKind.LinkDestination),
        ("constant.character.escape.markdown", TokenKind.Text),

        // A fenced block in a language with a grammar here is that language from plain text up; another stays code.
        ("meta.embedded.block.csharp", TokenKind.Text),
        ("meta.embedded.block.css", TokenKind.Text),
        ("meta.embedded.block.dockerfile", TokenKind.Text),
        ("meta.embedded.block.html", TokenKind.Text),
        ("meta.embedded.block.javascript", TokenKind.Text),
        ("meta.embedded.block.json", TokenKind.Text),
        ("meta.embedded.block.jsonc", TokenKind.Text),
        ("meta.embedded.block.less", TokenKind.Text),
        ("meta.embedded.block.scss", TokenKind.Text),
        ("meta.embedded.block.shellscript", TokenKind.Text),
        ("meta.embedded.block.typescript", TokenKind.Text),
        ("meta.embedded.block.xml", TokenKind.Text),
        ("meta.embedded.block.yaml", TokenKind.Text),

        // CSS: selectors in the tag's colour, property names in the keyword's, values plain, units with their numbers.
        ("entity.other.attribute-name.class.css", TokenKind.Tag),
        ("entity.other.attribute-name.id.css", TokenKind.Tag),
        ("entity.other.attribute-name.pseudo-class.css", TokenKind.Tag),
        ("entity.other.attribute-name.pseudo-element.css", TokenKind.Tag),
        ("support.type.property-name.css", TokenKind.Keyword),
        ("support.constant.property-value", TokenKind.Text),
        ("keyword.other.unit", TokenKind.Number),
        ("constant.other.color", TokenKind.Number),
        ("variable.css", TokenKind.Text),
        ("variable.argument.css", TokenKind.Text),

        // SCSS and Less as CSS, variables as fields; Less's bare words and keyframe offsets plain, its escapes strings.
        ("variable.scss", TokenKind.Field),
        ("entity.other.attribute-name.placeholder.css", TokenKind.Tag),
        ("entity.other.attribute-name.parent-selector-suffix.css", TokenKind.Tag),
        ("entity.other.attribute-name.scss", TokenKind.Text),
        ("variable.other.readwrite.less", TokenKind.Field),
        ("entity.other.attribute-name.class.less", TokenKind.Tag),
        ("entity.other.attribute-name.id.less", TokenKind.Tag),
        ("entity.other.attribute-name.parent.less", TokenKind.Tag),
        ("entity.other.attribute-name.pseudo-class.less", TokenKind.Tag),
        ("entity.other.attribute-name.pseudo-element.less", TokenKind.Tag),
        ("punctuation.definition.entity.less", TokenKind.Tag),
        ("support.type.property-name.less", TokenKind.Keyword),
        ("keyword.other.keyframe-selector.less", TokenKind.Text),
        ("string.unquoted.less", TokenKind.Text),
        ("markup.raw.inline.less", TokenKind.String),

        // Shell: commands, their arguments and variables plain.
        ("string.unquoted.argument", TokenKind.Text),
        ("string.unquoted.shell", TokenKind.Text),
        ("string.unquoted.pattern", TokenKind.Text),
        ("constant.other.option", TokenKind.Text),
        ("entity.name.command", TokenKind.Text),
        ("entity.name.function.call.shell", TokenKind.Text),
        ("entity.other.attribute-name.shell", TokenKind.Text),
        ("entity.other.attribute-name.bracket.shell", TokenKind.Text),
        ("variable.language.special", TokenKind.Text),
        ("support.function.builtin.shell", TokenKind.Text),
    ];
}
