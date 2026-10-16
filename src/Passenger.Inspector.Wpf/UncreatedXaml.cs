using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Xml;
using System.Xml.Linq;

namespace Passenger.Inspector.Wpf;

/// <summary>Proves a view's source and built-in property conversions without running its constructors,
/// static initializers or BAML loader. Unknown source, type and property shapes still need a restart.</summary>
internal static class UncreatedXaml
{
    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly Assembly[] Frameworks = [typeof(DependencyObject).Assembly, typeof(UIElement).Assembly, typeof(FrameworkElement).Assembly];

    public static Type? Prepare(XamlSourceUpdate source, string original, XamlUpdatePlan plan, List<XamlUpdateProblem> problems)
    {
        XamlPropertyUpdate first = plan.Properties[0];
        try
        {
            List<Type> candidates = [.. AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic)
                .Select(a => a.GetType(first.Class!, throwOnError: false, ignoreCase: false)).OfType<Type>()];
            if (candidates is not [Type type] || type.IsAbstract || type.ContainsGenericParameters || !typeof(FrameworkElement).IsAssignableFrom(type))
            {
                Refuse(first, "No single loaded WPF view type matches this source");
                return null;
            }
            if (AppDomain.CurrentDomain.GetAssemblies().Count(a => string.Equals(a.GetName().Name, type.Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)) != 1)
            {
                Refuse(first, "The compiled component's assembly name is ambiguous in the running app");
                return null;
            }
            if (Baseline(type.Assembly, source.File, original) is { } mismatch)
            {
                Refuse(first, mismatch);
                return null;
            }
            XElement built = XElement.Parse(original, LoadOptions.SetLineInfo);
            foreach (XamlPropertyUpdate edit in plan.Properties)
            {
                XElement? element = built.DescendantsAndSelf().SingleOrDefault(e => e is IXmlLineInfo line
                    && line.LineNumber == edit.Line && line.LinePosition == edit.Column);
                if (element is null || element.Name.NamespaceName != Presentation || Builtin(element.Name.LocalName) is not { } elementType)
                {
                    Refuse(edit, "This element's property cannot be verified before its first live instance; only built-in WPF elements are supported");
                    continue;
                }
                if (edit.Added && edit.Property.Contains('.', StringComparison.Ordinal))
                {
                    Refuse(edit, "An added attached property needs a matching live instance to verify its runtime value source");
                    continue;
                }
                string owner = edit.Property.Contains('.', StringComparison.Ordinal) ? edit.Property[..edit.Property.IndexOf('.')] : element.Name.LocalName;
                string name = edit.Property.Contains('.', StringComparison.Ordinal) ? edit.Property[(edit.Property.IndexOf('.') + 1)..] : edit.Property;
                Type? ownerType = Builtin(owner);
                FieldInfo? field = ownerType?.GetField(name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (field?.FieldType != typeof(DependencyProperty) || field.DeclaringType is not { } declaring || !Frameworks.Contains(declaring.Assembly)
                    || edit.Root && Shadows(type, elementType, name))
                {
                    Refuse(edit, $"{edit.Type}.{edit.Property} is not an unambiguous built-in WPF dependency property before its first live instance");
                    continue;
                }
                if (field.GetValue(null) is not DependencyProperty { ReadOnly: false } property)
                {
                    Refuse(edit, $"{edit.Type}.{edit.Property} is not a writable WPF dependency property");
                    continue;
                }
                try
                {
                    object? value = XamlHotReload.Convert(edit.Value, property.PropertyType);
                    if (!property.IsValidValue(value))
                    {
                        throw new FormatException($"'{edit.Value}' is not valid for {edit.Property}");
                    }
                }
                catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException or OverflowException)
                {
                    problems.Add(new XamlUpdateProblem(source.File, edit.EditedLine, edit.EditedColumn, $"{edit.Property}: {error.Message}", false));
                }
            }
            return type;
        }
        catch (Exception error)
        {
            Refuse(first, $"The view's compiled source or property metadata could not be verified: {error.Message}");
            return null;
        }

        void Refuse(XamlPropertyUpdate edit, string message) => problems.Add(new XamlUpdateProblem(source.File, edit.EditedLine, edit.EditedColumn,
            $"{message.TrimEnd('.')}. Restart to load the edit", true));
    }

    private static bool Shadows(Type type, Type element, string name)
    {
        if (!element.IsAssignableFrom(type))
        {
            return true;
        }
        for (Type? at = type; at is not null && !Frameworks.Contains(at.Assembly); at = at.BaseType)
        {
            if (at.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is not null
                || at.GetField(name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly) is not null)
            {
                return true;
            }
        }
        return false;
    }

    private static Type? Builtin(string name)
    {
        List<Type> found = [];
        foreach (Assembly assembly in Frameworks)
        {
            foreach (CustomAttributeData attribute in assembly.GetCustomAttributesData().Where(a => a.AttributeType == typeof(XmlnsDefinitionAttribute)))
            {
                if (attribute.ConstructorArguments is [{ Value: Presentation }, { Value: string space }]
                    && !attribute.NamedArguments.Any(a => a.MemberName == nameof(XmlnsDefinitionAttribute.AssemblyName) && a.TypedValue.Value is string { Length: > 0 })
                    && assembly.GetType(space + "." + name, throwOnError: false, ignoreCase: false) is { } type)
                {
                    found.Add(type);
                }
            }
        }
        return found.Distinct().ToArray() is [Type only] ? only : null;
    }

    private static string? Baseline(Assembly assembly, string file, string original)
    {
        if (!Path.IsPathFullyQualified(file) || assembly.Location is not { Length: > 0 } location || !File.Exists(location))
        {
            return "The view's original compiled source is unavailable";
        }
        using FileStream stream = File.OpenRead(location);
        using PEReader pe = new(stream);
        MetadataReader module = pe.GetMetadataReader();
        if (module.GetGuid(module.GetModuleDefinition().Mvid) != assembly.ManifestModule.ModuleVersionId)
        {
            return "The view's assembly on disk no longer matches the running module";
        }
        if (!pe.TryOpenAssociatedPortablePdb(location, path => File.Exists(path) ? File.OpenRead(path) : null,
            out MetadataReaderProvider? provider, out _) || provider is null)
        {
            return "The view needs its matching portable symbols to verify the original XAML";
        }
        using (provider)
        {
            MetadataReader pdb = provider.GetMetadataReader();
            List<Document> documents = [.. pdb.Documents.Select(pdb.GetDocument).Where(d => SameFile(pdb.GetString(d.Name), file))];
            if (documents is not [Document document] || document.HashAlgorithm.IsNil || document.Hash.IsNil)
            {
                return "The original XAML's checksum is missing or ambiguous in the view's portable symbols";
            }
            Guid algorithm = pdb.GetGuid(document.HashAlgorithm);
            byte[] expected = pdb.GetBlobBytes(document.Hash);
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
            {
                byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(original)];
                byte[]? actual = algorithm == new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460") ? SHA1.HashData(bytes)
                    : algorithm == new Guid("8829d00f-11b8-4213-878b-770e8597ac16") ? SHA256.HashData(bytes) : null;
                if (actual is not null && actual.AsSpan().SequenceEqual(expected))
                {
                    return null;
                }
            }
            return "The original XAML does not match its compiled checksum, or uses an unsupported source encoding or checksum algorithm";
        }
    }

    private static bool SameFile(string first, string second) => Path.IsPathFullyQualified(first)
        && string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
