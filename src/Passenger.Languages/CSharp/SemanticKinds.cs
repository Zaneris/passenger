using Passenger.Editor;

namespace Passenger.Languages.CSharp;

/// <summary>The editor's kind for each token type a C# server names, a file's semantic tokens' and a card's code's
/// (<see cref="ServerCode"/>) alike.</summary>
public static class SemanticKinds
{
    /// <summary>Roslyn's token types and Razor's as the editor's kinds: a component's tag a type's, its parameters a property's,
    /// HTML's the HTML grammar's, Razor's directives and transitions keywords, a regex's or JSON's string's inside code's (a
    /// regex's classes constants, its anchors keywords; JSON's names a JSON file's keys), a documentation comment's tags,
    /// attributes' names and values apart from its words. Operators are the theme's; punctuation and text are left to the
    /// syntax, but a regex's or JSON's, which stand inside a string.</summary>
    public static TokenKind? Of(string type) => type switch
    {
        "razorTransition" or "razorDirective" or "razorDirectiveColon" or "razorDirectiveAttribute" => TokenKind.Keyword,
        "razorComponentElement" or "razorTagHelperElement" => TokenKind.Class,
        "razorComponentAttribute" or "razorTagHelperAttribute" => TokenKind.Property,
        "razorComment" or "razorCommentStar" or "razorCommentTransition" or "markupComment" or "markupCommentPunctuation" => TokenKind.Comment,
        "markupElement" or "markupTagDelimiter" or "markupOperator" => TokenKind.Tag,
        "markupAttribute" => TokenKind.AttributeName,
        "markupAttributeValue" or "markupAttributeQuote" => TokenKind.AttributeValue,
        "namespace" => TokenKind.Namespace,
        "class" or "recordClass" or "recordClassName" or "className" or "record" => TokenKind.Class,
        "struct" or "recordStruct" or "recordStructName" or "structName" => TokenKind.Struct,
        "interface" or "interfaceName" => TokenKind.Interface,
        "enum" or "enumName" => TokenKind.Enum,
        "delegate" or "delegateName" => TokenKind.Delegate,
        "typeParameter" or "typeParameterName" => TokenKind.TypeParameter,
        "method" or "methodName" or "extensionMethod" or "extensionMethodName" or "function" => TokenKind.Method,
        "property" or "propertyName" => TokenKind.Property,
        "event" or "eventName" => TokenKind.Event,
        "field" or "fieldName" => TokenKind.Field,
        "constant" or "constantName" => TokenKind.Constant,
        "enumMember" or "enumMemberName" => TokenKind.EnumMember,
        "variable" or "local" or "localName" => TokenKind.Local,
        "parameter" or "parameterName" => TokenKind.Parameter,
        "label" or "labelName" => TokenKind.Label,
        "operator" or "operatorOverloaded" => TokenKind.Operator,
        "keyword" => TokenKind.Keyword,
        "controlKeyword" => TokenKind.ControlKeyword,
        "string" or "stringVerbatim" => TokenKind.String,
        "stringEscapeCharacter" => TokenKind.StringEscape,
        "number" => TokenKind.Number,
        "comment" => TokenKind.Comment,
        "excludedCode" => TokenKind.Excluded,
        "preprocessorKeyword" or "preprocessorText" => TokenKind.Preprocessor,
        "regexCharacterClass" => TokenKind.Constant,
        "regexAnchor" => TokenKind.Keyword,
        "regexQuantifier" or "regexAlternation" => TokenKind.Operator,
        "regexGrouping" => TokenKind.Punctuation,
        "regexSelfEscapedCharacter" or "regexOtherEscape" => TokenKind.StringEscape,
        "regexComment" or "jsonComment" => TokenKind.Comment,
        "regexText" or "jsonString" or "jsonText" => TokenKind.String,
        "jsonPropertyName" => TokenKind.JsonKey,
        "jsonNumber" => TokenKind.Number,
        "jsonKeyword" => TokenKind.Keyword,
        "jsonConstructorName" => TokenKind.Method,
        "jsonOperator" => TokenKind.Operator,
        "jsonPunctuation" or "jsonArray" or "jsonObject" => TokenKind.Punctuation,
        "xmlDocCommentName" or "xmlDocCommentDelimiter" or "xmlDocCommentEntityReference" or "xmlDocCommentProcessingInstruction" => TokenKind.DocCommentTag,
        "xmlDocCommentAttributeName" => TokenKind.DocCommentAttributeName,
        "xmlDocCommentAttributeValue" or "xmlDocCommentAttributeQuotes" => TokenKind.DocCommentAttributeValue,
        "xmlDocCommentComment" => TokenKind.Comment,
        _ when type.StartsWith("xmlDocComment", StringComparison.Ordinal) => TokenKind.DocComment,
        _ => null,
    };
}
