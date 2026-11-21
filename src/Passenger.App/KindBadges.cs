using Passenger.Lsp;

namespace Passenger.App;

/// <summary>What a kind badge marks: a kind of symbol, of member or of completion item.</summary>
public enum BadgeKind
{
    Other,
    Namespace,
    Class,
    Interface,
    Struct,
    Enum,
    EnumMember,
    TypeParameter,
    Method,
    ExtensionMethod,
    Property,
    Field,
    Constant,
    Event,
    Operator,
    Local,
    Parameter,

    /// <summary>A parameter named at a call to give it its argument (<c>value:</c>).</summary>
    NamedArgument,
    Keyword,
    Template,

    /// <summary>A node of a Godot scene.</summary>
    Node,

    /// <summary>A heading of a Markdown file.</summary>
    Heading,
}

/// <summary>The one map from a kind to its badge, its letter on its kind's colour (the theme's resource of that key), so a
/// kind shows alike wherever a badge marks it: Go to Symbol and File Structure, completion and its filters, New Item's
/// types. The protocol's kinds of symbol and of completion item are read into it here too. UiRulesTests fails a badge's
/// letter and colour written anywhere else.</summary>
public static class KindBadges
{
    public static (string Letter, string Brush) Of(BadgeKind kind) => kind switch
    {
        BadgeKind.Namespace => ("N", TokenKeys.Syntax_Keyword),
        BadgeKind.Class => ("C", TokenKeys.Syntax_Class),
        BadgeKind.Interface => ("I", TokenKeys.Syntax_Interface),
        BadgeKind.Struct => ("S", TokenKeys.Syntax_Struct),
        BadgeKind.Enum => ("E", TokenKeys.Syntax_Enum),
        BadgeKind.EnumMember => ("e", TokenKeys.Syntax_EnumMember),
        BadgeKind.TypeParameter => ("T", TokenKeys.Syntax_TypeParameter),
        BadgeKind.Method => ("m", TokenKeys.Syntax_Method),
        BadgeKind.ExtensionMethod => ("x", TokenKeys.Syntax_Method),
        BadgeKind.Property => ("p", TokenKeys.Syntax_Property),
        BadgeKind.Field => ("f", TokenKeys.Syntax_Field),
        BadgeKind.Constant => ("c", TokenKeys.Syntax_Constant),
        BadgeKind.Event => ("e", TokenKeys.Syntax_Event),
        BadgeKind.Operator => ("o", TokenKeys.TextMuted),
        BadgeKind.Local => ("v", TokenKeys.TextMuted),
        BadgeKind.Parameter => ("a", TokenKeys.TextMuted),
        BadgeKind.NamedArgument => ("n", TokenKeys.TextMuted),
        BadgeKind.Keyword => ("k", TokenKeys.Syntax_Keyword),
        BadgeKind.Template => ("s", TokenKeys.TextMuted),
        BadgeKind.Node => ("n", TokenKeys.Syntax_Class),
        BadgeKind.Heading => ("H", TokenKeys.Syntax_Heading),
        _ => ("·", TokenKeys.TextMuted),
    };

    /// <summary>The badge of a symbol of the protocol's kind; a variable a local's, a constructor a method's.</summary>
    public static BadgeKind Of(SymbolKind kind) => kind switch
    {
        SymbolKind.Namespace => BadgeKind.Namespace,
        SymbolKind.Class => BadgeKind.Class,
        SymbolKind.Method or SymbolKind.Function or SymbolKind.Constructor => BadgeKind.Method,
        SymbolKind.Property => BadgeKind.Property,
        SymbolKind.Field => BadgeKind.Field,
        SymbolKind.Enum => BadgeKind.Enum,
        SymbolKind.Interface => BadgeKind.Interface,
        SymbolKind.Variable => BadgeKind.Local,
        SymbolKind.Constant => BadgeKind.Constant,
        SymbolKind.EnumMember => BadgeKind.EnumMember,
        SymbolKind.Struct => BadgeKind.Struct,
        SymbolKind.Event => BadgeKind.Event,
        SymbolKind.Operator => BadgeKind.Operator,
        SymbolKind.TypeParameter => BadgeKind.TypeParameter,
        _ => BadgeKind.Other,
    };

    /// <summary>The badge of a completion item of the protocol's kind, read as its symbol's kind is: a variable a local's, a
    /// constructor a method's.</summary>
    public static BadgeKind Of(CompletionItemKind? kind) => kind switch
    {
        CompletionItemKind.Method or CompletionItemKind.Function or CompletionItemKind.Constructor => BadgeKind.Method,
        CompletionItemKind.Field => BadgeKind.Field,
        CompletionItemKind.Variable => BadgeKind.Local,
        CompletionItemKind.Class => BadgeKind.Class,
        CompletionItemKind.Interface => BadgeKind.Interface,
        CompletionItemKind.Module => BadgeKind.Namespace,
        CompletionItemKind.Property => BadgeKind.Property,
        CompletionItemKind.Enum => BadgeKind.Enum,
        CompletionItemKind.Keyword => BadgeKind.Keyword,
        CompletionItemKind.Snippet => BadgeKind.Template,
        CompletionItemKind.EnumMember => BadgeKind.EnumMember,
        CompletionItemKind.Constant => BadgeKind.Constant,
        CompletionItemKind.Struct => BadgeKind.Struct,
        CompletionItemKind.Event => BadgeKind.Event,
        CompletionItemKind.Operator => BadgeKind.Operator,
        CompletionItemKind.TypeParameter => BadgeKind.TypeParameter,
        _ => BadgeKind.Other,
    };
}
