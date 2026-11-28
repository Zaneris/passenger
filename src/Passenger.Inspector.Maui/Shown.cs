using System.Globalization;
using System.Reflection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using MauiElement = Microsoft.Maui.Controls.Element;

namespace Passenger.Inspector.Maui;

/// <summary>What a MAUI element shows and is called as the inspector gives it: its name (its x:Name, which MAUI's XAML gives
/// it as its StyleId), where its XAML made it, the text it shows, its bindable properties and their values in the window's
/// words.</summary>
internal static class Shown
{
    /// <summary>The launched project's folder, which the XAML files MAUI names relative to it are in; null when not told.</summary>
    public static string? Project { get; set; }

    /// <summary>Where the app's XAML made an element: its file in the project's folder, and the line and column MAUI's build
    /// recorded (it records them where ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO is "1"); null for one no XAML made, or with no
    /// project told.</summary>
    public static SourcePlace? Place(MauiElement element)
    {
        if (Project is not { } project || VisualDiagnostics.GetSourceInfo(element) is not (Uri uri, int line, int column) || line < 1)
        {
            return null;
        }
        // "Views/MainPage.xaml;assembly=MauiApp1", relative to the project, written with the build's own separators.
        string relative = uri.OriginalString.Split(';')[0].Replace('\\', '/');
        return new SourcePlace(Path.GetFullPath(Path.Combine(project, relative)), line, Math.Max(column, 1));
    }

    /// <summary>An element's name: its x:Name, else its automation id; null for none.</summary>
    public static string? Name(MauiElement element) =>
        element.StyleId is { Length: > 0 } style ? style : element.AutomationId is { Length: > 0 } automation ? automation : null;

    /// <summary>The text an element shows: a label's, a button's or an entry's (a password's masked), a page's or a window's
    /// title, a shell item's; null for none.</summary>
    public static string? TextOf(MauiElement element)
    {
        string? text = element switch
        {
            Label { FormattedText: { } formatted } => formatted.ToString(),
            IEntry { IsPassword: true } password => Words.Dots(password.Text),
            IText shows => shows.Text,
            RadioButton { Content: string content } => content,
            ITitledElement titled => titled.Title,
            BaseShellItem item => item.Title,
            _ => null,
        };
        return text is { Length: > 0 } ? Words.Shows(text) : null;
    }

    /// <summary>An element as a sentence names it: its type, then #name, else the text it shows.</summary>
    public static string Called(MauiElement element) =>
        Words.Called(Words.Describe(element.GetType(), Name(element)), Name(element) is null ? TextOf(element) : null);

    /// <summary>The bindable properties a type and its bases declare as their own, by name, the most derived first.</summary>
    public static IEnumerable<BindableProperty> Properties(Type type)
    {
        HashSet<string> named = [];
        for (Type? at = type; at is not null && at != typeof(object); at = at.BaseType)
        {
            foreach (FieldInfo field in at.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(BindableProperty) && field.GetValue(null) is BindableProperty property && property.DeclaringType.IsAssignableFrom(type)
                    && named.Add(property.PropertyName))
                {
                    yield return property;
                }
            }
        }
    }

    /// <summary>The values of the properties named that an element has (a bindable one by its name, an attached one as
    /// Owner.Name, else a CLR one), as an agent's look asks for them; null when it has none of them.</summary>
    public static Dictionary<string, string>? Values(MauiElement element, IReadOnlyList<string> names) =>
        Words.Values(names, name => Bindable(element.GetType(), name) is { } property ? Value(element.GetValue(property)) : Words.Read(element, name, Value));

    /// <summary>A bindable property by its name on a type (Owner.Name for one another type declares), or null.</summary>
    public static BindableProperty? Bindable(Type type, string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return Properties(type).FirstOrDefault(p => p.PropertyName == name);
        }
        Type? owner = typeof(MauiElement).Assembly.GetTypes().FirstOrDefault(t => t.IsPublic && t.Name == name[..dot]);
        return owner?.GetField($"{name[(dot + 1)..]}Property", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as BindableProperty;
    }

    /// <summary>A value as the window shows it, MAUI's own kinds as its XAML writes them.</summary>
    public static string Value(object? value) => Words.Value(value, Framed);

    private static string? Framed(object value) => value switch
    {
        Color color => color.ToArgbHex(includeAlpha: true),
        SolidColorBrush { Color: { } solid } => solid.ToArgbHex(includeAlpha: true),
        Thickness edges => Words.Thickness(edges.Left, edges.Top, edges.Right, edges.Bottom),
        LayoutOptions options => options.Alignment.ToString(),
        Rect rect => string.Create(CultureInfo.InvariantCulture, $"{rect.X},{rect.Y},{rect.Width},{rect.Height}"),
        Size size => string.Create(CultureInfo.InvariantCulture, $"{size.Width},{size.Height}"),
        FileImageSource { File: { } file } => file,
        UriImageSource { Uri: { } uri } => uri.ToString(),
        Microsoft.Maui.Font font => string.Create(CultureInfo.InvariantCulture, $"{font.Family ?? "(default)"} {font.Size}"),
        _ => null,
    };
}
