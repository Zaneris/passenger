using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Localization;
using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

public enum XamlSeverity
{
    Error,
    Warning,
}

/// <summary>A problem in a XAML file, where it is and in a person's words, and the fixes it offers.</summary>
public sealed record XamlDiagnostic(TextRange Range, XamlSeverity Severity, string Message)
{
    /// <summary>What puts it right (a theme variant's missing key added), each a quick fix.</summary>
    public IReadOnlyList<XamlFix> Fixes { get; init; } = [];

    /// <summary>For a localized string a language lacks (<see cref="XamlLocalization"/>), the key and the file to give it in,
    /// whose fix asks its value.</summary>
    public ResourceGap? Gap { get; init; }
}

/// <summary>A quick fix: its title, the file it changes, and its edits made from that file's text as it is when the fix
/// is chosen; with what its list says beside its title, and the other files it changes or makes with it.</summary>
public sealed record XamlFix(string Title, string Path, Func<string, IReadOnlyList<TextEdit>> Edits)
{
    /// <summary>What the list of fixes says beside it; the file it changes when null.</summary>
    public string? Detail { get; init; }

    /// <summary>The files it changes or makes beyond <see cref="Path"/>, made with it as one command.</summary>
    public IReadOnlyList<XamlFileChange> Others { get; init; } = [];
}

/// <summary>The problems Avalonia's XAML compiler would find, found as the file is typed: markup that is not
/// well-formed, an undeclared prefix, a type, property, event handler, markup extension or directive nothing resolves, a
/// value its property's type does not take (an enum's member, a boolean, a colour), a resource no scope defines, a
/// compiled binding's path its <c>x:DataType</c> does not have or one with no data type to start from, a setter's
/// property its style's target does not have, and a name given twice. A project whose assemblies are not read yet (not
/// restored) is checked for its markup only.</summary>
public static class XamlDiagnostics
{
    private static readonly HashSet<string> KnownDirectives = new(StringComparer.Ordinal)
    {
        "Class", "ClassModifier", "Name", "Key", "DataType", "CompileBindings", "FieldModifier", "Shared", "Arguments", "TypeArguments", "Uid",
        "Subclass", "Code",
    };

    public static IReadOnlyList<XamlDiagnostic> Of(XamlFile file)
    {
        List<XamlDiagnostic> found = [.. file.Tree.Problems.Select(p => new XamlDiagnostic(p.Range, XamlSeverity.Error, p.Message))];
        if (!file.Types.XmlnsDefinitions.Any(d => d.Uri == file.Dialect.PresentationUri))
        {
            return found;
        }
        found.AddRange(ThemeVariants.Of(file));
        Dictionary<string, XmlAttribute> named = new(StringComparer.Ordinal);
        bool islands = file.Tree.Elements.Any(e => e.LocalName == "XData");
        foreach (XmlElement element in file.Tree.Elements)
        {
            if (element.Name.Length == 0 || element.Prefix == "xml")
            {
                continue;
            }
            if (element.Prefix.Length > 0 && file.UriOf(element, element.Prefix) is null)
            {
                found.Add(new XamlDiagnostic(TextRange.FromBounds(element.NameStart, element.NameStart + element.Prefix.Length), XamlSeverity.Error,
                    $"Namespace prefix '{element.Prefix}' is not declared"));
                continue;
            }
            if (element.Prefix.Length > 0 && (file.KindOf(file.UriOf(element, element.Prefix)) == XamlNamespaceKind.Ignorable || file.IsIgnored(element, element.Prefix)))
            {
                continue;
            }
            // XAML's own elements (x:Array, x:Static, x:Code), and the XML an x:XData island holds, are no types of the project's.
            if ((element.LocalName is "Array" or "Static" or "Type" or "Null" or "Reference" or "Code" or "XData" && IsLanguage(file, element))
                || (islands && element.Ancestors().Any(a => a.LocalName == "XData" && IsLanguage(file, a))))
            {
                continue;
            }
            if (element.IsPropertyElement)
            {
                PropertyElement(file, element, found);
            }
            else if (file.TypeOf(element) is null)
            {
                found.Add(new XamlDiagnostic(element.NameRange, XamlSeverity.Error, $"Cannot resolve symbol '{element.Name}'"));
                continue;
            }
            foreach (XmlAttribute attribute in element.Attributes)
            {
                Attribute(file, attribute, found);
            }
            if (file.IsBindingElement(element))
            {
                BindingElement(file, element, found);
            }
            if (file.NameAttribute(element) is { Value.Length: > 0 } name && !InTemplate(element))
            {
                if (named.TryGetValue(name.Value, out XmlAttribute? first))
                {
                    found.Add(new XamlDiagnostic(name.ValueRange, XamlSeverity.Error, $"'{name.Value}' names another element already, on line {Line(file, first.ValueStart) + 1}"));
                }
                else
                {
                    named[name.Value] = name;
                }
            }
        }
        return found;
    }

    private static bool IsLanguage(XamlFile file, XmlElement element) =>
        element.Prefix.Length > 0 && file.KindOf(file.UriOf(element, element.Prefix)) == XamlNamespaceKind.Language;

    private static int Line(XamlFile file, int offset)
    {
        int line = 0;
        string text = file.Text;
        for (int i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    /// <summary>Whether an element is inside a template, whose names are a namescope of their own.</summary>
    private static bool InTemplate(XmlElement element) =>
        element.Ancestors().Any(a => !a.IsPropertyElement && a.LocalName.EndsWith("Template", StringComparison.Ordinal));

    private static void PropertyElement(XamlFile file, XmlElement element, List<XamlDiagnostic> found)
    {
        (ClrType? owner, XamlProperty? property) = file.PropertyElement(element);
        string local = element.LocalName;
        int dot = local.LastIndexOf('.');
        int ownerEnd = element.NameStart + element.Name.Length - (local.Length - dot);
        if (owner is null)
        {
            found.Add(new XamlDiagnostic(TextRange.FromBounds(element.NameStart, ownerEnd), XamlSeverity.Error, $"Cannot resolve symbol '{element.Name[..(element.Name.Length - (local.Length - dot))]}'"));
        }
        else if (property is null)
        {
            found.Add(new XamlDiagnostic(TextRange.FromBounds(ownerEnd + 1, element.NameEnd), XamlSeverity.Error, $"Cannot resolve property '{local[(dot + 1)..]}' on {owner.Name}"));
        }
    }

    private static void Attribute(XamlFile file, XmlAttribute attribute, List<XamlDiagnostic> found)
    {
        if (attribute.Prefix is "xml" || attribute.Name.Length == 0)
        {
            return;
        }
        XmlElement element = attribute.Owner;
        if (attribute.Prefix.Length > 0 && attribute.Prefix != "xmlns" && file.UriOf(element, attribute.Prefix) is null)
        {
            found.Add(new XamlDiagnostic(TextRange.FromBounds(attribute.NameStart, attribute.NameStart + attribute.Prefix.Length), XamlSeverity.Error,
                $"Namespace prefix '{attribute.Prefix}' is not declared"));
            return;
        }
        XamlAttributeMeaning meaning = file.MeaningOf(attribute);
        switch (meaning.Kind)
        {
            case XamlAttributeKind.Ignorable:
                return;
            case XamlAttributeKind.Namespace:
                Namespace(file, attribute, found);
                return;
            case XamlAttributeKind.Directive:
                Directive(file, attribute, found);
                return;
            case XamlAttributeKind.Event:
                Handler(file, attribute, found);
                return;
            case XamlAttributeKind.Unknown:
                if (file.ObjectOf(element) is { } type)
                {
                    int dot = attribute.LocalName.LastIndexOf('.');
                    found.Add(dot > 0 && meaning.Owner is null
                        ? new XamlDiagnostic(TextRange.FromBounds(attribute.NameStart, attribute.NameStart + attribute.Name.Length - (attribute.LocalName.Length - dot)),
                            XamlSeverity.Error, $"Cannot resolve symbol '{attribute.Name[..(attribute.Name.Length - (attribute.LocalName.Length - dot))]}'")
                        : new XamlDiagnostic(attribute.NameRange, XamlSeverity.Error,
                            $"Cannot resolve property '{(dot > 0 ? attribute.LocalName[(dot + 1)..] : attribute.LocalName)}' on {(meaning.Owner ?? type).Name}"));
                }
                return;
        }
        if (!attribute.HasValue)
        {
            return;
        }
        if (MarkupExtension.Parse(file.Text, attribute.ValueStart, attribute.ValueEnd) is { } extension)
        {
            Extension(file, attribute, extension, found);
            return;
        }
        if (meaning.Property is { } property)
        {
            Value(file, attribute, property, found);
        }
        Asset(file, attribute, found);
        if (attribute.Name == "Property" && XamlCompletion.IsSetterOrTrigger(element) && file.SetterTargetAt(element) is { } target
            && XamlCompletion.SetterProperty(file, element) is null)
        {
            found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Error, $"Cannot resolve property '{attribute.Value}' on {target.Name}"));
        }
        if (attribute.Name is "TargetName" or "SourceName" && XamlCompletion.IsSetterOrTrigger(element) && attribute.Value.Length > 0
            && file.ElementNamed(element, attribute.Value) is null)
        {
            found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Error, $"Cannot resolve element '{attribute.Value}'"));
        }
        if (element.LocalName == "Style" && attribute.Name == "Selector")
        {
            Selector(file, attribute, found);
        }
    }

    /// <summary>An asset a value names that is not there: an avares:// path of this project or one it references, or a
    /// path from its folder or the file's, where an include, an image or an icon is read from (a font's family after
    /// its #).</summary>
    private static void Asset(XamlFile file, XmlAttribute attribute, List<XamlDiagnostic> found)
    {
        string value = attribute.Value.Trim();
        bool named = value.StartsWith("avares://", StringComparison.Ordinal) || value.StartsWith("pack://application:", StringComparison.OrdinalIgnoreCase)
            || (attribute.Name is "Source" or "Icon" && value.Length > 0 && !value.Contains("://", StringComparison.Ordinal));
        if (!named)
        {
            return;
        }
        int hash = value.IndexOf('#');
        string path = hash < 0 ? value : value[..hash];
        if (file.Project.ResolveSource(path, file.Path) is { } local && !File.Exists(local) && !Directory.Exists(local))
        {
            found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Warning, $"'{path}' is not found"));
        }
    }

    private static void Namespace(XamlFile file, XmlAttribute attribute, List<XamlDiagnostic> found)
    {
        string uri = attribute.Value;
        if (file.KindOf(uri) != XamlNamespaceKind.Clr)
        {
            return;
        }
        string clr = file.ClrNamespacesOf(uri).FirstOrDefault() ?? string.Empty;
        if (clr.Length > 0 && !file.Types.Namespaces.Contains(clr, StringComparer.Ordinal) && !file.Types.Namespaces.Any(n => n.StartsWith(clr + ".", StringComparison.Ordinal)))
        {
            found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Warning, $"Namespace '{clr}' holds no types"));
        }
    }

    private static void Directive(XamlFile file, XmlAttribute attribute, List<XamlDiagnostic> found)
    {
        XmlElement element = attribute.Owner;
        if (!KnownDirectives.Contains(attribute.LocalName))
        {
            found.Add(new XamlDiagnostic(attribute.NameRange, XamlSeverity.Error, $"Cannot resolve directive '{attribute.Name}'"));
            return;
        }
        switch (attribute.LocalName)
        {
            case "Class" when element.Parent is not null:
                found.Add(new XamlDiagnostic(attribute.NameRange, XamlSeverity.Error, "x:Class is only allowed on the root element"));
                break;
            case "Class" when attribute.Value.Length > 0 && file.Types.Find(attribute.Value) is null:
                found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Error, $"Cannot resolve class '{attribute.Value}'"));
                break;
            case "DataType" when attribute.Value.Length > 0 && file.TypeFromValue(element, attribute.Value) is null:
                found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Error, $"Cannot resolve symbol '{attribute.Value}'"));
                break;
            case "CompileBindings" or "Shared" when !bool.TryParse(attribute.Value, out _):
                found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Error, $"'{attribute.Value}' is not True or False"));
                break;
        }
    }

    /// <summary>An event's handler: a method of the code-behind's class, private ones included.</summary>
    private static void Handler(XamlFile file, XmlAttribute attribute, List<XamlDiagnostic> found)
    {
        if (file.ClassType is not { IsFromSource: true } owner || attribute.Value.Length == 0 || attribute.Value.StartsWith('{'))
        {
            return;
        }
        if (!file.Types.SelfAndBases(owner).Any(t => t.Members.Any(m => m.Kind == MemberKind.Method && m.Name == attribute.Value)))
        {
            found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Error, $"Cannot resolve method '{attribute.Value}' in {owner.Name}"));
        }
    }

    /// <summary>A value as its property's type reads it: an enum's members (flags joined by commas), a boolean, a colour.</summary>
    private static void Value(XamlFile file, XmlAttribute attribute, XamlProperty property, List<XamlDiagnostic> found)
    {
        TypeRef type = file.Dialect.ValueTypeOf(property.ValueType);
        string value = attribute.Value.Trim();
        if (value.Length == 0)
        {
            return;
        }
        if (type.Name == "System.Boolean")
        {
            if (!bool.TryParse(value, out _))
            {
                found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Error, $"'{value}' is not True or False"));
            }
            return;
        }
        if (type.Name is "Avalonia.Media.IBrush" or "Avalonia.Media.Brush" or "Avalonia.Media.ISolidColorBrush" or "Avalonia.Media.SolidColorBrush" or "Avalonia.Media.Color"
            or "System.Windows.Media.Brush" or "System.Windows.Media.SolidColorBrush" or "System.Windows.Media.Color")
        {
            // rgb(), hsl() and hsv() are Avalonia's too, left to its own parser, as are WPF's sc# and ContextColor.
            if (KnownColours.Parse(value) is null && !value.Contains('(', StringComparison.Ordinal) && !value.StartsWith("sc#", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("ContextColor", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Error, $"'{value}' is not a colour"));
            }
            return;
        }
        if (file.Types.Resolve(type) is { Kind: ClrTypeKind.Enum } @enum)
        {
            HashSet<string> members = new(@enum.Members.Where(m => m.Kind == MemberKind.Field && m.IsStatic).Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
            foreach (string part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!members.Contains(part) && !int.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    found.Add(new XamlDiagnostic(attribute.ValueRange, XamlSeverity.Error, $"'{part}' is not a value of {@enum.Name}"));
                    return;
                }
            }
        }
    }

    private static void Extension(XamlFile file, XmlAttribute attribute, MarkupExtension extension, List<XamlDiagnostic> found, string? within = null)
    {
        XmlElement element = attribute.Owner;
        if (extension.Name.Length == 0)
        {
            return;
        }
        // The designer's own (d:DesignInstance) are read at design time only.
        if (extension.Prefix.Length > 0 && (file.KindOf(file.UriOf(element, extension.Prefix)) == XamlNamespaceKind.Ignorable || file.IsIgnored(element, extension.Prefix)))
        {
            return;
        }
        ClrType? type = file.ResolveType(element, extension.Name, extension: true);
        bool language = extension.Prefix.Length > 0 && file.KindOf(file.UriOf(element, extension.Prefix)) == XamlNamespaceKind.Language;
        if (type is null && !(language && extension.LocalName is "Static" or "Type" or "Null" or "True" or "False" or "Reference" or "Array"))
        {
            found.Add(new XamlDiagnostic(extension.NameRange, XamlSeverity.Error, $"Cannot resolve markup extension '{extension.Name}'"));
            return;
        }
        foreach (MarkupArgument argument in extension.Arguments)
        {
            if (argument.Nested is { } nested)
            {
                Extension(file, attribute, nested, found, argument.Key);
            }
        }
        switch (extension.LocalName)
        {
            case "StaticResource" or "DynamicResource" when extension.Argument(null) is { Nested: null, Value.Length: > 0 } key:
                Resource(file, key, dynamic: extension.LocalName == "DynamicResource", found, converter: within == "Converter");
                break;
            case "Binding" or "CompiledBinding" when !language:
                Binding(file, attribute, extension, found, nested: within is not null);
                break;
            case "Type" when language && extension.Argument(null) is { Nested: null, Value.Length: > 0 } named && file.ResolveType(element, named.Value) is null:
                found.Add(new XamlDiagnostic(named.ValueRange, XamlSeverity.Error, $"Cannot resolve symbol '{named.Value}'"));
                break;
            case "Static" when language && extension.Argument(null) is { Nested: null, Value.Length: > 0 } member:
                int dot = member.Value.LastIndexOf('.');
                ClrType? owner = dot > 0 ? file.ResolveType(element, member.Value[..dot]) : null;
                if (owner is null)
                {
                    found.Add(new XamlDiagnostic(member.ValueRange, XamlSeverity.Error, $"Cannot resolve symbol '{(dot > 0 ? member.Value[..dot] : member.Value)}'"));
                }
                else if (!file.Types.SelfAndBases(owner).Any(t => t.Members.Any(m => m.IsStatic && m.Name == member.Value[(dot + 1)..])))
                {
                    found.Add(new XamlDiagnostic(member.ValueRange, XamlSeverity.Error, $"Cannot resolve member '{member.Value[(dot + 1)..]}' of {owner.Name}"));
                }
                break;
        }
    }

    /// <summary>A resource key nothing the file reaches defines: an error for a static resource, which Avalonia looks up
    /// once and must find; a warning for a dynamic one, which may come later (a theme the app loads). A binding's
    /// converter nothing defines offers to create its class and resource.</summary>
    private static void Resource(XamlFile file, MarkupArgument key, bool dynamic, List<XamlDiagnostic> found, bool converter = false)
    {
        XamlProject project = file.Project;
        XamlFileSummary own = project.Files.TryGetValue(file.Path, out XamlFileSummary? summary) ? summary : XamlFileSummary.Of(file.Path, file.Tree, project.ResolveSource);
        if (project.Definitions(own, key.Value).Count > 0)
        {
            if (ThemeVariants.Use(file, own, key.ValueRange, key.Value) is { } missing)
            {
                found.Add(missing);
            }
            return;
        }
        if (project.ThemeKeys.Contains(key.Value) || ReferencedKey(project, key.Value))
        {
            return;
        }
        // A library's merged dictionary may define it unread, and a WPF library takes its resources from its application.
        if (project.ResourceScopes(own).Any(s => s.ForeignIncludes) || (file.Dialect is WpfDialect && project.Application is null))
        {
            return;
        }
        found.Add(new XamlDiagnostic(key.ValueRange, dynamic ? XamlSeverity.Warning : XamlSeverity.Error, $"Resource '{key.Value}' is not found")
        {
            Fixes = converter && CreateFromUsage.Converter(file, key.Value) is { } create ? [create] : [],
        });
    }

    /// <summary>Whether any of the project's XAML files defines a key, where a file this one reaches only through code
    /// (a dictionary loaded by the app at run time) does.</summary>
    private static bool ReferencedKey(XamlProject project, string key) =>
        project.Files.Values.Any(f => f.Resources.Any(r => r.Key == key));

    /// <summary>A compiled binding's path, read against the data type in scope: each step a property of the one before;
    /// one starting at a data context no type is given is marked (<see cref="CompiledBindings"/>). A path from another
    /// source (an element, a relative source but the data context's, a converter's own) is the compiler's to judge. In a
    /// dialect that compiles no binding (WPF's), a path its data context's known type lacks is warned of: it fails only as
    /// the app runs.</summary>
    private static void Binding(XamlFile file, XmlAttribute attribute, MarkupExtension binding, List<XamlDiagnostic> found, bool nested)
    {
        XmlElement element = attribute.Owner;
        bool compiled = file.Dialect.CompilesBindings;
        if (compiled && binding.LocalName == "Binding" && !file.CompilesBindingsAt(element))
        {
            return;
        }
        if (XamlFile.NamesSource(binding))
        {
            return;
        }
        MarkupArgument? path = binding.Argument(null) ?? binding.Argument("Path");
        if (compiled && path?.Nested is null && CompiledBindings.StartsAtDataContext(path?.Value) && !CompiledBindings.HasDataType(file, attribute, nested))
        {
            found.Add(new XamlDiagnostic(binding.NameRange, XamlSeverity.Error, CompiledBindings.NoDataType)
            {
                Fixes = CompiledBindings.Fixes(file, attribute, binding, path?.Value),
            });
            return;
        }
        if (path is null || path.Nested is not null || path.Value.Length == 0 || file.DataTypeOf(attribute, stopAtTemplates: true) is not { } type)
        {
            return;
        }
        Steps(file, type, path.Value, path.ValueStart, compiled, attribute, binding.Argument("Converter") is not null, found);
    }

    /// <summary>A <c>&lt;Binding&gt;</c> element, which compiles as a <c>{Binding}</c> does where bindings compile: one
    /// with no type to start from is marked, else its <c>Path</c> is read against the type in scope.</summary>
    private static void BindingElement(XamlFile file, XmlElement binding, List<XamlDiagnostic> found)
    {
        if (!file.CompilesBindingsAt(binding) || file.NamesSource(binding))
        {
            return;
        }
        XmlAttribute? path = binding.Attribute("Path");
        XmlElement? given = binding.Children.FirstOrDefault(c => c.IsPropertyElement && c.LocalName.EndsWith(".Path", StringComparison.Ordinal));
        if ((path is not null && MarkupExtension.Parse(file.Text, path.ValueStart, path.ValueEnd) is not null) || given is { Children.Count: > 0 })
        {
            return;
        }
        string? value = path?.Value ?? (given is null ? null : XmlEntities.Decode(file.Text[given.Content.Start..given.Content.End]));
        if (CompiledBindings.StartsAtDataContext(value) && !CompiledBindings.HasDataType(file, binding))
        {
            found.Add(new XamlDiagnostic(binding.NameRange, XamlSeverity.Error, CompiledBindings.NoDataType)
            {
                Fixes = CompiledBindings.Fixes(file, binding, value),
            });
            return;
        }
        if (path is { Value.Length: > 0 } && path.Value == path.RawValue && file.DataTypeOf(binding, stopAtTemplates: true) is { } type)
        {
            Steps(file, type, path.Value, path.ValueStart, compiled: true, attribute: null, converted: false, found);
        }
    }

    /// <summary>A path's steps read from a type, each a property of the one before, the first that is not marked, with a
    /// fix creating it where the binding is an attribute's; a path of anything but properties is left to the compiler.</summary>
    private static void Steps(XamlFile file, ClrType type, string path, int start, bool compiled, XmlAttribute? attribute, bool converted, List<XamlDiagnostic> found)
    {
        string text = path.TrimStart('!');
        if (text.Length == 0 || text == "." || text.IndexOfAny(['$', '#', '^', '(', '[', ' ', '/']) >= 0)
        {
            return;
        }
        int at = start + (path.Length - text.Length);
        ClrType current = type;
        string[] steps = text.Split('.');
        for (int i = 0; i < steps.Length; i++)
        {
            string step = steps[i];
            ClrMember? property = file.Types.MembersOf(current).FirstOrDefault(m => m.Kind is MemberKind.Property or MemberKind.Method && !m.IsStatic && m.Name == step);
            if (property is null)
            {
                // An interface's members may come from the interfaces it extends, which only a compiler that reads them judges.
                if (current.Kind == ClrTypeKind.Interface && (!current.IsFromSource || !compiled))
                {
                    return;
                }
                IReadOnlyList<XamlFix> fixes = attribute is not null && CreateFromUsage.Property(file, current, step, attribute, last: i == steps.Length - 1 && !converted) is { } create
                    ? [create]
                    : [];
                found.Add(compiled
                    ? new XamlDiagnostic(new TextRange(at, step.Length), XamlSeverity.Error, $"Cannot resolve property '{step}' on {current.Name}") { Fixes = fixes }
                    : new XamlDiagnostic(new TextRange(at, step.Length), XamlSeverity.Warning, $"Cannot resolve property '{step}' in data context of type {current.Name}") { Fixes = fixes });
                return;
            }
            if (file.Types.Resolve(property.Type) is not { } next || next.FullName == "System.Object")
            {
                return;
            }
            current = next;
            at += step.Length + 1;
        }
    }

    private static void Selector(XamlFile file, XmlAttribute attribute, List<XamlDiagnostic> found)
    {
        // A selector written with character references (&gt;) is placed as a whole, its pieces' places being the read text's.
        bool exact = attribute.Value == attribute.RawValue;
        foreach (SelectorToken token in Selectors.Tokenize(attribute.Value))
        {
            if (token.Kind == SelectorPart.Type && token.Text.Length > 0 && file.ResolveType(attribute.Owner, token.Text.Replace('|', ':')) is null)
            {
                found.Add(new XamlDiagnostic(exact ? new TextRange(attribute.ValueStart + token.Start, token.End - token.Start) : attribute.ValueRange, XamlSeverity.Error,
                    $"Cannot resolve symbol '{token.Text}'"));
            }
        }
    }
}
