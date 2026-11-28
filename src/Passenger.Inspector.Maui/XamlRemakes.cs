using System.Collections;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Internals;
using MauiElement = Microsoft.Maui.Controls.Element;

namespace Passenger.Inspector.Maui;

/// <summary>An open page made again from its XAML where MAUI's own patch cannot bring it to the text (a binding's, a C#
/// expression's or another markup extension's change, or one MAUI reloads only whole): the InitializeComponent the
/// program's update now carries run again on it, what the text before wrote cleared first (the handlers it named on the
/// page, the names it gave, the resources it wrote, the items of the page's lists it made, the page's properties the text
/// now leaves unset), and the page put back as it was where the text fails partway: its values, those the text bound or
/// took from a dynamic resource bound again, and its named fields. What the app put there itself stays, a dictionary it
/// merged into the page's resources too.</summary>
internal static class XamlRemakes
{
    /// <summary>Makes <paramref name="page"/> again from the text of <paramref name="type"/>'s XAML (its own class's, or
    /// one it derives from); why it could not, the page as it was, else null.</summary>
    public static string? Remake(MauiElement page, Type type, XamlChange change)
    {
        string file = Path.GetFileName(change.File);
        if (Initializer(type) is not { } build)
        {
            return $"{Shown.Called(page)} has no InitializeComponent to make it again from {file}: restart the app to see the change";
        }
        for (Type? between = page.GetType(); between is not null && between != type; between = between.BaseType)
        {
            if (Initializer(between) is not null)
            {
                return $"{Shown.Called(page)} is made by {between.Name}'s XAML too, over {file}'s: restart the app to see the change";
            }
        }
        if (Read(change.Before) is not { } was || Read(change.Text) is not { } now)
        {
            return $"{file} does not read as XML: restart the app to see the change";
        }
        Held held = new(page, type, change.File, was, now);
        held.Reset(now);
        try
        {
            build.Invoke(page, null);
        }
        catch (TargetInvocationException thrown) when (thrown.InnerException is { } failed)
        {
            held.PutBack(now);
            return $"{file} could not make {Shown.Called(page)} again: {failed.GetBaseException().Message}";
        }
        return null;
    }

    /// <summary>The method a class's own XAML makes it by, declared on the class itself; null where it has none.</summary>
    private static MethodInfo? Initializer(Type type)
    {
        const BindingFlags Own = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        return type.GetMethod("InitializeComponent", Own, Type.EmptyTypes) ?? type.GetMethod("InitializeComponentSourceGen", Own, Type.EmptyTypes);
    }

    /// <summary>What a text writes on its root: its attributes of no namespace (a property, or an event's handler), the
    /// properties it sets as elements, the keys of the resources it writes, and the names (x:Name) it gives.</summary>
    private sealed record Root(IReadOnlyDictionary<string, string> Attributes, IReadOnlySet<string> Elements, IReadOnlySet<string> Keys, IReadOnlySet<string> Names)
    {
        /// <summary>The properties it sets on its root, as attributes or as elements.</summary>
        public IEnumerable<string> Properties => Attributes.Keys.Concat(Elements);
    }

    private static Root? Read(string? text)
    {
        XElement root;
        try
        {
            root = XDocument.Parse(text ?? string.Empty).Root ?? throw new XmlException("no root");
        }
        catch (XmlException)
        {
            return null;
        }
        Dictionary<string, string> attributes = [];
        foreach (XAttribute attribute in root.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None))
        {
            attributes[attribute.Name.LocalName] = attribute.Value;
        }
        List<XElement> set = [.. root.Elements().Where(e => e.Name.LocalName.Contains('.', StringComparison.Ordinal))];
        HashSet<string> keys = [.. set.Where(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)).SelectMany(e => e.Descendants())
            .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key" && a.Name.Namespace != XNamespace.None)?.Value).OfType<string>()];
        HashSet<string> names = [.. root.DescendantsAndSelf().Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name" && a.Name.Namespace != XNamespace.None)?.Value)
            .OfType<string>()];
        return new Root(attributes, set.Select(e => e.Name.LocalName[(e.Name.LocalName.LastIndexOf('.') + 1)..]).ToHashSet(), keys, names);
    }

    /// <summary>A markup extension a value is written as, by its name and its arguments by name (the first unnamed as
    /// <c>""</c>): <c>{DynamicResource Accent}</c>, <c>{Binding Title, Mode=OneWay}</c>; null for a plain value.</summary>
    private static (string Name, Dictionary<string, string> Arguments)? Markup(string written)
    {
        string text = written.Trim();
        if (text.Length < 2 || text[0] != '{' || text[^1] != '}' || text.StartsWith("{}", StringComparison.Ordinal))
        {
            return null;
        }
        string inner = text[1..^1].Trim();
        int space = inner.IndexOfAny([' ', ',']);
        string name = space < 0 ? inner : inner[..space];
        Dictionary<string, string> arguments = new(StringComparer.Ordinal);
        string[] parts = space < 0 ? [] : inner[space..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int at = 0; at < parts.Length; at++)
        {
            int equals = parts[at].IndexOf('=', StringComparison.Ordinal);
            // Only the first argument goes unnamed; any other is a comma inside a value, which is not read here.
            if ((equals < 0 && at > 0) || !arguments.TryAdd(equals < 0 ? string.Empty : parts[at][..equals].Trim(), (equals < 0 ? parts[at] : parts[at][(equals + 1)..]).Trim().Trim('\'')))
            {
                return null;
            }
        }
        return (name, arguments);
    }

    /// <summary>A page held as it was before it is made again: what the text before wrote taken off it, to put back where the
    /// text fails.</summary>
    private sealed class Held
    {
        private readonly MauiElement page;
        private readonly Type type;
        private readonly string file;
        private readonly Root was;
        private readonly Dictionary<FieldInfo, object?> fields = [];
        private readonly INameScope? scope;
        private readonly ResourceDictionary? resources;
        private readonly Dictionary<BindableProperty, object?> values = [];
        private readonly List<(IList List, List<object> Items)> lists = [];
        private readonly List<(EventInfo Event, Delegate Handler)> unhooked = [];

        public Held(MauiElement page, Type type, string file, Root was, Root now)
        {
            this.page = page;
            this.type = type;
            this.file = file;
            this.was = was;
            // The fields of the names either text gives, which its initializer sets as it makes each.
            foreach (string name in was.Names.Union(now.Names))
            {
                if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } field)
                {
                    fields[field] = field.GetValue(page);
                }
            }
            scope = NameScope.GetNameScope(page);
            resources = ResourcesOf(page);
            foreach (string name in was.Properties.Append("Content"))
            {
                if (Shown.Bindable(page.GetType(), name) is { } property && page.IsSet(property))
                {
                    values[property] = page.GetValue(property);
                }
            }
            foreach (IList list in Lists(page))
            {
                lists.Add((list, [.. list.Cast<object>()]));
            }
        }

        /// <summary>The resources of a page, or of the app (App.xaml).</summary>
        private static ResourceDictionary? ResourcesOf(MauiElement page) => page switch
        {
            VisualElement { Resources: { } own } => own,
            Application { Resources: { } own } => own,
            _ => null,
        };

        private static void SetResources(MauiElement page, ResourceDictionary resources)
        {
            if (page is VisualElement visual)
            {
                visual.Resources = resources;
            }
            else if (page is Application app)
            {
                app.Resources = resources;
            }
        }

        /// <summary>The page's own lists a text adds to: its toolbar's items, behaviors, triggers and gesture recognizers.</summary>
        private static IEnumerable<IList> Lists(MauiElement page)
        {
            if (page is Page { ToolbarItems: IList toolbar })
            {
                yield return toolbar;
            }
            if (page is VisualElement { Behaviors: IList behaviors })
            {
                yield return behaviors;
            }
            if (page is VisualElement { Triggers: IList triggers })
            {
                yield return triggers;
            }
            if (page is View { GestureRecognizers: IList gestures })
            {
                yield return gestures;
            }
        }

        /// <summary>What the text before wrote taken off the page, and what it set that the text now does not cleared.</summary>
        public void Reset(Root now)
        {
            foreach ((EventInfo handled, Delegate handler) in Handlers(was))
            {
                handled.RemoveEventHandler(page, handler);
                unhooked.Add((handled, handler));
            }
            // MAUI sets a scope only where none is set.
            page.ClearValue(NameScope.NameScopeProperty);
            NameScope.SetNameScope(page, new NameScope());
            if (resources is not null && (was.Elements.Contains("Resources") || now.Elements.Contains("Resources")))
            {
                ResourceDictionary fresh = new();
                foreach (string key in resources.Keys.Where(k => !was.Keys.Contains(k) && !now.Keys.Contains(k)))
                {
                    fresh[key] = resources[key];
                }
                // A dictionary the app merged in itself (a theme's) stays; those the text merged it makes again.
                foreach (ResourceDictionary merged in resources.MergedDictionaries.Where(m => !MadeHere(m)))
                {
                    fresh.MergedDictionaries.Add(merged);
                }
                SetResources(page, fresh);
            }
            foreach ((IList list, List<object> items) in lists)
            {
                foreach (object item in items.Where(MadeHere))
                {
                    list.Remove(item);
                }
            }
            foreach (string dropped in was.Properties.Except(now.Properties))
            {
                if (Shown.Bindable(page.GetType(), dropped) is { IsReadOnly: false } property)
                {
                    page.ClearValue(property);
                }
            }
        }

        /// <summary>The text failed: the page as it was.</summary>
        public void PutBack(Root now)
        {
            foreach ((EventInfo handled, Delegate handler) in Handlers(now))
            {
                handled.RemoveEventHandler(page, handler);
            }
            page.ClearValue(NameScope.NameScopeProperty);
            if (scope is not null)
            {
                NameScope.SetNameScope(page, scope);
            }
            if (resources is not null)
            {
                SetResources(page, resources);
            }
            foreach ((IList list, List<object> items) in lists)
            {
                list.Clear();
                foreach (object item in items)
                {
                    list.Add(item);
                }
            }
            foreach (string named in now.Properties)
            {
                if (Shown.Bindable(page.GetType(), named) is { IsReadOnly: false } property && !values.ContainsKey(property))
                {
                    page.ClearValue(property);
                }
            }
            foreach ((BindableProperty property, object? value) in values)
            {
                Restore(property, value);
            }
            foreach ((FieldInfo field, object? value) in fields)
            {
                field.SetValue(page, value);
            }
            foreach ((EventInfo handled, Delegate handler) in unhooked)
            {
                handled.AddEventHandler(page, handler);
            }
        }

        /// <summary>A property put back as the text before wrote it: taken from its dynamic resource or bound again where the
        /// text wrote one (a value set alone would end either), else its value.</summary>
        private void Restore(BindableProperty property, object? value)
        {
            string? written = was.Attributes.GetValueOrDefault(property.PropertyName);
            switch (written is null ? null : Markup(written))
            {
                case ("DynamicResource", var arguments) when (arguments.GetValueOrDefault("Key") ?? arguments.GetValueOrDefault(string.Empty)) is { Length: > 0 } key:
                    page.SetDynamicResource(property, key);
                    return;
                case ("Binding", var arguments) when !arguments.Keys.Any(k => k is not ("" or "Path" or "Mode" or "StringFormat"))
                    && Enum.TryParse(arguments.GetValueOrDefault("Mode") ?? nameof(BindingMode.Default), out BindingMode mode):
                    page.SetBinding(property, new Binding(arguments.GetValueOrDefault("Path") ?? arguments.GetValueOrDefault(string.Empty) ?? ".", mode,
                        stringFormat: arguments.GetValueOrDefault("StringFormat")));
                    return;
                default:
                    page.SetValue(property, value);
                    return;
            }
        }

        /// <summary>Whether the file's XAML made an item: MAUI records where it made each.</summary>
        private bool MadeHere(object item) =>
            VisualDiagnostics.GetSourceInfo(item) is (Uri uri, int, int)
            && Path.GetFileName(uri.OriginalString.Split(';')[0].Replace('\\', '/')) == Path.GetFileName(file);

        /// <summary>The handlers a text names on the page's events, as its code adds them: equal to them, so they come off.</summary>
        private IEnumerable<(EventInfo Event, Delegate Handler)> Handlers(Root text)
        {
            foreach ((string name, string method) in text.Attributes)
            {
                if (page.GetType().GetEvent(name, BindingFlags.Public | BindingFlags.Instance) is { EventHandlerType: { } kind } handled
                    && type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == method)
                        .Select(m => Delegate.CreateDelegate(kind, page, m, throwOnBindFailure: false)).FirstOrDefault(d => d is not null) is { } handler)
                {
                    yield return (handled, handler);
                }
            }
        }
    }
}
