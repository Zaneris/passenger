using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.App.DependencyMap;

/// <summary>The map's words for the kinds the language server's extension names: a node's (<see cref="DependencyNode.Kind"/>)
/// and a use's (<see cref="DependencyKinds"/>), each a resource of its own.</summary>
public static partial class MapWords
{
    /// <summary>A node's kind as the map says it; one no resource names, as the server named it.</summary>
    public static Words NodeKind(string kind) => kind switch
    {
        "project" => KindProject,
        "namespace" => KindNamespace,
        "interface" => KindInterface,
        "struct" => KindStruct,
        "enum" => KindEnum,
        "delegate" => KindDelegate,
        "class" => KindClass,
        "record" => KindRecord,
        "record struct" => KindRecordStruct,
        "constructor" => KindConstructor,
        "operator" => KindOperator,
        "method" => KindMethod,
        "property" => KindProperty,
        "enum member" => KindEnumMember,
        "constant" => KindConstant,
        "field" => KindField,
        "event" => KindEvent,
        "symbol" => KindSymbol,
        _ => Words.Verbatim(kind),
    };

    /// <summary>A kind of use as an edge counts it.</summary>
    public static Words Kind(string kind) => kind switch
    {
        DependencyKinds.References => References,
        DependencyKinds.Inherits => Inherits,
        DependencyKinds.Implements => Implements,
        DependencyKinds.Calls => Calls,
        DependencyKinds.FieldType => FieldTypes,
        DependencyKinds.ParameterType => ParameterTypes,
        DependencyKinds.Attribute => Attributes,
        _ => Uses,
    };

    /// <summary>A kind of use as the edge filter's menu lists it.</summary>
    public static Words KindShown(string kind) => kind switch
    {
        DependencyKinds.References => ReferencesShown,
        DependencyKinds.Inherits => InheritsShown,
        DependencyKinds.Implements => ImplementsShown,
        DependencyKinds.Calls => CallsShown,
        DependencyKinds.FieldType => FieldTypesShown,
        DependencyKinds.ParameterType => ParameterTypesShown,
        DependencyKinds.Attribute => AttributesShown,
        _ => UsesShown,
    };
}
