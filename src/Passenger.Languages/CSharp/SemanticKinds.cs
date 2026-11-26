using Passenger.Editor;

namespace Passenger.Languages.CSharp;

/// <summary>The editor's kind for each token type a C# server names, a file's semantic tokens' and a card's code's
/// (<see cref="ServerCode"/>) alike.</summary>
public static class SemanticKinds
{
    /// <summary>Roslyn's token types, standard and its own, and Razor's for a Razor file's markup, as the editor's kinds:
    /// a component's tag in a type's colour and its parameters in a property's, HTML's in the HTML grammar's kinds,
    /// Razor's directives and transitions as keywords, and the inside of a regex's or JSON's string in code's kinds (a
    /// regex's classes as constants, its anchors as keywords; JSON's names as a JSON file's keys). Operators are the
    /// theme's; punctuation and text are left to the syntax, but a regex's or JSON's, which stand inside a string.</summary>
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
        _ when type.StartsWith("xmlDocComment", StringComparison.Ordinal) => TokenKind.DocComment,
        _ => null,
    };
}
