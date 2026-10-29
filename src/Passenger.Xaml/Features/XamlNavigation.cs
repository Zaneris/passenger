using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>What a XAML name leads to.</summary>
public enum XamlTargetKind
{
    /// <summary>A type: its declaration in the project's C#, or a library's (its source, or decompiled).</summary>
    Type,

    /// <summary>A member: a property, an event, a field, a handler.</summary>
    Member,

    /// <summary>A resource's definition, in this file or another.</summary>
    Resource,

    /// <summary>An element named with <c>x:Name</c>, in this file.</summary>
    Element,

    /// <summary>A file an asset path names.</summary>
    File,

    /// <summary>The control theme a theme library gives a type (Fluent's Button.xaml), or a part of its template.</summary>
    Theme,
}

/// <summary>A place a name leads to: what it is, its name, and where (a source place, a type and member of a library, a
/// resource, a file and range).</summary>
public sealed record XamlTarget(XamlTargetKind Kind, string Name)
{
    public SourcePlace? Place { get; init; }

    public ClrType? Type { get; init; }

    public ClrMember? Member { get; init; }

    public ResourceDefinition? Resource { get; init; }

    public string? Path { get; init; }

    public TextRange? Range { get; init; }

    /// <summary>For a theme's part (<c>PART_ContentPresenter</c>), the name inside the template to find.</summary>
    public string? Part { get; init; }
}

/// <summary>The name at a caret: where it is written, what it is, and where it leads; for an element's name
/// (<c>x:Name</c>, the elements a binding names), the attribute that names it, which rename changes.</summary>
public sealed record XamlSymbol(TextRange Range, string Name, IReadOnlyList<XamlTarget> Targets)
{
    /// <summary>The <c>x:Name</c> or <c>Name</c> attribute of the element the name is, for rename and its uses.</summary>
    public XmlAttribute? Named { get; init; }
}

/// <summary>What a name in a XAML file is: a tag's type (and the control theme a theme gives it), a property's
/// declaration, an attached property's, an event's handler, a resource key's definitions, a type in a value, a binding
/// path's property, a markup extension, an element's name, a selector's type or template part, an asset's file.</summary>
public static class XamlNavigation
{
    public static XamlSymbol? At(XamlFile file, int offset)
    {
        if (file.Tree.InComment(offset))
        {
            return null;
        }
        XmlPlace place = file.Tree.PlaceAt(offset);
        XmlElement? element = place.Element;
        if (element is null)
        {
            return null;
        }
        switch (place.Kind)
        {
            case XmlPlaceKind.StartTagName:
                return Tag(file, element, element.NameRange, offset);
            case XmlPlaceKind.EndTagName when element.EndTagNameRange is { } end:
                return Tag(file, element, end, offset);
            case XmlPlaceKind.AttributeName when place.Attribute is { } attribute:
                return AttributeName(file, attribute, offset);
            case XmlPlaceKind.AttributeValue when place.Attribute is { } attribute:
                return Value(file, attribute, offset);
        }
        return null;
    }

    private static XamlSymbol? Tag(XamlFile file, XmlElement element, TextRange range, int offset)
    {
        string name = file.Text[range.Start..range.End];
        if (element.IsPropertyElement)
        {
            int dot = name.LastIndexOf('.');
            (ClrType? owner, XamlProperty? property) = file.PropertyElement(element);
            if (offset > range.Start + dot && property is not null)
            {
                return new XamlSymbol(TextRange.FromBounds(range.Start + dot + 1, range.End), property.Name, [MemberTarget(property)]);
            }
            return owner is null ? null : new XamlSymbol(TextRange.FromBounds(range.Start, range.Start + dot), owner.Name, [TypeTarget(owner)]);
        }
        if (file.TypeOf(element) is not { } type)
        {
            return null;
        }
        List<XamlTarget> targets = [TypeTarget(type)];
        if (!type.IsFromSource && file.Dialect is AvaloniaDialect && file.Types.IsA(type, "Avalonia.Controls.Primitives.TemplatedControl"))
        {
            targets.Add(new XamlTarget(XamlTargetKind.Theme, $"{type.Name}'s control theme") { Type = type });
        }
        int colon = name.IndexOf(':');
        return new XamlSymbol(colon < 0 ? range : TextRange.FromBounds(range.Start + colon + 1, range.End), type.Name, targets);
    }

    private static XamlSymbol? AttributeName(XamlFile file, XmlAttribute attribute, int offset)
    {
        XamlAttributeMeaning meaning = file.MeaningOf(attribute);
        string local = attribute.LocalName;
        int dot = local.LastIndexOf('.');
        int nameStart = attribute.NameStart + attribute.Name.Length - local.Length;
        if (dot > 0 && meaning.Owner is { } owner && offset <= nameStart + dot)
        {
            return new XamlSymbol(TextRange.FromBounds(nameStart, nameStart + dot), owner.Name, [TypeTarget(owner)]);
        }
        TextRange range = TextRange.FromBounds(dot > 0 ? nameStart + dot + 1 : nameStart, attribute.NameEnd);
        return meaning switch
        {
            { Property: { } property } => new XamlSymbol(range, property.Name, [MemberTarget(property)]),
            { Event: { } @event } => new XamlSymbol(range, @event.Name, [MemberTarget(@event)]),
            _ => null,
        };
    }

    private static XamlSymbol? Value(XamlFile file, XmlAttribute attribute, int offset)
    {
        XmlElement element = attribute.Owner;
        if (MarkupExtension.Parse(file.Text, attribute.ValueStart, attribute.ValueEnd) is { } root)
        {
            return Extension(file, attribute, root.InnermostAt(offset), offset);
        }
        XamlAttributeMeaning meaning = file.MeaningOf(attribute);
        TextRange whole = attribute.ValueRange;
        string value = attribute.Value;
        if (meaning.Kind == XamlAttributeKind.Directive)
        {
            switch (attribute.LocalName)
            {
                case "Class" when file.Types.Find(value) is { } owner:
                    return new XamlSymbol(whole, owner.Name, [TypeTarget(owner)]);
                case "DataType" when file.TypeFromValue(element, value) is { } dataType:
                    return new XamlSymbol(whole, dataType.Name, [TypeTarget(dataType)]);
                case "Name":
                    return Named(file, attribute);
                case "Key" when value.Length > 0:
                    return new XamlSymbol(whole, value, [new XamlTarget(XamlTargetKind.Resource, value) { Path = file.Path, Range = whole }]);
            }
            return null;
        }
        if (attribute.Name == "Name" && !element.IsPropertyElement && file.NameAttribute(element) == attribute)
        {
            return Named(file, attribute);
        }
        if (meaning.Kind == XamlAttributeKind.Event && file.ClassType is { } code
            && file.Types.SelfAndBases(code).SelectMany(t => t.Members).FirstOrDefault(m => m.Kind == MemberKind.Method && m.Name == value) is { } handler)
        {
            return new XamlSymbol(whole, value, [MemberTarget(handler)]);
        }
        if (attribute.Name == "Property" && XamlCompletion.IsSetterOrTrigger(element) && XamlCompletion.SetterProperty(file, element) is { } set)
        {
            return new XamlSymbol(whole, set.Name, [MemberTarget(set)]);
        }
        if (IsNameReference(attribute) && file.ElementNamed(element, value) is { } target && file.NameAttribute(target) is { } targetName)
        {
            return new XamlSymbol(whole, value, [new XamlTarget(XamlTargetKind.Element, value) { Path = file.Path, Range = targetName.ValueRange }]) { Named = targetName };
        }
        if (element.LocalName == "Style" && attribute.Name == "Selector")
        {
            return Selector(file, attribute, offset);
        }
        if ((attribute.Name is "TargetType" or "DataType" || meaning.Property?.ValueType.Name == "System.Type") && file.TypeFromValue(element, value) is { } named)
        {
            return new XamlSymbol(whole, named.Name, [TypeTarget(named)]);
        }
        if (attribute.Name is "Source" or "Icon" && file.Project.ResolveSource(value, file.Path) is { } path && File.Exists(path))
        {
            return new XamlSymbol(whole, Path.GetFileName(path), [new XamlTarget(XamlTargetKind.File, Path.GetFileName(path)) { Path = path }]);
        }
        if (meaning.Property is { } property && file.Types.Resolve(file.Dialect.ValueTypeOf(property.ValueType)) is { Kind: ClrTypeKind.Enum } @enum
            && @enum.Members.FirstOrDefault(m => m.Kind == MemberKind.Field && string.Equals(m.Name, value.Trim(), StringComparison.OrdinalIgnoreCase)) is { } member)
        {
            return new XamlSymbol(whole, member.Name, [MemberTarget(member)]);
        }
        return null;
    }

    /// <summary>An element's name, which leads to itself and is what rename and its uses work from.</summary>
    private static XamlSymbol Named(XamlFile file, XmlAttribute attribute) =>
        new(attribute.ValueRange, attribute.Value, [new XamlTarget(XamlTargetKind.Element, attribute.Value) { Path = file.Path, Range = attribute.ValueRange }])
        {
            Named = attribute,
        };

    private static XamlSymbol? Extension(XamlFile file, XmlAttribute attribute, MarkupExtension extension, int offset)
    {
        XmlElement element = attribute.Owner;
        if (offset >= extension.NameStart && offset <= extension.NameEnd)
        {
            return file.ResolveType(element, extension.Name, extension: true) is { } type
                ? new XamlSymbol(extension.NameRange, extension.Name, [TypeTarget(type)])
                : null;
        }
        MarkupArgument? argument = extension.ArgumentAt(offset);
        if (argument is null || argument.Nested is not null)
        {
            return null;
        }
        switch (extension.LocalName)
        {
            case "StaticResource" or "DynamicResource" when argument.Key is null:
                return Resource(file, argument);
            case "Binding" or "CompiledBinding" or "ReflectionBinding" or "TemplateBinding" when argument.Key is null or "Path":
                return BindingStep(file, attribute, extension, argument, offset);
            case "Binding" or "CompiledBinding" or "ReflectionBinding" when argument.Key == "ElementName":
                return ElementNamed(file, argument.Value, argument.ValueRange);
            case "Reference" when argument.Key is null or "Name" && extension.Prefix.Length > 0 && file.KindOf(file.UriOf(element, extension.Prefix)) == XamlNamespaceKind.Language:
                return ElementNamed(file, argument.Value, argument.ValueRange);
            case "Type" when argument.Key is null && file.ResolveType(element, argument.Value) is { } type:
                return new XamlSymbol(argument.ValueRange, type.Name, [TypeTarget(type)]);
            case "Static" when argument.Key is null or "Member":
                int dot = argument.Value.LastIndexOf('.');
                if (dot > 0 && file.ResolveType(element, argument.Value[..dot]) is { } owner)
                {
                    if (offset <= argument.ValueStart + dot)
                    {
                        return new XamlSymbol(new TextRange(argument.ValueStart, dot), owner.Name, [TypeTarget(owner)]);
                    }
                    string memberName = argument.Value[(dot + 1)..];
                    if (file.Types.SelfAndBases(owner).SelectMany(t => t.Members).FirstOrDefault(m => m.IsStatic && m.Name == memberName) is { } member)
                    {
                        return new XamlSymbol(TextRange.FromBounds(argument.ValueStart + dot + 1, argument.ValueEnd), memberName, [MemberTarget(member)]);
                    }
                }
                return null;
        }
        return null;
    }

    /// <summary>A resource key: every definition the file can reach, the nearest first, a theme's own key from the theme.</summary>
    private static XamlSymbol? Resource(XamlFile file, MarkupArgument key)
    {
        XamlProject project = file.Project;
        XamlFileSummary own = project.Files.TryGetValue(file.Path, out XamlFileSummary? summary) ? summary : XamlFileSummary.Of(file.Path, file.Tree, project.ResolveSource);
        List<XamlTarget> targets = [.. project.Definitions(own, key.Value).Select(d => new XamlTarget(XamlTargetKind.Resource,
            d.Variant is null ? $"{d.Key} in {Path.GetFileName(d.Path)}" : $"{d.Key} ({d.Variant}) in {Path.GetFileName(d.Path)}")
        {
            Resource = d,
            Path = d.Path,
            Range = d.KeyRange,
        })];
        if (targets.Count == 0)
        {
            targets.AddRange(project.Files.Values.SelectMany(f => f.Resources).Where(r => r.Key == key.Value)
                .Select(d => new XamlTarget(XamlTargetKind.Resource, $"{d.Key} in {Path.GetFileName(d.Path)}") { Resource = d, Path = d.Path, Range = d.KeyRange }));
        }
        if (targets.Count == 0 && project.ThemeKeys.Contains(key.Value))
        {
            targets.Add(new XamlTarget(XamlTargetKind.Theme, $"{key.Value} in the theme") { Part = key.Value });
        }
        return targets.Count == 0 ? null : new XamlSymbol(key.ValueRange, key.Value, targets);
    }

    /// <summary>The step of a binding's path the caret is on, resolved from the data type through the steps before it;
    /// a <c>#name</c> step, the element named so.</summary>
    private static XamlSymbol? BindingStep(XamlFile file, XmlAttribute attribute, MarkupExtension binding, MarkupArgument path, int offset)
    {
        ClrType? type = binding.LocalName == "TemplateBinding" ? file.StyleTargetAt(attribute.Owner) : file.DataTypeOf(attribute, binding);
        string text = path.Value;
        int at = 0;
        while (at < text.Length && text[at] == '!')
        {
            at++;
        }
        if (at < text.Length && text[at] == '#')
        {
            int end = text.IndexOf('.', at);
            string name = end < 0 ? text[(at + 1)..] : text[(at + 1)..end];
            TextRange range = new(path.ValueStart + at + 1, name.Length);
            if (end < 0 || offset <= path.ValueStart + end)
            {
                return ElementNamed(file, name, range);
            }
            type = file.Names.Where(n => n.Name == name).Select(n => file.ObjectOf(n.Attribute.Owner)).FirstOrDefault();
            at = end + 1;
        }
        if (type is null)
        {
            return null;
        }
        int start = path.ValueStart + at;
        foreach (string step in text[at..].Split('.'))
        {
            int bracket = step.IndexOf('[');
            string member = (bracket < 0 ? step : step[..bracket]).Trim();
            ClrMember? property = file.Types.MembersOf(type).FirstOrDefault(m => m.Kind == MemberKind.Property && !m.IsStatic && m.Name == member);
            if (property is null)
            {
                return null;
            }
            if (offset <= start + step.Length)
            {
                return new XamlSymbol(new TextRange(start, member.Length), member, [MemberTarget(property)]);
            }
            if (file.Types.Resolve(property.Type) is not { } next)
            {
                return null;
            }
            type = next;
            start += step.Length + 1;
        }
        return null;
    }

    private static XamlSymbol? ElementNamed(XamlFile file, string name, TextRange range)
    {
        XmlAttribute? named = file.Names.Where(n => n.Name == name).Select(n => n.Attribute).FirstOrDefault();
        return named is null ? null : new XamlSymbol(range, name, [new XamlTarget(XamlTargetKind.Element, name) { Path = file.Path, Range = named.ValueRange }])
        {
            Named = named,
        };
    }

    /// <summary>A selector's type, or a template part's name after <c>/template/</c>, which leads into the theme's template
    /// of the type it is in.</summary>
    private static XamlSymbol? Selector(XamlFile file, XmlAttribute attribute, int offset)
    {
        if (attribute.Value != attribute.RawValue)
        {
            return null;
        }
        IReadOnlyList<SelectorToken> tokens = Selectors.Tokenize(attribute.Value);
        int at = offset - attribute.ValueStart;
        string? templated = null;
        string? lastType = null;
        bool inTemplate = false;
        foreach (SelectorToken token in tokens)
        {
            if (token.Kind == SelectorPart.Template)
            {
                templated = lastType;
                inTemplate = true;
            }
            if (at >= token.Start && at <= token.End)
            {
                TextRange range = new(attribute.ValueStart + token.Start, token.End - token.Start);
                if (token.Kind == SelectorPart.Type && file.ResolveType(attribute.Owner, token.Text.Replace('|', ':')) is { } type)
                {
                    return new XamlSymbol(range, type.Name, [TypeTarget(type)]);
                }
                if (token.Kind == SelectorPart.Name && inTemplate && templated is not null && file.ResolveType(attribute.Owner, templated.Replace('|', ':')) is { } owner)
                {
                    return new XamlSymbol(TextRange.FromBounds(range.Start + 1, range.End), token.Text,
                        [new XamlTarget(XamlTargetKind.Theme, $"{token.Text} in {owner.Name}'s template") { Type = owner, Part = token.Text }]);
                }
                return null;
            }
            if (token.Kind == SelectorPart.Type)
            {
                lastType = token.Text;
            }
        }
        return null;
    }

    /// <summary>Where a file names what <paramref name="isIt"/> picks out of what a name leads to, written as one of
    /// <paramref name="names"/>: each whole word of one resolved as <see cref="At"/> resolves it, so a use is a place Go to
    /// Declaration goes from to it.</summary>
    public static IReadOnlyList<TextRange> Uses(XamlFile file, IReadOnlyCollection<string> names, Func<XamlTarget, bool> isIt)
    {
        string text = file.Text;
        SortedDictionary<int, TextRange> uses = [];
        foreach (string name in names.Where(n => n.Length > 0))
        {
            for (int at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(name, at + name.Length, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsWordPart(text, at - 1) && !IsWordPart(text, at + name.Length) && At(file, at) is { } symbol && symbol.Targets.Any(isIt))
                {
                    uses.TryAdd(symbol.Range.Start, symbol.Range);
                }
            }
        }
        return [.. uses.Values];
    }

    private static bool IsWordPart(string text, int at) => at >= 0 && at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_');

    /// <summary>Where a file names an element: its <c>x:Name</c> (or <c>Name</c>) value, a binding's
    /// <c>ElementName</c> and <c>#name</c>, and a selector's <c>#name</c>; what renaming the element changes.</summary>
    public static IReadOnlyList<TextRange> NameUses(XamlFile file, string name)
    {
        List<TextRange> uses = [];
        foreach (XmlElement element in file.Tree.Elements)
        {
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (!attribute.HasValue)
                {
                    continue;
                }
                if (attribute == file.NameAttribute(element) || IsNameReference(attribute))
                {
                    if (attribute.Value == name)
                    {
                        uses.Add(attribute.ValueRange);
                    }
                    continue;
                }
                if (MarkupExtension.Parse(file.Text, attribute.ValueStart, attribute.ValueEnd) is { } extension)
                {
                    Uses(extension, name, uses);
                }
                else if (element.LocalName == "Style" && attribute.Name == "Selector" && attribute.Value == attribute.RawValue)
                {
                    uses.AddRange(Selectors.Tokenize(attribute.Value).Where(t => t.Kind == SelectorPart.Name && t.Text == name)
                        .Select(t => new TextRange(attribute.ValueStart + t.Start + 1, name.Length)));
                }
            }
        }
        return [.. uses.OrderBy(u => u.Start)];
    }

    private static void Uses(MarkupExtension extension, string name, List<TextRange> uses)
    {
        foreach (MarkupArgument argument in extension.Arguments)
        {
            if (argument.Nested is { } nested)
            {
                Uses(nested, name, uses);
                continue;
            }
            if ((argument.Key == "ElementName" || (extension.LocalName == "Reference" && argument.Key is null or "Name")) && argument.Value == name)
            {
                uses.Add(argument.ValueRange);
            }
            else if (argument.Key is null or "Path" && extension.LocalName is "Binding" or "CompiledBinding" or "ReflectionBinding")
            {
                string path = argument.Value.TrimStart('!');
                int at = argument.ValueStart + (argument.Value.Length - path.Length);
                if (path.Length > name.Length && path[0] == '#' && path.AsSpan(1).StartsWith(name, StringComparison.Ordinal)
                    && (path.Length == name.Length + 1 || path[name.Length + 1] == '.'))
                {
                    uses.Add(new TextRange(at + 1, name.Length));
                }
            }
        }
    }

    /// <summary>Whether an attribute's value is an element's name: WPF's setter <c>TargetName</c>, a trigger's
    /// <c>SourceName</c>, an animation's <c>Storyboard.TargetName</c>.</summary>
    private static bool IsNameReference(XmlAttribute attribute) =>
        attribute.Name is "Storyboard.TargetName" || (attribute.Name is "TargetName" or "SourceName" && XamlCompletion.IsSetterOrTrigger(attribute.Owner));

    /// <summary>Where a XAML text uses a resource key, in a <c>StaticResource</c> or <c>DynamicResource</c>.</summary>
    public static IReadOnlyList<TextRange> ResourceUses(XmlTree tree, string key)
    {
        List<TextRange> uses = [];
        foreach (XmlElement element in tree.Elements)
        {
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (attribute.HasValue && attribute.RawValue.Contains(key, StringComparison.Ordinal)
                    && MarkupExtension.Parse(tree.Text, attribute.ValueStart, attribute.ValueEnd) is { } extension)
                {
                    ResourceUses(extension, key, uses);
                }
            }
        }
        return uses;
    }

    private static void ResourceUses(MarkupExtension extension, string key, List<TextRange> uses)
    {
        if (extension.LocalName is "StaticResource" or "DynamicResource" && extension.Argument(null) is { Nested: null } argument && argument.Value == key)
        {
            uses.Add(argument.ValueRange);
        }
        foreach (MarkupArgument nested in extension.Arguments)
        {
            if (nested.Nested is { } inner)
            {
                ResourceUses(inner, key, uses);
            }
        }
    }

    /// <summary>What a name is, in words: a type with its base, a member with its type and declaring type, a resource with
    /// what makes it (and its colour), an element with its type.</summary>
    public static string Describe(XamlFile file, XamlSymbol symbol)
    {
        XamlTarget target = symbol.Targets[0];
        switch (target.Kind)
        {
            case XamlTargetKind.Type when target.Type is { } type:
                string kind = type.Kind switch { ClrTypeKind.Struct => "struct", ClrTypeKind.Enum => "enum", ClrTypeKind.Interface => "interface", _ => "class" };
                ClrType? baseType = file.Types.BaseOf(type);
                return $"{kind} {type.FullName}{(baseType is null || baseType.FullName == "System.Object" ? string.Empty : $" : {baseType.Name}")}";
            case XamlTargetKind.Member when target.Member is { } member:
                return member.Kind switch
                {
                    MemberKind.Method => $"{member.Type.Display} {member.DeclaringType.Name}.{member.Name}({string.Join(", ", member.Parameters.Select(p => p.Display))})",
                    MemberKind.Event => $"event {member.Type.Display} {member.DeclaringType.Name}.{member.Name}",
                    _ => $"{file.Dialect.ValueTypeOf(member.Type).Display} {member.DeclaringType.Name}.{member.Name}",
                };
            case XamlTargetKind.Resource when target.Resource is { } resource:
                return string.Join('\n', symbol.Targets.Select(t => t.Resource).OfType<ResourceDefinition>().Select(r =>
                    $"{r.ElementName} {r.Key}{(r.Variant is null ? string.Empty : $" ({r.Variant})")}{(r.Colour is null ? string.Empty : $" = {r.Colour}")} in {Path.GetFileName(r.Path)}"));
            case XamlTargetKind.Element when symbol.Named is { } named && file.ObjectOf(named.Owner) is { } element:
                return $"{element.Name} {symbol.Name}";
        }
        return target.Name;
    }

    private static XamlTarget TypeTarget(ClrType type) => new(XamlTargetKind.Type, type.FullName) { Type = type, Place = type.Origin.Place };

    private static XamlTarget MemberTarget(XamlProperty property) =>
        property.Declaration is { } member ? MemberTarget(member) : new XamlTarget(XamlTargetKind.Type, property.Owner.FullName) { Type = property.Owner, Place = property.Owner.Origin.Place };

    private static XamlTarget MemberTarget(ClrMember member) =>
        new(XamlTargetKind.Member, $"{member.DeclaringType.Name}.{member.Name}") { Type = member.DeclaringType, Member = member, Place = member.Place };
}
