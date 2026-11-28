using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.IO;
using System.Resources;
using Passenger.Core;

namespace Passenger.WinFormsDesigner;

/// <summary>A form's resources: its .resx and its cultures' as the request gives them (none: a form with none yet), read for
/// what its code takes from them and what only the designer reads, and what Windows Forms' serializers write of them at each
/// serialization, a .resx a culture, as its resource writer writes one; the form's localizer, whose Localizable and Language
/// its root shows and whose serializers write a localizable form's code.</summary>
internal sealed class FormResources(FormDesignRequest request, ITypeResolutionService types) : IResourceService
{
    private readonly Dictionary<string, StringWriter> writing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceSet?> read = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the .resx could not be read, where it could not.</summary>
    public string? Unread { get; private set; }

    /// <summary>The language the form is designed in, which its code's resources are read in; the invariant culture for its default.</summary>
    public CultureInfo Language { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>The localizer the surface's root takes Localizable and Language from, made once it is loaded.</summary>
    public CodeDomLocalizationProvider? Localizer { get; set; }

    public IResourceReader GetResourceReader(CultureInfo info) => Reader(info.Name);

    public IResourceWriter GetResourceWriter(CultureInfo info)
    {
        Utf8 written = new();
        writing[info.Name] = written;
        return new ResXResourceWriter(written, Framework);
    }

    /// <summary>What the last serialization wrote, a .resx whole by its culture's name (empty for the form's own).</summary>
    public Dictionary<string, string> Taken()
    {
        Dictionary<string, string> taken = writing.ToDictionary(w => w.Key, w => w.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        writing.Clear();
        return taken;
    }

    /// <summary>The form's resource manager as its code makes it, over the .resx files as they stand, in the form's language.</summary>
    public ComponentResourceManager Manager() => new FormResourceManager(this);

    /// <summary>A culture's resources, the form's own for the invariant culture; none for a culture it has no .resx of.</summary>
    public ResourceSet? SetOf(CultureInfo culture)
    {
        if (!read.TryGetValue(culture.Name, out ResourceSet? set))
        {
            read[culture.Name] = set = culture.Name.Length > 0 && TextOf(culture.Name) is null ? null : new ResourceSet(Reader(culture.Name));
        }
        return set;
    }

    /// <summary>Whether the form's .resx in its language, or a culture's it falls back to, holds a value of a name.</summary>
    public bool Holds(string name)
    {
        for (CultureInfo at = Language; ; at = at.Parent)
        {
            try
            {
                if (SetOf(at)?.GetObject(name) is not null)
                {
                    return true;
                }
            }
            catch (Exception unread) when (unread is ArgumentException or System.Xml.XmlException or InvalidOperationException or TypeLoadException or FormatException)
            {
                // A file that cannot be read sets nothing; the note says why where it is read for drawing.
                return false;
            }
            if (at.Equals(CultureInfo.InvariantCulture))
            {
                return false;
            }
        }
    }

    /// <summary>The form's own .resx's metadata, by name.</summary>
    public IEnumerable<(string Name, object? Value)> Metadata()
    {
        using ResXResourceReader reader = Reader(string.Empty);
        List<(string, object?)> found = [];
        try
        {
            IDictionaryEnumerator each = reader.GetMetadataEnumerator();
            while (each.MoveNext())
            {
                found.Add(((string)each.Key, each.Value));
            }
        }
        catch (Exception unread) when (unread is ArgumentException or System.Xml.XmlException or InvalidOperationException or TypeLoadException or FormatException)
        {
            Unread = $"{Path.GetFileName(request.ResourcesPath)} could not be read: {unread.Message}";
        }
        return found;
    }

    private string? TextOf(string culture) => culture.Length == 0 ? request.Resources : request.Cultures.GetValueOrDefault(culture);

    /// <summary>A reader of a culture's .resx, which reads it at its first entry asked: a malformed file throws there.</summary>
    private ResXResourceReader Reader(string culture) => TextOf(culture) is { } text
        ? new ResXResourceReader(new StringReader(text), types) { BasePath = Path.GetDirectoryName(request.ResourcesPath) }
        : Empty();

    private static ResXResourceReader Empty()
    {
        using StringWriter none = new(CultureInfo.InvariantCulture);
        using (ResXResourceWriter writer = new(none))
        {
            writer.Generate();
        }
        return new ResXResourceReader(new StringReader(none.ToString()));
    }

    /// <summary>A type's name as a .resx names it, the SDK's designer's way: a framework type by the .NET Framework assembly it
    /// was first in (System.Drawing, not System.Drawing.Common or .Primitives), which every .NET reads by its forwards, so the
    /// file reads the same whatever .NET it is built for.</summary>
    private static string Framework(Type? type)
    {
        if (type is null)
        {
            return string.Empty;
        }
        string? first = type.Assembly.GetName().Name switch
        {
            "System.Drawing.Common" or "System.Drawing.Primitives" => "System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
            "System.Windows.Forms" or "System.Windows.Forms.Primitives" => "System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
            "System.Private.CoreLib" => "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
            _ => null,
        };
        return first is null || type.IsGenericType ? type.AssemblyQualifiedName ?? type.FullName ?? type.Name : $"{type.FullName}, {first}";
    }

    /// <summary>Text written as a file in UTF-8 holds it, so the XML declaration a .resx starts with says UTF-8, as the file is saved.</summary>
    private sealed class Utf8() : StringWriter(CultureInfo.InvariantCulture)
    {
        public override System.Text.Encoding Encoding { get; } = new System.Text.UTF8Encoding(false);
    }

    /// <summary>A ComponentResourceManager over the form's .resx files, reading in the form's language what its code reads
    /// in the person's.</summary>
    private sealed class FormResourceManager(FormResources resources) : ComponentResourceManager
    {
        public override void ApplyResources(object value, string objectName, CultureInfo? culture) => base.ApplyResources(value, objectName, culture ?? resources.Language);

        public override object? GetObject(string name) => GetObject(name, resources.Language);

        public override string? GetString(string name) => GetString(name, resources.Language);

        public override ResourceSet? GetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents)
        {
            for (CultureInfo? at = culture; at is not null; at = tryParents && !at.Equals(CultureInfo.InvariantCulture) ? at.Parent : null)
            {
                if (resources.SetOf(at) is { } set)
                {
                    return set;
                }
            }
            return null;
        }

        protected override ResourceSet? InternalGetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents) => GetResourceSet(culture, createIfNotExists, tryParents);
    }
}
