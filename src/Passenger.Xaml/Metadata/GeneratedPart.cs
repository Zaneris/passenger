namespace Passenger.Xaml.Metadata;

/// <summary>What source generators added to a type the project's C# declares, as the language server's compilation has it,
/// whichever generator made it: the type by its full name, the files of its own that declare it, and the members only
/// generated code declares, each placed where a person goes to it in those files (the field or method it is made of).</summary>
public sealed record GeneratedPart(string FullName, IReadOnlyList<string> Files, IReadOnlyList<GeneratedMember> Members)
{
    /// <summary>What it says, as one string, to tell whether a new answer says anything new.</summary>
    public string Signature => signature ??= string.Join('|', [FullName, .. Files, .. Members.Select(m => m.Signature)]);

    private string? signature;
}

/// <summary>A member a generator declares, as <see cref="ClrMember"/> holds one.</summary>
public sealed record GeneratedMember(string Name, MemberKind Kind, TypeRef Type, bool IsStatic, bool CanWrite, IReadOnlyList<TypeRef> Parameters,
    IReadOnlyList<string> ParameterNames, IReadOnlyList<ClrAttribute> Attributes, SourcePlace Place)
{
    internal string Signature => string.Join(':', [Name, Kind.ToString(), Said(Type), IsStatic.ToString(), CanWrite.ToString(), .. Parameters.Select(Said),
        .. ParameterNames, .. Attributes.Select(a => $"{a.TypeName}({string.Join(',', a.Arguments)})"), Place.Path, Place.Line.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Place.Column.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    private static string Said(TypeRef type) =>
        $"{type.Name}{(type.IsGenericParameter ? "!" : string.Empty)}<{string.Join(',', type.Arguments.Select(Said))}>{(type.IsArray ? "[]" : string.Empty)}";

    /// <summary>The member of a type it is made part of.</summary>
    internal ClrMember Of(ClrType type) =>
        new(type, Name, Kind, Type, IsStatic, isPublic: true, CanWrite, Parameters, ParameterNames, Attributes, Place);
}
